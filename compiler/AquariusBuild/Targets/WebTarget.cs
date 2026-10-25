using System.Text.Json;
using AquariusLang.Compiler;
using AquariusLang.Libraries;

namespace AquariusLang.Build.Targets;

/// <summary>
/// A static website that runs the application in the browser's WebAssembly:
/// <code>
///   index.html, app.mjs        the page (web-glue/page)
///   glue/                      the web glue (web-glue: index.mjs, core, page and every Web API domain)
///   runtime/                   the web runtime pack: aquarius.wasm, libc.so, libc++.so, libc++abi.so
///   runtime/libraries/         the side modules of the libraries the application needs (default and external)
///   program.wasm               the application, its assets stored beside it
///   assets/                    the application's assets
///   aquarius.json              { program, entry, assets } for app.mjs
/// </code>
/// </summary>
public sealed class WebTarget : IWasmBuildTarget
{
    public const string Marker = ".aquarius-web-output";
    private static readonly string[] GlueExtensions = [".mjs", ".js", ".json", ".wasm", ".css"];
    public string Name => "web";
    public string ArtifactDescription => "Website";

    public void Build(WasmBuildRequest request)
    {
        var (application, entry) = Output.Read(request, Name);
        string output = Path.GetFullPath(request.Output);
        if (Path.GetFullPath(request.Input).StartsWith(Path.TrimEndingDirectorySeparator(output) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output folder must not contain the input .wasm.");
        var installation = AquariusInstallation.Require();
        string pack = installation.RequireRuntimePack("web", "aquarius.wasm", "libc.so");
        if (!File.Exists(Path.Combine(installation.WebGlue, "index.mjs")) || !File.Exists(Path.Combine(installation.WebGlue, "page", "app.mjs")))
            throw new IOException($"The web glue is missing from {installation.WebGlue}.");
        // Every needed library's side module, resolved before any output is touched.
        var sideModules = Output.NeededLibraries(application, request).Select(library => {
            string file = Output.ExternalBinary(library.Name, library.Library, "web") ?? Path.Combine(pack, "libraries", library.Name + ".wasm");
            if (!File.Exists(file)) throw new IOException($"The web runtime pack has no side module for the library {library.Name} ({file}).");
            return (library.Name, File: file);
        }).ToArray();

        Output.ReplaceDirectory(output, Marker, site => {
            CopyGlue(installation.WebGlue, Path.Combine(site, "glue"));
            File.Copy(Path.Combine(installation.WebGlue, "page", "index.html"), Path.Combine(site, "index.html"));
            File.Copy(Path.Combine(installation.WebGlue, "page", "app.mjs"), Path.Combine(site, "app.mjs"));
            string runtime = Path.Combine(site, "runtime");
            Directory.CreateDirectory(Path.Combine(runtime, "libraries"));
            foreach (string file in Directory.EnumerateFiles(pack)) File.Copy(file, Path.Combine(runtime, Path.GetFileName(file)));
            if (Directory.Exists(Path.Combine(pack, "licenses"))) Output.CopyDirectory(Path.Combine(pack, "licenses"), Path.Combine(runtime, "licenses"));
            foreach (var (name, file) in sideModules) File.Copy(file, Path.Combine(runtime, "libraries", name + ".wasm"));
            File.WriteAllBytes(Path.Combine(site, "program.wasm"), application.Program.WithExternalAssets().Bytes.ToArray());
            var assets = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var asset in application.Assets) {
                string path = Path.Combine(site, "assets", asset.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, asset.Value);
                assets[asset.Key] = "assets/" + string.Join('/', asset.Key.Split('/').Select(Uri.EscapeDataString));
            }
            File.WriteAllText(Path.Combine(site, "aquarius.json"), JsonSerializer.Serialize(new { program = "program.wasm", entry, assets },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        });
    }

    private static void CopyGlue(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
            if (GlueExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string directory in Directory.EnumerateDirectories(source)) CopyGlue(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
