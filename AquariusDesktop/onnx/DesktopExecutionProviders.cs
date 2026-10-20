using System.Runtime.InteropServices;
using AquariusLang.Onnx;
using Microsoft.ML.OnnxRuntime;

namespace AquariusLang.Desktop.Onnx;

/// <summary>Probe outcome for one desktop execution device.</summary>
internal sealed record ProviderStatus(bool Available, string Name, string? Reason = null, bool Automatic = true);

/// <summary>
/// Strategy for one execution provider on one hardware class. A strategy owns everything provider-specific:
/// platform eligibility, the availability probe, tuned EP options and session-level adjustments.
/// Adding a desktop provider means adding a subclass and placing it in <see cref="DesktopProviderCatalog"/>.
/// </summary>
internal abstract class DesktopExecutionProvider {
    public abstract OnnxProvider Provider { get; }
    public abstract OnnxDeviceKind Kind { get; }
    public virtual string Id => Provider.Id;
    /// <summary>False for providers used only on explicit request.</summary>
    public virtual bool Automatic => true;
    /// <summary>Dawn-backed providers share process-wide device state; their sessions are serialized.</summary>
    public virtual bool SerializesProcess => false;
    /// <summary>Additional providers appended right after this one (e.g. CUDA behind TensorRT).</summary>
    public virtual IReadOnlyList<DesktopExecutionProvider> Companions => Array.Empty<DesktopExecutionProvider>();
    /// <summary>Appends the provider with tuned options. Must throw when the provider cannot be loaded.</summary>
    public abstract void Append(SessionOptions options);
    /// <summary>Session-level adjustments required or recommended by this provider.</summary>
    public virtual void Configure(SessionOptions options) {
        // Accelerated sessions keep CPU fallback nodes on one thread, leaving cores for capture and drawing.
        if (Kind != OnnxDeviceKind.CPU) options.IntraOpNumThreads = 1;
    }
    /// <summary>Static eligibility (RID, engine build). A non-null reason means "not on this machine".</summary>
    protected virtual string? Precondition() => null;
    protected abstract string DisplayName { get; }
    /// <summary>Default probe: build and run the probe model through this provider and check the result.</summary>
    public virtual ProviderStatus Probe() {
        if (Precondition() is { } reason) return new(false, DisplayName, reason);
        try { DesktopOnnxBackend.RunProbe(this); return new(true, DisplayName, null, Automatic); }
        catch (Exception e) when (DesktopOnnxBackend.IsEngineException(e)) { return new(false, DisplayName, DesktopOnnxBackend.Describe(e)); }
    }
    protected static bool EngineHas(string epName) {
        try { return OrtEnv.Instance().GetAvailableProviders().Contains(epName, StringComparer.Ordinal); }
        catch (Exception e) when (DesktopOnnxBackend.IsEngineException(e)) { return false; }
    }
    protected static string Rid => DesktopPlatform.Rid;
}

/// <summary>Portable RID of the running process (distro-specific runtime builds report e.g. ubuntu.22.04-x64).</summary>
internal static class DesktopPlatform {
    public static string Rid { get; } = (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "unknown")
        + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
}

/// <summary>ORT's default CPU provider (MLAS). Always last in every chain; never appended explicitly.</summary>
internal sealed class CpuExecutionProvider : DesktopExecutionProvider {
    public override OnnxProvider Provider => OnnxProvider.CPU;
    public override OnnxDeviceKind Kind => OnnxDeviceKind.CPU;
    protected override string DisplayName => $"ONNX Runtime CPU ({RuntimeInformation.ProcessArchitecture}, {System.Environment.ProcessorCount} threads)";
    public override void Append(SessionOptions options) { }
    public override ProviderStatus Probe() {
        try { _ = OrtEnv.Instance(); return new(true, DisplayName); }
        catch (Exception e) when (DesktopOnnxBackend.IsEngineException(e)) { return new(false, DisplayName, DesktopOnnxBackend.Describe(e)); }
    }
}

