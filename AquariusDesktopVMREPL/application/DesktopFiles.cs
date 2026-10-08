using AquariusLang.Application;

namespace AquariusREPL.Application;

public sealed class DesktopFiles : IFileProvider {
    public string Id { get; } = "desktop-" + Guid.NewGuid().ToString("N");
    public FileCapabilities Capabilities { get; } = new(true, true, true, true, true, true);
    public FileResource Resolve(string location) {
        var path = Path.GetFullPath(location); return new(Id, path, Path.GetFileName(path), Directory.Exists(path) ? ResourceKind.Directory : ResourceKind.File, "desktop:" + (OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path));
    }
    private string PathOf(FileResource resource) {
        if (resource.Provider != Id || resource.Kind == ResourceKind.Asset) throw new ArgumentException("Resource belongs to another provider or is a packaged asset.");
        return Path.GetFullPath(resource.Id);
    }
    public ValueTask<ResourceMetadata> Stat(FileResource resource, CancellationToken cancellation = default) {
        cancellation.ThrowIfCancellationRequested(); var path = PathOf(resource);
        if (Directory.Exists(path)) return ValueTask.FromResult(new ResourceMetadata(true, ResourceKind.Directory, null, Directory.GetLastWriteTimeUtc(path)));
        var info = new FileInfo(path); return ValueTask.FromResult(new ResourceMetadata(info.Exists, ResourceKind.File, info.Exists ? info.Length : null, info.Exists ? info.LastWriteTimeUtc : null));
    }
    public ValueTask<Stream> Open(FileResource resource, ResourceOpenMode mode, CancellationToken cancellation = default) {
        cancellation.ThrowIfCancellationRequested(); string path = PathOf(resource);
        var fileMode = mode switch { ResourceOpenMode.Read => FileMode.Open, ResourceOpenMode.Create => FileMode.Create, ResourceOpenMode.OpenWrite => FileMode.Open, ResourceOpenMode.Append => FileMode.Append, _ => throw new ArgumentException("Invalid open mode.") };
        return ValueTask.FromResult<Stream>(new FileStream(path, fileMode, mode == ResourceOpenMode.Read ? FileAccess.Read : FileAccess.Write, FileShare.Read, 8192, FileOptions.Asynchronous));
    }
    public ValueTask<IReadOnlyList<FileResource>> Enumerate(FileResource directory, CancellationToken cancellation = default) {
        var result = new List<FileResource>(); foreach (var path in Directory.EnumerateFileSystemEntries(PathOf(directory))) {
            cancellation.ThrowIfCancellationRequested(); if (result.Count >= 4096) throw new ApplicationFailure(FailureKind.LimitExceeded, "Directory exceeds 4096 entries."); result.Add(Resolve(path));
        }
        return ValueTask.FromResult<IReadOnlyList<FileResource>>(result);
    }
    public ValueTask CreateDirectory(FileResource directory, CancellationToken cancellation = default) { cancellation.ThrowIfCancellationRequested(); Directory.CreateDirectory(PathOf(directory)); return ValueTask.CompletedTask; }
    public ValueTask Delete(FileResource resource, bool recursive, CancellationToken cancellation = default) {
        cancellation.ThrowIfCancellationRequested(); string path = PathOf(resource); if (Directory.Exists(path)) Directory.Delete(path, recursive); else File.Delete(path); return ValueTask.CompletedTask;
    }
    public async ValueTask Copy(FileResource source, FileResource destination, bool overwrite, CancellationToken cancellation = default) {
        string from = PathOf(source), to = PathOf(destination); cancellation.ThrowIfCancellationRequested();
        if (Directory.Exists(from)) {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (to.Equals(from, comparison) || to.StartsWith(Path.TrimEndingDirectorySeparator(from) + Path.DirectorySeparatorChar, comparison)) throw new ArgumentException("Directory cannot be copied into itself.");
            if (Directory.Exists(to) || File.Exists(to)) throw new ApplicationFailure(FailureKind.Conflict, "Directory copy requires a new destination.");
            string stageDirectory = to + ".aquarius-" + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                Directory.CreateDirectory(stageDirectory); int entries = 0;
                async Task CopyDirectory(string input, string output) {
                    foreach (var path in Directory.EnumerateFileSystemEntries(input)) {
                        cancellation.ThrowIfCancellationRequested(); if (++entries > 4096) throw new ApplicationFailure(FailureKind.LimitExceeded, "Directory copy exceeds 4096 entries.");
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ApplicationFailure(FailureKind.Unsupported, "Recursive copying does not follow symbolic links.");
                        string target = Path.Combine(output, Path.GetFileName(path));
                        if (Directory.Exists(path)) { Directory.CreateDirectory(target); await CopyDirectory(path, target).ConfigureAwait(false); }
                        else await Copy(Resolve(path), Resolve(target), false, cancellation).ConfigureAwait(false);
                    }
                }
                await CopyDirectory(from, stageDirectory).ConfigureAwait(false); cancellation.ThrowIfCancellationRequested(); Directory.Move(stageDirectory, to);
            } finally { if (Directory.Exists(stageDirectory)) Directory.Delete(stageDirectory, true); }
            return;
        }
        await using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        // Staging avoids leaving a truncated destination after cancellation.
        string stage = to + ".aquarius-" + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true)) { await input.CopyToAsync(output, cancellation).ConfigureAwait(false); await output.FlushAsync(cancellation).ConfigureAwait(false); output.Flush(true); }
            cancellation.ThrowIfCancellationRequested(); File.Move(stage, to, overwrite);
        } finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    public ValueTask Move(FileResource source, FileResource destination, bool overwrite, CancellationToken cancellation = default) {
        cancellation.ThrowIfCancellationRequested(); string from = PathOf(source), to = PathOf(destination);
        if (Directory.Exists(from)) { if (overwrite) throw new ApplicationFailure(FailureKind.Unsupported, "Directory replacement is not supported."); Directory.Move(from, to); }
        else File.Move(from, to, overwrite); return ValueTask.CompletedTask;
    }
    public async ValueTask<bool> Replace(FileResource resource, ReadOnlyMemory<byte> bytes, bool requireAtomic, CancellationToken cancellation = default) {
        string path = PathOf(resource), stage = path + ".aquarius-" + Guid.NewGuid().ToString("N") + ".tmp";
        cancellation.ThrowIfCancellationRequested();
        try {
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true)) { await output.WriteAsync(bytes, cancellation).ConfigureAwait(false); await output.FlushAsync(cancellation).ConfigureAwait(false); output.Flush(true); }
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(stage, path, null); else File.Move(stage, path);
            return true;
        } finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    public ValueTask<ITemporaryResource> Temporary(bool directory, CancellationToken cancellation = default) {
        cancellation.ThrowIfCancellationRequested(); string path = Path.Combine(Path.GetTempPath(), "aquarius-" + Guid.NewGuid().ToString("N"));
        if (directory) Directory.CreateDirectory(path); else using (File.Open(path, FileMode.CreateNew)) { }
        return ValueTask.FromResult<ITemporaryResource>(new TemporaryLease(Resolve(path), directory));
    }
    private sealed class TemporaryLease(FileResource resource, bool directory) : ITemporaryResource {
        private bool disposed;
        public FileResource Resource => !disposed ? resource : throw new ObjectDisposedException(nameof(TemporaryLease));
        public ValueTask DisposeAsync() { if (disposed) return ValueTask.CompletedTask; if (directory) { if (Directory.Exists(resource.Id)) Directory.Delete(resource.Id, true); } else File.Delete(resource.Id); disposed = true; return ValueTask.CompletedTask; }
    }
}
public sealed class DesktopStorage : IApplicationStorage {
    private readonly DesktopFiles files;
    public FileResource SettingsDirectory { get; }
    public FileResource DataDirectory { get; }
    public DesktopStorage(DesktopFiles files, string applicationId = "AquariusLang", string? root = null) {
        Preferences.ValidateKey(applicationId); this.files = files;
        string settings = root ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), applicationId);
        string data = root == null ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), applicationId) : Path.Combine(root, "data");
        SettingsDirectory = new(files.Id, settings, "settings", ResourceKind.Directory); DataDirectory = new(files.Id, data, "data", ResourceKind.Directory);
    }
    private FileResource Resource(string key) => files.Resolve(Path.Combine(SettingsDirectory.Id, Preferences.ValidateKey(key)));
    public async ValueTask<byte[]?> Read(string key, CancellationToken cancellation = default) => (await files.Stat(Resource(key), cancellation).ConfigureAwait(false)).Exists ? await new FileService(files).Read(Resource(key), cancellation).ConfigureAwait(false) : null;
    public async ValueTask Write(string key, ReadOnlyMemory<byte> bytes, CancellationToken cancellation = default) { new ApplicationLimits().Bytes(bytes.Length); await files.CreateDirectory(SettingsDirectory, cancellation).ConfigureAwait(false); await files.Replace(Resource(key), bytes, true, cancellation).ConfigureAwait(false); }
    public ValueTask Delete(string key, CancellationToken cancellation = default) => files.Delete(Resource(key), false, cancellation);
}
