using System.Diagnostics;

namespace AquariusLang.Android;

public static class AndroidToolchain
{
    /// <summary>Native libraries every generated project ships for the Aquarius runtime (per ABI). Applications cannot
    /// install libraries with these names, whether or not this compiler embeds the prebuilt runtime binaries.</summary>
    public static IReadOnlyList<string> RuntimeLibraries { get; } = new[] { "libaquarius_android.so", "libaquarius_runtime.so", "libwasmtime.so", "libwgpu_native.so", "libc++_shared.so" };

    public static void Build(string project)
    {
        string root = Path.Combine(project, "app/src/main");
        if (new[] { "arm64-v8a", "x86_64" }.Any(abi =>
                RuntimeLibraries.Any(name => !File.Exists(Path.Combine(root, "jniLibs", abi, name)))))
            throw new IOException("Android native runtime assets are missing. Build the compiler after running scripts/prepare-android.ps1.");
        string? gradle = Environment.GetEnvironmentVariable("AQUARIUS_GRADLE");
        gradle ??= Path.Combine(project, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");
        if (!OperatingSystem.IsWindows() && Path.GetFileName(gradle) == "gradlew")
            File.SetUnixFileMode(gradle, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() && gradle.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ? "cmd.exe" : gradle,
            WorkingDirectory = project,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (start.FileName == "cmd.exe") { start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add(gradle); }
        start.ArgumentList.Add("--no-daemon"); start.ArgumentList.Add("--console=plain"); start.ArgumentList.Add(":app:assembleDebug");
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Unable to start Gradle.");
            // Drain both pipes while Gradle runs: compiler callers may not have an attached
            // console, and dependency downloads can otherwise fill a redirected pipe.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(600000)) { process.Kill(entireProcessTree: true); throw new IOException("Android build timed out."); }
            string diagnostics = stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0) {
                const int maximumDiagnosticCharacters = 16384;
                if (diagnostics.Length > maximumDiagnosticCharacters) diagnostics = diagnostics[^maximumDiagnosticCharacters..];
                throw new IOException($"Android APK build failed (Gradle exit {process.ExitCode}). Check JAVA_HOME (JDK 17 or 21) and ANDROID_HOME.\n{diagnostics.Trim()}");
            }
        }
        catch (System.ComponentModel.Win32Exception e) { throw new IOException("Android APK builds require JDK 17+, Android SDK, and Gradle. Project generation requires no Android tools.", e); }
    }
}
