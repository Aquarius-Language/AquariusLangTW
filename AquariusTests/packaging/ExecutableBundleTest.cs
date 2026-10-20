using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using AquariusLang.Build;
using AquariusLang.Cli;
using AquariusLang.Packaging;
using AquariusLang.Compiler;
using AquariusLang.Desktop.runtime;

namespace AquariusTests.Packaging;

public class ExecutableBundleTest {
    private static string Wasm(ApplicationTestDirectory t) {
        var files = new[] { t.Write("src/main.aqua", "匯入(\"lib/工具.aqua\").值;"), t.Write("src/lib/工具.aqua", "變數 值=42;") };
        string asset = t.Write("src/assets/文字.txt", "封裝成功"), wasm = t.FilePath("app.wasm");
        WasmApplication.Compile(files, wasm, t.FilePath("src"), new[] { asset });
        return wasm;
    }
    private static MemoryStream Bundle(string wasm, string? entry = null) {
        using var host = new MemoryStream("synthetic runtime template"u8.ToArray());
        using var input = File.OpenRead(wasm);
        var result = new MemoryStream(); ExecutableBundle.Write(host, input, result, entry); return result;
    }

    [Fact] public void OverlayPreservesTemplateAndWasmAndCanonicalEntryAfterSourceRemoval() {
        using var t = new ApplicationTestDirectory(); string wasm = Wasm(t);
        using var executable = Bundle(wasm, "LIB/工具.AQUA");
        Directory.Delete(t.FilePath("src"), true); File.Delete(wasm);
        var program = ExecutableBundle.Load(executable);
        Assert.Equal("lib/工具.aqua", program.EntryPoint);
        Assert.Equal("main.aqua", program.Package.EntryPoint);
        Assert.Equal(2, program.Package.Scripts.Count);
        Assert.Equal("封裝成功", System.Text.Encoding.UTF8.GetString(program.Package.Assets["assets/文字.txt"]));
        Assert.True(executable.CanRead); // Caller retains the stream.
        Assert.True(executable.ToArray().AsSpan().StartsWith("synthetic runtime template"u8));
    }

