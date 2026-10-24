using System.Text;
using System.Text.Json.Nodes;
using AquariusLang.Wasm;
using AquariusLang.Object;
using AquariusLang.Packaging;
using AquariusLang.Desktop.runtime;

namespace AquariusTests.Wasm;

public class WasmArtifactTest
{
    /// <summary>The committed ABI 2 artifact (JSON metadata), built by the ABI 2 compiler.</summary>
    internal static byte[] Abi2Artifact() =>
        File.ReadAllBytes(Path.Combine(AquariusTests.architecture.ArchitectureRulesTest.Root, "AquariusTests/compiler/fixtures/abi2-application.wasm"));
    // Replaces the Aquarius custom sections of a module.
    private static byte[] WithSections(byte[] bytes, byte[] metadata, byte[]? assets)
    {
        using var output = new MemoryStream(); output.Write(bytes, 0, 8); int p = 8;
        int U() { int n = 0, shift = 0; byte b; do { b = bytes[p++]; n |= (b & 127) << shift; shift += 7; } while ((b & 128) != 0); return n; }
        while (p < bytes.Length)
        {
            int start = p; byte id = bytes[p++]; int length = U(), end = p + length;
            if (id == 0) { int name = U(); string section = Encoding.UTF8.GetString(bytes, p, name); if (section is WasmAbi.MetadataSection or WasmProgram.AssetSection) { p = end; continue; } }
            output.Write(bytes, start, end - start); p = end;
        }
        void Custom(string name, byte[] payload)
        {
            var body = new MemoryStream(); Text(body, name); body.Write(payload);
            output.WriteByte(0); W(output, (uint)body.Length); output.Write(body.ToArray());
        }
        Custom(WasmAbi.MetadataSection, metadata); if (assets != null) Custom(WasmProgram.AssetSection, assets);
        return output.ToArray();
    }
    private static string CustomSection(byte[] bytes, string wanted)
    {
        int p = 8;
        int U() { int n = 0, shift = 0; byte b; do { b = bytes[p++]; n |= (b & 127) << shift; shift += 7; } while ((b & 128) != 0); return n; }
        while (p < bytes.Length)
        {
            byte id = bytes[p++]; int length = U(), end = p + length;
            if (id == 0) { int name = U(); if (Encoding.UTF8.GetString(bytes, p, name) == wanted) return Encoding.UTF8.GetString(bytes, p + name, end - p - name); }
            p = end;
        }
        throw new InvalidDataException("Missing section " + wanted);
    }
    private static void W(Stream s, uint n) { do { byte b = (byte)(n & 127); n >>= 7; s.WriteByte((byte)(b | (n != 0 ? 128 : 0))); } while (n != 0); }
    private static void Text(Stream s, string value) { var b = Encoding.UTF8.GetBytes(value); W(s, (uint)b.Length); s.Write(b); }
    /// <summary>Edits the JSON metadata of an ABI 2 module.</summary>
    internal static byte[] WithMetadata(byte[] bytes, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(CustomSection(bytes, WasmAbi.MetadataSection))!.AsObject(); edit(node);
        return WithSections(bytes, Encoding.UTF8.GetBytes(node.ToJsonString()), null);
    }
    /// <summary>The ABI 3 binary metadata fields, in encoding order (see WasmProgram.Create).</summary>
    internal sealed record BinaryManifest(int HeapStart, int FunctionCount, (string Name, int Index)[] Modules, (string Name, int Length)[] Assets,
        int Abi = 3, int ValueAbi = 2, int ProgramAddress = 4194304, int FunctionLayout = 40, int Entry = 0, string[]? Platforms = null, byte Storage = 0);
    internal static BinaryManifest Manifest(WasmProgram program) => new(program.Metadata.HeapStart, program.Metadata.FunctionCount,
        program.Metadata.Modules.Select(m => (m.Key, m.Value)).ToArray(), program.Metadata.AssetNames.Select(n => (n, program.Assets[n].Length)).ToArray());
    internal static byte[] Encode(BinaryManifest m)
    {
        var s = new MemoryStream(); s.Write(new byte[] { 0, (byte)'A', (byte)'Q', (byte)'M', (byte)m.Abi });
        foreach (int value in new[] { m.ValueAbi, m.ProgramAddress, m.HeapStart, m.FunctionLayout, m.FunctionCount }) W(s, (uint)value);
        W(s, (uint)m.Modules.Length); foreach (var (name, index) in m.Modules) { Text(s, name); W(s, (uint)index); }
        W(s, (uint)m.Entry); W(s, m.Platforms == null ? 0 : (uint)m.Platforms.Length + 1); foreach (var p in m.Platforms ?? Array.Empty<string>()) Text(s, p);
        s.WriteByte(m.Storage); W(s, (uint)m.Assets.Length); foreach (var (name, length) in m.Assets) { Text(s, name); W(s, (uint)length); }
        return s.ToArray();
    }
    internal static byte[] WithManifest(WasmProgram program, BinaryManifest manifest, byte[]? assets = null) => WithSections(program.Bytes.ToArray(), Encode(manifest), assets);

