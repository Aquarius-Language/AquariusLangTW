using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AquariusLang.Application;

public sealed class FileService {
    public IFileProvider Provider { get; }
    public ApplicationLimits Limits { get; }
    public FileService(IFileProvider provider, ApplicationLimits? limits = null) { Provider = provider; Limits = limits ?? new(); }
    public async ValueTask<byte[]> Read(FileResource resource, CancellationToken cancellation = default) {
        cancellation.ThrowIfCancellationRequested();
        var metadata = await Provider.Stat(resource, cancellation).ConfigureAwait(false);
        if (metadata.Length is long size) Limits.Bytes(size);
        await using var stream = await Provider.Open(resource, ResourceOpenMode.Read, cancellation).ConfigureAwait(false);
        return await ReadBounded(stream, Limits.MaxBytes, cancellation).ConfigureAwait(false);
    }
    public async ValueTask<string> ReadText(FileResource resource, string encoding, CancellationToken cancellation = default) =>
        TextEncoding.Get(encoding).GetString(await Read(resource, cancellation).ConfigureAwait(false));
    public ValueTask<bool> Save(FileResource resource, ReadOnlyMemory<byte> bytes, bool requireAtomic = false, CancellationToken cancellation = default) {
        Limits.Bytes(bytes.Length); cancellation.ThrowIfCancellationRequested();
        return Provider.Replace(resource, bytes, requireAtomic, cancellation);
    }
    public ValueTask<bool> SaveText(FileResource resource, string text, string encoding, bool requireAtomic = false, CancellationToken cancellation = default) {
        var codec = TextEncoding.Get(encoding); Limits.Bytes(codec.GetByteCount(text));
        return Save(resource, codec.GetBytes(text), requireAtomic, cancellation);
    }
    public static async ValueTask<byte[]> ReadBounded(Stream input, int maximum, CancellationToken cancellation = default) {
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true) {
            int count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false); if (count == 0) break;
            if (output.Length + count > maximum) throw new ApplicationFailure(FailureKind.LimitExceeded, "Stream exceeds the byte limit.");
            output.Write(buffer, 0, count);
        }
        cancellation.ThrowIfCancellationRequested(); return output.ToArray();
    }
}
public static class TextEncoding {
    /// <summary>Strict, BOM-free encodings; invalid byte sequences and unpaired surrogates fail.</summary>
    public static Encoding Get(string name) => name.ToLowerInvariant() switch {
        "utf-8" => new UTF8Encoding(false, true), "utf-16le" => new UnicodeEncoding(false, false, true),
        "utf-16be" => new UnicodeEncoding(true, false, true),
        "ascii" => Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => throw new ApplicationFailure(FailureKind.Unsupported, "Encoding must be utf-8, utf-16le, utf-16be or ascii.")
    };
}
