using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AquariusLang.Compiler;
using AquariusLang.Libraries;

namespace AquariusLang.Build.Targets;

/// <summary>
/// A signed Android APK for every Android runtime pack present (arm64-v8a, x86_64): the packs' libaquarius.so (the
/// native activity) and the shared libraries installed beside it, the activity's classes.dex, the manifest with the
/// permissions the application's libraries declare, external libraries' libaqua_&lt;name&gt;.so, and the application
/// with its assets under assets/aquarius/. Uncompressed entries are page-aligned (zipalign -P 16) so the runtime maps
/// its libraries straight from the APK. Needs the Android SDK (aapt2, zipalign and apksigner from build-tools, a
/// platform's android.jar) and a JDK.
/// </summary>
public sealed class AndroidTarget : IWasmBuildTarget
{
    public const string ApplicationIdOption = "--application-id", NameOption = "--app-name";
    public const string KeystoreOption = "--keystore", KeyAliasOption = "--key-alias";
    public static readonly string[] Abis = ["arm64-v8a", "x86_64"];
    public string Name => "android";
    public string ArtifactDescription => "Android application";
    public IReadOnlyCollection<string> OptionNames => [ApplicationIdOption, NameOption, KeystoreOption, KeyAliasOption];

    public void Build(WasmBuildRequest request)
    {
        var (application, entry) = Output.Read(request, Name);
        string output = Path.GetFullPath(request.Output);
        if (!output.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Android builds write an .apk file.");
        string id = request.Option(ApplicationIdOption) ?? "org.aquariuslang.app.p" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output + "\n" + entry)))[..16].ToLowerInvariant();
        if (!Regex.IsMatch(id, @"\A[a-zA-Z][a-zA-Z0-9_]*(\.[a-zA-Z][a-zA-Z0-9_]*)+\z")) throw new ArgumentException("Invalid Android application ID.");
        string label = request.Option(NameOption) ?? Path.GetFileNameWithoutExtension(entry);
        if (string.IsNullOrWhiteSpace(label) || label.Length > 128) throw new ArgumentException("The Android app name must be 1 to 128 characters.");
        string? keystore = request.Option(KeystoreOption), alias = request.Option(KeyAliasOption);
        if ((keystore == null) != (alias == null)) throw new ArgumentException($"{KeystoreOption} and {KeyAliasOption} go together.");

        var installation = AquariusInstallation.Require();
        var packs = Abis.Select(abi => (Abi: abi, Pack: installation.RuntimePack("android-" + abi)))
            .Where(p => File.Exists(Path.Combine(p.Pack, "libaquarius.so")) && File.Exists(Path.Combine(p.Pack, "classes.dex"))).ToArray();
        if (packs.Length == 0) installation.RequireRuntimePack("android-" + Abis[0], "libaquarius.so", "classes.dex");
        foreach (string abi in Abis.Except(packs.Select(p => p.Abi)))
            Console.Error.WriteLine($"Warning: the android-{abi} runtime pack is missing; the APK runs only on {string.Join(", ", packs.Select(p => p.Abi))} devices.");
        var needed = Output.NeededLibraries(application, request);
        var natives = packs.SelectMany(p => needed.Select(l => (p.Abi, l.Name, Binary: Output.ExternalBinary(l.Name, l.Library, "android-" + p.Abi))))
            .Where(l => l.Binary != null).ToArray();
        var permissions = needed.SelectMany(l => l.Library?.AndroidPermissions ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var tools = AndroidTools.Locate();
        string template = Path.Combine(packs[0].Pack, "AndroidManifest.xml");
        if (!File.Exists(template)) template = Path.Combine(installation.Root, "runtime", "platform", "android", "AndroidManifest.xml");
        if (!File.Exists(template)) throw new IOException($"The Android runtime pack has no AndroidManifest.xml ({packs[0].Pack}).");
        var program = application.Program.WithEntry(entry).WithExternalAssets();

        string work = Path.Combine(Path.GetTempPath(), "aquarius-android-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try {
            // The binary manifest and resource table come from aapt2.
            string manifest = File.ReadAllText(template).Replace("@PACKAGE@", id).Replace("@LABEL@", System.Security.SecurityElement.Escape(label))
                .Replace("@VERSION_CODE@", "1").Replace("@VERSION_NAME@", "1.0")
                .Replace("@PERMISSIONS@", string.Join("\n", permissions.Select(p => $"    <uses-permission android:name=\"{p}\" />")));
            File.WriteAllText(Path.Combine(work, "AndroidManifest.xml"), manifest, new UTF8Encoding(false));
            string linked = Path.Combine(work, "linked.apk");
            Output.Run(tools.Aapt2, ["link", "-o", linked, "--manifest", Path.Combine(work, "AndroidManifest.xml"), "-I", tools.AndroidJar], tools.Environment);

            // Every entry stored: zipalign then aligns the shared libraries to pages.
            string unaligned = Path.Combine(work, "unaligned.apk");
            using (var archive = ZipFile.Open(unaligned, ZipArchiveMode.Create)) {
                var names = new HashSet<string>(StringComparer.Ordinal);
                void Add(string name, Stream data) {
                    if (!names.Add(name)) throw new InvalidDataException($"Two APK entries are named {name}.");
                    var entryStream = archive.CreateEntry(name, CompressionLevel.NoCompression);
                    entryStream.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var target = entryStream.Open();
                    data.CopyTo(target);
                }
                void AddFile(string name, string file) { using var data = File.OpenRead(file); Add(name, data); }
                using (var resources = ZipFile.OpenRead(linked))
                    foreach (var item in resources.Entries) { using var data = item.Open(); Add(item.FullName, data); }
                AddFile("classes.dex", Path.Combine(packs[0].Pack, "classes.dex"));
                foreach (var (abi, pack) in packs)
                    foreach (string library in Directory.EnumerateFiles(pack, "*.so").Order(StringComparer.Ordinal)) AddFile($"lib/{abi}/{Path.GetFileName(library)}", library);
                foreach (var (abi, library, binary) in natives) AddFile($"lib/{abi}/{Library.NativeFile(library, "android-" + abi)}", binary!);
                using (var bytes = new MemoryStream(program.Bytes.ToArray())) Add("assets/aquarius/program.wasm", bytes);
                foreach (var asset in application.Assets) { using var bytes = new MemoryStream(asset.Value); Add("assets/aquarius/assets/" + asset.Key, bytes); }
            }
            string aligned = Path.Combine(work, "aligned.apk");
            Output.Run(tools.Zipalign, ["-P", "16", "-f", "4", unaligned, aligned], tools.Environment);

            var signing = new Dictionary<string, string>(tools.Environment);
            string ks, ksPass, keyPass;
            if (keystore != null) {
                ks = Path.GetFullPath(keystore);
                if (!File.Exists(ks)) throw new IOException($"The keystore {ks} does not exist.");
                // Passwords never appear on command lines: apksigner reads them from these variables.
                ksPass = "env:AQUARIUS_KEYSTORE_PASSWORD"; keyPass = Environment.GetEnvironmentVariable("AQUARIUS_KEY_PASSWORD") != null ? "env:AQUARIUS_KEY_PASSWORD" : ksPass;
                if (Environment.GetEnvironmentVariable("AQUARIUS_KEYSTORE_PASSWORD") == null) throw new ArgumentException("Set AQUARIUS_KEYSTORE_PASSWORD (and AQUARIUS_KEY_PASSWORD if the key has its own) to sign with a keystore.");
            } else {
                ks = tools.DebugKeystore(); alias = "androiddebugkey"; ksPass = "pass:android"; keyPass = "pass:android";
            }
            // apksigner runs on the JDK directly (no .bat), so paths in any language reach it intact.
            string signed = Path.Combine(work, "signed.apk");
            Output.Run(tools.Java, ["-jar", tools.Apksigner, "sign", "--v4-signing-enabled", "false", "--ks", ks, "--ks-key-alias", alias!,
                "--ks-pass", ksPass, "--key-pass", keyPass, "--out", signed, aligned], signing);
            Output.ReplaceFile(output, stage => File.Copy(signed, stage));
        } finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }
}

