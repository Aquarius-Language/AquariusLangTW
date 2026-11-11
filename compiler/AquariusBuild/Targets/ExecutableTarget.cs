using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using AquariusLang.Build.Libraries;
using AquariusLang.Compiler;
using AquariusLang.Libraries;

namespace AquariusLang.Build.Targets;

/// <summary>
/// A desktop executable: the runtime pack's launcher (aquarius.exe / aquarius) with the application appended as an
/// overlay (<see cref="ExecutableBundle"/>). The files the pack installs beside its launcher (DLLs default libraries
/// load, licenses) are copied beside the executable, and external libraries into its libraries/ folder, where the
/// runtime looks for them. On macOS, where bytes appended to a Mach-O break the code signature every Apple Silicon
/// binary needs, the output is an app bundle instead (<c>Name.app</c>): the unmodified launcher and the pack's files in
/// Contents/MacOS, the overlay alone in Contents/Resources/application.aquarius, signed ad hoc as a whole.
/// </summary>
public sealed class ExecutableTarget(string name, string runtimeTarget) : IWasmBuildTarget
{
    public const string HideTerminalOption = "--hide-terminal";
    /// <summary>Selects the runtime pack built with ONNX Runtime's CUDA build (&lt;target&gt;-cuda: the CUDA and TensorRT
    /// providers, about 300 MB); projects set [onnx] cuda = true.</summary>
    public const string OnnxCudaOption = "--onnx-cuda";
    /// <summary>Compiles the application ahead of time (<see cref="AheadOfTime"/>): the pack's aquarius-aot launcher and an
    /// application library beside the executable instead of Wasmtime compiling the .wasm when it starts.</summary>
    public const string OptimizeOption = "--optimize";

    public string Name => name;
    public string RuntimeTarget => runtimeTarget;
    public string ArtifactDescription => "Executable";
    private bool Windows => AquariusLang.Compiler.RuntimeTarget.Get(runtimeTarget).Platform == "windows";
    private bool MacOS => AquariusLang.Compiler.RuntimeTarget.Get(runtimeTarget).Platform == "macos";
    /// <summary>The file in a macOS .app (relative to it) that holds the application's overlay.</summary>
    public const string AppPayload = "Contents/Resources/application.aquarius";
    /// <summary>Marks a .app this target wrote, so rebuilding may replace it.</summary>
    public const string AppMarker = "Contents/Resources/.aquarius-app-output";
    private string Exe => AquariusLang.Compiler.RuntimeTarget.Get(runtimeTarget).ExecutableSuffix;
    public IReadOnlyCollection<string> OptionNames => Windows ? [HideTerminalOption, OnnxCudaOption, OptimizeOption, NativeLibraryOutput.KindOption]
        : [OnnxCudaOption, OptimizeOption, NativeLibraryOutput.KindOption];