    [Theory]
    [InlineData("version")][InlineData("length")][InlineData("checksum")][InlineData("magic")][InlineData("truncated")]
    public void InvalidOverlaysAreRejectedBeforeExecuting(string damage) {
        using var t = new ApplicationTestDirectory(); using var executable = Bundle(Wasm(t));
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

    private static SelfContainedExecutableTarget Target(ApplicationTestDirectory t, int version = ExecutableBundle.FormatVersion, string? hash = null) {
        string template = t.Write("packs/win-x64/host.exe", "synthetic runtime template");
        t.Write("packs/win-x64/runtime.json", JsonSerializer.Serialize(new {
            format = "aquarius-runtime-pack", version = 1, runtimeIdentifier = "win-x64", bundleVersion = version,
            wasmAbiVersion = AquariusLang.Wasm.WasmAbi.Version,
            templateSha256 = hash ?? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(template)))
        }));
        return new SelfContainedExecutableTarget("windows", "win-x64", ".exe", t.FilePath("packs"));
    }

    [Fact] public void BuildsReplaceOnlyTheRequestedFileAndDoNotExecuteTheProgram() {
        using var t = new ApplicationTestDirectory(); string wasm = Wasm(t);
        string output = t.Write("dist/星泉 app.exe", "previous output"); t.Write("dist/keep.txt", "unrelated");
        Target(t).Build(new WasmBuildRequest(wasm, output));
        using var stream = File.OpenRead(output);
        Assert.Equal("main.aqua", ExecutableBundle.Load(stream).EntryPoint);
        Assert.Equal("unrelated", File.ReadAllText(t.FilePath("dist/keep.txt")));
        Assert.Equal(2, Directory.GetFiles(t.FilePath("dist")).Length);
    }

    [Theory]
    [InlineData("missing-pack")][InlineData("version")][InlineData("hash")][InlineData("entry")][InlineData("wasm")]
    public void FailedBuildsPreservePreviousOutputAndLeaveNoStagingFiles(string failure) {
        using var t = new ApplicationTestDirectory(); string wasm = Wasm(t);
        string output = t.Write("dist/app.exe", "previous output");
        var target = Target(t, failure == "version" ? 999 : ExecutableBundle.FormatVersion, failure == "hash" ? "wrong" : null);
        if (failure == "missing-pack") File.Delete(t.FilePath("packs/win-x64/runtime.json"));
        if (failure == "wasm") File.WriteAllText(wasm, "invalid archive");
        var error = Record.Exception(() => target.Build(new WasmBuildRequest(wasm, output, failure == "entry" ? "missing.aqua" : null)));
        if (failure == "missing-pack") Assert.IsType<IOException>(error);
        else Assert.IsType<InvalidDataException>(error);
        Assert.Equal("previous output", File.ReadAllText(output));
        Assert.Single(Directory.GetFiles(t.FilePath("dist")));
    }

    /// <summary>Writes a synthetic runtime pack; the CUDA flavour lives in RID-cuda and records onnxCuda.</summary>
    internal static void Pack(ApplicationTestDirectory t, string directory, string rid, bool onnxCuda, string extension = ".exe", bool? cudaRuntime = null) {
        string template = t.Write($"{directory}/host{extension}", "synthetic runtime template " + directory);
        t.Write($"{directory}/runtime.json", JsonSerializer.Serialize(new {
            format = "aquarius-runtime-pack", version = 1, runtimeIdentifier = rid, bundleVersion = ExecutableBundle.FormatVersion,
            wasmAbiVersion = AquariusLang.Wasm.WasmAbi.Version, templateSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(template))), onnxCuda, onnxCudaRuntime = cudaRuntime ?? onnxCuda
        }));
    }
    private static Dictionary<string, string> Cuda(string value) => new() { [SelfContainedExecutableTarget.OnnxCudaOption] = value };
    [Fact] public void OnnxCudaIsOptInAndSelectsTheCudaRuntimePackFlavour() {
        using var t = new ApplicationTestDirectory(); string wasm = Wasm(t);
        Pack(t, "packs/win-x64", "win-x64", false); Pack(t, "packs/win-x64-cuda", "win-x64", true);
        var target = new SelfContainedExecutableTarget("windows", "win-x64", ".exe", t.FilePath("packs"));
        Assert.Equal(new[] { SelfContainedExecutableTarget.OnnxCudaOption }, target.OptionNames);
        string standard = t.FilePath("dist/standard.exe"), cuda = t.FilePath("dist/cuda.exe");
        target.Build(new WasmBuildRequest(wasm, standard));
        target.Build(new WasmBuildRequest(wasm, cuda, null, Cuda("true")));
        Assert.StartsWith("synthetic runtime template packs/win-x64-cuda", File.ReadAllText(cuda));
        Assert.DoesNotContain("-cuda", File.ReadAllText(standard)[..40]);
        target.Build(new WasmBuildRequest(wasm, standard, null, Cuda("false")));
        Assert.Throws<ArgumentException>(() => target.Build(new WasmBuildRequest(wasm, cuda, null, Cuda("maybe"))));
        Assert.Empty(new SelfContainedExecutableTarget("macos", "osx-arm64", "", t.FilePath("packs")).OptionNames);
    }
    [Fact] public void OnnxCudaPackProblemsAreExplainedAndPreserveOutput() {
        using var t = new ApplicationTestDirectory(); string wasm = Wasm(t);
        Pack(t, "packs/win-x64", "win-x64", false);
        var target = new SelfContainedExecutableTarget("windows", "win-x64", ".exe", t.FilePath("packs"));
        string output = t.Write("dist/app.exe", "previous output");
        var missing = Assert.Throws<IOException>(() => target.Build(new WasmBuildRequest(wasm, output, null, Cuda("true"))));
        Assert.Contains("win-x64-cuda", missing.Message); Assert.Contains("-OnnxCuda", missing.Message); Assert.Contains("[onnx] cuda = true", missing.Message);
        Pack(t, "packs/win-x64-cuda", "win-x64", false);
        Assert.Contains("does not contain", Assert.Throws<InvalidDataException>(() => target.Build(new WasmBuildRequest(wasm, output, null, Cuda("true")))).Message);
        Pack(t, "packs/win-x64-cuda", "win-x64", true, cudaRuntime: false);
        Assert.Contains("bundled NVIDIA", Assert.Throws<InvalidDataException>(() => target.Build(new WasmBuildRequest(wasm, output, null, Cuda("true")))).Message);
        Pack(t, "packs/win-x64", "win-x64", true);
        Assert.Contains("CUDA flavour", Assert.Throws<InvalidDataException>(() => target.Build(new WasmBuildRequest(wasm, output))).Message);
        Assert.Equal("previous output", File.ReadAllText(output));
    }

    [Fact] public void TargetRegistryReportsSupportedTargetsAndRejectsDuplicateRegistration() {
        Assert.Equal("windows", CompilerBuildTargets.Default.Resolve("windows").Name);
        Assert.Equal("linux", CompilerBuildTargets.Default.Resolve("linux").Name);
        var web = CompilerBuildTargets.Default.Resolve("web"); Assert.Equal("web", web.Name);
        Assert.Contains("windows", Assert.Throws<ArgumentException>(() => CompilerBuildTargets.Default.Resolve("unknown")).Message);
        Assert.Throws<ArgumentException>(() => new BuildTargets(new[] { web, web }));
    }

    [Fact] public void LinuxOverlayPreservesElfTemplateAndIsExecutableWithoutAnExtension() {
        using var t = new ApplicationTestDirectory(); string wasm = Wasm(t);
        string template = t.FilePath("packs/linux-x64/host");
        Directory.CreateDirectory(Path.GetDirectoryName(template)!);
        byte[] elf = "\u007fELF synthetic Linux runtime"u8.ToArray();
        File.WriteAllBytes(template, elf);
        t.Write("packs/linux-x64/runtime.json", JsonSerializer.Serialize(new {
            format = "aquarius-runtime-pack", version = 1, runtimeIdentifier = "linux-x64",
            bundleVersion = ExecutableBundle.FormatVersion, wasmAbiVersion = AquariusLang.Wasm.WasmAbi.Version,
            templateSha256 = Convert.ToHexString(SHA256.HashData(elf))
        }));
        var target = new SelfContainedExecutableTarget("linux", "linux-x64", "", t.FilePath("packs"));
        string output = t.Write("dist/星泉 app", "previous output");
        target.Build(new WasmBuildRequest(wasm, output, "LIB/工具.AQUA"));
        using var stream = File.OpenRead(output);
        byte[] header = new byte[elf.Length]; stream.ReadExactly(header); Assert.Equal(elf, header);
        Assert.Equal("lib/工具.aqua", ExecutableBundle.Load(stream).EntryPoint);
        if (!OperatingSystem.IsWindows()) {
            var mode = File.GetUnixFileMode(output);
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
            Assert.True(mode.HasFlag(UnixFileMode.GroupExecute));
            Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));
        }
    }
}
