using System.Text;
using System.Text.Json.Nodes;
using AquariusLang.Wasm;
using AquariusLang.Object;
using AquariusLang.Packaging;
using AquariusLang.Desktop.runtime;

namespace AquariusTests.Wasm;

public class WasmArtifactTest
{
    internal static byte[] WithMetadata(WasmProgram program, Action<JsonObject> edit)
    {
        var bytes = program.Bytes.ToArray(); int p = 8, metadataStart = 0;
        int U() { int n = 0, shift = 0; byte b; do { b = bytes[p++]; n |= (b & 127) << shift; shift += 7; } while ((b & 128) != 0); return n; }
        JsonObject node = null!;
        while (p < bytes.Length)
        {
            int start = p; byte id = bytes[p++]; int length = U(), end = p + length;
            if (id == 0) { int name = U(); p += name; node = JsonNode.Parse(Encoding.UTF8.GetString(bytes, p, end - p))!.AsObject(); metadataStart = start; break; }
            p = end;
        }
        edit(node); var json = Encoding.UTF8.GetBytes(node.ToJsonString()); var nameBytes = Encoding.UTF8.GetBytes(WasmAbi.MetadataSection);
        using var output = new MemoryStream(); output.Write(bytes, 0, metadataStart); output.WriteByte(0);
        void W(uint n) { do { byte b = (byte)(n & 127); n >>= 7; output.WriteByte((byte)(b | (n != 0 ? 128 : 0))); } while (n != 0); }
        W((uint)(1 + nameBytes.Length + json.Length)); W((uint)nameBytes.Length); output.Write(nameBytes); output.Write(json); return output.ToArray();
    }
    [Theory]
    [InlineData("abi")]
    [InlineData("entry")]
    [InlineData("modules")]
    [InlineData("functions")]
    [InlineData("reference")]
    [InlineData("literal")]
    [InlineData("asset")]
    public void MalformedMetadataIsRejectedBeforeInstantiation(string damage)
    {
        var program = new WasmCompiler().Compile("函式(x){x;};");
        var bytes = WithMetadata(program, node =>
        {
            switch (damage)
            {
                case "abi": node["abiVersion"] = 99; break;
                case "entry": node["entry"] = null; break;
                case "modules": node["modules"] = null; break;
                case "functions": node["functions"] = null; break;
                case "reference": node["functions"]![0]!["pool"]![0]!["function"] = 999; break;
                case "literal": node["functions"]![0]!["pool"]![0]!["type"] = "unknown"; break;
                case "asset": node["assets"]!["safe.txt"] = null; break;
            }
        });
        Assert.Throws<InvalidDataException>(() => WasmProgram.Load(bytes));
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
