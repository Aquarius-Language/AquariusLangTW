using AquariusLang.Object;
using AquariusLang.Packaging;
using AquariusLang.Compiler;
using AquariusLang.Wasm;
using System.Runtime.InteropServices;
using System.Text.Json;
using AquariusLang.Desktop.runtime;

var outputEncoding = new System.Text.UTF8Encoding(false);
try { Console.OutputEncoding = outputEncoding; }
catch (IOException) when (OperatingSystem.IsWindows()) {
    // A GUI-subsystem process has no console code page. Standard handles can
    // still be redirected by a launcher, so configure their UTF-8 writers directly.
    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), outputEncoding) { AutoFlush = true });
    Console.SetError(new StreamWriter(Console.OpenStandardError(), outputEncoding) { AutoFlush = true });
}
try {
    string path = System.Environment.ProcessPath ?? throw new IOException("Cannot locate the application executable.");
    using var stream = File.OpenRead(path);
    if (args is ["--runtime-info"] && !ExecutableBundle.HasProgram(stream)) {
        string rid = RuntimeInformation.RuntimeIdentifier;
        string[] required = OperatingSystem.IsLinux() ? new[] {
            "libwgpu_native.so", "libwasmtime.so", $"ClearScriptV8.{rid}.so", "Magick.Native-Q8-x64.dll.so",
            "libonnxruntime.so", "libonnxruntime_providers_webgpu.so",
            $"runtimes/{rid}/native/libaquarius_graphics.so", $"runtimes/{rid}/native/libaquarius_audio.so"
        } : new[] {
            "wgpu_native.dll", "wasmtime.dll", "ClearScriptV8.win-x64.dll", "Magick.Native-Q8-x64.dll", "vcruntime140.dll",
            "onnxruntime.dll", "onnxruntime_providers_webgpu.dll",
            "runtimes/win-x64/native/aquarius_graphics.dll", "runtimes/win-x64/native/aquarius_audio.dll"
        };
        string[] missing = required.Where(file => !File.Exists(Path.Combine(AppContext.BaseDirectory, file))).ToArray();
        if (missing.Length != 0) throw new IOException("Application runtime dependencies are missing: " + string.Join(", ", missing));
        // Packs published with AquariusOnnxCuda=true carry ORT's CUDA/TensorRT provider libraries.
        bool onnxCuda = File.Exists(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsLinux() ? "libonnxruntime_providers_cuda.so" : "onnxruntime_providers_cuda.dll"));
        if (onnxCuda) CudaRuntimeBundle.Validate(AppContext.BaseDirectory, rid);
        Console.WriteLine(JsonSerializer.Serialize(new {
            format = "aquarius-runtime-pack", version = 1, runtimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            bundleVersion = ExecutableBundle.FormatVersion, wasmAbiVersion = WasmAbi.Version, onnxCuda, onnxCudaRuntime = onnxCuda,
            hideTerminal = OperatingSystem.IsWindows()
        }));
        return;
    }
    var program = ExecutableBundle.Load(stream);
    System.Environment.ExitCode = ScriptRunner.RunPackage(program.Package, path, program.EntryPoint, args) is ErrorObj ? 1 : 0;
} catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException) {
    Console.Error.WriteLine(error.Message);
    System.Environment.ExitCode = 1;
}
