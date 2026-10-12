using AquariusLang.Compiler;
using AquariusLang.Graphics;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Wasm;
using AquariusLang.Desktop;
using AquariusLang.Desktop.runtime;
using AquariusLang.Web;

namespace AquariusTests.Graphics;

public class PortableGraphicsTest {
    internal const string Triangle = "v 0 0 0\nv 2 0 0\nv 0 2 0\nf 1 2 3\n";
    internal static IObject Run(string source) {
        // Algorithm calls must stay in Wasm, even with no desktop or browser capability provider.
        var builtins = new Builtins(new Dictionary<string,BuiltinObj> {
            ["匯入"] = new(_ => throw new Exception("Unexpected capability import")),
            ["長度"] = BuiltinObj.FromPortable(1,_ => throw new Exception("Host length called")),
            ["加入"] = BuiltinObj.FromPortable(4,_ => throw new Exception("Host append called"))
        });
        using var runtime = new WasmRuntime(builtins);
        return runtime.Execute(new WasmCompiler().Compile(source));
    }
    internal static IObject Parse(string text,string format="OBJ") => Run("變數 m=匯入(\"Models\");m.Parse"+format+"(\""+text+"\");");
    internal static IObject Field(IObject obj,string name) => Assert.IsType<HashObj>(obj).Pairs[new StringObj(name).HashKey()].Value;
    internal static bool Ok(IObject obj) => Assert.IsType<BooleanObj>(Field(obj,"ok")).Value;

