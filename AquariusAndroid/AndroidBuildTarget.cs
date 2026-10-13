using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AquariusLang.Build;
using AquariusLang.Packaging;
using AquariusLang.runtime;
using AquariusLang.Hosting;
using AquariusLang.External;
using AquariusLang.Graphics;
using AquariusLang.Wasm;

namespace AquariusLang.Android;

/// <summary>Native Android deployment, independent of desktop and browser hosts.</summary>
public sealed class AndroidBuildTarget : IWasmBuildTarget
{
    public string Name => "android";
    public string ArtifactDescription => "Android application";
    public IReadOnlyCollection<string> OptionNames => new[] { "--application-id", "--app-name" };
    public void Build(WasmBuildRequest request)
    {
        var package = WasmApplication.Load(request.Input);
        string entry = package.ResolveScript(request.EntryPoint ?? package.EntryPoint);
        string output = Path.GetFullPath(request.Output);
        string id = request.Options?.GetValueOrDefault("--application-id") ?? "org.aquariuslang.app.p" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output + "\n" + entry)))[..16].ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"\A[a-zA-Z][a-zA-Z0-9_]*(\.[a-zA-Z][a-zA-Z0-9_]*)+\z")) throw new ArgumentException("Invalid Android application ID.");
        string label = request.Options?.GetValueOrDefault("--app-name") ?? Path.GetFileNameWithoutExtension(entry);
        if (string.IsNullOrWhiteSpace(label) || label.Length > 128) throw new ArgumentException("Android app name must be 1..128 characters.");
        if (output.Equals(Path.GetFullPath(request.Input), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Android output must differ from the input.");
        if (!output.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) { Generate(package, entry, output, id, label); return; }
        string parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        string project = Path.Combine(parent, ".aquarius-android-" + Guid.NewGuid().ToString("N"));
        try
        {
            Generate(package, entry, project, id, label);
            AndroidToolchain.Build(project);
            string apk = Path.Combine(project, "app/build/outputs/apk/debug/app-debug.apk");
            if (!File.Exists(apk)) throw new IOException("Gradle completed without producing an APK.");
            File.Move(apk, output, overwrite: true);
        }
        finally { if (Directory.Exists(project)) Directory.Delete(project, true); }
    }

    private static void Generate(WasmApplication package, string entry, string output, string id, string label)
    {
        output = Path.TrimEndingDirectorySeparator(output);
        string parent = Path.GetDirectoryName(output) ?? throw new ArgumentException("Select a named project directory.");
        if (File.Exists(output)) throw new ArgumentException("Android project output must be a directory.");
        // Generated projects are editable. Never silently replace the user's Java/Gradle changes.
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new ArgumentException("Android project output must be empty. Select a new directory to preserve project edits.");
        if (Directory.Exists(output) && (File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Android project output must not be a symbolic link.");
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, ".aquarius-android-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stage);
            var assembly = typeof(AndroidBuildTarget).Assembly;
            const string prefix = "AquariusAndroid.Template.";
            foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix)))
            {
                string relative = resource[prefix.Length..].Replace('\\', '/');
                string target = Path.Combine(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = assembly.GetManifestResourceStream(resource)!;
                using var file = File.Create(target); input.CopyTo(file);
            }
            string assets = Path.Combine(stage, "app/src/main/assets/aquarius");
            Directory.CreateDirectory(assets);
            foreach (var script in HostScriptAssets.Read()) File.WriteAllBytes(Path.Combine(assets, script.Key), script.Value);
            foreach (var library in ExternalLibraries.All)
                foreach (string name in library.Assets)
                    // Native ONNX supplies the engine; retain its notices without shipping browser engines.
                    if (library != ExternalLibraries.Onnx || name.EndsWith(".txt", StringComparison.Ordinal))
                        File.WriteAllBytes(Path.Combine(assets, library.DeploymentName(name)), ExternalLibraries.ReadAsset(library, name));
            File.WriteAllBytes(Path.Combine(assets, "program.wasm"), package.Program.WithExternalAssets().Bytes.ToArray());
            var assetFiles = new Dictionary<string, string>();
            foreach (var asset in package.Assets)
            {
                string name = "asset-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(asset.Key))).ToLowerInvariant() + ".bin";
                assetFiles.Add(asset.Key, name);
                File.WriteAllBytes(Path.Combine(assets, name), asset.Value);
            }
            File.WriteAllText(Path.Combine(assets, "program.json"), JsonSerializer.Serialize(new
            {
                version = WasmAbi.Version,
                entry,
                modules = package.Program.Metadata.Modules,
                assets = assetFiles,
                portableLibraries = PortableGraphicsLibraries.Modules,
                graphics = new { header = WgpuShaderAbi.Header, vertex = WgpuShaderAbi.Vertex, fragment = WgpuShaderAbi.Fragment, uniformBytes = WgpuShaderAbi.UniformBytes },
                physics = new { maxBodies = 4096 },
                catalog = LibraryCatalog.Functions.Concat(LibraryCatalog.GlobalFunctions).Select(f => new { library = f.Library, english = f.EnglishName, chinese = f.TraditionalChineseName, min = f.MinimumArguments, max = f.MaximumArguments })
            }), new UTF8Encoding(false));
            string gradle = Path.Combine(stage, "app/build.gradle");
            File.WriteAllText(gradle, File.ReadAllText(gradle).Replace("@APPLICATION_ID@", id), new UTF8Encoding(false));
            string manifest = Path.Combine(stage, "app/src/main/AndroidManifest.xml");
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("@APP_LABEL@", System.Security.SecurityElement.Escape(label)), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(stage, ".aquarius-android-output"), "Aquarius native Android project\n");
            if (Directory.Exists(output)) Directory.Delete(output);
            MoveDirectory(stage, output);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    private static void MoveDirectory(string source, string destination)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Move(source, destination); return; }
            catch (Exception e) when (attempt < 7 && OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException &&
                (e.HResult & 0xffff) is 5 or 32 or 33 && Directory.Exists(source) && !Directory.Exists(destination))
            { Thread.Sleep(25 << attempt); }
        }
    }
}
