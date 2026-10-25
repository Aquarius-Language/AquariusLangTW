using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using AquariusLang.Compiler;
using AquariusLang.Libraries;

namespace AquariusLang.Build.Targets;

/// <summary>
/// A desktop executable: the runtime pack's launcher (aquarius.exe / aquarius) with the application appended as an
/// overlay (<see cref="ExecutableBundle"/>). The files the pack installs beside its launcher (DLLs default libraries
/// load, licenses) are copied beside the executable, and external libraries into its libraries/ folder, where the
/// runtime looks for them.
/// </summary>
public sealed class ExecutableTarget(string name, string runtimeTarget) : IWasmBuildTarget
{
    public const string HideTerminalOption = "--hide-terminal";
    /// <summary>Selects the runtime pack built with ONNX Runtime's CUDA build (&lt;target&gt;-cuda: the CUDA and TensorRT
    /// providers, about 300 MB); projects set [onnx] cuda = true.</summary>
    public const string OnnxCudaOption = "--onnx-cuda";

    public string Name => name;
    public string RuntimeTarget => runtimeTarget;
    public string ArtifactDescription => "Executable";
    private bool Windows => runtimeTarget.StartsWith("windows-", StringComparison.Ordinal);
    private string Launcher => Windows ? "aquarius.exe" : "aquarius";
    public IReadOnlyCollection<string> OptionNames => Windows ? [HideTerminalOption, OnnxCudaOption] : [OnnxCudaOption];

    public void Build(WasmBuildRequest request)
    {
        bool hideTerminal = request.Flag(HideTerminalOption), cuda = request.Flag(OnnxCudaOption);
        if (hideTerminal && !Windows) throw new ArgumentException($"{HideTerminalOption} applies only to Windows executables.");
        string output = Path.GetFullPath(request.Output);
        if (Windows && !output.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Windows executables must be named *.exe.");
        var (application, entry) = Output.Read(request, name);
        var libraries = Output.NeededLibraries(application, request)
            .Select(l => (l.Name, Binary: Output.ExternalBinary(l.Name, l.Library, runtimeTarget))).Where(l => l.Binary != null).ToArray();

        var installation = AquariusInstallation.Require();
        string packTarget = runtimeTarget + (cuda ? "-cuda" : "");
        string pack = installation.RequireRuntimePack(packTarget, Launcher);
        string template = Path.Combine(pack, Launcher);
        if (output.Equals(template, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The output must not replace the runtime pack's launcher.");

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

        // The pack's other files (libraries' DLLs and shared objects, licenses) run beside the launcher.
        string directory = Path.GetDirectoryName(output)!;
        foreach (string file in Directory.EnumerateFiles(pack))
            if (!Path.GetFileName(file).Equals(Launcher, StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
        foreach (string folder in Directory.EnumerateDirectories(pack)) Output.CopyDirectory(folder, Path.Combine(directory, Path.GetFileName(folder)));
        foreach (var (library, binary) in libraries) {
            Directory.CreateDirectory(Path.Combine(directory, "libraries"));
            File.Copy(binary!, Path.Combine(directory, "libraries", Library.NativeFile(library, runtimeTarget)), true);
        }
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