/// <summary>
/// NVIDIA CUDA through ORT's CUDA build (provider-bridge library loaded on first use). Requires an NVIDIA GPU with a
/// recent driver plus the CUDA 13 runtime and cuDNN 9 on the library search path. The probe runs a convolution, so
/// missing cuDNN is detected before a real model is opened.
/// </summary>
internal sealed class CudaExecutionProvider : DesktopExecutionProvider {
    public override OnnxProvider Provider => OnnxProvider.CUDA;
    public override OnnxDeviceKind Kind => OnnxDeviceKind.GPU;
    /// <summary>CUDA is opt-in: the standard runtime omits ORT's 190-270 MB CUDA/TensorRT provider libraries.</summary>
    internal const string CudaOptIn = "此執行環境未包含 CUDA 提供者（預設不含）。請在 aqua.toml 加入 [onnx] cuda = true，或以 --onnx-cuda true 建置執行檔；開發時以 -p:AquariusOnnxCuda=true 建置。";
    internal static string DeviceId => System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_CUDA_DEVICE") is { Length: > 0 } id && int.TryParse(id, out int n) && n >= 0 ? n.ToString() : "0";
    protected override string DisplayName => $"NVIDIA CUDA GPU {DeviceId}";
    private static readonly object ProbeGate = new();
    private static ProviderStatus? probed;
    /// <summary>CUDA context creation is costly; the probe result is shared with TensorRT's companion check.</summary>
    public override ProviderStatus Probe() { lock (ProbeGate) return probed ??= base.Probe(); }
    protected override string? Precondition() => Rid is not ("win-x64" or "linux-x64") ? "此平台的 ONNX Runtime 不含 CUDA。"
        : !EngineHas("CUDAExecutionProvider") ? CudaOptIn : null;
    public override void Append(SessionOptions options) {
        WindowsCudaLibraries.EnsureSearchPath();
        using var cuda = new OrtCUDAProviderOptions();
        cuda.UpdateOptions(new Dictionary<string, string> {
            ["device_id"] = DeviceId,
            // Grow the arena by exact requests: avoids doubling reservations next to the app's own GPU work.
            ["arena_extend_strategy"] = "kSameAsRequested",
            ["cudnn_conv_algo_search"] = "EXHAUSTIVE",
            ["cudnn_conv_use_max_workspace"] = "1",
            ["do_copy_in_default_stream"] = "1"
        });
        options.AppendExecutionProvider_CUDA(cuda);
    }
}

/// <summary>
/// NVIDIA TensorRT with FP16 and persistent engine/timing caches; CUDA runs nodes TensorRT does not claim.
/// Explicit only: the first session per model builds an engine, which can take from seconds to minutes.
/// </summary>
internal sealed class TensorRtExecutionProvider : DesktopExecutionProvider {
    private static readonly CudaExecutionProvider Cuda = new();
    public override OnnxProvider Provider => OnnxProvider.TensorRT;
    public override OnnxDeviceKind Kind => OnnxDeviceKind.GPU;
    public override bool Automatic => false;
    public override IReadOnlyList<DesktopExecutionProvider> Companions { get; } = new[] { Cuda };
    protected override string DisplayName => $"NVIDIA TensorRT GPU {CudaExecutionProvider.DeviceId}";
    protected override string? Precondition() => Rid is not ("win-x64" or "linux-x64") ? "此平台的 ONNX Runtime 不含 TensorRT。"
        : !EngineHas("TensorrtExecutionProvider") ? CudaExecutionProvider.CudaOptIn : null;
    internal static string CacheDirectory {
        get {
            string root = System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_CACHE") is { Length: > 0 } custom ? custom
                : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData, System.Environment.SpecialFolderOption.Create), "AquariusLang", "onnx");
            string path = Path.Combine(root, "tensorrt"); Directory.CreateDirectory(path); return path;
        }
    }
    public override void Append(SessionOptions options) {
        using var trt = new OrtTensorRTProviderOptions();
        string cache = CacheDirectory;
        trt.UpdateOptions(new Dictionary<string, string> {
            ["device_id"] = CudaExecutionProvider.DeviceId,
            ["trt_fp16_enable"] = "1",
            ["trt_engine_cache_enable"] = "1", ["trt_engine_cache_path"] = cache,
            ["trt_timing_cache_enable"] = "1", ["trt_timing_cache_path"] = cache
        });
        options.AppendExecutionProvider_Tensorrt(trt);
        Cuda.Append(options);
    }
    /// <summary>Loading the provider proves the TensorRT libraries exist; building a probe engine would cost seconds.</summary>
    public override ProviderStatus Probe() {
        if (Precondition() is { } reason) return new(false, DisplayName, reason);
        var cuda = Cuda.Probe(); if (!cuda.Available) return new(false, DisplayName, "需要可用的 CUDA：" + cuda.Reason);
        try { using var options = new SessionOptions(); Append(options); return new(true, DisplayName, null, Automatic); }
        catch (Exception e) when (DesktopOnnxBackend.IsEngineException(e)) { return new(false, DisplayName, DesktopOnnxBackend.Describe(e)); }
    }
}

