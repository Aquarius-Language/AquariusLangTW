using System.Text;
using AquariusLang.Compiler;
using AquariusLang.Wasm;

namespace AquariusTests.Packaging;

/// <summary>The application artifact: a standard WebAssembly module with the <c>aquarius.application</c> metadata (ABI 4;
/// ABI 3 still loads, ABI 2 is rejected with a rebuild message), validated by the compiler's loader and the native
/// runtime before any code runs.</summary>
public class WasmArtifactTest
{
    // ---- Module sections ----------------------------------------------------------------------------------------

    /// <summary>Replaces the Aquarius custom sections of a module.</summary>
    internal static byte[] WithSections(byte[] bytes, byte[] metadata, byte[]? assets)
    {
        using var output = new MemoryStream(); output.Write(bytes, 0, 8); int p = 8;
        int U() { int n = 0, shift = 0; byte b; do { b = bytes[p++]; n |= (b & 127) << shift; shift += 7; } while ((b & 128) != 0); return n; }
        while (p < bytes.Length) {
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
    private static void W(Stream s, uint n) { do { byte b = (byte)(n & 127); n >>= 7; s.WriteByte((byte)(b | (n != 0 ? 128 : 0))); } while (n != 0); }
    private static void Text(Stream s, string value) { var b = Encoding.UTF8.GetBytes(value); W(s, (uint)b.Length); s.Write(b); }

    /// <summary>The binary metadata fields in encoding order (ABI 3 layout; ABI 4 appends the import table of bundled
    /// library modules and the native libraries).</summary>
    internal sealed record BinaryManifest(int HeapStart, int FunctionCount, (string Name, int Index)[] Modules, (string Name, int Length)[] Assets,
        int Abi = 4, int ValueAbi = 2, int ProgramAddress = 4194304, int FunctionLayout = 40, int Entry = 0, string[]? Platforms = null, byte Storage = 0,
        (string Name, int Module)[]? Imports = null, string[]? Libraries = null);
    internal static BinaryManifest Manifest(WasmProgram program)
    {
        var modules = program.Metadata.Modules.Select(m => (m.Key, m.Value)).ToArray();
        int Index(string path) => Array.FindIndex(modules, m => m.Key.Equals(path, StringComparison.OrdinalIgnoreCase));
        return new(program.Metadata.HeapStart, program.Metadata.FunctionCount, modules,
            program.Metadata.AssetNames.Select(n => (n, program.Assets[n].Length)).ToArray(),
            Abi: program.Metadata.AbiVersion, Entry: Index(program.Metadata.Entry), Platforms: program.Metadata.SupportedPlatforms,
            Storage: program.Metadata.ExternalAssets ? (byte)1 : (byte)0,
            Imports: program.Metadata.ImportTable.Select(i => (i.Key, Index(i.Value))).ToArray(), Libraries: program.Metadata.NativeLibraries.ToArray());
    }
    internal static byte[] Encode(BinaryManifest m)
    {
        var s = new MemoryStream(); s.Write(new byte[] { 0, (byte)'A', (byte)'Q', (byte)'M', (byte)m.Abi });
        foreach (int value in new[] { m.ValueAbi, m.ProgramAddress, m.HeapStart, m.FunctionLayout, m.FunctionCount }) W(s, (uint)value);
        W(s, (uint)m.Modules.Length); foreach (var (name, index) in m.Modules) { Text(s, name); W(s, (uint)index); }
        W(s, (uint)m.Entry); W(s, m.Platforms == null ? 0 : (uint)m.Platforms.Length + 1); foreach (var p in m.Platforms ?? []) Text(s, p);
        s.WriteByte(m.Storage); W(s, (uint)m.Assets.Length); foreach (var (name, length) in m.Assets) { Text(s, name); W(s, (uint)length); }
        if (m.Abi >= 4) s.Write(Tables(m));
        return s.ToArray();
    }
    /// <summary>The ABI 4 tables: uleb importCount, {name, moduleIndex}*, uleb libraryCount, {name}*.</summary>
    internal static byte[] Tables(BinaryManifest m)
    {
        var s = new MemoryStream();
        W(s, (uint)(m.Imports?.Length ?? 0)); foreach (var (name, module) in m.Imports ?? []) { Text(s, name); W(s, (uint)module); }
        W(s, (uint)(m.Libraries?.Length ?? 0)); foreach (string library in m.Libraries ?? []) Text(s, library);
        return s.ToArray();
    }
    internal static byte[] WithManifest(WasmProgram program, BinaryManifest manifest, byte[]? assets = null) => WithSections(program.Bytes.ToArray(), Encode(manifest), assets);
    internal static byte[] WithMetadata(WasmProgram program, byte[] metadata, byte[]? assets = null) => WithSections(program.Bytes.ToArray(), metadata, assets);

    /// <summary>A module as the ABI 2 compiler wrote it: JSON metadata in the application section.</summary>
    internal static byte[] Abi2Artifact() => WithMetadata(new WasmCompiler().Compile("42;"),
        Encoding.UTF8.GetBytes("""{"abiVersion":2,"valueAbi":2,"entry":"main.aqua","modules":{"main.aqua":0},"functions":[],"assets":{}}"""));

    private static string Save(TestFolder folder, byte[] bytes, string name = "app.wasm") => folder.Write(name, bytes);

    // A program whose metadata has every ABI 4 part: modules, an asset, a bundled library module (two import names, a
    // relatively imported library file) and platforms.
    private static WasmProgram LibraryProgram() => new WasmCompiler(Fixtures.Set("stars")).Compile(
        new Dictionary<string, LoweredProgram> { ["main.aqua"] = new LoweringCompiler().Compile("印出(\"RAN\"); 變數 s=匯入(\"Stars\"); s.亮度(4);") },
        "main.aqua", new Dictionary<string, byte[]> { ["data/a.bin"] = [1, 2, 3] });

    // ---- Encoding ------------------------------------------------------------------------------------------------

    [Fact]
    public void BinaryMetadataRoundTripsThroughTheTestEncoder()
    {
        var plain = new WasmCompiler().Compile("函式(x){x;};");
        Assert.Equal(WasmAbi.Version, plain.Metadata.AbiVersion);
        Assert.Equal(plain.Bytes.ToArray(), WithManifest(plain, Manifest(plain)));
        var linked = LibraryProgram();
        Assert.NotEmpty(linked.Metadata.ImportTable); Assert.NotNull(linked.Metadata.SupportedPlatforms);
        Assert.Equal(linked.Bytes.ToArray(), WithManifest(linked, Manifest(linked), [1, 2, 3]));
        var withLibraries = WasmProgram.Load(WithManifest(linked, Manifest(linked) with { Libraries = ["audio", "aqua_2"] }, [1, 2, 3]));
        Assert.Equal(["audio", "aqua_2"], withLibraries.Metadata.NativeLibraries);
        Assert.Equal(linked.Metadata.ImportTable, withLibraries.Metadata.ImportTable);
    }

    public static TheoryData<string> Damages() => new() {
        "abi2", "abi5", "valueAbi", "programAddress", "layout", "heapStart", "heapEnd", "functionCount", "entry", "module", "moduleIndex",
        "caseCollision", "moduleWithoutExtension", "platforms", "longPlatform", "storage", "assetLength", "missingAssets", "externalWithBytes", "assetModule",
        "importModule", "importEmpty", "importDuplicate", "importTooLong", "libraryName", "libraryDuplicate", "libraryEmpty",
        "libraryTooLong", "abi3WithTables", "abi4WithoutTables", "trailing", "truncated",
    };
    private static byte[] WithAbiByte(byte[] metadata, byte abi) { metadata[4] = abi; return metadata; }
    private static (byte[] Metadata, byte[]? Assets) Damage(WasmProgram program, string damage)
    {
        var m = Manifest(program); byte[] assets = [1, 2, 3];
        string main = m.Modules[0].Name;
        byte[] metadata = damage switch {
            "abi2" => Encode(m with { Abi = 2 }), "abi5" => Encode(m with { Abi = 5 }), "valueAbi" => Encode(m with { ValueAbi = 1 }),
            "programAddress" => Encode(m with { ProgramAddress = 0 }), "layout" => Encode(m with { FunctionLayout = 24 }),
            "heapStart" => Encode(m with { HeapStart = 16 }), "heapEnd" => Encode(m with { HeapStart = m.HeapStart + 8 }),
            "functionCount" => Encode(m with { FunctionCount = m.FunctionCount + 1 }), "entry" => Encode(m with { Entry = m.Modules.Length }),
            "module" => Encode(m with { Modules = [("../main.aqua", 0), .. m.Modules[1..]] }),
            "moduleIndex" => Encode(m with { Modules = [(main, m.FunctionCount), .. m.Modules[1..]] }),
            "caseCollision" => Encode(m with { Modules = [.. m.Modules, (main.ToUpperInvariant(), 0)] }),
            "moduleWithoutExtension" => Encode(m with { Modules = [("main.txt", 0), .. m.Modules[1..]] }),
            "platforms" => Encode(m with { Platforms = [] }), "longPlatform" => Encode(m with { Platforms = [new string('w', 33)] }),
            "storage" => Encode(m with { Storage = 2 }),
            "assetLength" => Encode(m with { Assets = [("data/a.bin", 4)] }), "missingAssets" => Encode(m with { Assets = [] }),
            "externalWithBytes" => Encode(m with { Storage = 1 }), "assetModule" => Encode(m with { Assets = [("data/a.aqua", 3)] }),
            "importModule" => Encode(m with { Imports = [("Stars", m.Modules.Length)] }),
            "importEmpty" => Encode(m with { Imports = [("", 1)] }),
            "importDuplicate" => Encode(m with { Imports = [("Stars", 1), ("Stars", 1)] }),
            "importTooLong" => Encode(m with { Imports = [(new string('星', 43), 1)] }),
            "libraryName" => Encode(m with { Libraries = ["Audio"] }), "libraryDuplicate" => Encode(m with { Libraries = ["audio", "audio"] }),
            "libraryEmpty" => Encode(m with { Libraries = [""] }), "libraryTooLong" => Encode(m with { Libraries = [new string('a', 65)] }),
            "abi3WithTables" => [.. Encode(m with { Abi = 3 }), .. Tables(m)],
            "abi4WithoutTables" => WithAbiByte(Encode(m with { Abi = 3 }), 4),
            "trailing" => [.. Encode(m), 0], "truncated" => Encode(m)[..^1],
            _ => throw new ArgumentException(damage),
        };
        return (metadata, assets);
    }

    [Theory, MemberData(nameof(Damages))]
    public void MalformedBinaryMetadataIsRejectedBeforeInstantiation(string damage)
    {
        var program = LibraryProgram();
        Assert.NotNull(WasmProgram.Load(WithManifest(program, Manifest(program), [1, 2, 3])));
        var (metadata, assets) = Damage(program, damage);
        Assert.Throws<InvalidDataException>(() => WasmProgram.Load(WithMetadata(program, metadata, assets)));
    }

    public static TheoryData<string> RuntimeDamages() { var all = new TheoryData<string>(); foreach (var row in Damages()) if ((string)row[0] != "functionCount") all.Add((string)row[0]); return all; }

    [RuntimeFact(Skip = RuntimeBug.FunctionCount)]
    public void TheNativeRuntimeRejectsAWrongFunctionCount() => TheNativeRuntimeRejectsTheSameMetadataWithoutRunningTheProgram("functionCount");

    [RuntimeTheory, MemberData(nameof(RuntimeDamages))]
    public void TheNativeRuntimeRejectsTheSameMetadataWithoutRunningTheProgram(string damage)
    {
        using var folder = new TestFolder();
        var program = LibraryProgram();
        var (metadata, assets) = Damage(program, damage);
        var result = WasmTestHost.RunWasm(Save(folder, WithMetadata(program, metadata, assets)));
        Assert.True(result.ExitCode != 0, $"{damage}: exit {result.ExitCode}\n{result.Output}\n{result.Error}");
        Assert.DoesNotContain("RAN", result.Output);
        Assert.NotEmpty(result.Error + result.Output);
    }

    // ---- Older formats -------------------------------------------------------------------------------------------

    [Fact]
    public void Abi2ArtifactsAreRejectedWithARebuildMessage()
    {
        var error = Assert.Throws<InvalidDataException>(() => WasmProgram.Load(Abi2Artifact()));
        Assert.Contains("ABI 2", error.Message); Assert.Contains("Rebuild it from source", error.Message);
        using var folder = new TestFolder();
        string wasm = Save(folder, Abi2Artifact());
        Assert.Contains("ABI 2", Assert.Throws<InvalidDataException>(() => AquariusLang.Packaging.WasmApplication.Load(wasm)).Message);
    }

    [RuntimeFact]
    public void TheNativeRuntimeRejectsAbi2ArtifactsWithARebuildMessage()
    {
        using var folder = new TestFolder();
        var result = WasmTestHost.RunWasm(Save(folder, Abi2Artifact()));
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("older AquariusLang compiler (ABI 2). Rebuild it from source.", result.Error);
        Assert.Equal("", result.Output);
    }

    /// <summary>An ABI 3 application as the previous compiler wrote it: no import or library tables; a library module
    /// imported by name was bundled as .aquarius/&lt;name&gt;.aqua.</summary>
    internal static byte[] Abi3Artifact(bool asset = false)
    {
        var program = new WasmCompiler(AquariusLang.Libraries.LibrarySet.Empty).Compile(new Dictionary<string, LoweredProgram> {
            ["main.aqua"] = new LoweringCompiler().Compile("變數 s=匯入(\"Stars\"); 變數 m=匯入(\"lib/工具.aqua\"); 印出(s.名稱); m.值;"),
            ["lib/工具.aqua"] = new LoweringCompiler().Compile("變數 值=42;"),
            ["bundled/Stars.aqua"] = new LoweringCompiler().Compile("變數 名稱=\"星星\";"),
        }, "main.aqua", asset ? new Dictionary<string, byte[]> { ["assets/字.txt"] = Encoding.UTF8.GetBytes("中文") } : null);
        var m = Manifest(program);
        return WithManifest(program, m with {
            Abi = 3, Imports = null, Libraries = null,
            Modules = m.Modules.Select(x => x.Name == "bundled/Stars.aqua" ? (".aquarius/Stars.aqua", x.Index) : x).ToArray(),
        }, asset ? Encoding.UTF8.GetBytes("中文") : null);
    }

    [Fact]
    public void Abi3ArtifactsStillLoadAndExportWithoutLibraryTables()
    {
        var program = WasmProgram.Load(Abi3Artifact(asset: true));
        Assert.Equal(3, program.Metadata.AbiVersion);
        Assert.Empty(program.Metadata.ImportTable); Assert.Empty(program.Metadata.NativeLibraries);
        Assert.Contains(".aquarius/Stars.aqua", program.Metadata.Modules.Keys);
        // Exports keep the format: the external-asset copy is still ABI 3 and loads again.
        var external = WasmProgram.Load(program.WithExternalAssets().Bytes.ToArray());
        Assert.Equal(3, external.Metadata.AbiVersion); Assert.True(external.Metadata.ExternalAssets);
        Assert.Equal(program.Metadata.Modules, external.Metadata.Modules);
        Assert.Equal("lib/工具.aqua", WasmProgram.Load(program.WithEntry("LIB/工具.AQUA").Bytes.ToArray()).Metadata.Entry);
    }

    [RuntimeFact]
    public void Abi3ArtifactsStillRunOnTheNativeRuntime()
    {
        using var folder = new TestFolder();
        var result = WasmTestHost.RunWasm(Save(folder, Abi3Artifact())).Succeeded();
        Assert.Equal("星星\n42\n", result.Output);
    }

    // ---- Execution and determinism -------------------------------------------------------------------------------

    [RuntimeTheory]
    [InlineData("42;", "42")]
    [InlineData("變數 f=函式(x){函式(y){x+y;};}; f(2)(40);", "42")]
    [InlineData("var f=function(x){function(y){x+y;};}; f(2)(40);", "42")]
    [InlineData("變數 n=0;迴圈(變數 i=0;i<100;i++){n+=i;}n;", "4950")]
    public void StandardWasmRoundTripExecutesWithoutSourceOrInstructionSerialization(string source, string expected)
    {
        var bytes = new WasmCompiler().Compile(source).Bytes.ToArray();
        Assert.Equal(new byte[] { 0, 97, 115, 109, 1, 0, 0, 0 }, bytes.Take(8));
        var loaded = WasmProgram.Load(bytes);
        Assert.Equal(bytes, loaded.Bytes.ToArray());
        Assert.DoesNotContain("\"code\":", Encoding.UTF8.GetString(bytes));
        using var folder = new TestFolder();
        Assert.Equal(expected + "\n", WasmTestHost.RunWasm(Save(folder, loaded.Bytes.ToArray())).Succeeded().Output);
    }

    [Fact]
    public void CompilationIsDeterministicAndHasNoExecutionSideEffects()
    {
        var compiler = new WasmCompiler();
        Assert.Equal(compiler.Compile("unknown();").Bytes.ToArray(), compiler.Compile("unknown();").Bytes.ToArray());
        Assert.Equal(LibraryProgram().Bytes.ToArray(), LibraryProgram().Bytes.ToArray());
    }

    [Fact]
    public void InvalidAndTruncatedArtifactsAreRejected()
    {
        var bytes = new WasmCompiler().Compile("42;").Bytes.ToArray();
        // Every short prefix, every cut in the metadata at the end and a stride through the module's code and data.
        var lengths = Enumerable.Range(0, 256).Concat(Enumerable.Range(bytes.Length - 512, 512)).Concat(Enumerable.Range(0, bytes.Length / 61).Select(i => i * 61));
        foreach (int i in lengths.Where(i => i >= 0 && i < bytes.Length).Distinct()) Assert.Throws<InvalidDataException>(() => WasmProgram.Load(bytes[..i]));
        bytes[4] = 99; Assert.Throws<InvalidDataException>(() => WasmProgram.Load(bytes));
        Assert.Throws<InvalidDataException>(() => WasmProgram.Load(Encoding.UTF8.GetBytes("invalid archive")));
    }

    [Fact]
    public void FailedCompilationPreservesExistingOutput()
    {
        using var temp = new TestFolder();
        string source = temp.Write("main.aqua", "42;"), output = temp.FilePath("app.wasm");
        AquariusLang.Packaging.WasmApplication.Compile([source], output, temp.Path); var original = File.ReadAllBytes(output);
        File.WriteAllText(source, "變數 = ;");
        Assert.Throws<CompilationException>(() => AquariusLang.Packaging.WasmApplication.Compile([source], output, temp.Path));
        Assert.Equal(original, File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [RuntimeFact]
    public void ModulesAndAssetsArePortableAndIndependentAcrossRuns()
    {
        using var temp = new TestFolder();
        var files = new[] {
            temp.Write("source/main.aqua", "變數 m=匯入(\"lib/工具.aqua\"); 變數 f=匯入(\"Files\"); 印出(f.ReadAssetText(\"assets/字.txt\", \"utf-8\")); m.加(40);"),
            temp.Write("source/lib/工具.aqua", "變數 n=0; 變數 加=函式(x){n++; x+2*n;};"),
        };
        string asset = temp.Write("source/assets/字.txt", "中文"), output = temp.FilePath("app.wasm");
        AquariusLang.Packaging.WasmApplication.Compile(files, output, temp.FilePath("source"), [asset]); Directory.Delete(temp.FilePath("source"), true);
        var app = AquariusLang.Packaging.WasmApplication.Load(output);
        Assert.Equal("中文", Encoding.UTF8.GetString(app.Assets["assets/字.txt"]));
        Assert.Equal(2, app.Scripts.Count);
        // Every run starts from the application, not from state an earlier run left.
        for (int i = 0; i < 2; i++) Assert.Equal("中文\n42\n", WasmTestHost.RunWasm(output).Succeeded().Output);
    }
}
