using AquariusLang.Build.Bindings;
using AquariusLang.Compiler;
using AquariusLang.Packaging;

namespace AquariusLang.Build.Libraries;

/// <summary>
/// The native library outputs of an application (docs/native-libraries.md): the step a build target takes instead of
/// its usual artifact when <c>--kind</c> names library kinds or the project has an <c>[export]</c> table. It reads the
/// application's export table, compiles the application ahead of time (wasm2c and the pinned Clang) with the runtime
/// pack's embed/ project into libraries exporting exactly that table's functions, and writes the bindings beside them:
/// <c>&lt;output&gt;/&lt;target&gt;/</c> on the desktops (include/, bin/ or lib/, licenses/; a framework on macOS when asked),
/// <c>&lt;output&gt;/android/</c> (one library per ABI, the Java class compiled, an .aar), <c>&lt;output&gt;/ios/</c> (the
/// device and simulator slices in an xcframework) and <c>&lt;output&gt;/web/</c> (the application for loadLibrary()).
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

    /// <summary>What a build of one platform's outputs works from.</summary>
    private sealed record Plan(WasmApplication Application, BindingContext Context, INativeLibraryPlatform Platform,
        IReadOnlyList<string> Kinds, IReadOnlyList<IBindingGenerator> Generators, IReadOnlyList<string> Libraries)
    {
        public bool Java => Generators.Any(g => g is JavaBinding);
    }

    private static Plan Prepare(WasmBuildRequest request, string platformName)
    {
        var platform = NativeLibraryPlatforms.Find(platformName) ?? throw new ArgumentException($"{platformName} has no native library outputs.");
        if (!platform.Builds) throw new ArgumentException($"Native library outputs for {platformName} are not built yet (docs/native-libraries.md lists the phases).");
        if (!platform.Verified) Console.Error.WriteLine(UnverifiedNote(platformName));
        var (application, entry) = Output.Read(request, platformName);
        if (!entry.Equals(application.EntryPoint, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Native library outputs export the application's entry module; --entry does not apply to them.");
        var exports = ExportTable.Read(application.Program) ?? throw new InvalidDataException(
            $"{entry} exports nothing: mark the functions a native library exposes with 匯出 / export (docs/native-libraries.md).");
        var libraries = LinkedLibraries(application, request);
        var settings = request.Export ?? NativeLibrarySettings.Default(Path.GetFileNameWithoutExtension(entry));
        return new Plan(application, new BindingContext(exports, settings, platformName), platform, Kinds(request, settings, platform),
            Generators(settings, platform), libraries);
    }

    /// <summary>The line a build of a platform whose outputs have not run on its hardware yet prints first.</summary>
    public static string UnverifiedNote(string platform) =>
        $"Note: {platform} native library outputs are designed and built as docs/native-libraries.md describes, but not verified on {platform} hardware yet (the spec lists the checks).";

    /// <summary>The outputs of a desktop target (windows-x64, linux-x64, macos-arm64) or of the web ("web").</summary>
    public static void Build(WasmBuildRequest request, string runtimeTarget)
    {
        var target = RuntimeTarget.Get(runtimeTarget);
        var plan = Prepare(request, target.Platform);
        if (target.Kind == TargetKind.Web) { BuildWeb(request, plan); return; }
        AquariusInstallation.RequireBuildableHere(runtimeTarget, "native library outputs");
        var installation = AquariusInstallation.Require();
        string pack = installation.RequireRuntimePack(runtimeTarget, Path.Combine("embed", "CMakeLists.txt"));
        string? jdk = plan.Java ? JavaClasses.Home() : null;
        bool framework = Package(plan) == "framework";
        Output.ReplaceDirectory(Path.Combine(Path.GetFullPath(request.Output), runtimeTarget), Marker, stage => {
            WriteBindings(plan, stage);
            var files = BuildKinds(request.Input, stage, plan, target, Path.Combine(pack, "embed"), jdk, framework);
            if (framework && files.Shared.Length > 0) files = MoveIntoFramework(plan, target, stage, files);
            Write(Path.Combine(stage, "lib", "cmake", plan.Context.Settings.Name, plan.Context.Settings.Name + "-config.cmake"), CMakePackage.Config(plan.Context, target, files));
            if (jdk != null) JavaClasses.Compile(jdk, Path.Combine(stage, "java"), Path.Combine(stage, "lib", plan.Context.Settings.Name + ".jar"));
            WriteLicenses(stage, installation, target);
        });
    }

    /// <summary>The Android outputs: the library of each ABI ([export.android] targets, else every Android target whose
    /// runtime pack is built) in lib/&lt;abi&gt;/, the bindings, the Java class compiled into &lt;name&gt;.jar, and the .aar
    /// (the default package) with jni/, classes.jar and Prefab headers.</summary>
    public static void BuildAndroid(WasmBuildRequest request)
    {
        var plan = Prepare(request, "android");
        var installation = AquariusInstallation.Require();
        var all = RuntimeTarget.Built.Where(t => t.Platform == "android").ToArray();
        var chosen = plan.Context.Settings.For("android").Targets is { } named ? all.Where(t => named.Contains(t.Name)).ToArray()
            : all.Where(t => installation.RuntimePack(t.Name) is { } pack && File.Exists(Path.Combine(pack, "embed", "CMakeLists.txt"))).ToArray();
        if (chosen.Length == 0) throw new IOException("No Android runtime pack has the embed runtime: run scripts/build-runtime.ps1 -Targets android-x86_64 (and android-arm64-v8a).");
        string jdk = plan.Java ? JavaClasses.Home() : "";
        string name = plan.Context.Settings.Name;
        Output.ReplaceDirectory(Path.Combine(Path.GetFullPath(request.Output), "android"), Marker, stage => {
            WriteBindings(plan, stage);
            foreach (var target in chosen) {
                string pack = installation.RequireRuntimePack(target.Name, Path.Combine("embed", "CMakeLists.txt"));
                string abiStage = Path.Combine(stage, ".abi-" + target.Name);
                BuildKinds(request.Input, abiStage, plan, target, Path.Combine(pack, "embed"), null);
                Output.CopyDirectory(Path.Combine(abiStage, "lib"), Path.Combine(stage, "lib", AndroidAbi(target)));
                if (Directory.Exists(Path.Combine(abiStage, "licenses"))) Output.CopyDirectory(Path.Combine(abiStage, "licenses"), Path.Combine(stage, "licenses"));
                Directory.Delete(abiStage, true);
            }
            if (plan.Java) JavaClasses.Compile(jdk, Path.Combine(stage, "java"), Path.Combine(stage, "lib", name + ".jar"));
            WriteLicenses(stage, installation, chosen[0]);
            if ((plan.Context.Settings.For("android").Package ?? plan.Platform.Packages[0]) == "aar")
                AndroidLibraryPackage.Write(Path.Combine(stage, name + ".aar"), plan.Context, chosen.Select(t => (AndroidAbi(t), Path.Combine(stage, "lib", AndroidAbi(t)))).ToArray(),
                    plan.Java ? Path.Combine(stage, "lib", name + ".jar") : null, Path.Combine(stage, "include"));
        });
    }

    /// <summary>The package <c>[export.&lt;platform&gt;] package</c> names, else the platform's default.</summary>
    private static string Package(Plan plan) => plan.Context.Settings.For(plan.Platform.Platform).Package ?? plan.Platform.Packages[0];

    /// <summary>A macOS framework package: the shared library (linked with the framework's install name) becomes
    /// &lt;Module&gt;.framework beside lib/, signed ad hoc as a bundle; the CMake package points at its binary.</summary>
    private static LibraryFiles MoveIntoFramework(Plan plan, RuntimeTarget target, string stage, LibraryFiles files)
    {
        var context = plan.Context with { Platform = target.Platform };
        string library = Path.Combine(stage, files.Shared);
        string framework = AppleLibraryPackage.WriteFramework(context, AppleLibraryPackage.Slice.Of(target), stage, library, Path.Combine(stage, "include"));
        File.Delete(library);
        AppleLibraryPackage.SignAdHoc(framework);
        string module = AppleLibraryPackage.FrameworkName(context);
        return files with { Shared = $"{module}.framework/Versions/A/{module}" };
    }

    /// <summary>The iOS outputs: each slice ([export.ios] targets, else every iOS target whose runtime pack is built: the
    /// device and the simulator) built with its pack, then the shared kind as a framework per slice and the static kind
    /// as a library with Headers/ per slice, gathered into &lt;Module&gt;.xcframework and &lt;Module&gt;-static.xcframework
    /// (the default package) or left as slice folders (package "folder"); the bindings and licenses beside them.</summary>
    public static void BuildIos(WasmBuildRequest request)
    {
        var plan = Prepare(request, "ios");
        var all = RuntimeTarget.Built.Where(t => t.Platform == "ios").ToArray();
        foreach (var target in all) AquariusInstallation.RequireBuildableHere(target.Name, "native library outputs");
        var installation = AquariusInstallation.Require();
        var chosen = plan.Context.Settings.For("ios").Targets is { } named ? all.Where(t => named.Contains(t.Name)).ToArray()
            : all.Where(t => installation.RuntimePack(t.Name) is { } pack && File.Exists(Path.Combine(pack, "embed", "CMakeLists.txt"))).ToArray();
        if (chosen.Length == 0) throw new IOException("No iOS runtime pack has the embed runtime: run scripts/build-runtime.ps1 -Targets ios-arm64,ios-simulator-arm64.");
        var context = plan.Context with { Platform = "ios" };
        string name = context.Settings.Name, module = AppleLibraryPackage.FrameworkName(context);
        bool xcframework = Package(plan) == "xcframework";
        Output.ReplaceDirectory(Path.Combine(Path.GetFullPath(request.Output), "ios"), Marker, stage => {
            WriteBindings(plan, stage);
            var frameworks = new List<(AppleLibraryPackage.Slice, string, string, string?)>();
            var statics = new List<(AppleLibraryPackage.Slice, string, string, string?)>();
            var bundles = new List<string>();
            string include = Path.Combine(stage, "include");
            foreach (var target in chosen) {
                var slice = AppleLibraryPackage.Slice.Of(target);
                string pack = installation.RequireRuntimePack(target.Name, Path.Combine("embed", "CMakeLists.txt"));
                string built = Path.Combine(stage, ".slice-" + target.Name);
                var files = BuildKinds(request.Input, built, plan, target, Path.Combine(pack, "embed"), null, framework: true);
                string shared = xcframework ? Path.Combine(stage, module + ".xcframework", slice.Identifier) : Path.Combine(stage, target.Name);
                string archive = xcframework ? Path.Combine(stage, module + "-static.xcframework", slice.Identifier) : Path.Combine(stage, target.Name, "static");
                if (files.Shared.Length > 0) {
                    Directory.CreateDirectory(shared);
                    string framework = AppleLibraryPackage.WriteFramework(context, slice, shared, Path.Combine(built, files.Shared), include);
                    frameworks.Add((slice, module + ".framework", $"{module}.framework/{module}", null));
                    if (!xcframework) bundles.Add(framework);
                }
                if (files.Static.Length > 0) {
                    AppleLibraryPackage.WriteStaticSlice(context, archive, Path.Combine(built, files.Static), include);
                    statics.Add((slice, target.StaticLibraryName(name), target.StaticLibraryName(name), "Headers"));
                }
                if (Directory.Exists(Path.Combine(built, "licenses"))) Output.CopyDirectory(Path.Combine(built, "licenses"), Path.Combine(stage, "licenses"));
                Directory.Delete(built, true);
            }
            if (xcframework && frameworks.Count > 0) {
                AppleLibraryPackage.WriteXcframework(Path.Combine(stage, module + ".xcframework"), frameworks);
                bundles.Add(Path.Combine(stage, module + ".xcframework"));
            }
            if (xcframework && statics.Count > 0) AppleLibraryPackage.WriteXcframework(Path.Combine(stage, module + "-static.xcframework"), statics);
            foreach (string bundle in bundles) AppleLibraryPackage.SignAdHoc(bundle);
            WriteLicenses(stage, installation, chosen[0]);
        });
    }

    /// <summary>The ABI of an Android target (android-x86_64 is x86_64).</summary>
    public static string AndroidAbi(RuntimeTarget target) => target.Name["android-".Length..];

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
    private static void BuildWeb(WasmBuildRequest request, Plan plan)
    {
        var installation = AquariusInstallation.Require();
        string pack = installation.RequireRuntimePack("web", "aquarius.wasm", "libc.so");
        var sideModules = plan.Libraries.Select(name => Path.Combine(pack, "libraries", name + ".wasm")).ToArray();
        if (sideModules.FirstOrDefault(m => !File.Exists(m)) is { } missing) throw new IOException($"The web runtime pack has no side module {missing}.");
        Output.ReplaceDirectory(Path.Combine(Path.GetFullPath(request.Output), "web"), Marker, stage => {
            WriteBindings(plan, stage);
            File.WriteAllBytes(Path.Combine(stage, plan.Context.Settings.Name + ".wasm"), plan.Application.Program.Bytes.ToArray());
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

    private static void WriteBindings(Plan plan, string stage)
    {
        foreach (var generator in plan.Generators)
            foreach (var file in generator.Generate(plan.Context)) Write(Path.Combine(stage, file.Path), file.Text);
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
    /// (with its import libraries), the static library and the prelinked object; with the Java binding, the JNI half
    /// (runtime/embed/jni.c) too, whose headers come from <paramref name="jdk"/> on the desktops.</summary>
    private static LibraryFiles BuildKinds(string wasm, string stage, Plan plan, RuntimeTarget target, string project, string? jdk, bool framework = false)
    {
        var context = plan.Context with { Platform = target.Platform };
        var settings = context.Settings;
        var kinds = plan.Kinds;
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
            File.WriteAllText(Path.Combine(table, "exports.def"), EmbedTable.Definitions(context, plan.Java));
            File.WriteAllText(Path.Combine(table, CBinding.HeaderName(context)), CBinding.Header(context));
            File.WriteAllText(Path.Combine(table, "symbols.txt"), string.Join("\n", EmbedTable.Symbols(context, plan.Java)) + "\n");
            File.WriteAllText(Path.Combine(table, "exported_symbols.txt"), EmbedTable.MachOSymbols(context, plan.Java));
            string Full(string relative) => Path.Combine(stage, relative);
            var definitions = new Dictionary<string, string> {
                ["AQUA_EMBED_TABLE"] = table, ["AQUA_EMBED_HEADER"] = CBinding.HeaderName(context),
                ["AQUA_EMBED_STATIC"] = CBinding.Macro(context) + "_STATIC", ["AQUA_EMBED_PREFIX"] = settings.SymbolPrefix,
                ["AQUA_EMBED_MODULE"] = Path.GetFullPath(wasm), ["AQUA_EMBED_LIBRARIES"] = string.Join(";", plan.Libraries),
            };
            if (plan.Java) {
                definitions["AQUA_EMBED_JAVA_CLASS"] = JavaBinding.JniClass(context);
                if (jdk != null) definitions["AQUA_EMBED_JDK"] = jdk;
            }
            if (files.Shared.Length > 0) {
                definitions["AQUA_EMBED_SHARED_OUTPUT"] = Full(files.Shared);
                if (versioned != null) definitions["AQUA_EMBED_SONAME"] = versioned;
                if (target.Apple) {
                    var (current, compatibility) = AppleLibraryPackage.Versions(settings);
                    definitions["AQUA_EMBED_INSTALL_NAME"] = AppleLibraryPackage.InstallName(context, target, framework);
                    definitions["AQUA_EMBED_CURRENT_VERSION"] = current;
                    definitions["AQUA_EMBED_COMPATIBILITY_VERSION"] = compatibility;
                }
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
        if (File.Exists(own)) File.Copy(own, Path.Combine(licenses, "AquariusLang.txt"), true);
        var notices = new List<string> {
            "This library contains the AquariusLang runtime (AquariusLang.txt) and the wasm2c runtime of WABT",
            "(https://github.com/WebAssembly/wabt, Apache License 2.0)."
        };
        if (target.Platform == "windows")
            notices.Add("On Windows it links the MinGW-w64 runtime and LLVM's compiler-rt statically (https://github.com/mstorsjo/llvm-mingw: ZPL 2.1, MIT and Apache License 2.0 with LLVM exceptions).");
        File.WriteAllText(Path.Combine(licenses, "THIRD-PARTY.txt"), string.Join("\n", notices) + "\n");
    }

    internal static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings("\n"));
    }
}
