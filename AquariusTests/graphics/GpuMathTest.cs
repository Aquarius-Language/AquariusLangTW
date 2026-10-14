using AquariusLang.Compiler;
using AquariusLang.Graphics;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Wasm;
using AquariusLang.Desktop.Graphics;

namespace AquariusTests.Graphics;

public class GpuMathTest {
    private static IObject Run(string source) {
        var builtins=new Builtins(new Dictionary<string,BuiltinObj> {
            ["匯入"]=new(a=>a[0] is StringObj {Value:"陣列" or "Array"}?ArrayLibrary.CreateModule():throw new Exception("Unexpected capability import")),
            ["長度"]=BuiltinObj.FromPortable(1,_=>throw new Exception("Host length called"))
        });
        using var runtime=new WasmRuntime(builtins);return runtime.Execute(new WasmCompiler().Compile(source));
    }
    [Theory, InlineData("GPUMath"), InlineData("GPU數學")]
    public void RulesLinkIntoWasmAndBothLanguagesShareFunctions(string name) {
        var module=Assert.IsType<ModuleObj>(Run($"匯入(\"{name}\");"));
        foreach(var item in LibraryCatalog.Members(name)) {
            var fn=Assert.IsType<FunctionObj>(module._Environment.GetOwned(item.EnglishName));
            Assert.Same(fn,module._Environment.GetOwned(item.TraditionalChineseName));
            Assert.Equal(item.MinimumArguments,fn.Parameters.Length);
        }
        var program=new WasmCompiler().Compile($"匯入(\"{name}\");");
        Assert.Contains(".aquarius/GPUMath.aqua",program.Metadata.Modules.Keys);
    }
    [Fact] public void ArbitraryRulesGeneratePortableOffsetsAndGuardTheTail() {
        var kernel=Run("""
            變數 m=匯入("GPU數學");m.建立核心("fn rule(index:u32)->f32 {var sum=0.0;for(var k=0u;k<3u;k++){sum+=input0(k);}return sum+input1(index);}",[3,65],65);
            """);
        Assert.True(PortableGraphicsTest.Ok(kernel));
        Assert.Equal(68,PortableGraphicsTest.Number(PortableGraphicsTest.Field(kernel,"outputOffset")));
        Assert.Equal(2,PortableGraphicsTest.Number(PortableGraphicsTest.Field(kernel,"groups")));
        var source=Assert.IsType<StringObj>(PortableGraphicsTest.Field(kernel,"source")).Value;
        Assert.Contains("data[3u+index]",source);Assert.Contains("id.x<itemCount",source);Assert.DoesNotContain("\\n",source);
    }
    [Theory,InlineData("[0],1"),InlineData("[1.5d],1"),InlineData("[16777216],1"),InlineData("[],0"),InlineData("[],4194241")]
    public void InvalidShapesFailBeforeAllocatingOrCreatingADevice(string arguments) {
        Assert.False(PortableGraphicsTest.Ok(Run("變數 m=匯入(\"GPUMath\");m.Expression(\"2.0\","+arguments+");")));
    }
    [Theory,InlineData(-1,1),InlineData(0,0),InlineData(2,3),InlineData(int.MaxValue,1)]
    public void RangesRejectOverflowAndOutOfBounds(int offset,int count)=>Assert.Throws<ArgumentException>(()=>WgpuBufferRange.Validate(4,offset,count));
    [WgpuFact] public void RealGpuExpressionLoopsRangeWritesAndRepeatedDispatchAgreeWithCpu() {
        var value=ProcessingTest.Evaluate("""
            變數 m=匯入("GPU數學");變數 d=m.建立裝置();
            變數 k=m.運算式("input0(index)*input0(index)+input1(index)",[3,3],3);
            變數 b=m.建立緩衝區(d,k,[[1,2,3],[4,5,6]]);m.派送(d,k,b);變數 first=m.讀取結果(k,b);
            m.寫入輸入(k,b,1,[7,8,9]);m.派送(d,k,b);變數 second=m.讀取結果(k,b);
            變數 raw=b.讀取範圍(0,3);b.釋放();d.釋放();[first,second,raw];
            """);
        Assert.False(value is ErrorObj,value.Inspect());var result=Assert.IsType<ArrayObj>(value);
        Assert.Equal(new[]{5d,9d,15d},Assert.IsType<ArrayObj>(result.Elements[0]).Elements.Select(PortableGraphicsTest.Number));
        Assert.Equal(new[]{8d,12d,18d},Assert.IsType<ArrayObj>(result.Elements[1]).Elements.Select(PortableGraphicsTest.Number));
        Assert.Equal(new[]{1d,2d,3d},Assert.IsType<ArrayObj>(result.Elements[2]).Elements.Select(PortableGraphicsTest.Number));
    }
    [WgpuFact] public void ArbitraryLoopsAndCompleteWgslRunAcrossPartialWorkgroups() {
        var value=ProcessingTest.Evaluate("""
            變數 m=匯入("GPUMath");變數 d=m.CreateDevice();
            變數 k=m.CreateKernel("fn rule(index:u32)->f32 {var sum=0.0;for(var i=0u;i<input0Length;i++){sum+=input0(i);}return sum+f32(index);}",[3],65);
            變數 result=m.Calculate(d,k,[[1,2,3]]);
            變數 b=d.CreateBuffer([2,3]);
            m.DispatchWGSL(d,"@group(0) @binding(0) var<storage,read_write> data:array<f32>;@compute @workgroup_size(1) fn cs_main(@builtin(global_invocation_id) id:vec3<u32>){if(id.x<2u){data[id.x]*=4.0;}}",b,2);
            變數 raw=b.Read();b.Dispose();d.Dispose();[result,raw];
            """);
        Assert.False(value is ErrorObj,value.Inspect());var result=Assert.IsType<ArrayObj>(value);
        Assert.Equal(Enumerable.Range(0,65).Select(i=>(double)i+6),Assert.IsType<ArrayObj>(result.Elements[0]).Elements.Select(PortableGraphicsTest.Number));
        Assert.Equal(new[]{8d,12d},Assert.IsType<ArrayObj>(result.Elements[1]).Elements.Select(PortableGraphicsTest.Number));
    }
}
