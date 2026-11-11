namespace AquariusLang.Build.Libraries.Platforms;

/// <summary>Windows: a DLL with MinGW and MSVC import libraries, a static library, an object file.</summary>
public sealed class WindowsLibraryPlatform : INativeLibraryPlatform
{
    public string Platform => "windows";
    public IReadOnlyList<string> Kinds => ["shared", "static", "object"];
    public IReadOnlyList<string> Bindings => ["c", "cpp", "rust", "java"];
    public IReadOnlyList<string> DefaultBindings => ["c", "cpp", "rust"];
    public IReadOnlyList<string> Packages => ["folder"];
    public IReadOnlyList<string> ManifestKeys => ["import_libraries", "java_package", "java_class"];
    public bool Builds => true;
    /// <summary>The import libraries a Windows DLL can come with.</summary>
    public static readonly IReadOnlyList<string> ImportLibraries = ["mingw", "msvc"];
}

/// <summary>Linux: a shared object with a SONAME, a static library, an object file.</summary>
public sealed class LinuxLibraryPlatform : INativeLibraryPlatform
{
    public string Platform => "linux";
    public IReadOnlyList<string> Kinds => ["shared", "static", "object"];
    public IReadOnlyList<string> Bindings => ["c", "cpp", "rust", "java"];
    public IReadOnlyList<string> DefaultBindings => ["c", "cpp", "rust"];
    public IReadOnlyList<string> Packages => ["folder"];
    public IReadOnlyList<string> ManifestKeys => ["java_package", "java_class"];
    public bool Builds => true;
}

/// <summary>macOS: a dylib or a framework, with a Swift binding (phase 5).</summary>
public sealed class MacOSLibraryPlatform : INativeLibraryPlatform
{
    public string Platform => "macos";
    public IReadOnlyList<string> Kinds => ["shared", "static", "object"];
    public IReadOnlyList<string> Bindings => ["c", "cpp", "rust", "swift", "java"];
    public IReadOnlyList<string> DefaultBindings => ["c", "cpp", "rust", "swift"];
    public IReadOnlyList<string> Packages => ["folder", "framework"];
    public IReadOnlyList<string> ManifestKeys => ["module", "java_package", "java_class"];
    public bool Builds => false;
}
