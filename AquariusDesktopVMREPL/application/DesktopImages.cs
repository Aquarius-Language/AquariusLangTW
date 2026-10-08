using AquariusLang.Application;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using PortableMetadata = AquariusLang.Application.ImageMetadata;
using PortableFormat = AquariusLang.Application.ImageFormat;

namespace AquariusREPL.Application;

public sealed class DesktopImages : IImageCodec {
    public IReadOnlyCollection<PortableFormat> Formats { get; } = Enum.GetValues<PortableFormat>();
    private static DecoderOptions Options(ApplicationLimits limits) {
        var config = Configuration.Default.Clone();
        config.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions { AllocationLimitMegabytes = Math.Max(1, limits.MaxBytes / (1024 * 1024)), MaximumPoolSizeMegabytes = 16 });
        return new DecoderOptions { Configuration = config, MaxFrames = checked((uint)limits.MaxFrames + 1) };
    }
    private static PortableMetadata Metadata(SixLabors.ImageSharp.Metadata.ImageMetadata metadata, string pixelFormat, bool supportsResolution) {
        int orientation = metadata.ExifProfile != null && metadata.ExifProfile.TryGetValue(ExifTag.Orientation, out var orientationValue) ? orientationValue.Value : 1;
        double factor = metadata.ResolutionUnits == PixelResolutionUnit.PixelsPerCentimeter ? 2.54 : metadata.ResolutionUnits == PixelResolutionUnit.PixelsPerMeter ? .0254 : 1;
        bool resolution = supportsResolution && metadata.ResolutionUnits != PixelResolutionUnit.AspectRatio;
        return new(orientation == 0 ? 1 : orientation, resolution && metadata.HorizontalResolution > 0 ? metadata.HorizontalResolution * factor : null,
            resolution && metadata.VerticalResolution > 0 ? metadata.VerticalResolution * factor : null, pixelFormat);
    }
    public async ValueTask<AquariusLang.Application.ImageInfo> Inspect(ReadOnlyMemory<byte> data, ApplicationLimits limits, CancellationToken cancellation = default) {
        limits.Bytes(data.Length); var format = ImageCodecRegistry.Identify(data.Span);
        using var input = new MemoryStream(data.ToArray(), false);
        try {
            var info = await Image.IdentifyAsync(Options(limits), input, cancellation).ConfigureAwait(false);
            int frames = Math.Max(1, info.FrameMetadataCollection.Count);
            limits.Pixels(info.Width, info.Height, frames);
            return new(format, info.Width, info.Height, frames, Metadata(info.Metadata, $"{info.PixelType.ColorType}, {info.PixelType.BitsPerPixel} bits, {info.PixelType.AlphaRepresentation}", format != PortableFormat.Gif));
        } catch (Exception error) when (error is ImageFormatException or InvalidMemoryOperationException) { throw new ApplicationFailure(FailureKind.InvalidData, "Image inspection failed: " + error.Message, error); }
    }
    public async ValueTask<PixelImage> Decode(ReadOnlyMemory<byte> data, int frame, ApplicationLimits limits, CancellationToken cancellation = default) {
        var info = await Inspect(data, limits, cancellation).ConfigureAwait(false);
        if (frame < 0 || frame >= info.Frames) throw new ArgumentOutOfRangeException(nameof(frame), "Select a valid frame/page explicitly.");
        using var input = new MemoryStream(data.ToArray(), false);
        try {
            using var image = await Image.LoadAsync<Rgba32>(Options(limits), input, cancellation).ConfigureAwait(false);
            using var selected = image.Frames.CloneFrame(frame); var pixels = new byte[limits.Pixels(selected.Width, selected.Height)];
            selected.CopyPixelDataTo(pixels); var metadata = Metadata(selected.Metadata, info.Metadata.SourcePixelFormat, info.Format != PortableFormat.Gif);
            var palette = info.Format switch {
                PortableFormat.Png => selected.Metadata.GetPngMetadata().ColorTable,
                PortableFormat.Bmp => selected.Metadata.GetBmpMetadata().ColorTable,
                PortableFormat.Gif => selected.Frames.RootFrame.Metadata.GetGifMetadata().LocalColorTable ?? selected.Metadata.GetGifMetadata().GlobalColorTable,
                PortableFormat.Tiff => selected.Frames.RootFrame.Metadata.GetTiffMetadata().LocalColorTable,
                _ => null
            };
            if (palette is ReadOnlyMemory<Color> colors) metadata = metadata with { Palette = colors.ToArray().Select(c => { var p = c.ToPixel<Rgba32>(); return (uint)(p.R << 24 | p.G << 16 | p.B << 8 | p.A); }).ToArray() };
            cancellation.ThrowIfCancellationRequested(); return new(selected.Width, selected.Height, pixels, metadata: metadata, limits: limits);
        } catch (Exception error) when (error is ImageFormatException or InvalidMemoryOperationException) { throw new ApplicationFailure(FailureKind.InvalidData, "Image decoding failed: " + error.Message, error); }
    }
    public async ValueTask<byte[]> Encode(PixelImage source, PortableFormat format, ImageEncodingOptions options, ApplicationLimits limits, CancellationToken cancellation = default) {
        limits.Pixels(source.Width, source.Height); var pixels = options.Prepare(source, format);
        cancellation.ThrowIfCancellationRequested();
        if (format is PortableFormat.Png or PortableFormat.Tiff) { var outputBytes = format == PortableFormat.Png ? RgbaImageEncoding.Png(source, options) : RgbaImageEncoding.Tiff(source, options); limits.Bytes(outputBytes.Length); cancellation.ThrowIfCancellationRequested(); return outputBytes; }
        using var image = Image.LoadPixelData<Rgba32>(pixels, source.Width, source.Height);
        if (source.Metadata.DpiX is double x) { image.Metadata.HorizontalResolution = x; image.Metadata.VerticalResolution = source.Metadata.DpiY ?? x; image.Metadata.ResolutionUnits = PixelResolutionUnit.PixelsPerInch; }
        if (format == PortableFormat.Jpeg) {
            image.Metadata.ExifProfile = new ExifProfile(); image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)source.Metadata.Orientation);
        }
        IImageEncoder encoder = format switch {
            PortableFormat.Jpeg => new JpegEncoder { Quality = options.Quality }, PortableFormat.Bmp => new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Bit32 },
            PortableFormat.Gif => new GifEncoder(), _ => throw new ArgumentException("Unsupported image format.")
        };
        using var output = new MemoryStream(); await image.SaveAsync(output, encoder, cancellation).ConfigureAwait(false); limits.Bytes(output.Length);
        var bytes = output.ToArray(); if (ImageCodecRegistry.Identify(bytes) != format) throw new ApplicationFailure(FailureKind.InvalidData, "Encoder returned a different format."); return bytes;
    }
}
