using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace AquariusLang.Packaging;

/// <summary>A platform-neutral executable overlay: host, bottle, entry metadata, and a versioned footer.</summary>
public static class ExecutableBundle {
    public const int FormatVersion = 1;
    public const int FooterSize = 64;
    public const long MaxBottleBytes = BottlePackage.MaxPackageBytes + 16 * 1024 * 1024;
    private const int MaxMetadataBytes = 64 * 1024;
    private static ReadOnlySpan<byte> Magic => "AQUARIUS-APP-V1!"u8;
    private sealed record Metadata(string EntryPoint);
    public sealed record Program(BottlePackage Package, string EntryPoint);

    public static bool HasProgram(Stream executable) {
        if (executable.Length < FooterSize) return false;
        Span<byte> magic = stackalloc byte[16];
        executable.Position = executable.Length - 16; executable.ReadExactly(magic);
        return magic.SequenceEqual(Magic);
    }

    /// <summary>Validate without executing, then append a bottle to an unmodified runtime template.</summary>
    public static void Write(Stream template, Stream bottle, Stream output, string? entryPoint = null) {
        if (bottle.Length > MaxBottleBytes) throw new InvalidDataException("Executable bottle exceeds the size limit.");
        bottle.Position = 0;
        var package = BottlePackage.Load(bottle);
        string entry = package.ResolveScript(entryPoint ?? package.EntryPoint);
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new Metadata(entry));
        if (metadata.Length > MaxMetadataBytes) throw new InvalidDataException("Executable metadata exceeds the size limit.");
        template.Position = 0;
        template.CopyTo(output);
        if (output.Position == 0) throw new InvalidDataException("The runtime template is empty.");
        bottle.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        CopyAndHash(bottle, output, hash);
        output.Write(metadata); hash.AppendData(metadata);
        Span<byte> footer = stackalloc byte[FooterSize];
        BinaryPrimitives.WriteInt32LittleEndian(footer, FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(footer[4..], metadata.Length);
        BinaryPrimitives.WriteInt64LittleEndian(footer[8..], bottle.Length);
        hash.GetHashAndReset().CopyTo(footer[16..48]);
        Magic.CopyTo(footer[48..]);
        output.Write(footer);
    }

    /// <summary>Verify and load the overlay without extracting source files or executing code.</summary>
    public static Program Load(Stream executable) {
        if (executable.Length < FooterSize) throw new InvalidDataException("Missing Aquarius executable footer.");
        Span<byte> footer = stackalloc byte[FooterSize];
        executable.Position = executable.Length - FooterSize; executable.ReadExactly(footer);
        if (!footer[48..].SequenceEqual(Magic)) throw new InvalidDataException("Missing Aquarius executable footer.");
        if (BinaryPrimitives.ReadInt32LittleEndian(footer) != FormatVersion)
            throw new InvalidDataException("Unsupported Aquarius executable version.");
        int metadataLength = BinaryPrimitives.ReadInt32LittleEndian(footer[4..]);
        long bottleLength = BinaryPrimitives.ReadInt64LittleEndian(footer[8..]);
        if (metadataLength <= 0 || metadataLength > MaxMetadataBytes || bottleLength <= 0 || bottleLength > MaxBottleBytes ||
            bottleLength + metadataLength + FooterSize >= executable.Length)
            throw new InvalidDataException("Invalid Aquarius executable payload size.");
        long start = executable.Length - FooterSize - metadataLength - bottleLength;
        using var payload = new SliceStream(executable, start, bottleLength + metadataLength);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        CopyAndHash(payload, Stream.Null, hash);
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), footer[16..48]))
            throw new InvalidDataException("Aquarius executable payload checksum mismatch.");
        executable.Position = start + bottleLength;
        byte[] bytes = new byte[metadataLength]; executable.ReadExactly(bytes);
        Metadata? metadata;
        try { metadata = JsonSerializer.Deserialize<Metadata>(bytes); }
        catch (JsonException error) { throw new InvalidDataException("Invalid executable entry metadata.", error); }
        if (metadata?.EntryPoint == null) throw new InvalidDataException("Missing executable entry point.");
        using var bottle = new SliceStream(executable, start, bottleLength);
        var package = BottlePackage.Load(bottle);
        return new Program(package, package.ResolveScript(metadata.EntryPoint));
    }

    private static void CopyAndHash(Stream input, Stream output, IncrementalHash hash) {
        byte[] buffer = new byte[81920];
        int count;
        while ((count = input.Read(buffer)) != 0) { hash.AppendData(buffer, 0, count); output.Write(buffer, 0, count); }
    }

    // ZIP offsets are relative to the bottle, never to the executable. The view also bounds all reads.
    private sealed class SliceStream(Stream source, long start, long length) : Stream {
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) {
            source.Position = start + position;
            int read = source.Read(buffer[..(int)Math.Min(buffer.Length, length - position)]);
            position += read; return read;
        }
        public override long Seek(long offset, SeekOrigin origin) {
            long basis = origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
            long value = checked(basis + offset);
            if (value < 0 || value > length) throw new IOException("Executable payload seek is out of bounds.");
            return position = value;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
