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
    /// <summary>Marks an output folder this step wrote, so rebuilding may replace it.</summary>
    public const string Marker = ".aquarius-native-library";

    /// <summary>Whether the request asks for native library outputs.</summary>
    public static bool Requested(WasmBuildRequest request) => request.Option(KindOption) != null || request.Export != null;

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
        if (application.NativeLibraries.Count > 0)
            throw new InvalidDataException($"Native library outputs cannot include native libraries yet; this application needs {string.Join(", ", application.NativeLibraries)}.");
        var settings = request.Export ?? NativeLibrarySettings.Default(Path.GetFileNameWithoutExtension(entry));
        var kinds = Kinds(request, settings, platform);
        if (kinds.FirstOrDefault(k => k != "shared") is { } unbuilt)
            throw new ArgumentException($"{unbuilt} native library outputs are not built yet; build shared ones (docs/native-libraries.md lists the phases).");
        var context = new BindingContext(exports, settings);
        var generators = Generators(settings, platform);

        AquariusInstallation.RequireBuildableHere(runtimeTarget, "native library outputs");
        var installation = AquariusInstallation.Require();
        string pack = installation.RequireRuntimePack(runtimeTarget, Path.Combine("embed", "CMakeLists.txt"));
        string output = Path.Combine(Path.GetFullPath(request.Output), runtimeTarget);
        Output.ReplaceDirectory(output, Marker, stage => {
            foreach (var generator in generators)
                foreach (var file in generator.Generate(context)) Write(Path.Combine(stage, file.Path), file.Text);
            BuildShared(request.Input, stage, context, target, Path.Combine(pack, "embed"), installation);
            WriteLicenses(stage, installation, target);
        });
    }

    /// <summary>The kinds to build: <c>--kind a,b</c>, else the manifest's for this platform; each allowed there.</summary>
    private static IReadOnlyList<string> Kinds(WasmBuildRequest request, NativeLibrarySettings settings, INativeLibraryPlatform platform)
    {
        var kinds = request.Option(KindOption) is { } option
            ? option.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : settings.KindsFor(platform.Platform);
        if (kinds.Count == 0) throw new ArgumentException($"{KindOption} needs at least one kind: {string.Join(", ", platform.Kinds)}.");
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
        // The C header is the ABI itself: it always ships.
        return platform.DefaultBindings.Prepend("c").Distinct().Select(BindingGenerators.Find).OfType<IBindingGenerator>().ToArray();
    }

    /// <summary>The shared library and its import libraries, compiled with the pack's embed/ project.</summary>
    private static void BuildShared(string wasm, string stage, BindingContext context, RuntimeTarget target, string project, AquariusInstallation installation)
    {
        var settings = context.Settings;
        string folder = Path.Combine(stage, target.SharedLibraryFolder);
        Directory.CreateDirectory(folder);
        string? versioned = target.VersionedSharedLibraryName(settings.Name, settings.Major);
        string library = Path.Combine(folder, versioned ?? target.SharedLibraryName(settings.Name));
        string table = Path.Combine(Path.GetTempPath(), "aquarius-embed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(table);
        try {
            File.WriteAllText(Path.Combine(table, "exports.def"), EmbedTable.Definitions(context));
            File.WriteAllText(Path.Combine(table, CBinding.HeaderName(context)), CBinding.Header(context));
            File.WriteAllText(Path.Combine(table, "symbols.txt"), string.Join("\n", EmbedTable.Symbols(context)) + "\n");
            var definitions = new Dictionary<string, string> {
                ["AQUA_EMBED_TABLE"] = table, ["AQUA_EMBED_HEADER"] = CBinding.HeaderName(context),
                ["AQUA_EMBED_STATIC"] = CBinding.Macro(context) + "_STATIC", ["AQUA_EMBED_PREFIX"] = settings.SymbolPrefix,
                ["AQUA_EMBED_MODULE"] = Path.GetFullPath(wasm),
            };
            if (versioned != null) definitions["AQUA_EMBED_SONAME"] = versioned;
            foreach (var (kind, file) in target.ImportLibraryNames(settings.Name).Where(i => ImportLibraryWanted(settings, target, i.Kind)))
                definitions["AQUA_EMBED_IMPORT_" + kind.ToUpperInvariant()] = Path.Combine(stage, "lib", file);
            if (definitions.Keys.Any(k => k.StartsWith("AQUA_EMBED_IMPORT_", StringComparison.Ordinal))) Directory.CreateDirectory(Path.Combine(stage, "lib"));
            AheadOfTime.Compile(wasm, library, target.Name, project, definitions);
        } finally {
            try { Directory.Delete(table, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        // Programs link against the unversioned name, which leads to the SONAME's file.
        if (versioned != null) File.CreateSymbolicLink(Path.Combine(folder, target.SharedLibraryName(settings.Name)), versioned);
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
