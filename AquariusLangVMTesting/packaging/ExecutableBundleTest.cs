using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using AquariusLang.Build;
using AquariusLang.Cli;
using AquariusLang.Packaging;
using AquariusLang.VM;
using AquariusREPL.runtime;

namespace AquariusLangVMTesting.Packaging;

public class ExecutableBundleTest {
    private static string Bottle(BottleTestDirectory t) {
        var files = new[] { t.Write("src/main.aqua", "匯入(\"lib/工具.aqua\").值;"), t.Write("src/lib/工具.aqua", "變數 值=42;") };
        string asset = t.Write("src/assets/文字.txt", "封裝成功"), bottle = t.FilePath("app.bottle");
        BottlePackage.Compile(files, bottle, t.FilePath("src"), new[] { asset });
        return bottle;
    }
    private static MemoryStream Bundle(string bottle, string? entry = null) {
        using var host = new MemoryStream("synthetic runtime template"u8.ToArray());
        using var input = File.OpenRead(bottle);
        var result = new MemoryStream(); ExecutableBundle.Write(host, input, result, entry); return result;
    }

    [Fact] public void OverlayPreservesTemplateAndBottleAndCanonicalEntryAfterSourceRemoval() {
        using var t = new BottleTestDirectory(); string bottle = Bottle(t);
        using var executable = Bundle(bottle, "LIB/工具.AQUA");
        Directory.Delete(t.FilePath("src"), true); File.Delete(bottle);
        var program = ExecutableBundle.Load(executable);
        Assert.Equal("lib/工具.rius", program.EntryPoint);
        Assert.Equal("main.rius", program.Package.EntryPoint);
        Assert.Equal(2, program.Package.Scripts.Count);
        Assert.Equal("封裝成功", System.Text.Encoding.UTF8.GetString(program.Package.Assets["assets/文字.txt"]));
        Assert.True(executable.CanRead); // Caller retains the stream.
        Assert.True(executable.ToArray().AsSpan().StartsWith("synthetic runtime template"u8));
    }

    [Theory]
    [InlineData("version")][InlineData("length")][InlineData("checksum")][InlineData("magic")][InlineData("truncated")]
    public void InvalidOverlaysAreRejectedBeforeExecuting(string damage) {
        using var t = new BottleTestDirectory(); using var executable = Bundle(Bottle(t));
        byte[] bytes = executable.ToArray(); int footer = bytes.Length - ExecutableBundle.FooterSize;
        switch (damage) {
            case "version": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(footer), 999); break;
            case "length": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(footer + 8), long.MaxValue); break;
            case "checksum": bytes[30] ^= 1; break;
            case "magic": bytes[^1] ^= 1; break;
            case "truncated": bytes = bytes[..^1]; break;
        }
        using var broken = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => ExecutableBundle.Load(broken));
    }

    private static SelfContainedExecutableTarget Target(BottleTestDirectory t, int version = ExecutableBundle.FormatVersion, string? hash = null) {
        string template = t.Write("packs/win-x64/host.exe", "synthetic runtime template");
        t.Write("packs/win-x64/runtime.json", JsonSerializer.Serialize(new {
            format = "aquarius-runtime-pack", version = 1, runtimeIdentifier = "win-x64", bundleVersion = version,
            bottleVersion = BottlePackage.FormatVersion, bytecodeVersion = BytecodeSerializer.FormatVersion,
            templateSha256 = hash ?? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(template)))
        }));
        return new SelfContainedExecutableTarget("windows", "win-x64", ".exe", t.FilePath("packs"));
    }

    [Fact] public void BuildsReplaceOnlyTheRequestedFileAndDoNotExecuteTheProgram() {
        using var t = new BottleTestDirectory(); string bottle = Bottle(t);
        string output = t.Write("dist/星泉 app.exe", "previous output"); t.Write("dist/keep.txt", "unrelated");
        Target(t).Build(new BottleBuildRequest(bottle, output));
        using var stream = File.OpenRead(output);
        Assert.Equal("main.rius", ExecutableBundle.Load(stream).EntryPoint);
        Assert.Equal("unrelated", File.ReadAllText(t.FilePath("dist/keep.txt")));
        Assert.Equal(2, Directory.GetFiles(t.FilePath("dist")).Length);
    }

    [Theory]
    [InlineData("missing-pack")][InlineData("version")][InlineData("hash")][InlineData("entry")][InlineData("bottle")]
    public void FailedBuildsPreservePreviousOutputAndLeaveNoStagingFiles(string failure) {
        using var t = new BottleTestDirectory(); string bottle = Bottle(t);
        string output = t.Write("dist/app.exe", "previous output");
        var target = Target(t, failure == "version" ? 999 : 1, failure == "hash" ? "wrong" : null);
        if (failure == "missing-pack") File.Delete(t.FilePath("packs/win-x64/runtime.json"));
        if (failure == "bottle") File.WriteAllText(bottle, "invalid archive");
        var error = Record.Exception(() => target.Build(new BottleBuildRequest(bottle, output, failure == "entry" ? "missing.rius" : null)));
        if (failure == "missing-pack") Assert.IsType<IOException>(error);
        else Assert.IsType<InvalidDataException>(error);
        Assert.Equal("previous output", File.ReadAllText(output));
        Assert.Single(Directory.GetFiles(t.FilePath("dist")));
    }

    [Fact] public void TargetRegistryReportsSupportedTargetsAndRejectsDuplicateRegistration() {
        Assert.Equal("windows", CompilerBuildTargets.Default.Resolve("windows").Name);
        var web = CompilerBuildTargets.Default.Resolve("web"); Assert.Equal("web", web.Name);
        Assert.Contains("windows", Assert.Throws<ArgumentException>(() => CompilerBuildTargets.Default.Resolve("unknown")).Message);
        Assert.Throws<ArgumentException>(() => new BuildTargets(new[] { web, web }));
    }
}
