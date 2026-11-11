using System.IO.Compression;
using System.Text;
using AquariusLang.Build.Bindings;

namespace AquariusLang.Build.Libraries;

/// <summary>
/// The .aar of an Android native library output, as Gradle consumes it: AndroidManifest.xml (the Java package), the
/// compiled Java binding as classes.jar, each ABI's library in jni/&lt;abi&gt;/, an empty R.txt, and Prefab metadata
/// (prefab/: the C header and each ABI's library) so C/C++ code of an app can link the library too.
/// </summary>
internal static class AndroidLibraryPackage
{
    /// <summary>The minimum Android API level of the libraries (the pinned NDK's API in scripts/toolchain.json).</summary>
    public const int MinimumApi = 29;

    public static void Write(string aar, BindingContext context, IReadOnlyList<(string Abi, string Folder)> abis, string? classesJar, string include)
    {
        string name = context.Settings.Name;
        using var archive = ZipFile.Open(aar, ZipArchiveMode.Create);
        void Text(string entry, string text) {
            using var stream = archive.CreateEntry(entry, CompressionLevel.Optimal).Open();
            stream.Write(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n")));
        }
        void File(string entry, string file) => archive.CreateEntryFromFile(file, entry, CompressionLevel.Optimal);
        string package = JavaBinding.Package(context);
        Text("AndroidManifest.xml", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <manifest xmlns:android="http://schemas.android.com/apk/res/android" package="{package}">
                <uses-sdk android:minSdkVersion="{MinimumApi}" />
            </manifest>

            """);
        Text("R.txt", "");
        if (classesJar != null) File("classes.jar", classesJar);
        else using (var empty = new ZipArchive(archive.CreateEntry("classes.jar").Open(), ZipArchiveMode.Create)) { }
        Text("prefab/prefab.json", $"{{\"schema_version\": 2, \"name\": \"{name}\", \"version\": \"{context.Settings.Version ?? "0.0.0"}\", \"dependencies\": []}}\n");
        Text($"prefab/modules/{name}/module.json", "{\"export_libraries\": [], \"android\": {}}\n");
        foreach (string header in Directory.EnumerateFiles(include)) File($"prefab/modules/{name}/include/{Path.GetFileName(header)}", header);
        foreach (var (abi, folder) in abis) {
            string library = Path.Combine(folder, $"lib{name}.so");
            File($"jni/{abi}/lib{name}.so", library);
            File($"prefab/modules/{name}/libs/android.{abi}/lib{name}.so", library);
            Text($"prefab/modules/{name}/libs/android.{abi}/abi.json", $"{{\"abi\": \"{abi}\", \"api\": {MinimumApi}, \"ndk\": 28, \"stl\": \"none\", \"static\": false}}\n");
        }
    }
}
