using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using AquariusLang.Packaging;
using AquariusLang.Compiler;
using AquariusLang.Wasm;

namespace AquariusLang.Build;

/// <summary>Package a wasm with an installed runtime pack. No SDK, network, or source compilation is needed.</summary>
public sealed class SelfContainedExecutableTarget(string name, string runtimeIdentifier, string extension, string? runtimePacksDirectory = null) : IWasmBuildTarget {
    public string Name => name;
    public string ArtifactDescription => "Executable";
    /// <summary>"true" selects the opt-in NVIDIA CUDA/TensorRT runtime pack (<c>RID-cuda</c>); projects set it with
    /// <c>[onnx] cuda = true</c> in aqua.toml. Only RIDs with an ONNX Runtime CUDA build accept it.</summary>
    public const string OnnxCudaOption = "--onnx-cuda";
    public const string HideTerminalOption = "--hide-terminal";
    public IReadOnlyCollection<string> OptionNames =>
        (SupportsOnnxCuda ? new[] { OnnxCudaOption } : Array.Empty<string>())
        .Concat(SupportsHideTerminal ? new[] { HideTerminalOption } : Array.Empty<string>()).ToArray();
    public bool SupportsOnnxCuda => runtimeIdentifier is "win-x64" or "linux-x64";
    public bool SupportsHideTerminal => runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal);
    private sealed record RuntimeManifest(string Format, int Version, string RuntimeIdentifier, int BundleVersion,
        int WasmAbiVersion, string TemplateSha256, bool OnnxCuda = false, bool OnnxCudaRuntime = false, bool HideTerminal = false);
    /// <summary>Parses the option; absent means the standard pack.</summary>
    public static bool OnnxCudaRequested(IReadOnlyDictionary<string, string>? options) {
        return BooleanOption(options, OnnxCudaOption);
    }
    private static bool BooleanOption(IReadOnlyDictionary<string, string>? options, string name) {
        if (options == null || !options.TryGetValue(name, out var value)) return false;
        return value.ToLowerInvariant() switch {
            "true" or "on" or "yes" or "1" => true, "false" or "off" or "no" or "0" => false,
            _ => throw new ArgumentException($"{name} expects true or false.")
        };
    }

    public void Build(WasmBuildRequest request) {
        bool hideTerminal = BooleanOption(request.Options, HideTerminalOption);
        if (hideTerminal && !SupportsHideTerminal) throw new ArgumentException($"{HideTerminalOption} is only supported for Windows executables.");
        string input = Path.GetFullPath(request.Input), output = Path.GetFullPath(request.Output);
        if (!input.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Executable builds require one .wasm input.");
        if (extension.Length != 0 && !output.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{Name} output must name an {extension} file.");
        if (input.Equals(output, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must differ from the input wasm.");
        if (Directory.Exists(output) || (File.Exists(output) && (File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0))
            throw new ArgumentException("Executable output must be a regular file, not a directory or link.");
        // Read and validate the input before looking for a pack or touching output.
        using var wasm = File.OpenRead(input);
        var package = WasmApplication.Load(wasm);
        package.CheckPlatform(Name);
        package.ResolveScript(request.EntryPoint ?? package.EntryPoint);
        bool onnxCuda = OnnxCudaRequested(request.Options);
        if (onnxCuda && !SupportsOnnxCuda) throw new ArgumentException($"{Name} executables do not support ONNX CUDA; it is available for win-x64 and linux-x64.");
        string root = runtimePacksDirectory ?? System.Environment.GetEnvironmentVariable("AQUARIUS_BUILD_TARGETS") ?? Path.Combine(AppContext.BaseDirectory, "build-targets");
        // CUDA is opt-in: its provider libraries add ~190-270 MB, so it lives in a separate pack flavour.
        string packName = runtimeIdentifier + (onnxCuda ? "-cuda" : "");
        string pack = Path.Combine(root, packName);
        string manifestPath = Path.Combine(pack, "runtime.json"), templatePath = Path.Combine(pack, "host" + extension);
        string script = runtimeIdentifier.StartsWith("linux-") ? "sh" + (onnxCuda ? " --onnx-cuda" : "") : "ps1" + (onnxCuda ? " -OnnxCuda" : "");
        if (!File.Exists(manifestPath) || !File.Exists(templatePath))
            throw new IOException($"Runtime pack {packName} is missing from {Path.GetFullPath(pack)}. " + (onnxCuda
                ? $"ONNX CUDA was requested ([onnx] cuda = true or {OnnxCudaOption} true); install an aqua distribution that includes the CUDA pack, or run scripts/publish-apphost.{script} when building from source."
                : $"Install the complete aqua distribution, or run scripts/publish-apphost.{script} when building from source."));
        RuntimeManifest? manifest;
        if (new FileInfo(manifestPath).Length > 64 * 1024) throw new InvalidDataException("Executable runtime manifest exceeds the size limit.");
        try { manifest = JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException error) { throw new InvalidDataException("Invalid executable runtime pack manifest.", error); }
        if (manifest == null || manifest.Format != "aquarius-runtime-pack" || manifest.Version != 1 ||
            manifest.RuntimeIdentifier != runtimeIdentifier || manifest.BundleVersion != ExecutableBundle.FormatVersion ||
            manifest.WasmAbiVersion != WasmAbi.Version)
            throw new InvalidDataException("Incompatible executable runtime pack. Rebuild or reinstall the pack with this compiler version.");
        if (hideTerminal && !manifest.HideTerminal)
            throw new InvalidDataException($"Runtime pack {packName} does not support {HideTerminalOption}. Rebuild it with scripts/publish-apphost.{script} or install an updated aqua distribution.");
        if (manifest.OnnxCuda != onnxCuda)
            throw new InvalidDataException(onnxCuda ? $"Runtime pack {packName} does not contain the ONNX CUDA providers. Republish it with scripts/publish-apphost.{script}."
                : $"Runtime pack {packName} is a CUDA flavour; publish the standard pack with scripts/publish-apphost.{script}.");
        if (onnxCuda && !manifest.OnnxCudaRuntime)
            throw new InvalidDataException($"Runtime pack {packName} lacks the bundled NVIDIA CUDA runtime libraries. Republish it with scripts/publish-apphost.{script}; CUDA executables must include their runtime dependencies.");
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
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.ReadWrite)) {
                ExecutableBundle.Write(template, wasm, stream, request.EntryPoint);
                if (hideTerminal) HideWindowsTerminal(stream);
                stream.Flush(flushToDisk: true);
            }
            // A newly created overlay otherwise has no execute bits on Unix.
            // Set them before the atomic replacement so a failed chmod preserves output.
            if (!OperatingSystem.IsWindows() && runtimeIdentifier.StartsWith("linux-"))
                File.SetUnixFileMode(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(stage, output, overwrite: true);
        } finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    private static void HideWindowsTerminal(Stream executable) {
        // Set the PE subsystem before publishing the staged file. Windows then starts
        // the app without allocating a console, including when launched from Explorer.
        // The shared runtime template and the embedded Wasm/checksum stay unchanged.
        executable.Position = 0;
        try {
            using var reader = new PEReader(executable, PEStreamOptions.LeaveOpen);
            var headers = reader.PEHeaders;
            if (headers.PEHeader == null || headers.PEHeader.Subsystem is not (Subsystem.WindowsCui or Subsystem.WindowsGui))
                throw new InvalidDataException("The Windows runtime template must be a console or GUI PE executable.");
            // Subsystem is a 16-bit field at offset 68 in both PE32 and PE32+ optional headers.
            executable.Position = headers.PEHeaderStartOffset + 68;
            Span<byte> subsystem = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(subsystem, (ushort)Subsystem.WindowsGui);
            executable.Write(subsystem);
        } catch (BadImageFormatException error) {
            throw new InvalidDataException("Invalid Windows PE runtime template for --hide-terminal.", error);
        }
    }
}
