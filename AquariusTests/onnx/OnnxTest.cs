using AquariusLang.Compiler;
using AquariusLang.Desktop;
using AquariusLang.Desktop.Graphics;
using AquariusLang.Desktop.Onnx;
using AquariusLang.Object;
using AquariusLang.Onnx;
using Microsoft.ML.OnnxRuntime;

namespace AquariusTests.Onnx;

public class OnnxTest {
    private static byte[] Model() { using var s = typeof(OnnxTest).Assembly.GetManifestResourceStream("AquariusTests.Onnx.add.onnx")!; using var b = new MemoryStream(); s.CopyTo(b); return b.ToArray(); }
    private static IObject Call(ModuleObj m, string name, params IObject[] args) => ((BuiltinObj)m._Environment.Get(name, out _)).Fn(args);
    private static IObject Get(ModuleObj m, string name) => m._Environment.Get(name, out _);
    private static Dictionary<string, OnnxTensor> Feeds() => new() { ["X"] = new("float32", new long[] { 2 }, new float[] { 1, 2 }), ["B"] = new("float32", new long[] { 2 }, new float[] { 3, 4 }) };
    [Fact]
    public void TensorsOwnSnapshotsAndValidateShapeAndType() {
        float[] data = { 1, 2 }; var tensor = new OnnxTensor("float32", new long[] { 2 }, data); data[0] = 7;
        var copy = (float[])tensor.CopyData(); Assert.Equal(1, copy[0]); copy[0] = 9; Assert.Equal(1, ((float[])tensor.CopyData())[0]);
        Assert.Equal(1, tensor.AsMemory<float>().Span[0]); Assert.Throws<OnnxFailure>(() => tensor.AsMemory<int>());
        Assert.Throws<OnnxFailure>(() => new OnnxTensor("int32", new long[] { 2 }, data));
        Assert.Throws<OnnxFailure>(() => new OnnxTensor("float32", new long[] { -1 }, data));
        Assert.Throws<OnnxFailure>(() => new OnnxTensor("float32", new long[] { 3 }, data));
        Assert.Equal(1, new OnnxTensor("float32", Array.Empty<long>(), new float[] { 1 }).Length);
        Assert.Equal(0, new OnnxTensor("float32", new long[] { 0 }, Array.Empty<float>()).Length);
    }

    // ---- Platform-independent selection policy ----
    private static OnnxDevice D(string provider, OnnxDeviceKind kind, bool available = true, bool automatic = true) {
        Assert.True(OnnxProviders.TryParse(provider, out var p));
        return new($"{provider}/{kind}", p, kind, provider, available, automatic, available ? null : "missing driver");
    }
    private static readonly OnnxDevice[] Windows = {
        D("CUDA", OnnxDeviceKind.GPU), D("WebGPU", OnnxDeviceKind.GPU), D("OpenVINO", OnnxDeviceKind.NPU, available: false),
        D("TensorRT", OnnxDeviceKind.GPU, automatic: false), D("CPU", OnnxDeviceKind.CPU)
    };
    [Fact]
    public void PlannerRanksPreferencesAndKeepsExplicitListsStrict() {
        Assert.Equal(new[] { "CUDA", "WebGPU", "CPU" }, OnnxPlanner.Plan(Windows, OnnxSelection.Auto).Select(p => p.Primary.Provider.Id));
        Assert.Equal(new[] { "CUDA", "WebGPU" }, OnnxPlanner.Plan(Windows, OnnxPreference.GPU).Select(p => p.Primary.Provider.Id));
        Assert.Equal(OnnxFailureKind.Unsupported, Assert.Throws<OnnxFailure>(() => OnnxPlanner.Plan(Windows, OnnxPreference.NPU)).Kind);
        var chain = Assert.Single(OnnxPlanner.Plan(Windows, OnnxSelection.Explicit(OnnxProvider.TensorRT, OnnxProvider.CUDA)));
        Assert.Equal(new[] { "TensorRT", "CUDA", "CPU" }, chain.WithCpuFallback().Select(p => p.Id));
        Assert.Contains("missing driver", Assert.Throws<OnnxFailure>(() => OnnxPlanner.Plan(Windows, OnnxProvider.OpenVINO)).Message);
        Assert.Equal(OnnxFailureKind.Unsupported, Assert.Throws<OnnxFailure>(() => OnnxPlanner.Plan(Windows, OnnxProvider.CoreML)).Kind);
        // CPU ranked first by a host still closes the Auto chain.
        Assert.Equal(new[] { "WebGPU", "CPU" }, OnnxPlanner.Plan(new[] { D("CPU", OnnxDeviceKind.CPU), D("WebGPU", OnnxDeviceKind.GPU) }, OnnxSelection.Auto).Select(p => p.Primary.Provider.Id));
    }
    [Fact]
    public void SelectionParsesBilingualNamesPreferencesAndLists() {
        Assert.Equal(OnnxPreference.Auto, OnnxSelection.Parse("自動").Preference);
        Assert.Equal(OnnxPreference.GPU, OnnxSelection.Parse("GPU").Preference);
        Assert.Equal(OnnxPreference.NPU, OnnxSelection.Parse("神經處理器").Preference);
        Assert.Equal(new[] { OnnxProvider.CUDA }, OnnxSelection.Parse("cuda").Providers);
        Assert.Equal(new[] { OnnxProvider.WebGPU }, OnnxSelection.Parse("網頁圖形加速").Providers);
        Assert.Equal(new[] { OnnxProvider.CoreML, OnnxProvider.CPU }, OnnxSelection.Parse("CoreML", "CPU").Providers);
        Assert.Throws<OnnxFailure>(() => OnnxSelection.Parse("CUDA", "CUDA"));
        Assert.Throws<OnnxFailure>(() => OnnxSelection.Parse("DirectX"));
        Assert.Equal(OnnxProviders.All.Count, OnnxProviders.All.Select(p => p.ChineseName).Distinct().Count());
    }

