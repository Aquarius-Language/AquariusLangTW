using System.Security.Cryptography;
using System.Text.Json;
using AquariusLang.Packaging;
using AquariusLang.VM;

namespace AquariusLang.Build;

/// <summary>Package a bottle with an installed runtime pack. No SDK, network, or source compilation is needed.</summary>
public sealed class SelfContainedExecutableTarget(string name, string runtimeIdentifier, string extension, string? runtimePacksDirectory = null) : IBottleBuildTarget {
    public string Name => name;
    public string ArtifactDescription => "Executable";
    private sealed record RuntimeManifest(string Format, int Version, string RuntimeIdentifier, int BundleVersion,
        int BottleVersion, int BytecodeVersion, string TemplateSha256);

    public void Build(BottleBuildRequest request) {
        string input = Path.GetFullPath(request.Input), output = Path.GetFullPath(request.Output);
        if (!input.EndsWith(".bottle", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Executable builds require one .bottle input.");
        if (!output.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{Name} output must name an {extension} file.");
        if (input.Equals(output, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must differ from the input bottle.");
        if (Directory.Exists(output) || (File.Exists(output) && (File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0))
            throw new ArgumentException("Executable output must be a regular file, not a directory or link.");
        // Read and validate the input before looking for a pack or touching output.
        using var bottle = File.OpenRead(input);
        var package = BottlePackage.Load(bottle);
        package.ResolveScript(request.EntryPoint ?? package.EntryPoint);
        string root = runtimePacksDirectory ?? System.Environment.GetEnvironmentVariable("AQUARIUS_BUILD_TARGETS") ?? Path.Combine(AppContext.BaseDirectory, "build-targets");
        string pack = Path.Combine(root, runtimeIdentifier);
        string manifestPath = Path.Combine(pack, "runtime.json"), templatePath = Path.Combine(pack, "host" + extension);
        if (!File.Exists(manifestPath) || !File.Exists(templatePath))
            throw new IOException($"Runtime pack {runtimeIdentifier} is missing from {Path.GetFullPath(pack)}. Install the complete aqua distribution, or run scripts/publish-apphost.ps1 when building from source.");
        RuntimeManifest? manifest;
        if (new FileInfo(manifestPath).Length > 64 * 1024) throw new InvalidDataException("Executable runtime manifest exceeds the size limit.");
        try { manifest = JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException error) { throw new InvalidDataException("Invalid executable runtime pack manifest.", error); }
        if (manifest == null || manifest.Format != "aquarius-runtime-pack" || manifest.Version != 1 ||
            manifest.RuntimeIdentifier != runtimeIdentifier || manifest.BundleVersion != ExecutableBundle.FormatVersion ||
            manifest.BottleVersion != BottlePackage.FormatVersion || manifest.BytecodeVersion != BytecodeSerializer.FormatVersion)
            throw new InvalidDataException("Incompatible executable runtime pack. Rebuild or reinstall the pack with this compiler version.");
        using var template = File.OpenRead(templatePath);
        string checksum = Convert.ToHexString(SHA256.HashData(template));
        if (!checksum.Equals(manifest.TemplateSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Executable runtime template checksum mismatch. Reinstall the runtime pack.");
        if (output.Equals(Path.GetFullPath(templatePath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must not overwrite the executable runtime template.");
        string parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try {
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write)) {
                ExecutableBundle.Write(template, bottle, stream, request.EntryPoint);
                stream.Flush(flushToDisk: true);
            }
            File.Move(stage, output, overwrite: true);
        } finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
