using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AquariusLang.Hosting;

/// <summary>Engine-neutral Wasm transport and library code shared by JavaScript-capable native hosts.</summary>
public static class HostScriptAssets {
    public static IReadOnlyDictionary<string, byte[]> Read() {
        const string prefix = "AquariusLang.Hosting.";
        var assembly = typeof(HostScriptAssets).Assembly;
        return assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(n => n[prefix.Length..], n => {
            using var source = assembly.GetManifestResourceStream(n)!;
            using var buffer = new MemoryStream(); source.CopyTo(buffer); return buffer.ToArray();
        });
    }
}
