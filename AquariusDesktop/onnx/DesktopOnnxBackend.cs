using System.Runtime.InteropServices;
using AquariusLang.Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AquariusLang.Desktop.Onnx;

/// <summary>
/// Native ONNX Runtime adapter for Windows, Linux and macOS. Provider-specific behaviour lives in
/// <see cref="DesktopExecutionProvider"/> strategies ranked by <see cref="DesktopProviderCatalog"/>; this class only
/// reports their probed status and builds sessions for a concrete plan. Dawn (WebGPU) device state is process-wide
/// and independent of the graphics host's wgpu-native device; native handles are never interchanged.
/// </summary>
public sealed class DesktopOnnxBackend : IOnnxBackend {
    private static readonly Lazy<Probed> Shared = new(() => Probe(DesktopProviderCatalog.Create()), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lazy<Probed> probed;
    public DesktopOnnxBackend() => probed = Shared;
    /// <summary>Test seam: a custom ranked provider table, probed independently of the process-wide table.</summary>
    internal DesktopOnnxBackend(IReadOnlyList<DesktopExecutionProvider> providers) => probed = new(() => Probe(providers));

    private sealed record Probed(IReadOnlyList<OnnxDevice> Devices, IReadOnlyDictionary<string, DesktopExecutionProvider> Providers);
    private static Probed Probe(IReadOnlyList<DesktopExecutionProvider> providers) {
        var devices = new List<OnnxDevice>(); var map = new Dictionary<string, DesktopExecutionProvider>(StringComparer.Ordinal);
        foreach (var provider in providers) {
            var status = provider.Probe();
            devices.Add(new(provider.Id, provider.Provider, provider.Kind, status.Name, status.Available, status.Available && status.Automatic && provider.Automatic, status.Reason));
            map[provider.Id] = provider;
        }
        if (Diagnostics) foreach (var d in devices) Console.Error.WriteLine($"[onnx] {d.Id,-14} {(d.Available ? "available" : "unavailable"),-11} auto={d.Automatic,-5} {d.Name} {d.Reason}");
        return new(devices.AsReadOnly(), map);
    }
    internal static bool Diagnostics => System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_DIAGNOSTICS") == "1";

    public IReadOnlyList<OnnxDevice> GetDevices() {
        try { return probed.Value.Devices; }
        catch (Exception e) when (IsEngineException(e)) { return new[] { OnnxDevice.Unavailable("CPU", OnnxProvider.CPU, OnnxDeviceKind.CPU, "ONNX Runtime", Describe(e)) }; }
    }

    public ValueTask<IOnnxSession> CreateSession(ReadOnlyMemory<byte> model, OnnxExecutionPlan plan, CancellationToken cancellation = default) {
        OnnxTensor.ValidateModel(model); cancellation.ThrowIfCancellationRequested();
        var strategies = plan.Devices.Select(d => probed.Value.Providers.TryGetValue(d.Id, out var s) ? s
            : throw new OnnxFailure(OnnxFailureKind.InvalidArgument, $"推論計畫包含此主機未提供的裝置「{d.Id}」。")).ToArray();
        bool serialize = strategies.Any(s => s.SerializesProcess || s.Companions.Any(c => c.SerializesProcess));
        return Gated<ValueTask<IOnnxSession>>(serialize, () => {
            try {
                using var settings = CreateOptions(strategies);
                var native = new InferenceSession(model.ToArray(), settings);
                try {
                    cancellation.ThrowIfCancellationRequested();
                    var providers = plan.WithCpuFallback(strategies.SelectMany(s => s.Companions).Select(c => c.Provider));
                    return new((IOnnxSession)new Session(native, plan, providers, serialize));
                } catch { native.Dispose(); throw; }
            } catch (Exception e) when (IsEngineException(e)) {
                if (Diagnostics) Console.Error.WriteLine(e);
                throw Translate(e, $"ONNX 模型或推論後端 {plan.Primary.Provider} 初始化失敗：{Describe(e)}");
            }
        });
    }

    /// <summary>Runs <paramref name="action"/> under the process-wide plugin gate when the providers share native device state.</summary>
    private static T Gated<T>(bool serialize, Func<T> action) {
        if (!serialize) return action();
        lock (OrtPluginLibrary.Gate) return action();
    }

    /// <summary>Shared session tuning: full graph optimization, no idle spinning, then each provider in plan order.</summary>
    internal static SessionOptions CreateOptions(IEnumerable<DesktopExecutionProvider> strategies) {
        var settings = new SessionOptions();
        try {
            settings.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            settings.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            // Camera/UI work must retain CPU time between inference calls.
            settings.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            settings.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
            foreach (var strategy in strategies) { strategy.Configure(settings); strategy.Append(settings); }
            return settings;
        } catch { settings.Dispose(); throw; }
    }

    /// <summary>Creates and runs the probe model through one provider and verifies the numeric result.</summary>
    internal static void RunProbe(DesktopExecutionProvider strategy) {
        Gated(strategy.SerializesProcess, () => {
            using var settings = CreateOptions(new[] { strategy });
            using var session = new InferenceSession(OnnxProbeModel.ToArray(), settings);
            float[] input = { 1.5f };
            using var value = OrtValue.CreateTensorValueFromMemory(input, new long[] { 1, 1, 1, 1 });
            using var runOptions = new RunOptions();
            using var outputs = session.Run(runOptions, new[] { OnnxProbeModel.Input }, new[] { value }, new[] { OnnxProbeModel.Output });
            float result = outputs[0].GetTensorDataAsSpan<float>()[0];
            if (Math.Abs(result - input[0] * OnnxProbeModel.Weight) > 1e-3f)
                throw new OnnxFailure(OnnxFailureKind.Engine, $"{strategy.Provider} 探測模型結果錯誤（{result}）。");
            return result;
        });
    }

    internal static bool IsEngineException(Exception e) => e is OnnxRuntimeException or OnnxFailure or DllNotFoundException or EntryPointNotFoundException
        or BadImageFormatException or NotSupportedException or TypeInitializationException or FileNotFoundException or MarshalDirectiveException or InvalidOperationException;
    /// <summary>First line of the engine message: enough to tell "cudnn64_9.dll missing" from "no adapter".</summary>
    internal static string Describe(Exception e) {
        var inner = e is TypeInitializationException { InnerException: { } i } ? i : e;
        string message = inner.Message.Split('\n', 2)[0].Trim();
        // Drop ORT's "[ErrorCode:Fail] /onnxruntime_src/...cc:123 fn() [ONNXRuntimeError] : 1 : FAIL : " preamble.
        var match = System.Text.RegularExpressions.Regex.Match(message, @"\[ONNXRuntimeError\] : \d+ : \w+ : (.*)$");
        if (match.Success) message = match.Groups[1].Value;
        else if (message.StartsWith("[ErrorCode:", StringComparison.Ordinal) && message.IndexOf(']') is > 0 and var end) message = message[(end + 1)..].Trim();
        return message.Length > 400 ? message[..400] + "…" : message;
    }
    private static OnnxFailure Translate(Exception e, string message) => e switch {
        OnnxFailure failure => failure,
        // ORT prefixes messages with its (internal) status code, e.g. "[ErrorCode:InvalidProtobuf] ...".
        OnnxRuntimeException when new[] { "[ErrorCode:InvalidProtobuf]", "[ErrorCode:InvalidGraph]", "[ErrorCode:NoModel]" }.Any(e.Message.StartsWith) =>
            new OnnxFailure(OnnxFailureKind.InvalidArgument, "ONNX 模型無效或格式不受支援：" + Describe(e), e),
        _ => new OnnxFailure(OnnxFailureKind.Engine, message, e)
    };

    private sealed class Session : IOnnxSession {
        private readonly InferenceSession native;
        private readonly object gate = new();
        private readonly bool serialize;
        private bool disposed;
        public OnnxExecutionPlan Plan { get; }
        public IReadOnlyList<OnnxProvider> Providers { get; }
        public IReadOnlyList<OnnxValueInfo> Inputs { get; }
        public IReadOnlyList<OnnxValueInfo> Outputs { get; }
        public Session(InferenceSession native, OnnxExecutionPlan plan, IReadOnlyList<OnnxProvider> providers, bool serialize) {
            this.native = native; Plan = plan; Providers = providers; this.serialize = serialize;
            Inputs = Metadata(native.InputNames, native.InputMetadata); Outputs = Metadata(native.OutputNames, native.OutputMetadata);
        }
        private static IReadOnlyList<OnnxValueInfo> Metadata(IReadOnlyList<string> names, IReadOnlyDictionary<string, NodeMetadata> map) =>
            Array.AsReadOnly(names.Select(n => new OnnxValueInfo(n, map[n].IsTensor ? TypeName(map[n].ElementDataType) : "unsupported",
                Array.AsReadOnly(map[n].IsTensor ? map[n].Dimensions.Select(d => (long)d).ToArray() : Array.Empty<long>()))).ToArray());
        private static string TypeName(TensorElementType t) => t switch {
            TensorElementType.Float => "float32", TensorElementType.Double => "float64", TensorElementType.Int32 => "int32",
            TensorElementType.UInt8 => "uint8", TensorElementType.Bool => "bool", _ => "unsupported"
        };
        public ValueTask<IReadOnlyDictionary<string, OnnxTensor>> Run(IReadOnlyDictionary<string, OnnxTensor> inputs, IReadOnlyList<string> outputs, CancellationToken cancellation = default) {
            lock (gate) { lock (serialize ? OrtPluginLibrary.Gate : gate) {
                if (disposed) throw new OnnxFailure(OnnxFailureKind.Closed, "推論工作階段已關閉。");
                cancellation.ThrowIfCancellationRequested(); OnnxTensor.ValidateRun(Inputs, Outputs, inputs, outputs);
                using var runOptions = new RunOptions();
                using var registration = cancellation.Register(() => runOptions.Terminate = true);
                var names = new string[inputs.Count]; var values = new OrtValue[inputs.Count]; int count = 0;
                try {
                    foreach (var (name, tensor) in inputs) { names[count] = name; values[count] = Input(tensor); count++; }
                    using var results = native.Run(runOptions, names, values, outputs.ToArray());
                    cancellation.ThrowIfCancellationRequested();
                    var map = new Dictionary<string, OnnxTensor>(StringComparer.Ordinal);
                    for (int i = 0; i < outputs.Count; i++) map[outputs[i]] = Output(results[i]);
                    return new((IReadOnlyDictionary<string, OnnxTensor>)map);
                } catch (OnnxRuntimeException e) {
                    cancellation.ThrowIfCancellationRequested();
                    throw new OnnxFailure(OnnxFailureKind.Engine, $"ONNX 推論失敗（{Plan.Primary.Provider}）；請確認模型運算子與輸入資料：{Describe(e)}", e);
                } finally { for (int i = 0; i < count; i++) values[i].Dispose(); }
            } }
        }
        /// <summary>Binds the immutable tensor snapshot directly as ORT input memory (no copy; ORT never writes inputs).</summary>
        private static OrtValue Input(OnnxTensor tensor) {
            long[] shape = tensor.Shape.ToArray();
            return tensor.Type switch {
                "float32" => Bind<float>(tensor, shape), "float64" => Bind<double>(tensor, shape), "int32" => Bind<int>(tensor, shape),
                "uint8" => Bind<byte>(tensor, shape), "bool" => Bind<bool>(tensor, shape),
                _ => throw new OnnxFailure(OnnxFailureKind.Unsupported, "不支援的輸入張量型別。")
            };
        }
        private static OrtValue Bind<T>(OnnxTensor tensor, long[] shape) where T : unmanaged =>
            OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, MemoryMarshal.AsMemory(tensor.AsMemory<T>()), shape);
        /// <summary>One copy from ORT-owned output memory into the owned snapshot (device outputs are already downloaded by ORT).</summary>
        private static OnnxTensor Output(OrtValue value) {
            if (!value.IsTensor) throw new OnnxFailure(OnnxFailureKind.Unsupported, "此版本不支援非張量的模型輸出。");
            var info = value.GetTensorTypeAndShape(); long[] shape = info.Shape;
            if (shape.Length > OnnxTensor.MaxRank || info.ElementCount > OnnxTensor.MaxElements)
                throw new OnnxFailure(OnnxFailureKind.Engine, "模型輸出尺寸超過上限或無效。");
            return info.ElementDataType switch {
                TensorElementType.Float => OnnxTensor.TakeOwnership("float32", shape, value.GetTensorDataAsSpan<float>().ToArray()),
                TensorElementType.Double => OnnxTensor.TakeOwnership("float64", shape, value.GetTensorDataAsSpan<double>().ToArray()),
                TensorElementType.Int32 => OnnxTensor.TakeOwnership("int32", shape, value.GetTensorDataAsSpan<int>().ToArray()),
                TensorElementType.UInt8 => OnnxTensor.TakeOwnership("uint8", shape, value.GetTensorDataAsSpan<byte>().ToArray()),
                TensorElementType.Bool => OnnxTensor.TakeOwnership("bool", shape, value.GetTensorDataAsSpan<bool>().ToArray()),
                _ => throw new OnnxFailure(OnnxFailureKind.Unsupported, "此版本不支援模型輸出的張量型別。")
            };
        }
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; lock (serialize ? OrtPluginLibrary.Gate : gate) native.Dispose(); } }
    }
}
