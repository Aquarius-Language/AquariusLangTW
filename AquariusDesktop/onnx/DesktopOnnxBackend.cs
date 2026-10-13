using AquariusLang.Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AquariusLang.Desktop.Onnx;

/// <summary>Native ONNX Runtime adapter. WebGPU uses the official Dawn plugin, independent of graphics' wgpu-native device.</summary>
public sealed class DesktopOnnxBackend : IOnnxBackend {
    // ORT's Dawn plugin shares process-wide provider state. Serialize provider
    // discovery/session creation and GPU runs/releases across all adapters.
    // CPU inference remains concurrent; separate-session GPU use cannot race
    // initialization or disposal of the shared native device.
    private static readonly object ProviderGate = new();
    private static bool registered;
    private static OrtEpDevice? WebGpuDevice() {
        lock (ProviderGate) {
            var env = OrtEnv.Instance();
            if (!registered) {
                env.RegisterExecutionProviderLibrary("aquarius_webgpu", WebGpuEp.GetLibraryPath());
                registered = true;
            }
            return env.GetEpDevices().FirstOrDefault(d => d.EpName == WebGpuEp.GetEpName());
        }
    }
    public bool IsSupported(OnnxProvider provider) {
        if (!Enum.IsDefined(provider)) return false;
        try { return provider == OnnxProvider.CPU ? OrtEnv.Instance() != null : WebGpuDevice() != null; }
        catch (Exception e) when (e is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException or TypeInitializationException or FileNotFoundException) { return false; }
    }
    public ValueTask<IOnnxSession> CreateSession(ReadOnlyMemory<byte> model, OnnxSessionOptions options, CancellationToken cancellation = default) {
        OnnxTensor.ValidateModel(model); cancellation.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(options.Provider)) throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "不支援的推論後端。");
        lock (ProviderGate) { try {
            using var settings = new SessionOptions();
            // Camera/UI work must retain CPU time between inference calls.
            settings.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            settings.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
            if (options.Provider == OnnxProvider.WebGPU) {
                settings.IntraOpNumThreads = 1;
                var device = WebGpuDevice() ?? throw new OnnxFailure(OnnxFailureKind.Unsupported, "找不到支援 WebGPU 的圖形裝置。");
                settings.AppendExecutionProvider(OrtEnv.Instance(), new[] { device }, new Dictionary<string, string>());
            }
            var native = new InferenceSession(model.ToArray(), settings);
            try { cancellation.ThrowIfCancellationRequested(); return new((IOnnxSession)new Session(native, options.Provider)); }
            catch { native.Dispose(); throw; }
        } catch (Exception e) when (e is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException or TypeInitializationException or FileNotFoundException) {
            if (System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_DIAGNOSTICS") == "1") Console.Error.WriteLine(e);
            throw new OnnxFailure(OnnxFailureKind.Engine, "ONNX 模型或推論後端初始化失敗。請確認模型與 WebGPU 驅動程式。", e);
        } }
    }
    private sealed class Session : IOnnxSession {
        private readonly InferenceSession native;
        private readonly object gate = new();
        private bool disposed;
        public OnnxProvider Provider { get; }
        public IReadOnlyList<OnnxValueInfo> Inputs { get; }
        public IReadOnlyList<OnnxValueInfo> Outputs { get; }
        public Session(InferenceSession native, OnnxProvider provider) {
            this.native = native; Provider = provider;
            Inputs = Metadata(native.InputNames, native.InputMetadata); Outputs = Metadata(native.OutputNames, native.OutputMetadata);
        }
        private static IReadOnlyList<OnnxValueInfo> Metadata(IReadOnlyList<string> names, IReadOnlyDictionary<string, NodeMetadata> map) =>
            Array.AsReadOnly(names.Select(n => new OnnxValueInfo(n, map[n].IsTensor ? TypeName(map[n].ElementType) : "unsupported",
                Array.AsReadOnly(map[n].IsTensor ? map[n].Dimensions.Select(d => (long)d).ToArray() : Array.Empty<long>()))).ToArray());
        private static string TypeName(Type t) => t == typeof(float) ? "float32" : t == typeof(double) ? "float64" : t == typeof(int) ? "int32" : t == typeof(byte) ? "uint8" : t == typeof(bool) ? "bool" : "unsupported";
        public ValueTask<IReadOnlyDictionary<string, OnnxTensor>> Run(IReadOnlyDictionary<string, OnnxTensor> inputs, IReadOnlyList<string> outputs, CancellationToken cancellation = default) {
            lock (gate) { lock (Provider == OnnxProvider.WebGPU ? ProviderGate : gate) {
                if (disposed) throw new OnnxFailure(OnnxFailureKind.Closed, "推論工作階段已關閉。");
                cancellation.ThrowIfCancellationRequested(); OnnxTensor.ValidateRun(Inputs, Outputs, inputs, outputs);
                using var runOptions = new RunOptions();
                using var registration = cancellation.Register(() => runOptions.Terminate = true);
                try {
                    var feeds = inputs.Select(pair => Input(pair.Key, pair.Value)).ToArray();
                    using var result = native.Run(feeds, outputs.ToArray(), runOptions);
                    cancellation.ThrowIfCancellationRequested();
                    return new((IReadOnlyDictionary<string, OnnxTensor>)result.ToDictionary(v => v.Name, Output, StringComparer.Ordinal));
                } catch (OnnxRuntimeException e) {
                    cancellation.ThrowIfCancellationRequested();
                    throw new OnnxFailure(OnnxFailureKind.Engine, "ONNX 推論失敗；請確認模型運算子與輸入資料。", e);
                }
            } }
        }
        private static NamedOnnxValue Input(string name, OnnxTensor tensor) {
            var shape = tensor.Shape.Select(d => checked((int)d)).ToArray();
            return tensor.Type switch {
                "float32" => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>((float[])tensor.CopyData(), shape)),
                "float64" => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<double>((double[])tensor.CopyData(), shape)),
                "int32" => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>((int[])tensor.CopyData(), shape)),
                "uint8" => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<byte>((byte[])tensor.CopyData(), shape)),
                "bool" => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<bool>((bool[])tensor.CopyData(), shape)),
                _ => throw new OnnxFailure(OnnxFailureKind.Unsupported, "不支援的輸入張量型別。")
            };
        }
        private static OnnxTensor Copy<T>(Tensor<T> t, string type) => new(type, t.Dimensions.ToArray().Select(d => (long)d).ToArray(), t.ToArray());
        private static OnnxTensor Output(DisposableNamedOnnxValue v) => v.ElementType switch {
            TensorElementType.Float => Copy(v.AsTensor<float>(), "float32"), TensorElementType.Double => Copy(v.AsTensor<double>(), "float64"),
            TensorElementType.Int32 => Copy(v.AsTensor<int>(), "int32"), TensorElementType.UInt8 => Copy(v.AsTensor<byte>(), "uint8"),
            TensorElementType.Bool => Copy(v.AsTensor<bool>(), "bool"),
            _ => throw new OnnxFailure(OnnxFailureKind.Unsupported, "此版本不支援模型輸出的張量型別。")
        };
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; lock (Provider == OnnxProvider.WebGPU ? ProviderGate : gate) native.Dispose(); } }
    }
}
