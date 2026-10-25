using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AquariusLang.Build;
using AquariusLang.Build.Targets;
using AquariusLang.Compiler;
using AquariusLang.Libraries;
using AquariusLang.Wasm;
using AquariusTests.Packaging;

namespace AquariusTests.Targets;

/// <summary>Builds APKs: needs the Android SDK (ANDROID_HOME or ANDROID_SDK_ROOT), a JDK and the Android runtime packs
/// (.native-build/install/android-*). The APK is never installed.</summary>
public sealed class AndroidFactAttribute : FactAttribute
{
    public AndroidFactAttribute() { if (AndroidTargetTest.Missing() is { } reason) Skip = reason; }
}

/// <summary>The android target (<see cref="AndroidTarget"/>): a signed APK holding both ABIs' runtime, the activity's
/// classes.dex, the application and its assets, external libraries' shared objects and the permissions the needed
/// libraries declare, with stored, page-aligned entries.</summary>
public class AndroidTargetTest
{
    internal static string? Sdk() => new[] { Environment.GetEnvironmentVariable("ANDROID_HOME"), Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") }
        .FirstOrDefault(d => !string.IsNullOrEmpty(d) && Directory.Exists(d));
    private static string Exe => OperatingSystem.IsWindows() ? ".exe" : "";
    internal static string? BuildTools() => Sdk() is { } sdk && Directory.Exists(Path.Combine(sdk, "build-tools"))
        ? Directory.GetDirectories(Path.Combine(sdk, "build-tools"))
            .Where(d => File.Exists(Path.Combine(d, "aapt2" + Exe)) && File.Exists(Path.Combine(d, OperatingSystem.IsWindows() ? "apksigner.bat" : "apksigner")))
            .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0)).FirstOrDefault()
        : null;
    internal static string? Jdk()
    {
        bool Usable(string? home) => !string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, "bin", "java" + Exe)) && File.Exists(Path.Combine(home, "bin", "keytool" + Exe));
        if (Usable(Environment.GetEnvironmentVariable("JAVA_HOME"))) return Environment.GetEnvironmentVariable("JAVA_HOME");
        string? toolchains = AquariusInstallation.Current is { } installation ? Path.Combine(installation.Root, ".native-build", "toolchains") : null;
        return toolchains != null && Directory.Exists(toolchains) ? Directory.GetDirectories(toolchains, "jdk-*").FirstOrDefault(Usable) : null;
    }
    internal static string[] Abis() => AquariusInstallation.Current is not { } installation ? [] : AndroidTarget.Abis
        .Where(abi => File.Exists(Path.Combine(installation.RuntimePack("android-" + abi), "libaquarius.so")) && File.Exists(Path.Combine(installation.RuntimePack("android-" + abi), "classes.dex")))
        .ToArray();
    internal static string? Missing()
    {
        if (Sdk() == null) return "Needs the Android SDK (ANDROID_HOME).";
        if (BuildTools() == null) return "Needs Android build-tools (aapt2, zipalign, apksigner).";
        if (Jdk() == null) return "Needs a JDK (JAVA_HOME, or scripts/install-toolchain.ps1 -Targets android-x86_64).";
        if (Abis().Length == 0) return "Needs an Android runtime pack (scripts/build-runtime.ps1 -Targets android-x86_64).";
        return null;
    }

    private static string Compile(TestFolder t, LibrarySet? libraries = null)
    {
        string wasm = t.FilePath("app.wasm");
        t.Write("src/assets/字.txt", "測試");
        WasmApplication.Compile([t.Write("src/main.aqua", "印出(\"DO_NOT_EXECUTE\"); 匯入(\"lib/中文.aqua\").值;"), t.Write("src/lib/中文.aqua", "變數 值 = 42;"),
            t.Write("src/second.aqua", "匯入(\"星雲\"); 匯入(\"Network\"); 2;")], wasm, t.FilePath("src"), [t.FilePath("src/assets")], libraries: libraries);
        return wasm;
    }

    /// <summary>Each entry's compression method and the offset of its data, from the ZIP central directory and local headers.</summary>
    private static Dictionary<string, (int Method, long DataOffset)> Entries(byte[] zip)
    {
        int end = zip.Length - 22;
        while (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end)) != 0x06054b50) end--;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(end + 10));
        int position = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16));
        var entries = new Dictionary<string, (int, long)>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++) {
            Assert.Equal(0x02014b50u, BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(position)));
            int method = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(position + 10));
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(position + 28)), extra = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(position + 30)),
                comment = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(position + 32));
            long local = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(position + 42));
            string name = Encoding.UTF8.GetString(zip, position + 46, nameLength);
            int localName = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan((int)local + 26)), localExtra = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan((int)local + 28));
            entries[name] = (method, local + 30 + localName + localExtra);
            position += 46 + nameLength + extra + comment;
        }
        return entries;
    }

    [AndroidFact]
    public void ApkHoldsBothRuntimesTheApplicationLibrariesAndPermissionsAndIsSigned()
    {
        using var t = new TestFolder();
        var nebula = Fixtures.ExternalLibrary(t, "android-arm64-v8a", "android-x86_64");
        var set = LibrarySet.Default.With([nebula]);
        string wasm = Compile(t, set), apk = t.FilePath("out/app.apk");
        new AndroidTarget().Build(new WasmBuildRequest(wasm, apk, "SECOND.aqua", new Dictionary<string, string> {
            [AndroidTarget.ApplicationIdOption] = "org.aquariuslang.tests.packaging", [AndroidTarget.NameOption] = "相機 & \"畫筆\"",
        }, set));
        Assert.Equal(["app.apk"], Directory.GetFiles(t.FilePath("out")).Select(Path.GetFileName));

        byte[] bytes = File.ReadAllBytes(apk);
        using var archive = new ZipArchive(new MemoryStream(bytes));
        byte[] Entry(string name) { using var stream = (archive.GetEntry(name) ?? throw new Xunit.Sdk.XunitException("Missing APK entry " + name)).Open(); var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray(); }
        var installation = AquariusInstallation.Require();
        foreach (string abi in Abis()) {
            string pack = installation.RuntimePack("android-" + abi);
            foreach (string library in Directory.GetFiles(pack, "*.so")) Assert.Equal(File.ReadAllBytes(library), Entry($"lib/{abi}/{Path.GetFileName(library)}"));
            Assert.Equal("dummy android-" + abi + " binary of nebula", Encoding.UTF8.GetString(Entry($"lib/{abi}/libaqua_nebula.so")));
        }
        Assert.Equal(File.ReadAllBytes(Path.Combine(installation.RuntimePack("android-" + Abis()[0]), "classes.dex")), Entry("classes.dex"));
        Assert.NotNull(archive.GetEntry("AndroidManifest.xml"));
        var program = WasmProgram.Load(Entry("assets/aquarius/program.wasm"));
        Assert.Equal("second.aqua", program.Metadata.Entry);
        Assert.True(program.Metadata.ExternalAssets);
        Assert.Contains("nebula", program.Metadata.NativeLibraries); Assert.Contains("network", program.Metadata.NativeLibraries);
        Assert.Equal(WasmProgram.Load(wasm).WithEntry("second.aqua").WithExternalAssets().Bytes.ToArray(), program.Bytes.ToArray());
        Assert.Equal("測試", Encoding.UTF8.GetString(Entry("assets/aquarius/assets/assets/字.txt")));
        Assert.Null(archive.GetEntry("assets/aquarius/main.aqua"));

        // Stored entries; shared libraries page-aligned so the runtime maps them straight from the APK.
        foreach (var (name, (method, offset)) in Entries(bytes)) {
            if (name.StartsWith("META-INF/", StringComparison.Ordinal)) continue;
            Assert.True(method == 0, $"{name} is compressed (method {method}).");
            if (name.EndsWith(".so", StringComparison.Ordinal)) Assert.True(offset % 16384 == 0, $"{name} starts at {offset}, not on a 16 KiB page.");
            else Assert.True(offset % 4 == 0, $"{name} starts at {offset}.");
        }

        // The manifest: identity, label and the needed libraries' permissions (aapt2 reads the binary XML).
        string tools = BuildTools()!;
        var badging = Processes.Run(Path.Combine(tools, "aapt2" + Exe), t.Path, ["dump", "badging", apk]);
        Assert.True(badging.ExitCode == 0, badging.Output + badging.Error);
        Assert.Contains("package: name='org.aquariuslang.tests.packaging'", badging.Output);
        Assert.Contains("application: label='相機 & \\\"畫筆\\\"'", badging.Output); // aapt2 escapes the quotes.
        var permissions = nebula.AndroidPermissions.Concat(LibrarySet.Default.OwnerOf("Network")?.AndroidPermissions ?? []).ToArray();
        Assert.Contains("android.permission.RECORD_AUDIO", permissions);
        foreach (string permission in permissions) Assert.Contains($"uses-permission: name='{permission}'", badging.Output);
        Assert.DoesNotContain("android.permission.CAMERA", badging.Output); // camera is not needed.

        // Signed: apksigner verifies it.
        string apksigner = Path.Combine(tools, OperatingSystem.IsWindows() ? "apksigner.bat" : "apksigner");
        var verify = OperatingSystem.IsWindows()
            ? Processes.Run("cmd.exe", t.Path, ["/d", "/c", apksigner, "verify", "--verbose", apk], environment: new Dictionary<string, string> { ["JAVA_HOME"] = Jdk()! })
            : Processes.Run(apksigner, t.Path, ["verify", "--verbose", apk], environment: new Dictionary<string, string> { ["JAVA_HOME"] = Jdk()! });
        Assert.True(verify.ExitCode == 0, verify.Output + verify.Error);
        Assert.Contains("Verifies", verify.Output);
        Assert.Matches("Verified using v2 scheme \\(APK Signature Scheme v2\\): true|Verified using v3 scheme \\(APK Signature Scheme v3\\): true", verify.Output);
    }

    [AndroidFact]
    public void ApkOutputPathsMayHoldSpacesAndChineseCharacters()
    {
        using var t = new TestFolder();
        string wasm = Compile(t, LibrarySet.Default.With([Fixtures.ExternalLibrary(t, "android-arm64-v8a", "android-x86_64")]));
        string apk = t.FilePath("輸出 folder/星泉 app.apk");
        var error = Record.Exception(() => new AndroidTarget().Build(new WasmBuildRequest(wasm, apk)));
        Assert.True(error == null, error?.Message);
        using var archive = ZipFile.OpenRead(apk);
        Assert.NotNull(archive.GetEntry("assets/aquarius/program.wasm"));
        Assert.Equal(["星泉 app.apk"], Directory.GetFiles(t.FilePath("輸出 folder")).Select(Path.GetFileName));
    }

    [AndroidFact]
    public void MissingExternalAndroidBinariesFailBeforeWritingTheApk()
    {
        using var t = new TestFolder();
        var set = LibrarySet.Default.With([Fixtures.ExternalLibrary(t, "web")]);
        string wasm = Compile(t, set), apk = t.Write("app.apk", "previous output");
        var error = Assert.Throws<IOException>(() => new AndroidTarget().Build(new WasmBuildRequest(wasm, apk, "second.aqua", Libraries: set)));
        Assert.Contains("aqua library build", error.Message);
        Assert.Equal("previous output", File.ReadAllText(apk));
    }

    [Theory]
    [InlineData("org.example';boom")]
    [InlineData("../escape")]
    [InlineData("single")]
    [InlineData("org.1invalid")]
    [InlineData("org.例子.app")]
    public void InvalidApplicationIdsCreateNoOutput(string id)
    {
        using var t = new TestFolder();
        string wasm = Compile(t, LibrarySet.Default.With([Fixtures.ExternalLibrary(t, "web")]));
        Assert.Throws<ArgumentException>(() => new AndroidTarget().Build(new WasmBuildRequest(wasm, t.FilePath("android/app.apk"),
            Options: new Dictionary<string, string> { [AndroidTarget.ApplicationIdOption] = id })));
        Assert.False(Directory.Exists(t.FilePath("android")));
    }

    [Theory]
    [InlineData("extension")][InlineData("label")][InlineData("keystore")][InlineData("entry")][InlineData("wasm")]
    public void InvalidRequestsPreserveThePreviousApk(string failure)
    {
        using var t = new TestFolder();
        string wasm = Compile(t, LibrarySet.Default.With([Fixtures.ExternalLibrary(t, "web")]));
        string apk = t.Write(failure == "extension" ? "app.zip" : "app.apk", "previous output");
        if (failure == "wasm") File.WriteAllText(wasm, "invalid archive");
        var options = new Dictionary<string, string>();
        if (failure == "label") options[AndroidTarget.NameOption] = new string('名', 129);
        if (failure == "keystore") options[AndroidTarget.KeystoreOption] = t.FilePath("release.keystore");
        var error = Record.Exception(() => new AndroidTarget().Build(new WasmBuildRequest(wasm, apk, failure == "entry" ? "missing.aqua" : null, options)));
        if (failure is "entry" or "wasm") Assert.IsType<InvalidDataException>(error); else Assert.IsType<ArgumentException>(error);
        Assert.Equal("previous output", File.ReadAllText(apk));
    }
}
