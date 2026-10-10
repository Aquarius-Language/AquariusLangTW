using AquariusLang.Object;
using AquariusLang.Packaging;
using AquariusLang.VM;
using System.Runtime.InteropServices;
using System.Text.Json;
using AquariusREPL.runtime;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
try {
    string path = System.Environment.ProcessPath ?? throw new IOException("Cannot locate the application executable.");
    using var stream = File.OpenRead(path);
    if (args is ["--runtime-info"] && !ExecutableBundle.HasProgram(stream)) {
        string[] required = {
            "wgpu_native.dll", "joltc.dll", "joltc_double.dll", "Magick.Native-Q8-x64.dll", "vcruntime140.dll",
            "runtimes/win-x64/native/aquarius_graphics.dll"
        };
        string[] missing = required.Where(file => !File.Exists(Path.Combine(AppContext.BaseDirectory, file))).ToArray();
        if (missing.Length != 0) throw new IOException("Application runtime dependencies are missing: " + string.Join(", ", missing));
        Console.WriteLine(JsonSerializer.Serialize(new {
            format = "aquarius-runtime-pack", version = 1, runtimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            bundleVersion = ExecutableBundle.FormatVersion, bottleVersion = BottlePackage.FormatVersion,
            bytecodeVersion = BytecodeSerializer.FormatVersion
        }));
        return;
    }
    var program = ExecutableBundle.Load(stream);
    System.Environment.ExitCode = ScriptRunner.RunPackage(program.Package, path, program.EntryPoint, args) is ErrorObj ? 1 : 0;
} catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException) {
    Console.Error.WriteLine(error.Message);
    System.Environment.ExitCode = 1;
}
