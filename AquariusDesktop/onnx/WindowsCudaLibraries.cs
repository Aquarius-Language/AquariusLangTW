namespace AquariusLang.Desktop.Onnx;

/// <summary>Find CUDA's separately distributed DLLs before ORT loads its CUDA provider bridge.</summary>
internal static class WindowsCudaLibraries {
    private static readonly object Gate = new();
    private static bool configured;

    internal static void EnsureSearchPath() {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate) {
            if (configured) return;
            var roots = new List<string>();
            if (System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_CUDA_PATH") is { Length: > 0 } extra)
                roots.AddRange(extra.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            // Single-file apps extract ORT to AppContext.BaseDirectory, while user-supplied DLLs
            // live beside the original executable. Never resolve libraries from the working directory.
            if (System.Environment.ProcessPath is { } exe && Path.GetDirectoryName(exe) is { } directory) roots.Add(directory);
            roots.Add(AppContext.BaseDirectory);
            if (System.Environment.GetEnvironmentVariable("CUDA_PATH") is { Length: > 0 } toolkit) roots.Add(toolkit);
            var directories = FindDirectories(roots);
            if (directories.Count != 0) {
                // ORT uses LoadLibraryEx(LOAD_WITH_ALTERED_SEARCH_PATH); PATH also covers cuDNN's
                // delayed component loads. Change only this process, leaving Windows settings alone.
                string previous = System.Environment.GetEnvironmentVariable("PATH") ?? "";
                System.Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, directories) + Path.PathSeparator + previous);
            }
            configured = true;
        }
    }

    internal static IReadOnlyList<string> FindDirectories(IEnumerable<string> roots) {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots) {
            try {
                if (!Path.IsPathFullyQualified(root)) continue;
                foreach (string relative in new[] { "onnx-providers/cuda", "", "bin/x86_64", "bin", "cu13/bin/x86_64", "cudnn/bin" }) {
                    string directory = Path.GetFullPath(Path.Combine(root, relative));
                    if (seen.Add(directory) && new[] { "cublasLt64_13.dll", "cudnn64_9.dll", "cudart64_13.dll", "nvrtc64_130_0.dll" }
                        .Any(file => File.Exists(Path.Combine(directory, file)))) result.Add(directory);
                }
            } catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) {
                // An invalid optional installation path must not prevent CPU/WebGPU from starting.
            }
        }
        return result.AsReadOnly();
    }
}
