using AquariusLang.Desktop.Onnx;

namespace AquariusTests.Onnx;

public class WindowsCudaLibrariesTest {
    [Fact]
    public void FindsPortableSidecarsAndSplitNvidiaInstallationsWithoutSearchingWorkingDirectory() {
        string root = Path.Combine(Path.GetTempPath(), "aquarius-cuda-" + Guid.NewGuid().ToString("N"));
        try {
            string exe = Path.Combine(root, "portable app");
            string sidecar = Path.Combine(exe, "onnx-providers", "cuda");
            string toolkit = Path.Combine(root, "nvidia", "cu13", "bin", "x86_64");
            string cudnn = Path.Combine(root, "nvidia", "cudnn", "bin");
            foreach (var (directory, file) in new[] { (sidecar, "cublasLt64_13.dll"), (toolkit, "cudart64_13.dll"), (cudnn, "cudnn64_9.dll") }) {
                Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, file), Array.Empty<byte>());
            }
            Assert.Equal(new[] { sidecar, toolkit, cudnn }, WindowsCudaLibraries.FindDirectories(new[] { "relative", exe, exe, Path.Combine(root, "nvidia"), toolkit }));
            Assert.Empty(WindowsCudaLibraries.FindDirectories(new[] { Path.Combine(root, "missing"), root }));
        } finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
