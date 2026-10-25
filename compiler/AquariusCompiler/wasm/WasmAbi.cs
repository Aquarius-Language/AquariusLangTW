namespace AquariusLang.Wasm;

/// <summary>The application ABI between compiled modules and the native runtime (runtime/host/application.c).</summary>
public static class WasmAbi
{
    /// <summary>Metadata format written by this compiler: 4 is the ABI 3 binary metadata plus the import table of bundled
    /// library modules and the native libraries the application needs.</summary>
    public const int Version = 4;
    /// <summary>The oldest metadata format this compiler still reads (applications it exports to other targets).</summary>
    public const int OldestVersion = 3;
    public const string ImportModule = "aquarius_v2";
    public const string MetadataSection = "aquarius.application";
}
