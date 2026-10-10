using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AquariusLang.Application;

public sealed class Preferences {
    private readonly IApplicationStorage storage;
    private readonly uint schema;
    private readonly Dictionary<uint, Func<JsonElement, JsonElement>> migrations = new();
    public Preferences(IApplicationStorage storage, uint schema = 1) { if (schema == 0) throw new ArgumentException("Schema starts at 1."); this.storage = storage; this.schema = schema; }
    public void AddMigration(uint fromVersion, Func<JsonElement, JsonElement> migration) {
        if (fromVersion == 0 || fromVersion >= schema) throw new ArgumentException("Migration must advance an earlier version by one.");
        migrations.Add(fromVersion, migration);
    }
    public async ValueTask<JsonElement?> Load(string key, CancellationToken cancellation = default) {
        var bytes = await storage.Read(ValidateKey(key), cancellation).ConfigureAwait(false); if (bytes == null) return null;
        var (version, value) = DocumentSerialization.ReadBinary(bytes);
        if (version > schema) throw new ApplicationFailure(FailureKind.Unsupported, "Settings use a newer schema.");
        while (version < schema) {
            if (!migrations.TryGetValue(version, out var migrate)) throw new ApplicationFailure(FailureKind.Unsupported, "Missing settings migration.");
            value = migrate(value); version++;
        }
        return value;
    }
    public ValueTask Save(string key, object? value, CancellationToken cancellation = default) => storage.Write(ValidateKey(key), DocumentSerialization.Binary(value, schema), cancellation);
    public static string ValidateKey(string key) {
        if (key.Length is < 1 or > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Storage key must contain 1..128 ASCII letters, digits, dots, hyphens or underscores.");
        if (key is "." or "..") throw new ArgumentException("Invalid storage key."); return key;
    }
    public static IReadOnlyList<FileResource> Remember(IReadOnlyList<FileResource> recent, FileResource resource, int maximum = 20) {
        if (maximum is < 1 or > 1000) throw new ArgumentException("Recent resource limit must be 1..1000.");
        return new[] { resource }.Concat(recent.Where(r => resource.PersistentIdentity != null ? r.PersistentIdentity != resource.PersistentIdentity : r.Provider != resource.Provider || r.Id != resource.Id)).Take(maximum).ToArray();
    }
}
