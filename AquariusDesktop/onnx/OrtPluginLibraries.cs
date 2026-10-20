using AquariusLang.Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;

namespace AquariusLang.Desktop.Onnx;

/// <summary>An EP device reported by a plugin library, with the hardware description used for ranking.</summary>
internal sealed record PluginDevice(OrtEpDevice Device, OnnxDeviceKind Kind, string Name, bool Software);

/// <summary>
/// One ORT plugin execution-provider library: located once, registered once per process with
/// <see cref="OrtEnv.RegisterExecutionProviderLibrary"/>, then queried for its devices. Bundled plugins (WebGPU)
/// resolve from their NuGet helper; optional plugins (OpenVINO, QNN) are discovered side by side so applications
/// opt into their large runtimes by copying the plugin folder next to the program or into
/// <c>AQUARIUS_ONNX_PROVIDER_PATH</c>.
/// </summary>
internal sealed class OrtPluginLibrary {
    /// <summary>Process-wide gate for ORT environment/device state shared by every plugin library (Dawn included).</summary>
    internal static readonly object Gate = new();
    private readonly string registration;
    private readonly Func<string?> locate;
    private readonly Func<string, bool> matches;
    private bool attempted;
    private string? failure;
    private IReadOnlyList<PluginDevice> devices = Array.Empty<PluginDevice>();
    public OnnxProvider Provider { get; }
    public bool SerializesProcess { get; }
    private OrtPluginLibrary(OnnxProvider provider, string registration, Func<string?> locate, Func<string, bool> matches, bool serializes) {
        Provider = provider; this.registration = registration; this.locate = locate; this.matches = matches; SerializesProcess = serializes;
    }

    public static OrtPluginLibrary WebGpu { get; } = new(OnnxProvider.WebGPU, "aquarius_webgpu", LocateWebGpu,
        name => name == WebGpuEp.GetEpName(), serializes: true);
    private static string? LocateWebGpu() {
        try { if (WebGpuEp.GetLibraryPath() is { } packaged && File.Exists(packaged)) return packaged; }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException or InvalidOperationException or NotSupportedException) { }
        return Find(OperatingSystem.IsWindows() ? "onnxruntime_providers_webgpu.dll" : OperatingSystem.IsMacOS() ? "libonnxruntime_providers_webgpu.dylib" : "libonnxruntime_providers_webgpu.so");
    }
    public static OrtPluginLibrary OpenVino { get; } = new(OnnxProvider.OpenVINO, "aquarius_openvino",
        () => Find(OperatingSystem.IsWindows() ? "onnxruntime_providers_openvino_plugin.dll" : "libonnxruntime_providers_openvino_plugin.so"),
        name => name.Contains("OpenVINO", StringComparison.OrdinalIgnoreCase), serializes: false);
    public static OrtPluginLibrary Qnn { get; } = new(OnnxProvider.QNN, "aquarius_qnn",
        () => OperatingSystem.IsWindows() ? Find("onnxruntime_providers_qnn.dll") : null,
        name => name.Contains("QNN", StringComparison.OrdinalIgnoreCase), serializes: false);

    /// <summary>Null when registered and enumerated; otherwise why the plugin cannot be used.</summary>
    public string? Unavailable() { Ensure(); return failure; }
    public PluginDevice? Device(OnnxDeviceKind kind) { Ensure(); return devices.FirstOrDefault(d => d.Kind == kind && !d.Software) ?? devices.FirstOrDefault(d => d.Kind == kind); }

    private void Ensure() {
        lock (Gate) {
            if (attempted) return; attempted = true;
            string? path;
            try { path = locate(); } catch (Exception e) when (e is IOException or PlatformNotSupportedException or InvalidOperationException) { path = null; failure = e.Message; return; }
            if (path == null || !File.Exists(path)) {
                failure = $"未找到 {Provider} 外掛程式庫；將其放在程式旁或 AQUARIUS_ONNX_PROVIDER_PATH 指定的資料夾。"; return;
            }
            try {
                var env = OrtEnv.Instance();
                env.RegisterExecutionProviderLibrary(registration, path);
                devices = env.GetEpDevices().Where(d => matches(d.EpName)).Select(Describe).ToArray();
                if (devices.Count == 0) failure = $"{Provider} 外掛已載入，但沒有回報可用裝置（請確認驅動程式）。";
            } catch (Exception e) when (DesktopOnnxBackend.IsEngineException(e)) {
                failure = DesktopOnnxBackend.Describe(e);
                if (System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_DIAGNOSTICS") == "1") Console.Error.WriteLine(e);
            }
        }
    }
    private static PluginDevice Describe(OrtEpDevice device) {
        var hardware = device.HardwareDevice;
        var kind = hardware.Type switch { OrtHardwareDeviceType.GPU => OnnxDeviceKind.GPU, OrtHardwareDeviceType.NPU => OnnxDeviceKind.NPU, _ => OnnxDeviceKind.CPU };
        IReadOnlyDictionary<string, string> metadata;
        try { metadata = hardware.Metadata.Entries; } catch (Exception e) when (DesktopOnnxBackend.IsEngineException(e)) { metadata = new Dictionary<string, string>(); }
        string description = metadata.TryGetValue("Description", out var d) && d.Length > 0 ? d : $"{hardware.Vendor} 0x{hardware.DeviceId:x4}";
        // Mesa llvmpipe/lavapipe (0x10005), Microsoft Basic Render/WARP (0x1414) and SwiftShader (0x1AE0 Google) are CPU rasterizers.
        bool software = kind == OnnxDeviceKind.GPU && (hardware.VendorId is 0x10005 or 0x1414 ||
            description.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) || description.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Basic Render", StringComparison.OrdinalIgnoreCase));
        return new(device, kind, $"{description} ({device.EpName})", software);
    }

    /// <summary>Search order: the program's own folders (including the self-extraction directory), then each
    /// directory in AQUARIUS_ONNX_PROVIDER_PATH (and an optional per-provider subfolder of the same name).</summary>
    internal static string? Find(string fileName) {
        var roots = new List<string> { AppContext.BaseDirectory };
        if (System.Environment.ProcessPath is { } exe && Path.GetDirectoryName(exe) is { } exeDir) roots.Add(exeDir);
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_PROVIDER_PATH") is { Length: > 0 } extra)
            roots.AddRange(extra.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (string root in roots.Distinct())
            foreach (string candidate in new[] { Path.Combine(root, fileName), Path.Combine(root, "runtimes", DesktopPlatform.Rid, "native", fileName),
                         Path.Combine(root, "onnx-providers", fileName) })
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        return null;
    }
}
