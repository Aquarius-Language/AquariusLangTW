using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AquariusLang.Application;

/// <summary>The existing RGBA PNG writer, shared with application codecs; baseline RGBA TIFF preserves straight alpha.</summary>
public static class RgbaImageEncoding {
    private static CompressionLevel Level(int compression) => compression == 0 ? CompressionLevel.NoCompression : compression <= 2 ? CompressionLevel.Fastest : compression == 9 ? CompressionLevel.SmallestSize : CompressionLevel.Optimal;
    public static byte[] Png(PixelImage image, ImageEncodingOptions? options = null) {
        options ??= new(); var pixels = options.Prepare(image, ImageFormat.Png); using var output = new MemoryStream(); output.Write(new byte[] {137,80,78,71,13,10,26,10});
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, image.Width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height); header[8] = 8; header[9] = 6; Chunk(output,"IHDR",header);
        if (image.Metadata.DpiX is double x) { var resolution = new byte[9]; BinaryPrimitives.WriteUInt32BigEndian(resolution, checked((uint)Math.Round(x / .0254))); BinaryPrimitives.WriteUInt32BigEndian(resolution.AsSpan(4), checked((uint)Math.Round((image.Metadata.DpiY ?? x) / .0254))); resolution[8] = 1; Chunk(output,"pHYs",resolution); }
        if (image.Metadata.Orientation != 1) { using var exif = new MemoryStream(); using var writer = new BinaryWriter(exif, Encoding.UTF8, true); writer.Write((ushort)0x4949); writer.Write((ushort)42); writer.Write(8u); writer.Write((ushort)1); Entry(writer,274,3,1,(uint)image.Metadata.Orientation); writer.Write(0u); Chunk(output,"eXIf",exif.ToArray()); }
        using var compressed = new MemoryStream(); using (var z = new ZLibStream(compressed, Level(options.Compression), true)) for (int y = 0; y < image.Height; y++) { z.WriteByte(0); z.Write(pixels, y * image.Width * 4, image.Width * 4); }
        Chunk(output,"IDAT",compressed.ToArray()); Chunk(output,"IEND",Array.Empty<byte>()); return output.ToArray();
    }
    private static void Chunk(Stream output, string name, byte[] bytes) {
        Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length); output.Write(size); var type = Encoding.ASCII.GetBytes(name); output.Write(type); output.Write(bytes);
        var content = new byte[4 + bytes.Length]; type.CopyTo(content, 0); bytes.CopyTo(content, 4); BinaryPrimitives.WriteUInt32BigEndian(size, DocumentSerialization.Crc32(content)); output.Write(size);
    }
    public static byte[] Tiff(PixelImage image, ImageEncodingOptions? options = null) {
        options ??= new(); var pixels = options.Prepare(image, ImageFormat.Tiff); byte[] encoded;
        if (options.Compression == 0) encoded = pixels;
        else { using var compressed = new MemoryStream(); using (var codec = new ZLibStream(compressed, Level(options.Compression), true)) codec.Write(pixels); encoded = compressed.ToArray(); }
        const ushort count = 15; const uint extra = 8 + 2 + count * 12 + 4, pixelOffset = extra + 24;
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write((ushort)0x4949); writer.Write((ushort)42); writer.Write(8u); writer.Write(count);
        Entry(writer,256,4,1,(uint)image.Width); Entry(writer,257,4,1,(uint)image.Height); Entry(writer,258,3,4,extra);
        Entry(writer,259,3,1,options.Compression == 0 ? 1u : 8u); Entry(writer,262,3,1,2); Entry(writer,273,4,1,pixelOffset); Entry(writer,274,3,1,(uint)image.Metadata.Orientation);
        Entry(writer,277,3,1,4); Entry(writer,278,4,1,(uint)image.Height); Entry(writer,279,4,1,(uint)encoded.Length);
        Entry(writer,282,5,1,extra + 8); Entry(writer,283,5,1,extra + 16); Entry(writer,284,3,1,1); Entry(writer,296,3,1,2); Entry(writer,338,3,1,2); writer.Write(0u);
        for (int i = 0; i < 4; i++) writer.Write((ushort)8);
        writer.Write(checked((uint)Math.Round((image.Metadata.DpiX ?? 72) * 1000))); writer.Write(1000u); writer.Write(checked((uint)Math.Round((image.Metadata.DpiY ?? image.Metadata.DpiX ?? 72) * 1000))); writer.Write(1000u);
        writer.Write(encoded); return output.ToArray();
    }
    private static void Entry(BinaryWriter writer, ushort tag, ushort type, uint count, uint value) { writer.Write(tag); writer.Write(type); writer.Write(count); writer.Write(value); }
}
