using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AquariusLang.Application;

public enum FailureKind { Unsupported, PermissionDenied, Unavailable, Cancelled, InvalidData, LimitExceeded, NotFound, Conflict }
public sealed class ApplicationFailure : Exception {
    public FailureKind Kind { get; }
    public ApplicationFailure(FailureKind kind, string message, Exception? inner = null) : base(message, inner) { Kind = kind; }
}
public sealed record Capability(bool Available, string? Restriction = null);
public sealed record ApplicationLimits(int MaxBytes = 64 * 1024 * 1024, int MaxDimension = 8192,
    int MaxPixels = 16 * 1024 * 1024, int MaxFrames = 256, int MaxEntries = 4096, int MaxDepth = 64) {
    public void Bytes(long count) {
        if (count < 0 || count > MaxBytes) throw new ApplicationFailure(FailureKind.LimitExceeded, "Data exceeds the byte limit.");
    }
    public int Pixels(int width, int height, int frames = 1) {
        long pixels = (long)width * height * frames;
        if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension || frames < 1 || frames > MaxFrames || pixels > MaxPixels)
            throw new ApplicationFailure(FailureKind.LimitExceeded, "Image dimensions or frame allocation exceeds the limit.");
        Bytes(pixels * 4); return checked((int)pixels * 4);
    }
}

public enum ResourceKind { File, Directory, Asset }
/// <summary>Opaque provider identity. Id is not necessarily an OS path. A provider validates ownership.</summary>
public sealed record FileResource(string Provider, string Id, string Name, ResourceKind Kind = ResourceKind.File, string? PersistentIdentity = null);
public sealed record ResourceMetadata(bool Exists, ResourceKind Kind, long? Length, DateTimeOffset? Modified);
public sealed record FileCapabilities(bool Paths, bool Streams, bool Seek, bool Directories, bool AtomicReplace, bool Persistent);
public enum ResourceOpenMode { Read, Create, OpenWrite, Append }
public interface IFileProvider {
    string Id { get; }
    FileCapabilities Capabilities { get; }
    FileResource Resolve(string location);
    ValueTask<ResourceMetadata> Stat(FileResource resource, CancellationToken cancellation = default);
    ValueTask<Stream> Open(FileResource resource, ResourceOpenMode mode, CancellationToken cancellation = default);
    ValueTask<IReadOnlyList<FileResource>> Enumerate(FileResource directory, CancellationToken cancellation = default);
    ValueTask CreateDirectory(FileResource directory, CancellationToken cancellation = default);
    ValueTask Delete(FileResource resource, bool recursive, CancellationToken cancellation = default);
    ValueTask Copy(FileResource source, FileResource destination, bool overwrite, CancellationToken cancellation = default);
    ValueTask Move(FileResource source, FileResource destination, bool overwrite, CancellationToken cancellation = default);
    /// <summary>Commit complete output or throw. Return true only for an atomic replacement.</summary>
    ValueTask<bool> Replace(FileResource resource, ReadOnlyMemory<byte> bytes, bool requireAtomic, CancellationToken cancellation = default);
    ValueTask<ITemporaryResource> Temporary(bool directory, CancellationToken cancellation = default);
}
public interface ITemporaryResource : IAsyncDisposable { FileResource Resource { get; } }
public interface IApplicationStorage {
    FileResource SettingsDirectory { get; }
    FileResource DataDirectory { get; }
    ValueTask<byte[]?> Read(string key, CancellationToken cancellation = default);
    ValueTask Write(string key, ReadOnlyMemory<byte> bytes, CancellationToken cancellation = default);
    ValueTask Delete(string key, CancellationToken cancellation = default);
}
public interface IApplicationHost {
    IFileProvider Files { get; }
    IImageCodec Images { get; }
    IClipboard Clipboard { get; }
    IApplicationStorage Storage { get; }
    IFontService Fonts { get; }
    IHostServices Services { get; }
    IReadOnlyDictionary<string, Capability> Capabilities { get; }
}
