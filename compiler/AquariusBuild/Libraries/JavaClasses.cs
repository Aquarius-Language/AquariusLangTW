namespace AquariusLang.Build.Libraries;

/// <summary>Compiles the Java binding (JavaBinding's sources) into a jar with a JDK: JAVA_HOME, else the JDK pinned in
/// scripts/toolchain.json (scripts/install-toolchain.ps1 -Tools jdk). Java 11 bytecode, which Android's d8 and every
/// current JVM read.</summary>
internal static class JavaClasses
{
    public static string Home() => Targets.AndroidTools.JavaHome()
        ?? throw new IOException("The Java binding needs a JDK: set JAVA_HOME (JDK 17 or newer), or run scripts/install-toolchain.ps1 -Tools jdk in a checkout.");

    public static void Compile(string jdk, string sources, string jar)
    {
        string exe = OperatingSystem.IsWindows() ? ".exe" : "";
        string classes = Path.Combine(Path.GetTempPath(), "aquarius-java-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(classes);
        try {
            var files = Directory.EnumerateFiles(sources, "*.java", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            Output.Run(Path.Combine(jdk, "bin", "javac" + exe), ["--release", "11", "-encoding", "UTF-8", "-d", classes, .. files]);
            Directory.CreateDirectory(Path.GetDirectoryName(jar)!);
            Output.Run(Path.Combine(jdk, "bin", "jar" + exe), ["cf", jar, "-C", classes, "."]);
        } finally {
            try { Directory.Delete(classes, true); } catch (IOException) { }
        }
    }
}