    [Fact]
    public void JavaScriptAndAndroidHostsMirrorTheCoreCatalog() {
        string script = System.Text.Encoding.UTF8.GetString(AquariusLang.Hosting.HostScriptAssets.Read()["onnx-providers.mjs"]);
        foreach (var p in OnnxProviders.All)
            Assert.Contains($"['{p.Provider.Id}', '{p.EnglishName}', '{p.ChineseName}', [{string.Join(", ", p.Kinds.Select(k => $"'{k}'"))}]]", script);
        foreach (var (_, english, chinese) in OnnxProviders.Preferences) Assert.Contains($"['{english}', '{chinese}']", script);
        Assert.Contains($"CONTRACT_VERSION = {OnnxContract.Version};", script);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) {
            string java = Path.Combine(dir.FullName, "AquariusAndroid/template/app/src/main/java/org/aquariuslang/android/OnnxBackend.java");
            if (!File.Exists(java)) continue;
            Assert.Contains(OnnxProbeModel.Base64, File.ReadAllText(java)); break;
        }
    }

    private sealed class FakeBackend(params OnnxDevice[] devices) : IOnnxBackend {
        public readonly List<string> Attempts = new();
        public Func<OnnxExecutionPlan, OnnxFailure?> Fail = _ => null;
        public IReadOnlyList<OnnxDevice> GetDevices() => devices;
        public ValueTask<IOnnxSession> CreateSession(ReadOnlyMemory<byte> model, OnnxExecutionPlan plan, CancellationToken cancellation = default) {
            Attempts.Add(plan.Primary.Provider.Id);
            if (Fail(plan) is { } failure) throw failure;
            return new(new FakeSession(plan));
        }
        private sealed class FakeSession(OnnxExecutionPlan plan) : IOnnxSession {
            public OnnxExecutionPlan Plan => plan;
            public IReadOnlyList<OnnxProvider> Providers => plan.WithCpuFallback();
            public IReadOnlyList<OnnxValueInfo> Inputs => Array.Empty<OnnxValueInfo>();
            public IReadOnlyList<OnnxValueInfo> Outputs => Array.Empty<OnnxValueInfo>();
            public ValueTask<IReadOnlyDictionary<string, OnnxTensor>> Run(IReadOnlyDictionary<string, OnnxTensor> inputs, IReadOnlyList<string> outputs, CancellationToken cancellation = default) => throw new NotSupportedException();
            public void Dispose() { }
        }
    }
    [Fact]
    public async Task FactoryFallsBackOnlyForPreferencesAndOnlyOnInitializationFailures() {
        var backend = new FakeBackend(Windows) { Fail = p => p.Primary.Provider == OnnxProvider.CUDA ? new(OnnxFailureKind.Engine, "cudnn64_9.dll missing") : null };
        var attempts = new List<OnnxSelectionAttempt>();
        using (var session = await OnnxSessionFactory.Create(backend, new byte[] { 1 }, OnnxSelection.Auto, observe: attempts.Add)) Assert.Equal(OnnxProvider.WebGPU, session.Provider);
        Assert.Equal(new[] { "CUDA", "WebGPU" }, backend.Attempts); Assert.Contains("cudnn", attempts[0].Failure!.Message);
        backend.Attempts.Clear();
        Assert.Contains("cudnn", (await Assert.ThrowsAsync<OnnxFailure>(async () => await backend.CreateSession(new byte[] { 1 }, OnnxProvider.CUDA))).Message);
        Assert.Equal(new[] { "CUDA" }, backend.Attempts);
        backend.Fail = _ => new(OnnxFailureKind.InvalidArgument, "bad model"); backend.Attempts.Clear();
        await Assert.ThrowsAsync<OnnxFailure>(async () => await backend.CreateSession(new byte[] { 1 }, OnnxSelection.Auto));
        Assert.Equal(new[] { "CUDA" }, backend.Attempts);
        backend.Fail = _ => new(OnnxFailureKind.Engine, "no device");
        var all = await Assert.ThrowsAsync<OnnxFailure>(async () => await backend.CreateSession(new byte[] { 1 }, OnnxPreference.GPU));
        Assert.Contains("CUDA", all.Message); Assert.Contains("WebGPU", all.Message);
        Assert.True(backend.IsSupported(OnnxPreference.GPU)); Assert.False(backend.IsSupported(OnnxPreference.NPU)); Assert.False(backend.IsSupported(OnnxProvider.OpenVINO));
    }

    // ---- Native desktop adapter ----
    [Fact]
    public void DesktopReportsRankedDevicesWithReasons() {
        var devices = new DesktopOnnxBackend().GetDevices();
        Assert.Equal(OnnxProvider.CPU, devices[^1].Provider); Assert.True(devices[^1].Available);
        Assert.Contains(devices, d => d.Provider == OnnxProvider.WebGPU);
        Assert.All(devices.Where(d => !d.Available), d => Assert.False(string.IsNullOrWhiteSpace(d.Reason)));
        Assert.Equal(devices.Count, devices.Select(d => d.Id).Distinct().Count());
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64) {
            Assert.Equal(OnnxProvider.CUDA, devices[0].Provider);
            Assert.Contains("CUDAExecutionProvider", OrtEnv.Instance().GetAvailableProviders());
        }
        if (OperatingSystem.IsMacOS()) Assert.Equal(OnnxProvider.CoreML, devices[0].Provider);
    }
    [Fact]
    public async Task NativeCpuInferenceMetadataValidationAndDisposal() {
        var backend = new DesktopOnnxBackend(); Assert.True(backend.IsSupported(OnnxProvider.CPU));
        using var session = await backend.CreateSession(Model(), OnnxProvider.CPU);
        Assert.Equal(new[] { OnnxProvider.CPU }, session.Providers); Assert.Equal(OnnxDeviceKind.CPU, session.Device);
        Assert.Equal(new[] { "X", "B" }, session.Inputs.Select(m => m.Name)); Assert.Equal("float32", session.Outputs[0].Type);
        var feeds = Feeds();
        var result = await session.Run(feeds, new[] { "Y" }); Assert.Equal(new float[] { 4, 6 }, (float[])result["Y"].CopyData());
        Assert.Equal(new float[] { 1, 2 }, (float[])feeds["X"].CopyData());
        await Assert.ThrowsAsync<OnnxFailure>(async () => await session.Run(feeds, new[] { "wrong" }));
        feeds["B"] = new("float32", new long[] { 1, 2 }, new float[] { 3, 4 });
        await Assert.ThrowsAsync<OnnxFailure>(async () => await session.Run(feeds, new[] { "Y" }));
        session.Dispose(); session.Dispose(); await Assert.ThrowsAsync<OnnxFailure>(async () => await session.Run(feeds, new[] { "Y" }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await backend.CreateSession(Model(), OnnxProvider.CPU, new CancellationToken(true)));
        Assert.Equal(OnnxFailureKind.InvalidArgument, (await Assert.ThrowsAsync<OnnxFailure>(async () => await backend.CreateSession(new byte[] { 1, 2 }, OnnxProvider.CPU))).Kind);
    }
    [Fact]
    public async Task AutoSelectsTheBestWorkingDeviceAndEndsWithCpu() {
        var backend = new DesktopOnnxBackend();
        var expected = OnnxPlanner.Plan(backend.GetDevices(), OnnxSelection.Auto)[0].Primary;
        using var session = await backend.CreateSession(Model(), OnnxSelection.Auto);
        Assert.Equal(expected.Provider, session.Provider); Assert.Equal(OnnxProvider.CPU, session.Providers[^1]);
        Assert.Equal(new float[] { 4, 6 }, (float[])(await session.Run(Feeds(), new[] { "Y" }))["Y"].CopyData());
    }

    /// <summary>A provider whose probe passes but whose engine fails to load, like CUDA without cuDNN.</summary>
    private sealed class BrokenGpu : DesktopExecutionProvider {
        public override OnnxProvider Provider => OnnxProvider.CUDA;
        public override OnnxDeviceKind Kind => OnnxDeviceKind.GPU;
        protected override string DisplayName => "Broken GPU";
        public override ProviderStatus Probe() => new(true, DisplayName);
        public override void Append(SessionOptions options) => throw new DllNotFoundException("cudnn64_9.dll was not found");
    }
    [Fact]
    public async Task DesktopAutoFallsBackWhenAnAcceleratorFailsToInitialize() {
        var backend = new DesktopOnnxBackend(new DesktopExecutionProvider[] { new BrokenGpu(), new CpuExecutionProvider() });
        using var session = await backend.CreateSession(Model(), OnnxSelection.Auto);
        Assert.Equal(OnnxProvider.CPU, session.Provider);
        var explicitFailure = await Assert.ThrowsAsync<OnnxFailure>(async () => await backend.CreateSession(Model(), OnnxProvider.CUDA));
        Assert.Equal(OnnxFailureKind.Engine, explicitFailure.Kind); Assert.Contains("cudnn64_9.dll", explicitFailure.Message);
        await Assert.ThrowsAsync<OnnxFailure>(async () => await backend.CreateSession(Model(), OnnxPreference.GPU));
    }
    [Fact]
    public void ProbeModelRunsOnCpu() => DesktopOnnxBackend.RunProbe(new CpuExecutionProvider());

    [Fact]
    public async Task NativeWebGpuInferenceWhenRequired() {
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_WEBGPU") != "1") return;
        var backend = new DesktopOnnxBackend(); Assert.True(backend.IsSupported(OnnxProvider.WebGPU));
        using var session = await backend.CreateSession(Model(), OnnxProvider.WebGPU);
        Assert.Equal(OnnxProvider.WebGPU, session.Provider);
        Assert.Equal(new float[] { 4, 6 }, (float[])(await session.Run(Feeds(), new[] { "Y" }))["Y"].CopyData());
    }
    /// <summary>Set AQUARIUS_ONNX_REQUIRE to a provider (CUDA, TensorRT, CoreML, OpenVINO, QNN...) to require real
    /// inference on that accelerator, e.g. on a Windows machine with an NVIDIA GPU, CUDA 13 and cuDNN 9.</summary>
    [Fact]
    public async Task RequiredAcceleratorRunsRealInference() {
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_REQUIRE") is not { Length: > 0 } required) return;
        var backend = new DesktopOnnxBackend(); var selection = OnnxSelection.Parse(required.Split(','));
        var device = backend.GetDevices().First(d => d.Provider == selection.Providers[0]);
        Assert.True(device.Available, $"{device.Provider}: {device.Reason}");
        using var session = await backend.CreateSession(Model(), selection);
        Assert.Equal(selection.Providers[0], session.Provider);
        Assert.Equal(new float[] { 4, 6 }, (float[])(await session.Run(Feeds(), new[] { "Y" }))["Y"].CopyData());
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
變數 成功 = 中文資料[0] == 4 && 英文資料[1] == 6 && 輸入.名稱 == ""X"" && 工作.後端 == ""CPU"" && 工作.裝置 == ""CPU"";
工作.釋放(); 工作.Dispose(); 成功;"));
        Assert.True(Assert.IsType<BooleanObj>(result).Value);
        using var first = new OnnxRuntime(new DesktopOnnxBackend(), _ => Model()); first.TryImport("ONNX", out var module);
        Assert.Same(module._Environment.Get("CreateTensor", out _), module._Environment.Get("建立張量", out _));
        Assert.IsType<ErrorObj>(Call(module, "建立張量", new StringObj("uint8"), new ArrayObj(new IObject[] { new IntegerObj(1) }), new ArrayObj(new IObject[] { new IntegerObj(256) })));
    }
    [Fact]
    public void BilingualProviderSelectionAndDeviceListing() {
        using var graphics = new GraphicsRuntime(); graphics.ResourcePath = _ => WriteModel(); using var builtins = new DesktopBuiltins(graphics);
        var result = new CompiledRuntime(builtins).Execute(new LoweringCompiler().Compile(@"
變數 引擎 = 匯入(""推論引擎"");
變數 清單 = 引擎.取得後端清單(); 變數 最後 = 清單[len(清單) - 1];
變數 自動 = 引擎.建立工作階段(""模型.onnx"");
變數 鏈 = 自動.後端清單;
變數 成功 = 最後.後端 == 引擎.CPU && 最後.可用 && 引擎.是否支援(引擎.自動) && 引擎.Auto == ""Auto"" && 引擎.輝達CUDA == ""CUDA""
    && 引擎.圖形處理器 == 引擎.GPU && 鏈[len(鏈) - 1] == ""CPU"" && typeOf(自動.後端) == ""string"";
自動.釋放(); 成功;"));
        Assert.True(Assert.IsType<BooleanObj>(result).Value, result.Inspect());
        using var runtime = new OnnxRuntime(new DesktopOnnxBackend(), _ => Model()); runtime.TryImport("ONNX", out var module);
        Assert.IsType<ErrorObj>(Call(module, "CreateSession", new StringObj("model"), new StringObj("DirectX")));
        Assert.IsType<ErrorObj>(Call(module, "CreateSession", new StringObj("model"), new ArrayObj(new IObject[] { new StringObj("CPU"), new StringObj("CPU") })));
        Assert.IsType<ErrorObj>(Call(module, "CreateSession", new StringObj("model"), new StringObj("CoreML"), new StringObj("extra")));
        Assert.IsType<BooleanObj>(Call(module, "IsSupported", new StringObj("神經處理器")));
        var chained = Assert.IsType<ModuleObj>(Call(module, "CreateSession", new StringObj("model"), new ArrayObj(new IObject[] { new StringObj("中央處理器") })));
        Assert.Equal("CPU", Assert.IsType<StringObj>(Get(chained, "後端")).Value); Call(chained, "釋放");
        var listed = Assert.IsType<ArrayObj>(Call(module, "GetProviders"));
        Assert.All(listed.Elements, e => Assert.IsType<StringObj>(Get(Assert.IsType<ModuleObj>(e), "名稱")));
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
    public async Task PoseModelCpuAndAcceleratorParity() {
        string? path = System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_POSE_MODEL"); if (path == null) return;
        var backend = new DesktopOnnxBackend(); var model = File.ReadAllBytes(path);
        using var cpu = await backend.CreateSession(model, OnnxProvider.CPU);
        var info = Assert.Single(cpu.Inputs); Assert.Equal(new long[] {1, 256, 256, 3}, info.Shape);
        var feeds = new Dictionary<string, OnnxTensor> { [info.Name] = new("float32", info.Shape, Enumerable.Repeat(0.25f, 256 * 256 * 3).ToArray()) };
        var names = cpu.Outputs.Where(m => m.Shape.Aggregate(1L, (a, b) => a * b) is 195 or 1).Select(m => m.Name).ToArray();
        var expected = await cpu.Run(feeds, names); Assert.Equal(2, expected.Count);
        Assert.All(expected.Values.SelectMany(t => (float[])t.CopyData()), v => Assert.True(float.IsFinite(v)));
        var accelerated = new List<OnnxSelection>();
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_WEBGPU") == "1") accelerated.Add(OnnxProvider.WebGPU);
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_REQUIRE") is { Length: > 0 } required) accelerated.Add(OnnxSelection.Parse(required.Split(',')));
        foreach (var selection in accelerated) {
            using var gpu = await backend.CreateSession(model, selection); var actual = await gpu.Run(feeds, names);
            // TensorRT runs FP16 engines; allow its documented precision trade-off.
            float tolerance = gpu.Provider == OnnxProvider.TensorRT ? 0.1f : 0.05f;
            foreach (string name in names) {
                var a = (float[])expected[name].CopyData(); var b = (float[])actual[name].CopyData(); Assert.Equal(a.Length, b.Length);
                for (int i = 0; i < a.Length; i++) Assert.True(Math.Abs(a[i] - b[i]) <= tolerance + Math.Abs(a[i]) * 0.01f, $"{gpu.Provider} {name}[{i}]: {a[i]} vs {b[i]}");
            }
        }
    }
    [Fact]
    public async Task IndependentWebGpuAdaptersCanCreateRunAndDisposeConcurrently() {
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_WEBGPU") != "1") return;
        await Task.WhenAll(Enumerable.Range(0,4).Select(worker=>Task.Run(async()=>{
            var backend=new DesktopOnnxBackend();
            for(int i=0;i<3;i++){
                using var session=await backend.CreateSession(Model(),OnnxProvider.WebGPU);
                var feeds=new Dictionary<string,OnnxTensor>{["X"]=new("float32",new long[]{2},new float[]{worker,1}),["B"]=new("float32",new long[]{2},new float[]{i,2})};
                var result=await session.Run(feeds,new[]{"Y"});Assert.Equal(new float[]{worker+i,3},(float[])result["Y"].CopyData());
            }
        })));
    }
}
