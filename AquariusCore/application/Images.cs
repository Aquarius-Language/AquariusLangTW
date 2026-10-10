using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AquariusLang.Application;

public enum ImageFormat { Png, Jpeg, Bmp, Gif, Tiff }
public enum AlphaRepresentation { Opaque, Straight, Premultiplied }
public sealed record ImageMetadata(int Orientation = 1, double? DpiX = null, double? DpiY = null,
    string SourcePixelFormat = "RGBA8", uint[]? Palette = null);
public sealed record ImageInfo(ImageFormat Format, int Width, int Height, int Frames, ImageMetadata Metadata);
/// <summary>Owned, top-down RGBA8 pixels. Providers must copy borrowed native or canvas storage.</summary>
public sealed class PixelImage {
    private readonly byte[] pixels;
    public int Width { get; }
    public int Height { get; }
    public AlphaRepresentation Alpha { get; }
    public ImageMetadata Metadata { get; }
    public ReadOnlyMemory<byte> Pixels => pixels;
    public PixelImage(int width, int height, ReadOnlySpan<byte> rgba, AlphaRepresentation alpha = AlphaRepresentation.Straight,
        ImageMetadata? metadata = null, ApplicationLimits? limits = null) {
        if (!Enum.IsDefined(alpha)) throw new ArgumentException("Unknown alpha representation.");
        if (rgba.Length != (limits ?? new()).Pixels(width, height)) throw new ArgumentException("RGBA byte count must equal width * height * 4.");
        Width = width; Height = height; Alpha = alpha; pixels = rgba.ToArray(); Metadata = (metadata ?? new()) with { Palette = metadata?.Palette?.ToArray() };
        if (Metadata.Orientation is < 1 or > 8 || Metadata.DpiX is double x && (!double.IsFinite(x) || x <= 0) || Metadata.DpiY is double y && (!double.IsFinite(y) || y <= 0))
            throw new ArgumentException("Invalid orientation or resolution.");
        if (Metadata.DpiX.HasValue != Metadata.DpiY.HasValue) Metadata = Metadata with { DpiX = Metadata.DpiX ?? Metadata.DpiY, DpiY = Metadata.DpiY ?? Metadata.DpiX };
        if (alpha == AlphaRepresentation.Opaque && Enumerable.Range(0, width * height).Any(i => pixels[i * 4 + 3] != 255))
            throw new ArgumentException("Opaque pixels must have alpha 255.");
    }
    public byte[] StraightPixels() {
        var result = pixels.ToArray();
        if (Alpha == AlphaRepresentation.Premultiplied) for (int i = 0; i < result.Length; i += 4)
            for (int c = 0; c < 3; c++) result[i + c] = result[i + 3] == 0 ? (byte)0 : (byte)Math.Min(255, (result[i + c] * 255 + result[i + 3] / 2) / result[i + 3]);
        return result;
    }
}
public sealed record ImageEncodingOptions(int Quality = 90, int Compression = 6, uint? Background = null, bool AllowMetadataLoss = false) {
    public void Validate() {
        if (Quality is < 1 or > 100 || Compression is < 0 or > 9) throw new ArgumentException("Quality must be 1..100; compression must be 0..9.");
    }
    public byte[] Prepare(PixelImage image, ImageFormat format) {
        if (!Enum.IsDefined(format)) throw new ArgumentException("Unknown image format.");
        Validate(); var pixels = image.StraightPixels();
        bool transparency = Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[i * 4 + 3] < 255);
        // BMP interoperability and GIF's binary alpha cannot preserve arbitrary alpha.
        bool losesAlpha = format is ImageFormat.Jpeg or ImageFormat.Bmp || format == ImageFormat.Gif &&
            Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[i * 4 + 3] is > 0 and < 255);
        if (transparency && losesAlpha) {
            if (Background is not uint color) throw new ApplicationFailure(FailureKind.InvalidData, "This conversion requires an explicit opaque RGB background.");
            for (int i = 0; i < pixels.Length; i += 4) { int alpha = pixels[i + 3];
                for (int c = 0; c < 3; c++) pixels[i + c] = (byte)((pixels[i + c] * alpha + ((color >> (16 - c * 8)) & 255) * (255 - alpha) + 127) / 255);
                pixels[i + 3] = 255;
            }
        }
        if (!AllowMetadataLoss && format is ImageFormat.Bmp or ImageFormat.Gif && image.Metadata.Orientation != 1)
            throw new ApplicationFailure(FailureKind.InvalidData, "Target format cannot preserve orientation; allow metadata loss explicitly.");
        if (!AllowMetadataLoss && format == ImageFormat.Gif && image.Metadata.DpiX.HasValue)
            throw new ApplicationFailure(FailureKind.InvalidData, "GIF cannot preserve physical resolution; allow metadata loss explicitly.");
        return pixels;
    }
}
public interface IImageCodec {
    IReadOnlyCollection<ImageFormat> Formats { get; }
    ValueTask<ImageInfo> Inspect(ReadOnlyMemory<byte> data, ApplicationLimits limits, CancellationToken cancellation = default);
    /// <summary>Frame/page is mandatory. Pixels retain stored orientation; metadata describes the transform.</summary>
    ValueTask<PixelImage> Decode(ReadOnlyMemory<byte> data, int frame, ApplicationLimits limits, CancellationToken cancellation = default);
    ValueTask<byte[]> Encode(PixelImage image, ImageFormat format, ImageEncodingOptions options, ApplicationLimits limits, CancellationToken cancellation = default);
}
public sealed class ImageCodecRegistry : IImageCodec {
    private readonly Dictionary<ImageFormat, IImageCodec> codecs = new();
    public IReadOnlyCollection<ImageFormat> Formats => codecs.Keys;
    public void Register(IImageCodec codec) {
        var formats = codec.Formats.ToArray();
        if (formats.Distinct().Count() != formats.Length) throw new ArgumentException("Provider declares duplicate formats.");
        foreach (var format in formats) if (codecs.ContainsKey(format)) throw new ArgumentException("Codec already registered: " + format);
        foreach (var format in formats) codecs.Add(format, codec);
    }
    private IImageCodec Get(ImageFormat format) => codecs.TryGetValue(format, out var codec) ? codec : throw new ApplicationFailure(FailureKind.Unsupported, "Codec unavailable: " + format);
    public static ImageFormat Identify(ReadOnlySpan<byte> data) {
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) return ImageFormat.Png;
        if (data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255) return ImageFormat.Jpeg;
        if (data.Length >= 2 && data[0] == 66 && data[1] == 77) return ImageFormat.Bmp;
        if (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) return ImageFormat.Gif;
        if (data.Length >= 4 && (data[..4].SequenceEqual(new byte[] {73,73,42,0}) || data[..4].SequenceEqual(new byte[] {77,77,0,42}))) return ImageFormat.Tiff;
        throw new ApplicationFailure(FailureKind.Unsupported, "Unknown or unsupported image signature.");
    }
    public ValueTask<ImageInfo> Inspect(ReadOnlyMemory<byte> data, ApplicationLimits limits, CancellationToken cancellation = default) { limits.Bytes(data.Length); return Get(Identify(data.Span)).Inspect(data, limits, cancellation); }
    public ValueTask<PixelImage> Decode(ReadOnlyMemory<byte> data, int frame, ApplicationLimits limits, CancellationToken cancellation = default) { limits.Bytes(data.Length); return Get(Identify(data.Span)).Decode(data, frame, limits, cancellation); }
    public ValueTask<byte[]> Encode(PixelImage image, ImageFormat format, ImageEncodingOptions options, ApplicationLimits limits, CancellationToken cancellation = default) => Get(format).Encode(image, format, options, limits, cancellation);
}
