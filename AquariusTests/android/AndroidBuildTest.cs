using System.Text.Json;
using AquariusLang.Android;
using AquariusLang.Build;
using AquariusLang.Hosting;
using AquariusTests.Packaging;
using AquariusLang.Desktop.runtime;
using AquariusLang.Wasm;

namespace AquariusTests.Android;

public class AndroidBuildTest
{
    [Fact]
    public void DynamicNativeLibrariesAreInstalledIntoAbiFoldersAndCannotReplaceRuntime()
    {
        using var t = new ApplicationTestDirectory();
        string source = t.Write("main.aqua", "1;"), wasm = t.FilePath("app.wasm");
        foreach (string abi in new[] { "arm64-v8a", "x86_64" }) t.Write($"native/android/{abi}/libplugin.so", "fixture");
        WasmApplication.Compile(new[] { source }, wasm, t.Path, new[] { t.FilePath("native") });
        new AndroidBuildTarget().Build(new(wasm, t.FilePath("android")));
        foreach (string abi in new[] { "arm64-v8a", "x86_64" })
            Assert.Equal("fixture", File.ReadAllText(t.FilePath($"android/app/src/main/jniLibs/{abi}/libplugin.so")));
        Assert.True(File.Exists(t.FilePath("android/app/src/main/assets/aquarius/dynamic-library-core.mjs")));
        t.Write("native/android/arm64-v8a/libaquarius_android.so", "cannot replace runtime");
        WasmApplication.Compile(new[] { source }, wasm, t.Path, new[] { t.FilePath("native") });
        Assert.Throws<ArgumentException>(() => new AndroidBuildTarget().Build(new(wasm, t.FilePath("collision"))));
        Assert.False(Directory.Exists(t.FilePath("collision")));
    }
    [Fact]
    public void MissingNativeRuntimeIsRejectedBeforeLaunchingGradle()
    {
        using var t = new ApplicationTestDirectory();
        var error = Assert.Throws<IOException>(() => AndroidToolchain.Build(t.Path));
        Assert.Contains("prepare-android.ps1", error.Message);
    }
    [Fact]
    public void ExportPreservesWasmModulesAssetsAndNativeProject()
    {
        using var t = new ApplicationTestDirectory(); string main = t.Write("src/main.aqua", "印出(\"DO_NOT_EXECUTE\");變數 m=匯入(\"lib/中文.aqua\");m.值;");
        string module = t.Write("src/lib/中文.aqua", "變數 值=42;"); t.Write("src/assets/字.txt", "測試");
        string wasm = t.FilePath("app.wasm"); WasmApplication.Compile(new[] { main, module }, wasm, t.FilePath("src"), new[] { t.FilePath("src/assets") });
        string output = t.FilePath("Android project"); new AndroidBuildTarget().Build(new(wasm, output));
        var original = WasmProgram.Load(wasm); var deployed = WasmProgram.Load(Path.Combine(output, "app/src/main/assets/aquarius/program.wasm"));
        Assert.Equal(original.WithExternalAssets().Bytes.ToArray(), deployed.Bytes.ToArray());
        Assert.Equal("", deployed.Metadata.Assets["assets/字.txt"]);
        Assert.True(File.Exists(Path.Combine(output, "gradle/wrapper/gradle-wrapper.jar")));
        Assert.Contains("com.caoccao.javet", File.ReadAllText(Path.Combine(output, "app/build.gradle")));
        Assert.DoesNotContain("WebView", File.ReadAllText(Path.Combine(output, "app/src/main/java/org/aquariuslang/android/AquariusActivity.java")));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "app/src/main/assets/aquarius/program.json")));
        Assert.Equal("main.aqua", json.RootElement.GetProperty("entry").GetString()); Assert.True(json.RootElement.GetProperty("assets").TryGetProperty("assets/字.txt", out _));
        string asset = json.RootElement.GetProperty("assets").GetProperty("assets/字.txt").GetString()!;
        Assert.Equal("測試", File.ReadAllText(Path.Combine(output, "app/src/main/assets/aquarius", asset)));
        Assert.True(File.Exists(Path.Combine(output, "app/src/main/assets/aquarius/capability-host.mjs")));
        foreach (string name in new[] { "gpu-commands.mjs", "frame-timing.mjs" })
            Assert.True(File.Exists(Path.Combine(output, "app/src/main/assets/aquarius", name)));
        Assert.True(File.Exists(Path.Combine(output, "app/src/main/cpp/gpu_batch.h")));
        Assert.True(File.Exists(Path.Combine(output, "app/src/main/java/org/aquariuslang/android/FrameClock.java")));
        Assert.False(File.Exists(Path.Combine(output, "index.html")));
        Assert.False(File.Exists(Path.Combine(output, "app/src/main/assets/aquarius/ort.webgpu.min.mjs")));
    }
    [Theory]
    [InlineData("main.aqua")]
    [InlineData("second.aqua")]
    public void ExportSelectsAnEntryWithoutExecutingIt(string entry)
    {
        using var t = new ApplicationTestDirectory(); var sources = new[] { t.Write("main.aqua", "1;"), t.Write("second.aqua", "2;") }; string wasm = t.FilePath("app.wasm"); WasmApplication.Compile(sources, wasm, t.Path);
        new AndroidBuildTarget().Build(new(wasm, t.FilePath("android"), entry));
        using var doc = JsonDocument.Parse(File.ReadAllText(t.FilePath("android/app/src/main/assets/aquarius/program.json"))); Assert.Equal(entry, doc.RootElement.GetProperty("entry").GetString());
    }
    [Fact]
    public void InvalidEntryAndMalformedWasmPreserveExistingOutputs()
    {
        using var t = new ApplicationTestDirectory(); string source = t.Write("main.aqua", "1;"), wasm = t.FilePath("app.wasm"); WasmApplication.Compile(new[] { source }, wasm, t.Path); t.Write("android/important.txt", "keep");
        Assert.Throws<InvalidDataException>(() => new AndroidBuildTarget().Build(new(wasm, t.FilePath("android"), "missing.aqua")));
        t.Write("bad.wasm", "bad"); Assert.Throws<InvalidDataException>(() => new AndroidBuildTarget().Build(new(t.FilePath("bad.wasm"), t.FilePath("android"))));
        Assert.Equal("keep", File.ReadAllText(t.FilePath("android/important.txt")));
    }
    [Fact]
    public void EditableProjectIsNeverOverwritten()
    {
        using var t = new ApplicationTestDirectory(); string source = t.Write("main.aqua", "1;"), wasm = t.FilePath("app.wasm"); WasmApplication.Compile(new[] { source }, wasm, t.Path); t.Write("android/app.java", "my edits");
        Assert.Throws<ArgumentException>(() => new AndroidBuildTarget().Build(new(wasm, t.FilePath("android")))); Assert.Equal("my edits", File.ReadAllText(t.FilePath("android/app.java")));
    }
    [Fact]
    public void SharedHostResourcesContainNoBrowserUiDependencies()
    {
        var assets = HostScriptAssets.Read(); Assert.Contains("wasm-host.mjs", assets.Keys);
        foreach (string name in new[] { "capability-host.mjs", "processing.mjs", "application-runtime.mjs" })
        {
            string source = System.Text.Encoding.UTF8.GetString(assets[name]); Assert.DoesNotContain("document.", source); Assert.DoesNotContain("window.", source); Assert.DoesNotContain("BrowserApplication", source);
        }
        Assert.DoesNotContain(typeof(AndroidBuildTarget).Assembly.GetReferencedAssemblies(), a => a.Name is "AquariusDesktop" or "AquariusWebCompiler");
    }
    [Theory]
    [InlineData("org.example.demo")]
    [InlineData("tw.aquarius.mobile_2")]
    public void ApplicationIdentityAndLabelArePreservedAndXmlEscaped(string id)
    {
        using var t = new ApplicationTestDirectory(); string source = t.Write("main.aqua", "1;"), wasm = t.FilePath("app.wasm"); WasmApplication.Compile(new[] { source }, wasm, t.Path);
        new AndroidBuildTarget().Build(new(wasm, t.FilePath("android"), Options: new Dictionary<string, string> { { "--application-id", id }, { "--app-name", "相機 & \"畫筆\"" } }));
        Assert.Contains("applicationId '" + id + "'", File.ReadAllText(t.FilePath("android/app/build.gradle")));
        var xml = System.Xml.Linq.XDocument.Load(t.FilePath("android/app/src/main/AndroidManifest.xml"));
        Assert.Equal("相機 & \"畫筆\"", xml.Root!.Element("application")!.Attribute(System.Xml.Linq.XName.Get("label", "http://schemas.android.com/apk/res/android"))!.Value);
    }
    [Theory]
    [InlineData("org.example';boom")]
    [InlineData("../escape")]
    [InlineData("single")]
    [InlineData("org.1invalid")]
    [InlineData("")]
    public void InvalidIdentityDoesNotCreateOutputs(string id)
    {
        using var t = new ApplicationTestDirectory(); string source = t.Write("main.aqua", "1;"), wasm = t.FilePath("app.wasm"); WasmApplication.Compile(new[] { source }, wasm, t.Path);
        Assert.Throws<ArgumentException>(() => new AndroidBuildTarget().Build(new(wasm, t.FilePath("android"), Options: new Dictionary<string, string> { { "--application-id", id } })));
        Assert.False(Directory.Exists(t.FilePath("android")));
    }
}