    public void Build(WasmBuildRequest request)
    {
        // --kind or an [export] table: native library outputs instead of an executable (docs/native-libraries.md).
        if (NativeLibraryOutput.Requested(request)) { NativeLibraryOutput.Build(request, runtimeTarget); return; }
        bool hideTerminal = request.Flag(HideTerminalOption), cuda = request.Flag(OnnxCudaOption), optimize = request.Flag(OptimizeOption);
        if (hideTerminal && !Windows) throw new ArgumentException($"{HideTerminalOption} applies only to Windows executables.");
        string output = Path.GetFullPath(request.Output);
        if (Windows && !output.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Windows executables must be named *.exe.");
        if (MacOS && !Path.TrimEndingDirectorySeparator(output).EndsWith(".app", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("macOS applications must be named *.app.");
        var (application, entry) = Output.Read(request, name);
        var libraries = Output.NeededLibraries(application, request)
            .Select(l => (l.Name, Binary: Output.ExternalBinary(l.Name, l.Library, runtimeTarget))).Where(l => l.Binary != null).ToArray();

        var installation = AquariusInstallation.Require();
        string packTarget = runtimeTarget + (cuda ? "-cuda" : "");
        string launcher = (optimize ? "aquarius-aot" : "aquarius") + Exe;
        string pack = optimize ? installation.RequireRuntimePack(packTarget, launcher, Path.Combine("aot", "CMakeLists.txt"))
            : installation.RequireRuntimePack(packTarget, launcher);
        string template = Path.Combine(pack, launcher);
        if (output.Equals(template, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The output must not replace the runtime pack's launcher.");

        if (MacOS) { BuildApp(request, output, entry, template, pack, optimize, libraries); return; }
        Output.ReplaceFile(output, stage => {
            using var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.ReadWrite);
            using (var launcher = File.OpenRead(template)) using (var wasm = File.OpenRead(request.Input)) ExecutableBundle.Write(launcher, wasm, stream, entry);
            if (hideTerminal) WindowsGui(stream);
            stream.Flush(flushToDisk: true);
            stream.Close();
            if (!OperatingSystem.IsWindows() && !Windows)
                File.SetUnixFileMode(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        });

        if (optimize) AheadOfTime.BuildLibrary(request.Input, output, runtimeTarget, pack);

        string directory = Path.GetDirectoryName(output)!;
        CopyPack(pack, directory, directory, libraries);
    }

    /// <summary>The pack's other files (libraries' DLLs and shared objects, licenses) run beside the launcher; the
    /// launchers and the ahead-of-time project stay in the pack. External libraries go into libraries/ there. Files
    /// that are not code (licenses) go into <paramref name="resources"/>, which is the same folder except in a macOS
    /// .app, whose Contents/MacOS may hold only code.</summary>
    private void CopyPack(string pack, string directory, string resources, (string Name, string? Binary)[] libraries)
    {
        string suffix = AquariusLang.Compiler.RuntimeTarget.Get(runtimeTarget).SharedLibrarySuffix;
        foreach (string file in Directory.EnumerateFiles(pack))
            if (Path.GetFileName(file) is var name && !name.Equals("aquarius" + Exe, StringComparison.OrdinalIgnoreCase)
                && !name.Equals("aquarius-aot" + Exe, StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(name.Contains(suffix, StringComparison.OrdinalIgnoreCase) ? directory : resources, name), true);
        foreach (string folder in Directory.EnumerateDirectories(pack))
            if (Path.GetFileName(folder) is var name && name != "aot")
                Output.CopyDirectory(folder, Path.Combine(name == "libraries" ? directory : resources, name));
        foreach (var (library, binary) in libraries) {
            Directory.CreateDirectory(Path.Combine(directory, "libraries"));
            File.Copy(binary!, Path.Combine(directory, "libraries", Library.NativeFile(library, runtimeTarget)), true);
        }
    }

    /// <summary>A macOS app bundle: Contents/MacOS/&lt;Name&gt; (the unmodified launcher, the pack's files, external
    /// libraries and the --optimize library), Contents/Resources/application.aquarius (the overlay), Contents/Info.plist,
    /// signed ad hoc inside out so <c>codesign --verify --strict</c> accepts it.</summary>
    private void BuildApp(WasmBuildRequest request, string output, string entry, string template, string pack, bool optimize,
        (string Name, string? Binary)[] libraries)
    {
        string name = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(output));
        if (name.Length == 0 || name.IndexOfAny(['/', '\\', ':']) >= 0) throw new ArgumentException("macOS applications need a name before .app.");
        Output.ReplaceDirectory(output, AppMarker, app => {
            string macos = Path.Combine(app, "Contents", "MacOS"), resources = Path.Combine(app, "Contents", "Resources");
            Directory.CreateDirectory(macos); Directory.CreateDirectory(resources);
            string executable = Path.Combine(macos, name);
            File.Copy(template, executable);
            using (var wasm = File.OpenRead(request.Input)) using (var payload = new FileStream(Path.Combine(app, AppPayload), FileMode.CreateNew))
                ExecutableBundle.WritePayload(wasm, payload, entry);
            File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), InfoPlist(name));
            if (optimize) AheadOfTime.BuildLibrary(request.Input, executable, runtimeTarget, pack);
            CopyPack(pack, macos, resources, libraries);
            // Nested code first, then the bundle, which seals Contents/Resources (the overlay) into its signature.
            foreach (string file in Directory.EnumerateFiles(macos, "*.dylib", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                Output.Run("codesign", ["--force", "--sign", "-", "--timestamp=none", file]);
            Output.Run("codesign", ["--force", "--sign", "-", "--timestamp=none", app]);
        });
    }

    /// <summary>The bundle's Info.plist: its launcher, a bundle identifier made from its name, and Retina support.</summary>
    public static string InfoPlist(string name)
    {
        string identifier = "org.aquariuslang.app." + new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray());
        string Xml(string text) => System.Security.SecurityElement.Escape(text);
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleExecutable</key><string>{Xml(name)}</string>
              <key>CFBundleIdentifier</key><string>{Xml(identifier)}</string>
              <key>CFBundleName</key><string>{Xml(name)}</string>
              <key>CFBundlePackageType</key><string>APPL</string>
              <key>CFBundleShortVersionString</key><string>1.0</string>
              <key>CFBundleVersion</key><string>1</string>
              <key>LSMinimumSystemVersion</key><string>13.0</string>
              <key>NSHighResolutionCapable</key><true/>
            </dict>
            </plist>

            """;
    }

    /// <summary>Marks the executable as a Windows GUI program, so it starts without a console window.</summary>
    private static void WindowsGui(Stream executable)
    {
        executable.Position = 0;
        try {
            using var reader = new PEReader(executable, PEStreamOptions.LeaveOpen);
            var headers = reader.PEHeaders;
            if (headers.PEHeader == null || headers.PEHeader.Subsystem is not (Subsystem.WindowsCui or Subsystem.WindowsGui))
                throw new InvalidDataException("The Windows runtime launcher must be a console or GUI PE executable.");
            // The subsystem is a 16-bit field at offset 68 of both PE32 and PE32+ optional headers.
            executable.Position = headers.PEHeaderStartOffset + 68;
            Span<byte> subsystem = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(subsystem, (ushort)Subsystem.WindowsGui);
            executable.Write(subsystem);
        } catch (BadImageFormatException error) {
            throw new InvalidDataException("The Windows runtime launcher is not a valid PE executable.", error);
        }
    }
}