/// <summary>
/// Apple Core ML using the ML Program format. The NPU device lets Core ML schedule across the Neural Engine, GPU and
/// CPU (MLComputeUnits=ALL); the GPU device pins work to Metal (CPUAndGPU) for models the Neural Engine handles poorly.
/// </summary>
internal sealed class CoreMlExecutionProvider(OnnxDeviceKind kind) : DesktopExecutionProvider {
    public override OnnxProvider Provider => OnnxProvider.CoreML;
    public override OnnxDeviceKind Kind => kind;
    public override string Id => $"CoreML/{kind}";
    protected override string DisplayName => kind == OnnxDeviceKind.NPU ? "Apple Neural Engine + GPU (Core ML)" : "Apple GPU (Core ML, Metal)";
    protected override string? Precondition() => !OperatingSystem.IsMacOS() ? "Core ML 僅適用於 macOS。"
        : !EngineHas("CoreMLExecutionProvider") ? "目前的 ONNX Runtime 不含 Core ML。" : null;
    public override void Append(SessionOptions options) => options.AppendExecutionProvider("CoreML", new Dictionary<string, string> {
        ["ModelFormat"] = "MLProgram",
        ["MLComputeUnits"] = kind == OnnxDeviceKind.NPU ? "ALL" : "CPUAndGPU",
        ["RequireStaticInputShapes"] = "0",
        ["EnableOnSubgraphs"] = "0"
    });
}

/// <summary>
/// An ORT plugin execution provider library (ABI-stable EP shipped as a separate shared library) bound to one
/// hardware class. The device comes from ORT's own device discovery (<see cref="OrtEnv.GetEpDevices"/>).
/// </summary>
internal sealed class PluginExecutionProvider(OrtPluginLibrary library, OnnxDeviceKind kind, bool automatic = true,
    IReadOnlyDictionary<string, string>? epOptions = null) : DesktopExecutionProvider {
    public override OnnxProvider Provider => library.Provider;
    public override OnnxDeviceKind Kind => kind;
    public override string Id => $"{library.Provider}/{kind}";
    public override bool Automatic => automatic;
    public override bool SerializesProcess => library.SerializesProcess;
    protected override string DisplayName => library.Provider + " " + kind;
    public override void Append(SessionOptions options) {
        var device = library.Device(kind) ?? throw new OnnxFailure(OnnxFailureKind.Unsupported, $"找不到 {library.Provider} 的 {kind} 裝置。");
        options.AppendExecutionProvider(OrtEnv.Instance(), new[] { device.Device }, epOptions ?? new Dictionary<string, string>());
    }
    public override ProviderStatus Probe() {
        try {
            if (library.Unavailable() is { } reason) return new(false, DisplayName, reason);
            var device = library.Device(kind);
            if (device == null) return new(false, DisplayName, $"{library.Provider} 未回報可用的 {kind} 裝置。");
            return new(true, device.Name, device.Software ? "軟體模擬的圖形介面卡，僅在明確指定時使用。" : null, automatic && !device.Software);
        } catch (Exception e) when (DesktopOnnxBackend.IsEngineException(e)) { return new(false, DisplayName, DesktopOnnxBackend.Describe(e)); }
    }
}

/// <summary>
/// Ranked desktop provider table per runtime identifier. Order is the Auto ranking: discrete/vendor-optimized GPU
/// paths first, the portable WebGPU path next, NPUs after GPUs (NPU support is model dependent) and CPU last.
/// </summary>
internal static class DesktopProviderCatalog {
    public static IReadOnlyList<DesktopExecutionProvider> Create() {
        var list = new List<DesktopExecutionProvider>();
        string rid = DesktopPlatform.Rid;
        var webGpu = OrtPluginLibrary.WebGpu; var openVino = OrtPluginLibrary.OpenVino; var qnn = OrtPluginLibrary.Qnn;
        switch (rid) {
            case "win-x64" or "linux-x64":
                list.Add(new CudaExecutionProvider());
                list.Add(new PluginExecutionProvider(openVino, OnnxDeviceKind.GPU));
                list.Add(new PluginExecutionProvider(webGpu, OnnxDeviceKind.GPU));
                list.Add(new PluginExecutionProvider(openVino, OnnxDeviceKind.NPU));
                list.Add(new TensorRtExecutionProvider());
                list.Add(new PluginExecutionProvider(openVino, OnnxDeviceKind.CPU, automatic: false));
                break;
            case "win-arm64":
                list.Add(new PluginExecutionProvider(qnn, OnnxDeviceKind.NPU, epOptions: new Dictionary<string, string> {
                    ["htp_performance_mode"] = "burst", ["enable_htp_fp16_precision"] = "1"
                }));
                list.Add(new PluginExecutionProvider(webGpu, OnnxDeviceKind.GPU));
                break;
            case "osx-arm64" or "osx-x64":
                list.Add(new CoreMlExecutionProvider(OnnxDeviceKind.NPU));
                list.Add(new CoreMlExecutionProvider(OnnxDeviceKind.GPU));
                list.Add(new PluginExecutionProvider(webGpu, OnnxDeviceKind.GPU));
                break;
            default:
                list.Add(new PluginExecutionProvider(webGpu, OnnxDeviceKind.GPU));
                break;
        }
        list.Add(new CpuExecutionProvider());
        return list.AsReadOnly();
    }
}
