using System;
using System.Collections.Generic;
using System.Linq;

namespace AquariusLang.Onnx;

/// <summary>Hardware class an execution device runs on, matching ORT's <c>OrtHardwareDeviceType</c>.</summary>
public enum OnnxDeviceKind { CPU, GPU, NPU }

/// <summary>
/// Stable, platform-neutral execution provider identity. Values are the language-visible identifiers
/// (for example <c>"CUDA"</c>); each host maps them to its engine's EP names and tuned options.
/// </summary>
public readonly record struct OnnxProvider(string Id) {
    public static OnnxProvider CPU { get; } = new("CPU");
    public static OnnxProvider CUDA { get; } = new("CUDA");
    public static OnnxProvider TensorRT { get; } = new("TensorRT");
    public static OnnxProvider CoreML { get; } = new("CoreML");
    public static OnnxProvider WebGPU { get; } = new("WebGPU");
    public static OnnxProvider WebNN { get; } = new("WebNN");
    public static OnnxProvider NNAPI { get; } = new("NNAPI");
    public static OnnxProvider XNNPACK { get; } = new("XNNPACK");
    public static OnnxProvider OpenVINO { get; } = new("OpenVINO");
    public static OnnxProvider QNN { get; } = new("QNN");
    public override string ToString() => Id;
}

/// <summary>Catalog entry: identity, bilingual language constant and the hardware classes it can target.</summary>
public sealed record OnnxProviderDescriptor(OnnxProvider Provider, string EnglishName, string ChineseName,
    IReadOnlyList<OnnxDeviceKind> Kinds, string Summary);

/// <summary>Device-class selection policy, mirroring ORT's <c>ExecutionProviderDevicePolicy</c>.</summary>
public enum OnnxPreference {
    /// <summary>Best available device in the host's ranking, falling back down to CPU (ORT MAX_PERFORMANCE).</summary>
    Auto,
    /// <summary>Best available GPU provider; never silently becomes CPU (ORT PREFER_GPU without CPU fallback).</summary>
    GPU,
    /// <summary>Best available NPU provider; never silently becomes CPU (ORT PREFER_NPU without CPU fallback).</summary>
    NPU
}

/// <summary>
/// The single source of truth for provider identities and their bilingual names. Hosts may implement any subset;
/// a new provider is added here once and then declared by the hosts that support it.
/// </summary>
public static class OnnxProviders {
    private static readonly OnnxDeviceKind[] Cpu = { OnnxDeviceKind.CPU }, Gpu = { OnnxDeviceKind.GPU },
        Npu = { OnnxDeviceKind.NPU }, GpuNpu = { OnnxDeviceKind.GPU, OnnxDeviceKind.NPU },
        Any = { OnnxDeviceKind.GPU, OnnxDeviceKind.NPU, OnnxDeviceKind.CPU };
    public static IReadOnlyList<OnnxProviderDescriptor> All { get; } = Array.AsReadOnly(new OnnxProviderDescriptor[] {
        new(OnnxProvider.CPU, "CPU", "中央處理器", Cpu, "ONNX Runtime default CPU kernels (MLAS), Wasm SIMD in browsers."),
        new(OnnxProvider.CUDA, "CUDA", "輝達CUDA", Gpu, "NVIDIA CUDA/cuDNN on Windows and Linux x64."),
        new(OnnxProvider.TensorRT, "TensorRT", "輝達TensorRT", Gpu, "NVIDIA TensorRT engines with FP16 and engine caching; CUDA handles remaining nodes."),
        new(OnnxProvider.CoreML, "CoreML", "蘋果CoreML", GpuNpu, "Apple Core ML on the Neural Engine and Metal GPU."),
        new(OnnxProvider.WebGPU, "WebGPU", "網頁圖形加速", Gpu, "Portable GPU kernels via Dawn (D3D12/Vulkan/Metal) or the browser's WebGPU."),
        new(OnnxProvider.WebNN, "WebNN", "網頁神經網路", GpuNpu, "Browser Web Neural Network API on GPU or NPU."),
        new(OnnxProvider.NNAPI, "NNAPI", "安卓神經網路", Npu, "Android Neural Networks API vendor drivers (NPU/DSP/GPU)."),
        new(OnnxProvider.XNNPACK, "XNNPACK", "行動處理器加速", Cpu, "XNNPACK-optimized ARM/x86 CPU kernels for mobile."),
        new(OnnxProvider.OpenVINO, "OpenVINO", "英特爾OpenVINO", Any, "Intel OpenVINO plugin for Intel CPU, GPU and NPU."),
        new(OnnxProvider.QNN, "QNN", "高通QNN", GpuNpu, "Qualcomm AI Engine Direct plugin for Snapdragon NPU (HTP) and GPU.")
    });
    /// <summary>Language constants for device preferences (provider constants come from <see cref="All"/>).</summary>
    public static IReadOnlyList<(OnnxPreference Preference, string EnglishName, string ChineseName)> Preferences { get; } =
        Array.AsReadOnly(new[] { (OnnxPreference.Auto, "Auto", "自動"), (OnnxPreference.GPU, "GPU", "圖形處理器"), (OnnxPreference.NPU, "NPU", "神經處理器") });
    public static OnnxProviderDescriptor Describe(OnnxProvider provider) => All.FirstOrDefault(d => d.Provider == provider)
        ?? throw new OnnxFailure(OnnxFailureKind.InvalidArgument, $"未知的推論後端「{provider.Id}」。");
    public static bool TryParse(string text, out OnnxProvider provider) {
        var match = All.FirstOrDefault(d => string.Equals(d.Provider.Id, text, StringComparison.OrdinalIgnoreCase) || d.ChineseName == text);
        provider = match?.Provider ?? default; return match != null;
    }
    public static string DeviceName(OnnxDeviceKind kind) => kind.ToString();
}

