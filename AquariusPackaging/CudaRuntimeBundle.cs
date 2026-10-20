using System.Security.Cryptography;
using System.Text.Json;

namespace AquariusLang.Packaging;

/// <summary>The redistributable CUDA inference payload embedded by a CUDA-enabled runtime pack.</summary>
public static class CudaRuntimeBundle {
    public const string ManifestName = "aquarius-cuda-runtime.json";
    private sealed record Payload(string Format, int Version, string RuntimeIdentifier, PayloadFile[] Files);
    private sealed record PayloadFile(string Path, long Bytes, string Sha256);

    /// <summary>Validate the extracted payload without loading CUDA or requiring an NVIDIA driver.</summary>
    public static void Validate(string directory, string runtimeIdentifier) {
        string manifestPath = Path.Combine(directory, ManifestName);
        if (!File.Exists(manifestPath)) throw new InvalidDataException("CUDA runtime payload is missing. Republish the CUDA runtime pack with scripts/publish-apphost.");
        if (new FileInfo(manifestPath).Length > 256 * 1024) throw new InvalidDataException("CUDA runtime payload manifest is too large.");
        Payload? payload;
        try { payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException e) { throw new InvalidDataException("Invalid CUDA runtime payload manifest.", e); }
        if (payload == null || payload.Format != "aquarius-cuda-runtime" || payload.Version != 1 || payload.RuntimeIdentifier != runtimeIdentifier || payload.Files is not { Length: > 0 and <= 128 })
            throw new InvalidDataException("Incompatible CUDA runtime payload.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in payload.Files) {
            if (file == null || string.IsNullOrEmpty(file.Path) || file.Path.Contains('\\') || file.Path.StartsWith('/') ||
                file.Path.Split('/').Any(p => p is "" or "." or ".." || p.Contains(':')) || !names.Add(file.Path) || file.Bytes <= 0 ||
                file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("Invalid CUDA runtime payload file entry.");
            string path = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes) throw new InvalidDataException("Missing or incomplete CUDA runtime file: " + file.Path);
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("CUDA runtime checksum mismatch: " + file.Path);
        }
        string[] required = runtimeIdentifier == "win-x64" ? new[] { "cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll", "cudnn64_9.dll", "cudnn_ops64_9.dll", "cudnn_cnn64_9.dll", "cudnn_graph64_9.dll", "cudnn_engines_precompiled64_9.dll", "nvrtc64_130_0.dll" }
            : runtimeIdentifier == "linux-x64" ? new[] { "libcudart.so.13", "libcublas.so.13", "libcublasLt.so.13", "libcudnn.so.9", "libcudnn_ops.so.9", "libcudnn_cnn.so.9", "libcudnn_graph.so.9", "libcudnn_engines_precompiled.so.9", "libnvrtc.so.13" }
            : throw new InvalidDataException("Unsupported CUDA runtime platform.");
        if (required.Any(name => !names.Contains(name)) || !names.Any(n => n.StartsWith(runtimeIdentifier == "win-x64" ? "nvrtc-builtins64_" : "libnvrtc-builtins.so.", StringComparison.Ordinal)) ||
            names.Count(n => n.StartsWith("licenses/onnx-cuda/", StringComparison.Ordinal)) < 4)
            throw new InvalidDataException("CUDA runtime payload lacks required NVIDIA libraries or license notices.");
    }
}
