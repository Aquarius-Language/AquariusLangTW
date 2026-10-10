using AquariusLang.Application;
using ImageMagick;
using ImageMagick.Configuration;
using PortableMetadata = AquariusLang.Application.ImageMetadata;
using PortableFormat = AquariusLang.Application.ImageFormat;

namespace AquariusLang.Desktop.Application;

public sealed class DesktopImages : IImageCodec {
    // Resource limits are process-wide: serialize adapter operations and restore
    // limits only after the native images have been disposed.
    private static readonly SemaphoreSlim CodecGate = new(1, 1);
    static DesktopImages() {
        var configuration = ConfigurationFiles.Default;
        configuration.Policy.Data = "<policymap><policy domain=\"delegate\" rights=\"none\" pattern=\"*\"/><policy domain=\"path\" rights=\"none\" pattern=\"*\"/><policy domain=\"coder\" rights=\"none\" pattern=\"*\"/><policy domain=\"coder\" rights=\"read|write\" pattern=\"{PNG,JPEG,BMP,GIF,TIFF,RGBA}\"/></policymap>";
        MagickNET.Initialize(configuration);
    }
    public IReadOnlyCollection<PortableFormat> Formats { get; } = Enum.GetValues<PortableFormat>();
    private static MagickFormat NativeFormat(PortableFormat format) => format switch {
        PortableFormat.Png => MagickFormat.Png, PortableFormat.Jpeg => MagickFormat.Jpeg,
        PortableFormat.Bmp => MagickFormat.Bmp, PortableFormat.Gif => MagickFormat.Gif,
        PortableFormat.Tiff => MagickFormat.Tiff, _ => throw new ArgumentException("Unsupported image format.")
    };
    private sealed class ResourceScope : IDisposable {
        private readonly ulong width = ResourceLimits.Width, height = ResourceLimits.Height,
            frames = ResourceLimits.ListLength, memory = ResourceLimits.Memory, disk = ResourceLimits.Disk,
            request = ResourceLimits.MaxMemoryRequest, profile = ResourceLimits.MaxProfileSize;
        public ResourceScope(ApplicationLimits limits) {
            ResourceLimits.ListLength = Math.Min(frames, (ulong)Math.Max(1L, (long)limits.MaxFrames + 1));
            ResourceLimits.Memory = Math.Min(memory, (ulong)Math.Max(1024 * 1024L, (long)limits.MaxBytes * 2));
            ResourceLimits.Disk = 0;
            ResourceLimits.MaxMemoryRequest = Math.Min(request, (ulong)Math.Max(1024 * 1024, limits.MaxBytes));
            ResourceLimits.MaxProfileSize = Math.Min(profile, (ulong)Math.Max(1, Math.Min(1024 * 1024, limits.MaxBytes)));
        }
        public void Dispose() {
            ResourceLimits.Width = width; ResourceLimits.Height = height; ResourceLimits.ListLength = frames;
            ResourceLimits.Memory = memory; ResourceLimits.Disk = disk;
            ResourceLimits.MaxMemoryRequest = request; ResourceLimits.MaxProfileSize = profile;
        }
    }
    private static async ValueTask<T> Run<T>(ApplicationLimits limits, CancellationToken cancellation, Func<T> operation) {
        await CodecGate.WaitAsync(cancellation).ConfigureAwait(false);
        try {
            return await Task.Run(() => {
                using var resources = new ResourceScope(limits);
                try {
                    cancellation.ThrowIfCancellationRequested(); var result = operation();
                    cancellation.ThrowIfCancellationRequested(); return result;
                } catch (MagickException error) {
                    cancellation.ThrowIfCancellationRequested();
                    var kind = error is MagickResourceLimitErrorException ? FailureKind.LimitExceeded : FailureKind.InvalidData;
                    throw new ApplicationFailure(kind, "Image codec failed: " + error.Message, error);
                }
            }, cancellation).ConfigureAwait(false);
        } finally { CodecGate.Release(); }
    }
    private static PortableMetadata Metadata(IMagickImage<byte> image, bool supportsResolution, bool palette = false) {
        var density = image.Density; double factor = density.Units == DensityUnit.PixelsPerCentimeter ? 2.54 : 1;
        bool resolution = supportsResolution && density.Units != DensityUnit.Undefined;
        uint[]? colors = null;
        if (palette && image.ColormapSize > 0) {
            colors = new uint[image.ColormapSize];
            for (int i = 0; i < colors.Length; i++) {
                var color = image.GetColormapColor(i) ?? throw new ApplicationFailure(FailureKind.InvalidData, "Invalid indexed image palette.");
                colors[i] = (uint)(color.R << 24 | color.G << 16 | color.B << 8 | color.A);
            }
        }
        int orientation = (int)image.Orientation;
        return new(orientation is >= 1 and <= 8 ? orientation : 1,
            resolution && density.X > 0 ? density.X * factor : null,
            resolution && density.Y > 0 ? density.Y * factor : null,
            $"{image.ColorType}, {image.Depth} bits/channel", colors);
    }
    private static MagickReadSettings Settings(PortableFormat format, ApplicationLimits limits) => new() {
        Format = NativeFormat(format), FrameCount = checked((uint)Math.Max(1L, (long)limits.MaxFrames + 1))
    };
    private static void LimitDimensions(ApplicationLimits limits) {
        ResourceLimits.Width = Math.Min(ResourceLimits.Width, (ulong)Math.Max(1, limits.MaxDimension));
        ResourceLimits.Height = Math.Min(ResourceLimits.Height, (ulong)Math.Max(1, limits.MaxDimension));
    }
    private static AquariusLang.Application.ImageInfo InspectCore(ReadOnlyMemory<byte> data, ApplicationLimits limits, CancellationToken cancellation) {
        var format = ImageCodecRegistry.Identify(data.Span);
        using var images = new MagickImageCollection(); images.Ping(data.Span, Settings(format, limits));
        if (images.Count < 1 || images.Count > limits.MaxFrames) throw new ApplicationFailure(FailureKind.LimitExceeded, "Image frame count exceeds the limit.");
        long pixels = 0;
        foreach (var image in images) {
            cancellation.ThrowIfCancellationRequested();
            uint width = format == PortableFormat.Gif ? Math.Max(image.Width, image.Page.Width) : image.Width;
            uint height = format == PortableFormat.Gif ? Math.Max(image.Height, image.Page.Height) : image.Height;
            if (width > int.MaxValue || height > int.MaxValue) throw new ApplicationFailure(FailureKind.LimitExceeded, "Image dimensions exceed the limit.");
            limits.Pixels((int)width, (int)height); pixels += (long)width * height;
        }
        if (pixels > limits.MaxPixels) throw new ApplicationFailure(FailureKind.LimitExceeded, "Image frame allocation exceeds the limit.");
        limits.Bytes(pixels * 4);
        var first = images[0];
        int firstWidth = checked((int)(format == PortableFormat.Gif ? Math.Max(first.Width, first.Page.Width) : first.Width));
        int firstHeight = checked((int)(format == PortableFormat.Gif ? Math.Max(first.Height, first.Page.Height) : first.Height));
        return new(format, firstWidth, firstHeight, images.Count, Metadata(first, format != PortableFormat.Gif));
    }
    public ValueTask<AquariusLang.Application.ImageInfo> Inspect(ReadOnlyMemory<byte> data, ApplicationLimits limits, CancellationToken cancellation = default) {
        limits.Bytes(data.Length); return Run(limits, cancellation, () => InspectCore(data, limits, cancellation));
    }
    public ValueTask<PixelImage> Decode(ReadOnlyMemory<byte> data, int frame, ApplicationLimits limits, CancellationToken cancellation = default) {
        limits.Bytes(data.Length);
        return Run(limits, cancellation, () => {
            var info = InspectCore(data, limits, cancellation);
            if (frame < 0 || frame >= info.Frames) throw new ArgumentOutOfRangeException(nameof(frame), "Select a valid frame/page explicitly.");
            LimitDimensions(limits);
            using var images = new MagickImageCollection(); images.Read(data.Span, Settings(info.Format, limits));
            // GIF subframes may contain only a changed rectangle; return the displayed canvas.
            if (info.Format == PortableFormat.Gif) images.Coalesce();
            var selected = images[frame]; int width = checked((int)selected.Width), height = checked((int)selected.Height);
            limits.Pixels(width, height); var metadata = Metadata(selected, info.Format != PortableFormat.Gif, true);
            if (selected.ColorSpace != ColorSpace.sRGB) selected.ColorSpace = ColorSpace.sRGB;
            using var pixels = selected.GetPixels(); var rgba = pixels.ToByteArray(PixelMapping.RGBA);
            cancellation.ThrowIfCancellationRequested(); return new PixelImage(width, height, rgba, metadata: metadata, limits: limits);
        });
    }
    public ValueTask<byte[]> Encode(PixelImage source, PortableFormat format, ImageEncodingOptions options, ApplicationLimits limits, CancellationToken cancellation = default) {
        limits.Pixels(source.Width, source.Height); cancellation.ThrowIfCancellationRequested(); var pixels = options.Prepare(source, format);
        return Run(limits, cancellation, () => {
            // Portable encoders preserve RGBA and EXIF exactly and honor compression levels.
            if (format is PortableFormat.Png or PortableFormat.Tiff) {
                var output = format == PortableFormat.Png ? RgbaImageEncoding.Png(source, options) : RgbaImageEncoding.Tiff(source, options);
                limits.Bytes(output.Length); return output;
            }
            LimitDimensions(limits);
            using var image = new MagickImage(); image.Progress += (_, progress) => progress.Cancel = cancellation.IsCancellationRequested;
            image.ReadPixels(pixels, new PixelReadSettings((uint)source.Width, (uint)source.Height, StorageType.Char, PixelMapping.RGBA));
            image.Depth = 8; image.Quality = (uint)options.Quality;
            if (source.Metadata.DpiX is double x) image.Density = new Density(x, source.Metadata.DpiY ?? x, DensityUnit.PixelsPerInch);
            if (format == PortableFormat.Jpeg) {
                image.Orientation = (OrientationType)source.Metadata.Orientation;
                var exif = new ExifProfile(); exif.SetValue(ExifTag.Orientation, (ushort)source.Metadata.Orientation); image.SetProfile(exif);
            }
            var bytes = image.ToByteArray(NativeFormat(format)); limits.Bytes(bytes.Length);
            if (ImageCodecRegistry.Identify(bytes) != format) throw new ApplicationFailure(FailureKind.InvalidData, "Encoder returned a different format.");
            return bytes;
        });
    }
}
