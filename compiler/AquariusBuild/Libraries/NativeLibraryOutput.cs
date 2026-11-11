using AquariusLang.Build.Bindings;
using AquariusLang.Compiler;
using AquariusLang.Packaging;

namespace AquariusLang.Build.Libraries;

/// <summary>
/// The native library outputs of an application (docs/native-libraries.md): the step a build target takes instead of
/// its usual artifact when <c>--kind</c> is given or the project has an <c>[export]</c> table. It reads the application's
/// export table, compiles the application ahead of time (wasm2c and the pinned Clang) with the runtime pack's embed/
/// project into a library exporting exactly that table's functions, and writes the bindings beside it:
/// <c>&lt;output&gt;/&lt;target&gt;/</c> with include/ (bindings), bin/ or lib/ (the library and import libraries) and licenses/.
/// </summary>
public static class NativeLibraryOutput
{
    public const string KindOption = "--kind";
    /// <summary>The kind that asks a target for its usual artifact (the executable), also from a project with [export].</summary>
    public const string ExecutableKind = "executable";
    /// <summary>Marks an output folder this step wrote, so rebuilding may replace it.</summary>
    public const string Marker = ".aquarius-native-library";

    /// <summary>Whether the request asks for native library outputs: --kind names library kinds, or the project has
    /// [export] and --kind is not "executable".</summary>
    public static bool Requested(WasmBuildRequest request) => request.Option(KindOption) is { } kind
        ? kind.Trim() != ExecutableKind : request.Export != null;

