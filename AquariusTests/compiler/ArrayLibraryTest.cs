using System.Text.Json;
using System.Buffers.Binary;
using AquariusLang.Desktop.runtime;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Wasm;
using AquariusLang.Desktop;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusTests.Wasm;

public class ArrayLibraryTest {
    private static string Root {
        get { var path = new DirectoryInfo(AppContext.BaseDirectory); while (path != null && !File.Exists(Path.Combine(path.FullName, "AquariusLang.sln"))) path = path.Parent; return path!.FullName; }
    }
    private static string Chinese(string source) {
        foreach (var function in ArrayLibrary.Functions.OrderByDescending(f => f.EnglishName.Length))
            source = source.Replace("Array." + function.EnglishName, "陣列." + function.TraditionalChineseName)
                .Replace("[" + function.EnglishName + "]", "[" + function.TraditionalChineseName + "]");
        return source.Replace("Array.(", "陣列.(").Replace("變數 Array=", "變數 陣列=").Replace(",Array];", ",陣列];");
    }
    public static IEnumerable<object[]> ValidCases() {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tests", "array-library.json")));
        foreach (var item in json.RootElement.GetProperty("valid").EnumerateArray()) {
            string name=item[0].GetString()!,source=item[1].GetString()!,expected=item[2].GetString()!;
            yield return new object[] { name + " English", source, expected };
            yield return new object[] { name + " 繁體中文", Chinese(source), expected };
        }
    }
    public static IEnumerable<object[]> InvalidCases() {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tests", "array-library.json")));
        foreach (var item in json.RootElement.GetProperty("invalid").EnumerateArray()) {
            yield return new object[] { item.GetString()! }; yield return new object[] { Chinese(item.GetString()!) };
        }
    }
    private static Builtins Tokens() {
        var builtins = new Builtins();builtins.BuiltinFuncs["push"]=BuiltinObj.FromPortable(4,_=>throw new Exception("Host array algorithm called."));return builtins;
    }
    [Theory, MemberData(nameof(ValidCases))]
    public void PortableOperations(string name,string source,string expected) {
        using var runtime = new WasmRuntime(Tokens());Assert.True(expected==runtime.Execute(new WasmCompiler().Compile(source)).Inspect(),name);
    }
    [Theory, MemberData(nameof(InvalidCases))]
    public void InvalidArgumentsLeaveRetainedArrayUnchanged(string source) {
        var array = new ArrayObj(new IObject[] {new IntegerObj(1),new IntegerObj(2)});
        var scope=AquaEnvironment.NewEnvironment();scope.Create("a",array);
        using var runtime = new WasmRuntime(Tokens());
        Assert.Contains("Array",Assert.IsType<ErrorObj>(runtime.Execute(new WasmCompiler().Compile(source),scope)).Message);
        Assert.Equal("[1, 2]",array.Inspect());
    }
    [Theory, InlineData("Array"), InlineData("陣列")]
    public void ImportAndCatalogExposeBothNames(string name) {
        using var runtime = new WasmRuntime(new DesktopBuiltins());
        Assert.Equal("[7, 7]",runtime.Execute(new WasmCompiler().Compile($"變數 arrays=匯入(\"{name}\");arrays.建立(2,7);")).Inspect());
        Assert.Equal("真",runtime.Execute(new WasmCompiler().Compile("匯入(\"Array\")==匯入(\"陣列\");")).Inspect());
        Assert.Equal(14,LibraryCatalog.Members(name).Count());
        Assert.All(ArrayLibrary.Functions,f=>{Assert.NotNull(LibraryCatalog.Find(name,f.EnglishName));Assert.NotNull(LibraryCatalog.Find(name,f.TraditionalChineseName));});
    }
    [Fact]
    public void FunctionsSurviveHostTransportAndModuleProjection() {
        var builtins=Tokens();builtins.BuiltinFuncs["identity"]=new BuiltinObj(args=>args[0]);
        using var runtime = new WasmRuntime(builtins);
        Assert.Equal("[8, 8]",runtime.Execute(new WasmCompiler().Compile("變數 make=identity(Array.Make);make(2,8);")).Inspect());
        var module=Assert.IsType<ModuleObj>(runtime.Execute(new WasmCompiler().Compile("Array;")));
        Assert.Equal(6,Assert.IsType<BuiltinObj>(module._Environment.Get("Make",out var found)).PortableId);Assert.True(found);
    }
    [Fact]
    public void RetainedHostArrayReceivesLengthAndContentChanges() {
        var array=new ArrayObj(new IObject[]{new IntegerObj(1),new IntegerObj(2)});
        var scope=AquaEnvironment.NewEnvironment();scope.Create("a",array);
        var builtins=Tokens();builtins.BuiltinFuncs["check"]=new BuiltinObj(_=>new StringObj(array.Inspect()));
        using var runtime=new WasmRuntime(builtins);
        Assert.Equal("[9, 3]",runtime.Execute(new WasmCompiler().Compile("Array.Append(a,3);Array.Delete(a,0,1);Array.Copy(a,[9]);check();"),scope).Inspect());
    }
    [Fact]
    public void CollectionsRetainNestedValuesAndSnapshotSemantics() {
        using var runtime=new WasmRuntime(Tokens());
        var program=new WasmCompiler().Compile("""
            變數 child=[7];變數 a=Array.Make(100,child);變數 snapshot=push(a,9);
            迴圈(變數 i=0;i<100000;i++){變數 garbage=[i,i,i,i];}
            Array.Delete(a,0,99);Array.Append(a,8);child[0]=42;
            [a[0][0],a[1],snapshot[0][0],snapshot[100],Array.Len(snapshot)];
            """);
        Assert.Equal("[42, 8, 42, 9, 101]",runtime.Execute(program).Inspect());
    }
    [Fact]
    public void CollectionResetsOwnershipAndAppendFrontierOfDeadSnapshots() {
        var program=new WasmCompiler().Compile("變數 a=[1,2];變數 dead=push(a,3);a;");
        IWasmInstance instance=null!;
        var imports=new Dictionary<string,Delegate> {
            ["service"]=new Func<int,int,int,int,int,int,int,int>((op,context,subject,name,args,count,output)=>{
                Assert.Equal(0,op);var cell=instance.Memory(output,16);cell.Clear();
                BinaryPrimitives.WriteInt32LittleEndian(cell,14);BinaryPrimitives.WriteInt32LittleEndian(cell[4..],4);return 0;
            })
        };
        using(instance=new WasmtimeEngine().Instantiate(program.Bytes,imports)) {
            instance.Invoke("aqua_initialize",program.Metadata.HeapStart);
            int execution=instance.Invoke("aqua_execute",program.Metadata.ProgramAddress,0,instance.Invoke("aqua_scope",0),0);
            Assert.Equal(4,instance.Invoke("aqua_run",execution,100000));
            int result=instance.Invoke("aqua_result",execution);
            int array=BinaryPrimitives.ReadInt32LittleEndian(instance.Memory(result+4,4));
            int storage=BinaryPrimitives.ReadInt32LittleEndian(instance.Memory(array+24,4));
            int Read(int offset)=>BinaryPrimitives.ReadInt32LittleEndian(instance.Memory(storage+offset,4));
            Assert.Equal(2,Read(0));Assert.Equal(3,Read(8));
            instance.Invoke("aqua_collect");
            Assert.Equal(1,Read(0));Assert.Equal(2,Read(8));
            Assert.Equal(storage,BinaryPrimitives.ReadInt32LittleEndian(instance.Memory(array+24,4)));
            instance.Invoke("aqua_release_execution",execution);
        }
    }
}
