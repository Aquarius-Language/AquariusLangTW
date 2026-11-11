namespace AquariusLang.Build.Libraries.Platforms;

/// <summary>iOS: an xcframework of the device and simulator builds, with a Swift binding (phase 5).</summary>
public sealed class IosLibraryPlatform : INativeLibraryPlatform
{
    public string Platform => "ios";
    public IReadOnlyList<string> Kinds => ["shared", "static"];
    public IReadOnlyList<string> Bindings => ["c", "swift"];
    public IReadOnlyList<string> DefaultBindings => ["c", "swift"];
    public IReadOnlyList<string> Packages => ["xcframework", "folder"];
    public IReadOnlyList<string> ManifestKeys => ["module"];
    public bool Builds => false;
}

/// <summary>Android: a shared library per ABI with a JNI Java class, packaged as an .aar (phase 4).</summary>
public sealed class AndroidLibraryPlatform : INativeLibraryPlatform
{
    public string Platform => "android";
    public IReadOnlyList<string> Kinds => ["shared", "static"];
    public IReadOnlyList<string> Bindings => ["c", "java"];
    public IReadOnlyList<string> DefaultBindings => ["c", "java"];
    public IReadOnlyList<string> Packages => ["aar", "folder"];
    public IReadOnlyList<string> ManifestKeys => ["java_package", "java_class"];
    public bool Builds => false;
}

/// <summary>The web: the application's .wasm, loaded by the glue's loadLibrary(), with its .d.ts.</summary>
public sealed class WebLibraryPlatform : INativeLibraryPlatform
{
    public string Platform => "web";
    public IReadOnlyList<string> Kinds => ["shared"];
    public IReadOnlyList<string> Bindings => ["javascript"];
    public IReadOnlyList<string> DefaultBindings => ["javascript"];
    public IReadOnlyList<string> Packages => ["folder"];
    public IReadOnlyList<string> ManifestKeys => [];
    public bool Builds => true;
}