    public static void Build(WasmBuildRequest request, string runtimeTarget)
    {
        var target = RuntimeTarget.Get(runtimeTarget);
        var platform = NativeLibraryPlatforms.Find(target.Platform) ?? throw new ArgumentException($"{target.Platform} has no native library outputs.");
        if (!platform.Builds) throw new ArgumentException($"Native library outputs for {target.Platform} are not built yet (docs/native-libraries.md lists the phases).");
        var (application, entry) = Output.Read(request, target.Platform);
        if (!entry.Equals(application.EntryPoint, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Native library outputs export the application's entry module; --entry does not apply to them.");
        var exports = ExportTable.Read(application.Program) ?? throw new InvalidDataException(
            $"{entry} exports nothing: mark the functions a native library exposes with 匯出 / export (docs/native-libraries.md).");
        var linked = LinkedLibraries(application, request);
        var settings = request.Export ?? NativeLibrarySettings.Default(Path.GetFileNameWithoutExtension(entry));
        var kinds = Kinds(request, settings, platform);
        var context = new BindingContext(exports, settings);
        var generators = Generators(settings, platform);
        if (target.Kind == TargetKind.Web) { BuildWeb(request, application, context, generators, linked); return; }

        AquariusInstallation.RequireBuildableHere(runtimeTarget, "native library outputs");
        var installation = AquariusInstallation.Require();
        string pack = installation.RequireRuntimePack(runtimeTarget, Path.Combine("embed", "CMakeLists.txt"));
        string output = Path.Combine(Path.GetFullPath(request.Output), runtimeTarget);
        Output.ReplaceDirectory(output, Marker, stage => {
            foreach (var generator in generators)
                foreach (var file in generator.Generate(context)) Write(Path.Combine(stage, file.Path), file.Text);
            var files = BuildKinds(request.Input, stage, context, target, kinds, linked, Path.Combine(pack, "embed"));
            Write(Path.Combine(stage, "lib", "cmake", settings.Name, settings.Name + "-config.cmake"), CMakePackage.Config(context, target, files));
            WriteLicenses(stage, installation, target);
        });
    }

    /// <summary>The default native libraries the application needs, which link into its outputs. A library that needs a
    /// window system ([native] window = true) cannot be included yet, nor can an external library's native part.</summary>
    private static IReadOnlyList<string> LinkedLibraries(WasmApplication application, WasmBuildRequest request)
    {
        foreach (var (name, library) in Output.NeededLibraries(application, request)) {
            if (library is { NeedsWindow: true })
                throw new InvalidDataException($"The application needs the {library.Name} library, which needs a window system: native library outputs cannot include it yet (docs/native-libraries.md).");
            if (library is not { External: false })
                throw new InvalidDataException($"The application needs the external native library {name}: native library outputs include only default libraries yet.");
        }
        return application.NativeLibraries;
    }

    /// <summary>The web's library: no native code is built. <c>&lt;output&gt;/web/</c> holds the application as
    /// &lt;name&gt;.wasm, the web pack in runtime/ with the needed libraries' side modules in runtime/libraries/, the glue
    /// (whose loadLibrary() loads it) in glue/, the bindings and licenses/.</summary>
    private static void BuildWeb(WasmBuildRequest request, WasmApplication application, BindingContext context,
        IReadOnlyList<IBindingGenerator> generators, IReadOnlyList<string> libraries)
    {
        var installation = AquariusInstallation.Require();
        string pack = installation.RequireRuntimePack("web", "aquarius.wasm", "libc.so");
        var sideModules = libraries.Select(name => Path.Combine(pack, "libraries", name + ".wasm")).ToArray();
        if (sideModules.FirstOrDefault(m => !File.Exists(m)) is { } missing) throw new IOException($"The web runtime pack has no side module {missing}.");
        string output = Path.Combine(Path.GetFullPath(request.Output), "web");
        Output.ReplaceDirectory(output, Marker, stage => {
            foreach (var generator in generators)
                foreach (var file in generator.Generate(context)) Write(Path.Combine(stage, file.Path), file.Text);
            File.WriteAllBytes(Path.Combine(stage, context.Settings.Name + ".wasm"), application.Program.Bytes.ToArray());
            string runtime = Path.Combine(stage, "runtime");
            Directory.CreateDirectory(Path.Combine(runtime, "libraries"));
            foreach (string file in Directory.EnumerateFiles(pack)) File.Copy(file, Path.Combine(runtime, Path.GetFileName(file)));
            foreach (string module in sideModules) File.Copy(module, Path.Combine(runtime, "libraries", Path.GetFileName(module)));
            Targets.WebTarget.CopyGlue(installation.WebGlue, Path.Combine(stage, "glue"));
            string licenses = Path.Combine(stage, "licenses");
            if (Directory.Exists(Path.Combine(pack, "licenses"))) Output.CopyDirectory(Path.Combine(pack, "licenses"), licenses);
            Directory.CreateDirectory(licenses);
            string own = Path.Combine(installation.Root, "LICENSE");
            if (File.Exists(own)) File.Copy(own, Path.Combine(licenses, "AquariusLang.txt"), true);
        });
    }

    /// <summary>The kinds to build: <c>--kind a,b</c>, else the manifest's for this platform; each allowed there.</summary>
    private static IReadOnlyList<string> Kinds(WasmBuildRequest request, NativeLibrarySettings settings, INativeLibraryPlatform platform)
    {
        var kinds = request.Option(KindOption) is { } option
            ? option.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : settings.KindsFor(platform.Platform);
        if (kinds.Count == 0) throw new ArgumentException($"{KindOption} needs at least one kind: {string.Join(", ", platform.Kinds)}.");
        if (kinds.Contains(ExecutableKind)) throw new ArgumentException($"{KindOption} {ExecutableKind} builds the executable alone; build library kinds in another build.");
        if (kinds.FirstOrDefault(k => !platform.Kinds.Contains(k)) is { } wrong)
            throw new ArgumentException($"{platform.Platform} builds {string.Join(", ", platform.Kinds)} native library outputs, not {wrong}.");
        return kinds;
    }

    /// <summary>The binding generators: the manifest's bindings for this platform must all exist; of the platform's
    /// defaults, those this compiler has.</summary>
    private static IReadOnlyList<IBindingGenerator> Generators(NativeLibrarySettings settings, INativeLibraryPlatform platform)
    {
        if (settings.For(platform.Platform).Bindings is { } explicitly) {
            if (explicitly.FirstOrDefault(b => BindingGenerators.Find(b) == null) is { } missing)
                throw new ArgumentException($"The {missing} binding is not written yet (docs/native-libraries.md lists the phases).");
            return explicitly.Select(b => BindingGenerators.Find(b)!).ToArray();
        }
        // The C header is the ABI itself: it ships wherever the platform has a C binding.
        var defaults = platform.Bindings.Contains("c") ? platform.DefaultBindings.Prepend("c").Distinct() : platform.DefaultBindings;
        return defaults.Select(BindingGenerators.Find).OfType<IBindingGenerator>().ToArray();
    }

    /// <summary>The files of the kinds built, relative to the output folder; empty strings for kinds not built.</summary>
    public sealed record LibraryFiles(string Shared = "", string ImportLibrary = "", string Static = "", string Object = "",
        IReadOnlyList<string>? SystemLibraries = null);

    /// <summary>The kinds asked for, compiled with the pack's embed/ project from one translation: the shared library
    /// (with its import libraries), the static library and the prelinked object.</summary>
    private static LibraryFiles BuildKinds(string wasm, string stage, BindingContext context, RuntimeTarget target, IReadOnlyList<string> kinds,
        IReadOnlyList<string> libraries, string project)
    {
        var settings = context.Settings;
        string? versioned = target.VersionedSharedLibraryName(settings.Name, settings.Major);
        var files = new LibraryFiles(
            Shared: kinds.Contains("shared") ? $"{target.SharedLibraryFolder}/{versioned ?? target.SharedLibraryName(settings.Name)}" : "",
            Static: kinds.Contains("static") ? "lib/" + target.StaticLibraryName(settings.Name) : "",
            Object: kinds.Contains("object") ? "lib/" + target.ObjectName(settings.Name) : "");
        string table = Path.Combine(Path.GetTempPath(), "aquarius-embed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(table);
        Directory.CreateDirectory(Path.Combine(stage, "lib"));
        if (files.Shared.Length > 0) Directory.CreateDirectory(Path.Combine(stage, target.SharedLibraryFolder));
        try {
            File.WriteAllText(Path.Combine(table, "exports.def"), EmbedTable.Definitions(context));
            File.WriteAllText(Path.Combine(table, CBinding.HeaderName(context)), CBinding.Header(context));
            File.WriteAllText(Path.Combine(table, "symbols.txt"), string.Join("\n", EmbedTable.Symbols(context)) + "\n");
            string Full(string relative) => Path.Combine(stage, relative);
            var definitions = new Dictionary<string, string> {
                ["AQUA_EMBED_TABLE"] = table, ["AQUA_EMBED_HEADER"] = CBinding.HeaderName(context),
                ["AQUA_EMBED_STATIC"] = CBinding.Macro(context) + "_STATIC", ["AQUA_EMBED_PREFIX"] = settings.SymbolPrefix,
                ["AQUA_EMBED_MODULE"] = Path.GetFullPath(wasm), ["AQUA_EMBED_LIBRARIES"] = string.Join(";", libraries),
            };
            if (files.Shared.Length > 0) {
                definitions["AQUA_EMBED_SHARED_OUTPUT"] = Full(files.Shared);
                if (versioned != null) definitions["AQUA_EMBED_SONAME"] = versioned;
                foreach (var (kind, file) in target.ImportLibraryNames(settings.Name).Where(i => ImportLibraryWanted(settings, target, i.Kind))) {
                    definitions["AQUA_EMBED_IMPORT_" + kind.ToUpperInvariant()] = Full("lib/" + file);
                    if (kind == "mingw") files = files with { ImportLibrary = "lib/" + file };
                }
            }
            if (files.Static.Length > 0) definitions["AQUA_EMBED_STATIC_OUTPUT"] = Full(files.Static);
            if (files.Object.Length > 0) definitions["AQUA_EMBED_OBJECT_OUTPUT"] = Full(files.Object);
            string primary = new[] { files.Shared, files.Static, files.Object }.First(f => f.Length > 0);
            AheadOfTime.Compile(wasm, Full(primary), target.Name, project, definitions);
            files = files with { SystemLibraries = File.ReadAllLines(Path.Combine(table, "link.txt")).Where(l => l.Length > 0).ToArray() };
            ShipLibraryFiles(table, stage, target);
        } finally {
            try { Directory.Delete(table, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        // Programs link against the unversioned name, which leads to the SONAME's file.
        if (versioned != null && files.Shared.Length > 0)
            File.CreateSymbolicLink(Path.Combine(stage, target.SharedLibraryFolder, target.SharedLibraryName(settings.Name)), versioned);
        return files;
    }

    /// <summary>The linked libraries' runtime files (ONNX Runtime, say), beside the shared library where they are looked
    /// for, and their licenses under licenses/&lt;library&gt;/, from the lists the embed project wrote.</summary>
    private static void ShipLibraryFiles(string table, string stage, RuntimeTarget target)
    {
        foreach (string file in File.ReadAllLines(Path.Combine(table, "runtime.txt")).Where(l => l.Length > 0)) {
            Directory.CreateDirectory(Path.Combine(stage, target.SharedLibraryFolder));
            File.Copy(file, Path.Combine(stage, target.SharedLibraryFolder, Path.GetFileName(file)), true);
        }
        foreach (string line in File.ReadAllLines(Path.Combine(table, "licenses.txt")).Where(l => l.Contains('|'))) {
            string[] parts = line.Split('|', 2);
            Output.CopyDirectory(parts[1], Path.Combine(stage, "licenses", parts[0]));
        }
    }

    private static bool ImportLibraryWanted(NativeLibrarySettings settings, RuntimeTarget target, string kind) =>
        settings.For(target.Platform).ImportLibraries?.Contains(kind) ?? true;

    /// <summary>The licenses of what the library contains: AquariusLang's runtime, the wasm2c runtime (WABT) and, on
    /// Windows, the statically linked MinGW-w64 and LLVM runtime pieces.</summary>
    private static void WriteLicenses(string stage, AquariusInstallation installation, RuntimeTarget target)
    {
        string licenses = Path.Combine(stage, "licenses");
        Directory.CreateDirectory(licenses);
        string own = Path.Combine(installation.Root, "LICENSE");
        if (File.Exists(own)) File.Copy(own, Path.Combine(licenses, "AquariusLang.txt"));
        var notices = new List<string> {
            "This library contains the AquariusLang runtime (AquariusLang.txt) and the wasm2c runtime of WABT",
            "(https://github.com/WebAssembly/wabt, Apache License 2.0)."
        };
        if (target.Platform == "windows")
            notices.Add("On Windows it links the MinGW-w64 runtime and LLVM's compiler-rt statically (https://github.com/mstorsjo/llvm-mingw: ZPL 2.1, MIT and Apache License 2.0 with LLVM exceptions).");
        File.WriteAllText(Path.Combine(licenses, "THIRD-PARTY.txt"), string.Join("\n", notices) + "\n");
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings("\n"));
    }
}
