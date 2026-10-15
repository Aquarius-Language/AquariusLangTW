using System.Text;
using AquariusLang.Compiler;
using AquariusLang.Desktop;
using AquariusLang.Desktop.Linking;
using AquariusLang.Linking;
using AquariusLang.Object;
using AquariusLang.runtime;

namespace AquariusTests.Linking;

public class DynamicLibraryTest {
    [Fact] public void BottleAssetNamespaceMatchesImportsAndLockedNativeCleanupDoesNotMaskResults() {
        using var t=new AquariusLang.Desktop.runtime.ApplicationTestDirectory();
        string source=t.Write("src/main.aqua","變數 f=匯入(\"Files\");長度(f.ReadAssetBytes(\"/bottles/test/data.txt\"));");
        string asset=t.Write("src/bottles/test/data.txt","hello");string output=t.FilePath("app.wasm");
        AquariusLang.Packaging.WasmApplication.Compile([source],output,t.FilePath("src"),[asset]);
        var package=AquariusLang.Packaging.WasmApplication.Load(output);
        var runtime=new AquariusLang.Desktop.runtime.WasmApplicationRuntime(package,output);
        string directory=runtime.ResourceDirectory!;
        try {
            using(var locked=File.Open(Path.Combine(directory,"bottles/test/data.txt"),FileMode.Open,FileAccess.Read,FileShare.None))runtime.Dispose();
        } finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
        Assert.Equal(5,Assert.IsType<IntegerObj>(AquariusLang.Desktop.runtime.ScriptRunner.RunWasm(output)).Value);
    }
    [Fact]
    public void BundledEsModulesHaveIndependentFactoriesAsyncBytesAndOwnedDisposal() {
        using var t = new AquariusLang.Desktop.runtime.ApplicationTestDirectory();
        string path=t.Write("library.mjs", """
            function version(){return 1;} function factory(){let n=0;return {async echo(b){await Promise.resolve();return b;},counter(){return Uint8Array.of(++n);},aqua_dispose(){n=0;}};}
            export {version as aqua_library_abi,factory as aqua_create_library};
            """);
        var backend=new DesktopDynamicLibraryBackend();Assert.True(backend.IsSupported("mjs"));
        using var first=backend.Load(path);using var second=backend.Load(path);
        var input=Encoding.UTF8.GetBytes("繁體中文\0🌊");Assert.Equal(input,first.Invoke("echo",input));
        Assert.Equal(new byte[]{1},first.Invoke("counter",[]));Assert.Equal(new byte[]{2},first.Invoke("counter",[]));Assert.Equal(new byte[]{1},second.Invoke("counter",[]));
        Assert.ThrowsAny<Exception>(()=>first.Invoke("missing",[]));first.Dispose();first.Dispose();Assert.Throws<ObjectDisposedException>(()=>first.HasSymbol("echo"));
    }
    private static IObject Call(ModuleObj module, string name, params IObject[] args) => ((BuiltinObj)module._Environment.Get(name, out _)).Fn(args);
    private sealed class Library : IDynamicLibrary {
        public int Releases;
        public bool HasSymbol(string name) => name == "echo";
        public byte[] Invoke(string name, byte[] input) => input.ToArray();
        public void Dispose() { Releases++; }
    }
    private sealed class Backend : IDynamicLibraryBackend {
        public readonly List<Library> Libraries = new();
        public string Platform => "linux";
        public string? Path;
        public bool IsSupported(string kind) => kind == "so";
        public IDynamicLibrary Load(string path) { Path = path; var library = new Library(); Libraries.Add(library); return library; }
    }
    [Fact]
    public void BilingualAliasesValidationAndOwnership() {
        var backend = new Backend(); using var runtime = new DynamicLibraryRuntime(backend);
        Assert.True(runtime.TryImport("動態函式庫", out var module)); runtime.TryImport("DynamicLibrary", out var english); Assert.Same(module, english);
        Assert.Same(module._Environment.Get("Load", out _), module._Environment.Get("載入", out _));
        Assert.Equal("DynamicLibrary", LibraryCatalog.CanonicalLibrary("動態函式庫"));
        Assert.Equal("DynamicLibrary.Handle", LibraryCatalog.Find("動態函式庫", "載入")!.ReturnLibrary);
        Assert.IsType<ErrorObj>(Call(module, "Load")); Assert.IsType<ErrorObj>(Call(module, "Load", new IntegerObj(3)));
        var handle = Assert.IsType<ModuleObj>(Call(module, "載入", new StringObj("demo.so")));
        Assert.Same(handle._Environment.Get("Call", out _), handle._Environment.Get("呼叫", out _));
        Assert.Equal("你好🌊\0Hello", Assert.IsType<StringObj>(Call(handle, "呼叫", new StringObj("echo"), new StringObj("你好🌊\0Hello"))).Value);
        Assert.IsType<ErrorObj>(Call(handle, "Call", new StringObj("aqua_library_abi"), new StringObj("")));
        Assert.IsType<ErrorObj>(Call(handle, "Call", new StringObj("echo"), new StringObj("\ud800")));
        foreach (IObject invalid in new IObject[] { new IntegerObj(256), new IntegerObj(-1), new DoubleObj(0.5), new DoubleObj(double.NaN), new StringObj("a") })
            Assert.IsType<ErrorObj>(Call(handle, "Invoke", new StringObj("echo"), new ArrayObj(new[] { invalid })));
        var result = Assert.IsType<ArrayObj>(Call(handle, "呼叫位元組", new StringObj("echo"), new ArrayObj(new IObject[] { new IntegerObj(0), new IntegerObj(255) })));
        Assert.Equal(new[] { 0, 255 }, result.Elements.Cast<IntegerObj>().Select(n => n.Value));
        Call(handle, "釋放"); Call(handle, "Dispose"); Assert.Equal(1, backend.Libraries[0].Releases);
        Assert.IsType<ErrorObj>(Call(handle, "HasSymbol", new StringObj("echo")));
        Call(module, "Load", new StringObj("another.so")); runtime.Dispose(); runtime.Dispose();
        Assert.Equal(1, backend.Libraries[1].Releases); Assert.IsType<ErrorObj>(Call(module, "IsSupported", new StringObj("so")));
    }
    [Fact]
    public void RealWasmLibraryCopiesBytesTracksIndependentStateAndRejectsFailures() {
        string fixture = Path.Combine(AppContext.BaseDirectory, "linking/fixtures/showcase.wasm");
        var backend = new DesktopDynamicLibraryBackend();
        using var library = backend.Load(fixture); using var second = backend.Load(fixture);
        Assert.True(library.HasSymbol("echo")); Assert.False(library.HasSymbol("missing"));
        var bytes = Encoding.UTF8.GetBytes("繁體中文 & English\0🌊"); Assert.Equal(bytes, library.Invoke("echo", bytes));
        Assert.Equal(Array.Empty<byte>(), library.Invoke("echo", Array.Empty<byte>()));
        Assert.Equal(new byte[] {42}, library.Invoke("sum", new byte[] {10,20,12}));
        Assert.Equal(new byte[] {1}, library.Invoke("counter", Array.Empty<byte>()));
        Assert.Equal(new byte[] {2}, library.Invoke("counter", Array.Empty<byte>()));
        Assert.Equal(new byte[] {1}, second.Invoke("counter", Array.Empty<byte>()));
        Assert.ThrowsAny<Exception>(() => library.Invoke("failure", bytes)); Assert.ThrowsAny<Exception>(() => library.Invoke("oversized", bytes));
        Assert.ThrowsAny<Exception>(() => library.Invoke("missing", bytes));
        Assert.Throws<ArgumentException>(() => library.Invoke("echo", new byte[DynamicLibraryAbi.MaxBytes + 1]));
        // Repeated large calls exercise allocation/free and copied ownership.
        var maximum = new byte[DynamicLibraryAbi.MaxBytes]; maximum[^1] = 255;
        for (int i = 0; i < 3; i++) Assert.Equal(maximum, library.Invoke("echo", maximum));
        library.Dispose(); library.Dispose(); Assert.Throws<ObjectDisposedException>(() => library.HasSymbol("echo"));
        using var binding = new DynamicLibraryRuntime(backend); binding.TryImport("DynamicLibrary", out var module);
        var handle = Assert.IsType<ModuleObj>(Call(module, "Load", new StringObj(fixture)));
        Assert.IsType<ErrorObj>(Call(handle, "Call", new StringObj("invalid_utf8"), new StringObj("")));
    }
    [Fact]
    public void DynamicWasmAndJavaScriptAssetsSurviveRelocationAndWebExport()
    {
        using var t = new AquariusLang.Desktop.runtime.ApplicationTestDirectory();
        string source = t.Write("src/main.aqua", "變數 d=匯入(\"DynamicLibrary\");變數 l=d.Load(\"libraries/showcase.wasm\");變數 result=l.Call(\"echo\",\"已封裝 / bundled\");l.Dispose();result;");
        t.Write("src/libraries/showcase.mjs", "export function aqua_library_abi(){return 1;}");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "linking/fixtures/showcase.wasm"), t.FilePath("src/libraries/showcase.wasm"));
        string wasm = t.FilePath("app.wasm");
        AquariusLang.Packaging.WasmApplication.Compile(new[] { source }, wasm, t.FilePath("src"), new[] { t.FilePath("src/libraries") });
        Directory.Delete(t.FilePath("src"), true);
        Assert.Equal("已封裝 / bundled", Assert.IsType<StringObj>(AquariusLang.Desktop.runtime.ScriptRunner.RunWasm(wasm)).Value);
        string web = t.FilePath("web"); AquariusLang.Web.WebsiteCompiler.BuildWasm(wasm, web);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(web,"program.json")));
        string module = json.RootElement.GetProperty("assets").GetProperty("libraries/showcase.mjs").GetString()!;
        Assert.EndsWith(".mjs", module); Assert.Contains("aqua_library_abi", File.ReadAllText(Path.Combine(web,module)));
        // A second export must recognize its own generated module assets.
        AquariusLang.Web.WebsiteCompiler.BuildWasm(wasm, web);
    }
    [Fact]
    public void NativeAndSourceFreeWasmExecutionWhenFixtureIsProvided() {
        string? native = System.Environment.GetEnvironmentVariable("AQUARIUS_DYNAMIC_LIBRARY_NATIVE"); if (native == null) return;
        var backend = new DesktopDynamicLibraryBackend(); using var library = backend.Load(native);
        Assert.Equal(Encoding.UTF8.GetBytes("你好\0Hello"), library.Invoke("echo", Encoding.UTF8.GetBytes("你好\0Hello")));
        Assert.Equal(new byte[] {42}, library.Invoke("sum", new byte[] {10,20,12}));
        Assert.ThrowsAny<Exception>(() => library.Invoke("failure", Array.Empty<byte>()));
        using var builtins = new DesktopBuiltins();
        string path = native.Replace("\\", "/");
        var result = new CompiledRuntime(builtins).Execute(new LoweringCompiler().Compile($$"""
變數 d = 匯入("動態函式庫");
變數 lib = d.載入({"{{backend.Platform}}":"{{path}}"});
變數 text = lib.呼叫("echo", "中文 & English");
變數 sum = lib.Invoke("sum", [10,20,12]); lib.Dispose();
text == "中文 & English" && sum[0] == 42;
"""));
        Assert.True(Assert.IsType<BooleanObj>(result).Value);
    }
}