    [Fact] public void LibrariesLinkTransitivelyOnlyWhenRequestedAndCannotBeShadowed() {
        Assert.Single(new WasmCompiler().Compile("42;").Metadata.Modules);
        var program = new WasmCompiler().Compile("變數 p=匯入(\"PBR\");p.Version;");
        Assert.Equal(3,program.Metadata.Modules.Count);
        Assert.Equal(1,Assert.IsType<IntegerObj>(Run("變數 p=匯入(\"PBR\");p.Version;")).Value);
        Assert.Equal(program.Bytes.ToArray(),new WasmCompiler().Compile("變數 p=匯入(\"PBR\");p.Version;").Bytes.ToArray());
        Assert.Throws<CompilationException>(()=>new WasmCompiler().Compile(new Dictionary<string,LoweredProgram> { [".aquarius/Models.aqua"]=new LoweringCompiler().Compile("42;") },".aquarius/Models.aqua"));
    }
    [Fact] public void StringIndexIsPortableAndBoundsMatchArrayIndex() {
        var result=Assert.IsType<ArrayObj>(Run("變數 s=\"星泉\";[s[0],s[1],s[-1],s[2]];"));
        Assert.Equal("星",Assert.IsType<StringObj>(result.Elements[0]).Value);
        Assert.Equal("泉",Assert.IsType<StringObj>(result.Elements[1]).Value);
        Assert.IsType<NullObj>(result.Elements[2]);Assert.IsType<NullObj>(result.Elements[3]);
    }
    [Theory,InlineData("Models"),InlineData("PBR")] public void PortableCatalogExportsBothAliasesWithDeclaredArities(string name) {
        var library=Assert.IsType<ModuleObj>(Run("匯入(\""+name+"\");"));
        foreach(var descriptor in LibraryCatalog.Members(name)) {
            var english=Assert.IsType<FunctionObj>(library._Environment.GetOwned(descriptor.EnglishName));
            Assert.Same(english,library._Environment.GetOwned(descriptor.TraditionalChineseName));
            Assert.Equal(descriptor.MinimumArguments,english.Parameters.Length);
        }
    }
    [Fact] public void ObjPreservesNormalsUvsNegativeIndicesBoundsAndMaterialGroups() {
        var result=Parse("\uFEFF# UTF8 BOM\r\no 星泉\r\nmtllib colors.mtl\r\n"+Triangle.Replace("f 1 2 3\n","")+"vt 0 0\nvt 1 0\nvt 0 1\nvn 0 0 2\nusemtl 金屬\nf -3/1/1 -2/2/1 -1/3/1\n");
        Assert.True(Ok(result));Assert.Equal(1,Assert.IsType<IntegerObj>(Field(result,"triangleCount")).Value);
        var vertices=Assert.IsType<ArrayObj>(Field(result,"vertices"));Assert.Equal(3,vertices.Elements.Length);
        Assert.Equal(new[]{2d,0d,0d,0d,0d,1d,1d,0d},Assert.IsType<ArrayObj>(vertices.Elements[1]).Elements.Select(Number));
        var group=Assert.IsType<ArrayObj>(Field(result,"groups")).Elements[0];
        Assert.Equal("金屬",Assert.IsType<StringObj>(Field(group,"material")).Value);
        Assert.Equal("星泉",Assert.IsType<StringObj>(Field(group,"name")).Value);
        Assert.Equal(2,Number(Assert.IsType<ArrayObj>(Field(Field(result,"bounds"),"max")).Elements[0]));
    }
    [Theory,InlineData(false),InlineData(true)] public void ConcaveFacesTriangulateWithoutChangingWindingOrArea(bool reversed) {
        string positions="v 0 0 0\nv 2 0 0\nv 2 2 0\nv 1 1 0\nv 0 2 0\n";
        var result=Parse(positions+(reversed?"f 5 4 3 2 1":"f 1 2 3 4 5"));Assert.True(Ok(result));
        var vertices=Assert.IsType<ArrayObj>(Field(result,"vertices")).Elements.Select(v=>Assert.IsType<ArrayObj>(v).Elements.Select(Number).ToArray()).ToArray();
        Assert.Equal(9,vertices.Length);double area=0;
        for(int i=0;i<vertices.Length;i+=3) { var a=vertices[i];var b=vertices[i+1];var c=vertices[i+2];double cross=(b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]);Assert.True(reversed?cross<0:cross>0);area+=cross/2;Assert.Equal(reversed?-1d:1d,a[5],8); }
        Assert.Equal(reversed?-3d:3d,area,8);
    }
    [Theory]
    [InlineData("v .0 0 0\nv +2e0 0 0\nv 0 2E-0 0\nf 1 2 3")]
    [InlineData("v 0 0 0\nv 0 2 0\nv 0 0 2\nf 1 2 3")]
    [InlineData("v 0 0 0\nv 2 0 0\nv 0 0 2\nf 1 2 3")]
    public void NumericGrammarAndDominantPlaneProjectionWork(string obj) => Assert.True(Ok(Parse(obj)));
    [Theory]
    [InlineData("",0)]
    [InlineData("v NaN 0 0",1)][InlineData("v 1e99 0 0",1)][InlineData("v 1x 0 0",1)]
    [InlineData("v 0 0",1)][InlineData("vn 0 0 0",1)][InlineData("f 1 2 3",1)]
    [InlineData("v 0 0 0\nv 1 0 0\nv 2 0 0\nf 1 2 3",4)]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 0 2 3",4)]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf -4 2 3",4)]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1/1 2 3",4)]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1//1 2 3",4)]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1/ 2 3",4)]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1.0 2 3",4)]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nv 1 1 1\nf 1 2 3 4",5)]
    [InlineData("curv 1 2 3",1)]
    public void InvalidObjFailsWithLineInformation(string obj,int line) { var r=Parse(obj);Assert.False(Ok(r));Assert.Equal(line,Number(Field(r,"line")));Assert.NotEmpty(Assert.IsType<StringObj>(Field(r,"error")).Value); }
    [Fact] public void SizeTokenAndPolygonLimitsFailBeforeExpensiveWork() {
        Assert.False(Ok(Parse(new string('x',2097153))));
        Assert.False(Ok(Parse("o "+new string('x',257))));
        Assert.False(Ok(Parse(Triangle+"f "+string.Join(' ',Enumerable.Repeat("1",257)))));
        Assert.False(Ok(Parse("#"+new string('x',16384))));
    }
    [Fact] public void FinalGroupCannotBypassTheGroupLimit() {
        string obj=Triangle.Replace("f 1 2 3\n","")+string.Concat(Enumerable.Range(0,1025).Select(i=>"g group"+i+"\nf 1 2 3\n"));
        var result=Parse(obj);Assert.False(Ok(result));Assert.Contains("group limit",Assert.IsType<StringObj>(Field(result,"error")).Value);
    }
    [Fact] public void MtlImportsPbrAndConvertsLegacyShininess() {
        var r=Parse("newmtl Copper\nKd .8 .4 .1\nPm 1\nNs 98\nnewmtl Ceramic\nPr .7\n","MTL");Assert.True(Ok(r));
        var items=Assert.IsType<ArrayObj>(Field(r,"materials"));Assert.Equal(2,items.Elements.Length);
        Assert.Equal(0.1414213562373095,Number(Field(items.Elements[0],"roughness")),12);
        Assert.Equal(1,Number(Field(items.Elements[0],"metallic")));
    }
    [Theory,InlineData("newmtl a\nKd 2 0 0"),InlineData("newmtl a\nPm -1"),InlineData("newmtl a\nPr NaN"),InlineData("newmtl a\nmap_Kd image.png"),InlineData("newmtl a\nd .5"),InlineData("newmtl a\nnewmtl a"),InlineData("Kd 1 1 1")]
    public void UnsupportedOrInvalidMtlFailsExplicitly(string text)=>Assert.False(Ok(Parse(text,"MTL")));
    [Theory,InlineData("[0.4d,0.2d,0.1d],1.0d,0.0d",true),InlineData("[1,0,0],0.0d,1.0d",true),InlineData("[2,0,0],0,0",false),InlineData("[1,1,1],-1,0",false),InlineData("[1,1,1],0,1.1d",false),InlineData("[1,1,1],0.0d/0.0d,0",false)]
    public void PbrMaterialValidationRunsInWasm(string args,bool valid)=>Assert.Equal(valid,Ok(Run("變數 p=匯入(\"PBR\");p.Material("+args+");")));
    internal static double Number(IObject n)=>n switch {IntegerObj i=>i.Value,FloatObj f=>f.Value,DoubleObj d=>d.Value,_=>throw new Exception("Expected numeric value: "+n.Inspect())};
}

