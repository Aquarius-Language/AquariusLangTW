using System.Security.Cryptography;
using System.Text.Json;
using AquariusLang.Packaging;
using AquariusLang.Desktop.runtime;

namespace AquariusTests.Packaging;

public class CudaRuntimeBundleTest {
    private static void Payload(ApplicationTestDirectory t, string? omit = null, string? extra = null) {
        string[] names = { "cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll", "cudnn64_9.dll", "cudnn_ops64_9.dll", "cudnn_cnn64_9.dll", "cudnn_graph64_9.dll", "cudnn_engines_precompiled64_9.dll", "nvrtc64_130_0.dll", "nvrtc-builtins64_134.dll",
            "licenses/onnx-cuda/runtime.txt", "licenses/onnx-cuda/cublas.txt", "licenses/onnx-cuda/cudnn.txt", "licenses/onnx-cuda/nvrtc.txt" };
        var files = names.Where(n => n != omit).Select(n => {
            string path = t.Write(n, "fixture " + n); byte[] bytes = File.ReadAllBytes(path);
            return new { path = n, bytes = (long)bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        }).ToList();
        if (extra != null) files.Add(new { path = extra, bytes = 1L, sha256 = new string('0', 64) });
        t.Write(CudaRuntimeBundle.ManifestName, JsonSerializer.Serialize(new { format = "aquarius-cuda-runtime", version = 1, runtimeIdentifier = "win-x64", files }));
    }
    [Fact] public void CompletePayloadVerifiesWithoutLoadingGpuLibrariesAndDetectsCorruption() {
        using var t = new ApplicationTestDirectory(); Payload(t);
        CudaRuntimeBundle.Validate(t.FilePath("."), "win-x64");
        File.WriteAllText(t.FilePath("cublasLt64_13.dll"), "corrupt");
        Assert.Throws<InvalidDataException>(() => CudaRuntimeBundle.Validate(t.FilePath("."), "win-x64"));
    }
    [Theory]
    [InlineData("cudnn64_9.dll")]
    [InlineData("licenses/onnx-cuda/cudnn.txt")]
    public void PayloadMustIncludeDependenciesAndLicenseNotices(string omit) {
        using var t = new ApplicationTestDirectory(); Payload(t, omit);
        Assert.Contains("required NVIDIA", Assert.Throws<InvalidDataException>(() => CudaRuntimeBundle.Validate(t.FilePath("."), "win-x64")).Message);
    }
    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("C:/outside.dll")]
    [InlineData("cublasLt64_13.dll")]
    public void RejectsEscapingPathsAndDuplicateEntries(string entry) {
        using var t = new ApplicationTestDirectory(); Payload(t, extra: entry);
        Assert.Throws<InvalidDataException>(() => CudaRuntimeBundle.Validate(t.FilePath("."), "win-x64"));
    }
}