/// <summary>The Android SDK and JDK tools APK packaging runs.</summary>
internal sealed record AndroidTools(string Aapt2, string Zipalign, string Apksigner, string AndroidJar, string Java, string Keytool, IReadOnlyDictionary<string, string> Environment)
{
    public static AndroidTools Locate()
    {
        string? sdk = new[] { System.Environment.GetEnvironmentVariable("ANDROID_HOME"), System.Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
                OperatingSystem.IsWindows() ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk")
                    : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Android", "Sdk") }
            .FirstOrDefault(d => !string.IsNullOrEmpty(d) && Directory.Exists(d));
        if (sdk == null) throw new IOException("Android builds need the Android SDK: set ANDROID_HOME to it.");
        var pins = Pins();
        string exe = OperatingSystem.IsWindows() ? ".exe" : "";
        string? Newest(string folder, Func<string, bool> usable, string? pinned) {
            string root = Path.Combine(sdk, folder);
            if (pinned != null && Directory.Exists(Path.Combine(root, pinned)) && usable(Path.Combine(root, pinned))) return Path.Combine(root, pinned);
            return Directory.Exists(root) ? Directory.EnumerateDirectories(root).Where(usable).OrderByDescending(d => VersionOf(Path.GetFileName(d))).FirstOrDefault() : null;
        }
        string buildTools = Newest("build-tools", d => File.Exists(Path.Combine(d, "aapt2" + exe)) && File.Exists(Path.Combine(d, "lib", "apksigner.jar")), pins.BuildTools)
            ?? throw new IOException($"Install Android build-tools {pins.BuildTools ?? ""} with sdkmanager (aapt2, zipalign and apksigner are needed).");
        string platform = Newest("platforms", d => File.Exists(Path.Combine(d, "android.jar")), pins.Platform == null ? null : "android-" + pins.Platform)
            ?? throw new IOException($"Install an Android platform (android-{pins.Platform ?? "36"}) with sdkmanager.");
        string java = JavaHome() ?? throw new IOException("Android builds need a JDK: set JAVA_HOME (JDK 17 or newer), or run scripts/install-toolchain.ps1 -Targets android-x86_64 in a checkout.");
        return new AndroidTools(Path.Combine(buildTools, "aapt2" + exe), Path.Combine(buildTools, "zipalign" + exe), Path.Combine(buildTools, "lib", "apksigner.jar"),
            Path.Combine(platform, "android.jar"), Path.Combine(java, "bin", "java" + exe), Path.Combine(java, "bin", "keytool" + exe),
            new Dictionary<string, string> { ["JAVA_HOME"] = java });
    }

    private static Version VersionOf(string folder) {
        string text = folder.Replace("android-", "");
        return Version.TryParse(text, out var version) ? version : int.TryParse(text, out int major) ? new Version(major, 0) : new Version(0, 0);
    }

    // A checkout pins the SDK pieces and the JDK in scripts/toolchain.json.
    private static (string? BuildTools, string? Platform, string? Jdk) Pins()
    {
        if (AquariusInstallation.Current is not { IsRepository: true } repository) return (null, null, null);
        try {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository.Root, "scripts", "toolchain.json")));
            var android = json.RootElement.GetProperty("androidSdk");
            string? jdk = json.RootElement.TryGetProperty("jdk", out var pin) ? pin.GetProperty("directory").GetString() : null;
            string host = OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";
            return (android.GetProperty("buildTools").GetString(), android.GetProperty("platform").ToString(),
                jdk == null ? null : Path.Combine(repository.Root, ".native-build", "toolchains", jdk.Replace("{platform}", host)));
        } catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return (null, null, null); }
    }

    private static string? JavaHome()
    {
        string exe = OperatingSystem.IsWindows() ? ".exe" : "";
        bool Usable(string? home) => !string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, "bin", "java" + exe)) && File.Exists(Path.Combine(home, "bin", "keytool" + exe));
        string? home = System.Environment.GetEnvironmentVariable("JAVA_HOME");
        if (Usable(home)) return home;
        string? pinned = Pins().Jdk;
        return Usable(pinned) ? pinned : null;
    }

    /// <summary>The per-user debug keystore (~/.aquarius/android/debug.keystore), created on first use.</summary>
    public string DebugKeystore()
    {
        string keystore = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".aquarius", "android", "debug.keystore");
        if (File.Exists(keystore)) return keystore;
        Directory.CreateDirectory(Path.GetDirectoryName(keystore)!);
        Output.Run(Keytool, ["-genkeypair", "-keystore", keystore, "-alias", "androiddebugkey", "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000",
            "-dname", "CN=Android Debug,O=Android,C=US", "-storepass", "android", "-keypass", "android"], Environment);
        return keystore;
    }
}