[Collection("Starship console output")]
public class PortableGraphicsPackagingTest {
    [AquariusLang.Desktop.Graphics.WgpuFact] public void RealGpuPixelsValidateNormalsAndMetallicRoughness() {
        Assert.True(Assert.IsType<BooleanObj>(ScriptRunner.RunFile(Path.Combine(AppContext.BaseDirectory,"examples/pbr_models/gpu-smoke.aqua"))).Value);
    }
    [Fact] public void PackagedModelsAndCoreLibrariesWorkAfterSourceFilesAreRemoved() {
        using var t=new ApplicationTestDirectory();
        string source=t.Write("main.aqua","變數 m=匯入(\"Models\");變數 f=匯入(\"Files\");變數 model=m.LoadAssetOBJ(目前工作目錄+\"/assets/model.obj\");model[\"triangleCount\"];"),asset=t.Write("assets/model.obj",PortableGraphicsTest.Triangle),wasm=t.FilePath("app.wasm");
        WasmApplication.Compile(new[]{source},wasm,t.Path,new[]{asset});File.Delete(source);File.Delete(asset);
        Assert.Equal(1,Assert.IsType<IntegerObj>(ScriptRunner.RunWasm(wasm)).Value);
        WebsiteCompiler.BuildWasm(wasm,t.FilePath("web"));
        Assert.Contains("portableLibraries",File.ReadAllText(t.FilePath("web/program.json")));
    }
}
