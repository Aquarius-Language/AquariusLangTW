using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AquariusLang.Application;

/// <summary>JSON values: null, Boolean, finite numbers, strings, arrays and string-keyed objects. No type activation.</summary>
public static class DocumentSerialization {
    public static byte[] Json(object? value, ApplicationLimits? limits = null) {
        limits ??= new();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { MaxDepth = limits.MaxDepth });
        limits.Bytes(bytes.Length); return bytes;
    }
    public static JsonElement Parse(ReadOnlyMemory<byte> data, ApplicationLimits? limits = null) {
        limits ??= new(); limits.Bytes(data.Length);
        using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = limits.MaxDepth });
        ValidateNumbers(document.RootElement);
        return document.RootElement.Clone();
    }
    private static void ValidateNumbers(JsonElement value) {
        if (value.ValueKind == JsonValueKind.Number && !double.IsFinite(value.GetDouble())) throw new ApplicationFailure(FailureKind.InvalidData, "JSON numbers must be finite.");
        if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) ValidateNumbers(child);
        if (value.ValueKind == JsonValueKind.Object) foreach (var child in value.EnumerateObject()) ValidateNumbers(child.Value);
    }
    // AQD1 + uint32 schema + uint32 payload length + CRC32 + strict UTF-8 JSON.
    public static byte[] Binary(object? value, uint schema, ApplicationLimits? limits = null) {
        if (schema == 0) throw new ArgumentException("Schema version starts at 1.");
        var payload = Json(value, limits); (limits ?? new()).Bytes((long)payload.Length + 16);
        var output = new byte[payload.Length + 16]; "AQD1"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), schema);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), Crc32(payload)); payload.CopyTo(output, 16); return output;
    }
    public static (uint Schema, JsonElement Value) ReadBinary(ReadOnlyMemory<byte> data, ApplicationLimits? limits = null) {
        (limits ?? new()).Bytes(data.Length); var bytes = data.Span;
        if (bytes.Length < 16 || !bytes[..4].SequenceEqual("AQD1"u8)) throw new ApplicationFailure(FailureKind.InvalidData, "Invalid document header or version.");
        uint schema = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (schema == 0 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != bytes.Length - 16 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != Crc32(bytes[16..]))
            throw new ApplicationFailure(FailureKind.InvalidData, "Invalid document length, schema or checksum.");
        return (schema, Parse(data[16..], limits));
    }
    public static uint Crc32(ReadOnlySpan<byte> bytes) {
        uint crc = uint.MaxValue;
        foreach (byte b in bytes) { crc ^= b; for (int bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) != 0 ? 0xEDB88320u : 0); }
        return ~crc;
    }
    public static uint Adler32(ReadOnlySpan<byte> bytes) { uint a = 1, b = 0; foreach (byte value in bytes) { a = (a + value) % 65521; b = (b + a) % 65521; } return b << 16 | a; }
}
public enum CompressionFormat { Gzip, Zlib, Deflate, Brotli }
public static class DocumentCompression {
    private static Stream Codec(Stream stream, CompressionFormat format, CompressionMode mode, CompressionLevel level = CompressionLevel.Optimal) => (format, mode) switch {
        (CompressionFormat.Gzip, CompressionMode.Compress) => new GZipStream(stream, level, true),
        (CompressionFormat.Zlib, CompressionMode.Compress) => new ZLibStream(stream, level, true),
        (CompressionFormat.Deflate, CompressionMode.Compress) => new DeflateStream(stream, level, true),
        (CompressionFormat.Brotli, CompressionMode.Compress) => new BrotliStream(stream, level, true),
        (CompressionFormat.Gzip, _) => new GZipStream(stream, mode, true), (CompressionFormat.Zlib, _) => new ZLibStream(stream, mode, true),
        (CompressionFormat.Deflate, _) => new DeflateStream(stream, mode, true), (CompressionFormat.Brotli, _) => new BrotliStream(stream, mode, true),
        _ => throw new ArgumentException("Unknown compression format.")
    };
    public static byte[] Compress(ReadOnlyMemory<byte> bytes, CompressionFormat format, CompressionLevel level = CompressionLevel.Optimal, ApplicationLimits? limits = null) {
        limits ??= new(); limits.Bytes(bytes.Length); using var output = new MemoryStream();
        using (var codec = Codec(output, format, CompressionMode.Compress, level)) codec.Write(bytes.Span);
        limits.Bytes(output.Length); return output.ToArray();
    }
    public static async ValueTask<byte[]> Decompress(ReadOnlyMemory<byte> bytes, CompressionFormat format, ApplicationLimits? limits = null, CancellationToken cancellation = default) {
        limits ??= new(); limits.Bytes(bytes.Length); using var input = new MemoryStream(bytes.ToArray(), false);
        await using var codec = Codec(input, format, CompressionMode.Decompress); var output = await FileService.ReadBounded(codec, limits.MaxBytes, cancellation).ConfigureAwait(false);
        ValidateContainer(bytes.Span, output, format); return output;
    }
    private static void ValidateContainer(ReadOnlySpan<byte> input, ReadOnlySpan<byte> output, CompressionFormat format) {
        bool invalid = format switch {
            CompressionFormat.Gzip => input.Length < 18 || input[0] != 31 || input[1] != 139 || input[2] != 8 || (input[3] & 0xe0) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(input[^8..]) != DocumentSerialization.Crc32(output) || BinaryPrimitives.ReadUInt32LittleEndian(input[^4..]) != output.Length,
            CompressionFormat.Zlib => input.Length < 6 || (input[0] & 15) != 8 || input[0] >> 4 > 7 || ((input[0] << 8) | input[1]) % 31 != 0 || (input[1] & 32) != 0 || BinaryPrimitives.ReadUInt32BigEndian(input[^4..]) != DocumentSerialization.Adler32(output),
            _ => false
        };
        if (invalid) throw new ApplicationFailure(FailureKind.InvalidData, "Invalid compressed container checksum, header or length (one gzip member is supported).");
    }
}
public static class DocumentArchive {
    public static string ValidateName(string name) {
        if (string.IsNullOrEmpty(name) || name.Contains('\\') || name.Contains(':') || name.Contains('\0') || name.StartsWith('/') ||
            name.Split('/').Any(p => p is "" or "." or "..")) throw new ApplicationFailure(FailureKind.InvalidData, "Invalid archive resource name.");
        return name;
    }
    public static byte[] Create(IReadOnlyDictionary<string, byte[]> entries, ApplicationLimits? limits = null) {
        limits ??= new(); if (entries.Count > limits.MaxEntries) throw new ApplicationFailure(FailureKind.LimitExceeded, "Too many archive entries.");
        limits.Bytes(entries.Sum(x => (long)x.Value.Length)); using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var pair in entries) {
            using var target = archive.CreateEntry(ValidateName(pair.Key), CompressionLevel.Optimal).Open(); target.Write(pair.Value);
        }
        limits.Bytes(output.Length); return output.ToArray();
    }
    public static async ValueTask<IReadOnlyDictionary<string, byte[]>> Read(ReadOnlyMemory<byte> bytes, ApplicationLimits? limits = null, CancellationToken cancellation = default) {
        limits ??= new(); limits.Bytes(bytes.Length); using var input = new MemoryStream(bytes.ToArray(), false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read); var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (archive.Entries.Count > limits.MaxEntries) throw new ApplicationFailure(FailureKind.LimitExceeded, "Too many archive entries.");
        long total = 0;
        foreach (var entry in archive.Entries) {
            cancellation.ThrowIfCancellationRequested(); string name = ValidateName(entry.FullName); total += entry.Length; limits.Bytes(total);
            if (result.ContainsKey(name)) throw new ApplicationFailure(FailureKind.InvalidData, "Duplicate archive resource.");
            await using var stream = entry.Open(); var data = await FileService.ReadBounded(stream, (int)entry.Length, cancellation).ConfigureAwait(false);
            if (data.Length != entry.Length || DocumentSerialization.Crc32(data) != entry.Crc32) throw new ApplicationFailure(FailureKind.InvalidData, "Invalid archive checksum or size.");
            result.Add(name, data);
        }
        return result;
    }
}
