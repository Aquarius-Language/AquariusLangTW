namespace AquariusLang.Wasm;

/// <summary>The application ABI between compiled modules and the native runtime (runtime/host/application.c).</summary>
public static class WasmAbi
{
    /// <summary>Metadata format written by this compiler: 4 is the ABI 3 binary metadata plus the import table of bundled
    /// library modules and the native libraries the application needs.</summary>
    public const int Version = 4;
    /// <summary>The oldest metadata format this compiler still reads (applications it exports to other targets).</summary>
    public const int OldestVersion = 3;
    /// <summary>The value ABI of the guest runtime this compiler embeds: 3 packs typed number and Boolean arrays (their
    /// storage's layout word names the element type); 2 is the runtime before that, whose arrays all hold whole Values.
    /// Hosts accept both and read array elements by the application's value ABI; older hosts reject 3.</summary>
    public const int ValueAbi = 3, OldestValueAbi = 2;
    public const string ImportModule = "aquarius_v2";
    public const string MetadataSection = "aquarius.application";
}