    [Theory]
    [InlineData("abi")][InlineData("entry")][InlineData("modules")][InlineData("functions")][InlineData("reference")][InlineData("literal")]
    [InlineData("asset")][InlineData("valueAbi")][InlineData("programAddress")][InlineData("heapStart")][InlineData("frame")][InlineData("cacheBindings")]
    public void MalformedLegacyMetadataIsRejectedBeforeInstantiation(string damage)
    {
        var bytes = WithMetadata(Abi2Artifact(), node =>
        {
            int pooled = node["functions"]!.AsArray().Select((f, i) => (f, i)).First(p => p.f!["pool"]!.AsArray().Count > 0).i;
            switch (damage)
            {
                case "abi": node["abiVersion"] = 99; break;
                case "entry": node["entry"] = null; break;
                case "modules": node["modules"] = null; break;
                case "functions": node["functions"] = null; break;
                case "reference": node["functions"]![pooled]!["pool"]![0] = new JsonObject { ["type"] = "function", ["function"] = 999, ["parameters"] = new JsonArray() }; break;
                case "literal": node["functions"]![pooled]!["pool"]![0]!["type"] = "unknown"; break;
                case "asset": node["assets"]!["safe.txt"] = null; break;
                case "valueAbi": node["valueAbi"] = 1; break;
                case "programAddress": node["programAddress"] = 0; break;
                case "heapStart": node["heapStart"] = 16; break;
                case "frame": node["functions"]![0]!["stackCapacity"] = 0; break;
                case "cacheBindings": node["functions"]![0]!["cacheBindings"] = "yes"; break;
            }
        });
        Assert.Throws<InvalidDataException>(() => WasmProgram.Load(bytes));
    }
    [Fact]
    public void BinaryMetadataRoundTripsThroughTheTestEncoder()
    {
        var program = new WasmCompiler().Compile("函式(x){x;};");
        Assert.Equal(program.Bytes.ToArray(), WithManifest(program, Manifest(program)));
    }
    [Theory]
    [InlineData("abi")][InlineData("valueAbi")][InlineData("programAddress")][InlineData("layout")][InlineData("heapStart")][InlineData("heapEnd")]
    [InlineData("functionCount")][InlineData("entry")][InlineData("module")][InlineData("moduleIndex")][InlineData("caseCollision")][InlineData("platforms")]
    [InlineData("storage")][InlineData("assetLength")][InlineData("missingAssets")][InlineData("externalWithBytes")][InlineData("assetModule")][InlineData("trailing")][InlineData("truncated")]
    public void MalformedBinaryMetadataIsRejectedBeforeInstantiation(string damage)
    {
        var program = new WasmCompiler().Compile("函式(x){x;};"); var m = Manifest(program); byte[]? assets = null;
        var metadata = damage switch {
            "abi" => Encode(m with { Abi = 2 }), "valueAbi" => Encode(m with { ValueAbi = 1 }), "programAddress" => Encode(m with { ProgramAddress = 0 }),
            "layout" => Encode(m with { FunctionLayout = 24 }), "heapStart" => Encode(m with { HeapStart = 16 }), "heapEnd" => Encode(m with { HeapStart = m.HeapStart + 8 }),
            "functionCount" => Encode(m with { FunctionCount = m.FunctionCount + 1 }), "entry" => Encode(m with { Entry = 5 }),
            "module" => Encode(m with { Modules = new[] { ("../main.aqua", 0) } }), "moduleIndex" => Encode(m with { Modules = new[] { ("main.aqua", m.FunctionCount) } }),
            "caseCollision" => Encode(m with { Modules = new[] { ("main.aqua", 0), ("MAIN.aqua", 0) } }), "platforms" => Encode(m with { Platforms = Array.Empty<string>() }),
            "storage" => Encode(m with { Storage = 2 }),
            "assetLength" => Encode(m with { Assets = new[] { ("a.bin", 3) } }), "missingAssets" => Encode(m with { Assets = new[] { ("a.bin", 1) } }),
            "externalWithBytes" => Encode(m with { Storage = 1, Assets = new[] { ("a.bin", 1) } }), "assetModule" => Encode(m with { Assets = new[] { ("x.aqua", 1) } }),
            "trailing" => Encode(m).Append((byte)0).ToArray(), "truncated" => Encode(m)[..^1], _ => throw new ArgumentException(damage)
        };
        if (damage is "assetLength") assets = new byte[] { 1, 2 };
        if (damage is "externalWithBytes" or "assetModule") assets = new byte[] { 1 };
        Assert.NotNull(WasmProgram.Load(WithManifest(program, m)));
        Assert.Throws<InvalidDataException>(() => WasmProgram.Load(WithSections(program.Bytes.ToArray(), metadata, assets)));
    }
    [Theory]
    [InlineData("42;", "42")]
    [InlineData("變數 f=函式(x){函式(y){x+y;};}; f(2)(40);", "42")]
    [InlineData("變數 n=0;迴圈(變數 i=0;i<100;i++){n+=i;}n;", "4950")]
    public void StandardWasmRoundTripExecutesWithoutSourceOrInstructionSerialization(string source, string expected)
    {
        var bytes = new WasmCompiler().Compile(source).Bytes.ToArray();
        Assert.Equal(new byte[] { 0, 97, 115, 109, 1, 0, 0, 0 }, bytes.Take(8));
        var loaded = WasmProgram.Load(bytes);
        Assert.Equal(expected, new WasmRuntime(engine: new WasmtimeEngine()).Execute(loaded).Inspect());
        Assert.DoesNotContain("\"code\":", Encoding.UTF8.GetString(bytes));
    }
    [Fact]
    public void CompilationIsDeterministicAndHasNoExecutionSideEffects()
    {
        var compiler = new WasmCompiler();
        Assert.Equal(compiler.Compile("unknown();").Bytes.ToArray(), compiler.Compile("unknown();").Bytes.ToArray());
    }
    [Fact]
    public void InvalidAndTruncatedArtifactsAreRejected()
    {
        var bytes = new WasmCompiler().Compile("42;").Bytes.ToArray();
        for (int i = 0; i < bytes.Length; i++) Assert.Throws<InvalidDataException>(() => WasmProgram.Load(bytes[..i]));
        bytes[4] = 99; Assert.Throws<InvalidDataException>(() => WasmProgram.Load(bytes));
    }
    [Fact]
    public void FailedCompilationPreservesExistingOutput()
    {
        using var temp = new ApplicationTestDirectory();
        string source = temp.Write("main.aqua", "42;"), output = temp.FilePath("app.wasm");
        WasmApplication.Compile(new[] { source }, output, temp.Path); var original = File.ReadAllBytes(output);
        File.WriteAllText(source, "變數 = ;");
        Assert.Throws<AquariusLang.Compiler.CompilationException>(() => WasmApplication.Compile(new[] { source }, output, temp.Path));
        Assert.Equal(original, File.ReadAllBytes(output));
    }
    [Fact]
    public void ModulesAndAssetsArePortableAndIndependentAcrossRuns()
    {
        using var temp = new ApplicationTestDirectory();
        var files = new[] { temp.Write("source/main.aqua", "變數 m=匯入(\"lib/工具.aqua\");m.加(40);"), temp.Write("source/lib/工具.aqua", "變數 加=函式(x){x+2;};") };
        string asset = temp.Write("source/assets/字.txt", "中文"), output = temp.FilePath("app.wasm");
        WasmApplication.Compile(files, output, temp.FilePath("source"), new[] { asset }); Directory.Delete(temp.FilePath("source"), true);
        var app = WasmApplication.Load(output);
        Assert.Equal("中文", Encoding.UTF8.GetString(app.Assets["assets/字.txt"]));
        Assert.Equal(2, app.Scripts.Count);
        for (int i = 0; i < 2; i++) Assert.Equal(42, Assert.IsType<IntegerObj>(ScriptRunner.RunWasm(output)).Value);
    }
}
