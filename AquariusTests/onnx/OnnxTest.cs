using AquariusLang.Compiler;
using AquariusLang.Desktop;
using AquariusLang.Desktop.Graphics;
using AquariusLang.Desktop.Onnx;
using AquariusLang.Object;
using AquariusLang.Onnx;

namespace AquariusTests.Onnx;

public class OnnxTest {
    private static byte[] Model() { using var s = typeof(OnnxTest).Assembly.GetManifestResourceStream("AquariusTests.Onnx.add.onnx")!; using var b = new MemoryStream(); s.CopyTo(b); return b.ToArray(); }
    private static IObject Call(ModuleObj m, string name, params IObject[] args) => ((BuiltinObj)m._Environment.Get(name, out _)).Fn(args);
    [Fact]
    public void TensorsOwnSnapshotsAndValidateShapeAndType() {
        float[] data = { 1, 2 }; var tensor = new OnnxTensor("float32", new long[] { 2 }, data); data[0] = 7;
        var copy = (float[])tensor.CopyData(); Assert.Equal(1, copy[0]); copy[0] = 9; Assert.Equal(1, ((float[])tensor.CopyData())[0]);
        Assert.Throws<OnnxFailure>(() => new OnnxTensor("int32", new long[] { 2 }, data));
        Assert.Throws<OnnxFailure>(() => new OnnxTensor("float32", new long[] { -1 }, data));
        Assert.Throws<OnnxFailure>(() => new OnnxTensor("float32", new long[] { 3 }, data));
        Assert.Equal(1, new OnnxTensor("float32", Array.Empty<long>(), new float[] { 1 }).Length);
        Assert.Equal(0, new OnnxTensor("float32", new long[] { 0 }, Array.Empty<float>()).Length);
    }
    [Fact]
    public async Task NativeCpuInferenceMetadataValidationAndDisposal() {
        var backend = new DesktopOnnxBackend(); Assert.True(backend.IsSupported(OnnxProvider.CPU));
        using var session = await backend.CreateSession(Model(), new(OnnxProvider.CPU));
        Assert.Equal(new[] { "X", "B" }, session.Inputs.Select(m => m.Name)); Assert.Equal("float32", session.Outputs[0].Type);
        var feeds = new Dictionary<string, OnnxTensor> { ["X"] = new("float32", new long[] { 2 }, new float[] { 1, 2 }), ["B"] = new("float32", new long[] { 2 }, new float[] { 3, 4 }) };
        var result = await session.Run(feeds, new[] { "Y" }); Assert.Equal(new float[] { 4, 6 }, (float[])result["Y"].CopyData());
        await Assert.ThrowsAsync<OnnxFailure>(async () => await session.Run(feeds, new[] { "wrong" }));
        feeds["B"] = new("float32", new long[] { 1, 2 }, new float[] { 3, 4 });
        await Assert.ThrowsAsync<OnnxFailure>(async () => await session.Run(feeds, new[] { "Y" }));
        session.Dispose(); session.Dispose(); await Assert.ThrowsAsync<OnnxFailure>(async () => await session.Run(feeds, new[] { "Y" }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await backend.CreateSession(Model(), new(OnnxProvider.CPU), new CancellationToken(true)));
        await Assert.ThrowsAsync<OnnxFailure>(async () => await backend.CreateSession(new byte[] { 1, 2 }, new(OnnxProvider.CPU)));
    }
    [Fact]
    public async Task NativeWebGpuInferenceWhenRequired() {
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_WEBGPU") != "1") return;
        var backend = new DesktopOnnxBackend(); Assert.True(backend.IsSupported(OnnxProvider.WebGPU));
        using var session = await backend.CreateSession(Model(), new(OnnxProvider.WebGPU));
        var feeds = new Dictionary<string, OnnxTensor> { ["X"] = new("float32", new long[] { 2 }, new float[] { 1, 2 }), ["B"] = new("float32", new long[] { 2 }, new float[] { 3, 4 }) };
        Assert.Equal(OnnxProvider.WebGPU, session.Provider);
        Assert.Equal(new float[] { 4, 6 }, (float[])(await session.Run(feeds, new[] { "Y" }))["Y"].CopyData());
    }
    [Fact]
    public void BilingualWasmBindingAndOwnership() {
        using var graphics = new GraphicsRuntime(); graphics.ResourcePath = _ => WriteModel(); using var builtins = new DesktopBuiltins(graphics);
        var result = new CompiledRuntime(builtins).Execute(new LoweringCompiler().Compile(@"
變數 引擎 = 匯入(""推論引擎"");
變數 工作 = 引擎.建立工作階段(""模型.onnx"", 引擎.中央處理器);
變數 甲 = 引擎.建立張量(引擎.浮點32, [2], [1, 2]);
變數 乙 = 引擎.CreateTensor(引擎.Float32, [2], [3, 4]);
變數 結果 = 工作.推論([""X"", ""B""], [甲, 乙], [""Y""]);
變數 輸出 = 結果[0]; 變數 資訊 = 工作.取得輸入資訊(); 變數 輸入 = 資訊[0];
變數 中文資料 = 輸出.取得資料(); 變數 英文資料 = 輸出.GetData();
變數 成功 = 中文資料[0] == 4 && 英文資料[1] == 6 && 輸入.名稱 == ""X"";
工作.釋放(); 工作.Dispose(); 成功;"));
        Assert.True(Assert.IsType<BooleanObj>(result).Value);
        using var first = new OnnxRuntime(new DesktopOnnxBackend(), _ => Model()); first.TryImport("ONNX", out var module);
        Assert.Same(module._Environment.Get("CreateTensor", out _), module._Environment.Get("建立張量", out _));
        Assert.IsType<ErrorObj>(Call(module, "建立張量", new StringObj("uint8"), new ArrayObj(new IObject[] { new IntegerObj(1) }), new ArrayObj(new IObject[] { new IntegerObj(256) })));
    }
    private static string WriteModel() {
        string dir = Path.Combine(AppContext.BaseDirectory, "onnx-fixtures"); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "add.onnx"); File.WriteAllBytes(path, Model()); return path;
    }
    [Fact]
    public void TensorOwnershipUsesScopeIdentityAndRejectsForeignRuntimes() {
        using var owner = new OnnxRuntime(new DesktopOnnxBackend(), _ => Model()); owner.TryImport("ONNX", out var module);
        var shape = new ArrayObj(new IObject[] { new IntegerObj(2) });
        var data = new ArrayObj(new IObject[] { new IntegerObj(1), new IntegerObj(2) });
        var tensor = Assert.IsType<ModuleObj>(Call(module, "CreateTensor", new StringObj("float32"), shape, data));
        var session = Assert.IsType<ModuleObj>(Call(module, "CreateSession", new StringObj("model"), new StringObj("CPU")));
        var names = new ArrayObj(new IObject[] { new StringObj("X"), new StringObj("B") });
        var outputs = new ArrayObj(new IObject[] { new StringObj("Y") });
        var values = new ArrayObj(new IObject[] { new ModuleObj(tensor._Environment), new ModuleObj(tensor._Environment) });
        GC.Collect(); GC.WaitForPendingFinalizers();
        Assert.IsType<ArrayObj>(Call(session, "Run", names, values, outputs));
        using var foreign = new OnnxRuntime(new DesktopOnnxBackend(), _ => Model()); foreign.TryImport("推論引擎", out var other);
        var foreignTensor = Call(other, "CreateTensor", new StringObj("float32"), shape, data);
        Assert.IsType<ErrorObj>(Call(session, "Run", names, new ArrayObj(new[] { foreignTensor, tensor }), outputs));
    }
    [Fact]
    public async Task PoseModelCpuAndWebGpuParity() {
        string? path = System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_POSE_MODEL"); if (path == null) return;
        var backend = new DesktopOnnxBackend(); var model = File.ReadAllBytes(path);
        using var cpu = await backend.CreateSession(model, new(OnnxProvider.CPU));
        var info = Assert.Single(cpu.Inputs); Assert.Equal(new long[] {1, 256, 256, 3}, info.Shape);
        var feeds = new Dictionary<string, OnnxTensor> { [info.Name] = new("float32", info.Shape, Enumerable.Repeat(0.25f, 256 * 256 * 3).ToArray()) };
        var names = cpu.Outputs.Where(m => m.Shape.Aggregate(1L, (a, b) => a * b) is 195 or 1).Select(m => m.Name).ToArray();
        var expected = await cpu.Run(feeds, names); Assert.Equal(2, expected.Count);
        Assert.All(expected.Values.SelectMany(t => (float[])t.CopyData()), v => Assert.True(float.IsFinite(v)));
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_WEBGPU") != "1") return;
        using var gpu = await backend.CreateSession(model, new(OnnxProvider.WebGPU)); var actual = await gpu.Run(feeds, names);
        foreach (string name in names) {
            var a = (float[])expected[name].CopyData(); var b = (float[])actual[name].CopyData(); Assert.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++) Assert.True(Math.Abs(a[i] - b[i]) <= 0.05f + Math.Abs(a[i]) * 0.01f, $"{name}[{i}]: {a[i]} vs {b[i]}");
        }
    }
    [Fact]
    public async Task IndependentWebGpuAdaptersCanCreateRunAndDisposeConcurrently() {
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_WEBGPU") != "1") return;
        await Task.WhenAll(Enumerable.Range(0,4).Select(worker=>Task.Run(async()=>{
            var backend=new DesktopOnnxBackend();
            for(int i=0;i<3;i++){
                using var session=await backend.CreateSession(Model(),new(OnnxProvider.WebGPU));
                var feeds=new Dictionary<string,OnnxTensor>{["X"]=new("float32",new long[]{2},new float[]{worker,1}),["B"]=new("float32",new long[]{2},new float[]{i,2})};
                var result=await session.Run(feeds,new[]{"Y"});Assert.Equal(new float[]{worker+i,3},(float[])result["Y"].CopyData());
            }
        })));
    }
}
