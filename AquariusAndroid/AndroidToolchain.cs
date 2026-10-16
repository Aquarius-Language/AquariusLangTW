using System.Diagnostics;

namespace AquariusLang.Android;

public static class AndroidToolchain
{
    public static void Build(string project)
    {
        string root = Path.Combine(project, "app/src/main");
        if (new[] { "arm64-v8a", "x86_64" }.Any(abi =>
                new[] { "libaquarius_android.so", "libaquarius_runtime.so", "libwasmtime.so", "libwgpu_native.so", "libc++_shared.so" }.Any(name => !File.Exists(Path.Combine(root, "jniLibs", abi, name)))))
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
            CreateNoWindow = true
        };
        if (start.FileName == "cmd.exe") { start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add(gradle); }
        start.ArgumentList.Add("--no-daemon"); start.ArgumentList.Add("--console=plain"); start.ArgumentList.Add(":app:assembleDebug");
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Unable to start Gradle.");
            if (!process.WaitForExit(600000)) { process.Kill(entireProcessTree: true); throw new IOException("Android build timed out."); }
            if (process.ExitCode != 0) throw new IOException($"Android APK build failed (Gradle exit {process.ExitCode}). Check JAVA_HOME and ANDROID_HOME.");
        }
        catch (System.ComponentModel.Win32Exception e) { throw new IOException("Android APK builds require JDK 17+, Android SDK, and Gradle. Project generation requires no Android tools.", e); }
    }
}