/// <summary>
/// What a script asked for: either a device preference or an explicit, ordered provider list
/// (ORT's <c>executionProviders</c> priority list). Explicit lists are strict; preferences fall back.
/// </summary>
public sealed record OnnxSelection {
    public OnnxPreference? Preference { get; }
    public IReadOnlyList<OnnxProvider> Providers { get; }
    private OnnxSelection(OnnxPreference? preference, IReadOnlyList<OnnxProvider> providers) { Preference = preference; Providers = providers; }
    public static OnnxSelection Auto { get; } = new(OnnxPreference.Auto, Array.Empty<OnnxProvider>());
    public static OnnxSelection Prefer(OnnxPreference preference) => new(preference, Array.Empty<OnnxProvider>());
    public static OnnxSelection Explicit(params OnnxProvider[] providers) {
        if (providers.Length == 0) throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "推論後端清單不得為空。");
        if (providers.Distinct().Count() != providers.Length) throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "推論後端清單不得重複。");
        foreach (var p in providers) OnnxProviders.Describe(p);
        return new(null, Array.AsReadOnly(providers.ToArray()));
    }
    public static implicit operator OnnxSelection(OnnxProvider provider) => Explicit(provider);
    public static implicit operator OnnxSelection(OnnxPreference preference) => Prefer(preference);
    /// <summary>Parses a language value: a provider id/name, a preference name, or several providers.</summary>
    public static OnnxSelection Parse(params string[] values) {
        if (values.Length == 1) {
            string text = values[0];
            foreach (var (preference, english, chinese) in OnnxProviders.Preferences)
                if (string.Equals(text, english, StringComparison.OrdinalIgnoreCase) || text == chinese) return Prefer(preference);
        }
        if (values.Length == 0) throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "推論後端清單不得為空。");
        return Explicit(values.Select(v => OnnxProviders.TryParse(v, out var p) ? p
            : throw new OnnxFailure(OnnxFailureKind.InvalidArgument, $"未知的推論後端「{v}」。可用：自動、圖形處理器、神經處理器或 " +
                string.Join("、", OnnxProviders.All.Select(d => d.Provider.Id)) + "。")).ToArray());
    }
    public override string ToString() => Preference?.ToString() ?? string.Join(",", Providers);
}
