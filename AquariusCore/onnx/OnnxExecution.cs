using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AquariusLang.Onnx;

/// <summary>
/// An execution provider bound to one hardware class on this host (ORT's <c>OrtEpDevice</c>).
/// <paramref name="Id"/> is unique within one backend and lets the adapter map the device back to its native handle.
/// <paramref name="Automatic"/> is false for providers that are only used on explicit request (for example
/// TensorRT, whose first-run engine build takes long, or a software GPU adapter that is slower than CPU).
/// </summary>
public sealed record OnnxDevice(string Id, OnnxProvider Provider, OnnxDeviceKind Kind, string Name, bool Available,
    bool Automatic = true, string? Reason = null) {
    public static OnnxDevice Unavailable(string id, OnnxProvider provider, OnnxDeviceKind kind, string name, string reason) =>
        new(id, provider, kind, name, false, false, reason);
}

/// <summary>A concrete, ordered provider chain for one session. ORT appends the CPU provider implicitly.</summary>
public sealed record OnnxExecutionPlan {
    public IReadOnlyList<OnnxDevice> Devices { get; }
    public OnnxDevice Primary => Devices[0];
    public OnnxExecutionPlan(IReadOnlyList<OnnxDevice> devices) {
        if (devices.Count == 0) throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "推論計畫至少需要一個後端。");
        if (devices.Select(d => d.Provider).Distinct().Count() != devices.Count)
            throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "推論計畫的後端不得重複。");
        if (devices.FirstOrDefault(d => !d.Available) is { } missing)
            throw new OnnxFailure(OnnxFailureKind.Unsupported, $"推論後端 {missing.Provider} 目前無法使用：{missing.Reason ?? "未提供原因"}");
        Devices = Array.AsReadOnly(devices.ToArray());
    }
    public OnnxExecutionPlan(params OnnxDevice[] devices) : this((IReadOnlyList<OnnxDevice>)devices) { }
    /// <summary>The chain as the engine sees it: requested providers followed by the implicit CPU fallback.</summary>
    public IReadOnlyList<OnnxProvider> WithCpuFallback(IEnumerable<OnnxProvider>? companions = null) {
        var chain = new List<OnnxProvider>();
        foreach (var p in Devices.Select(d => d.Provider).Concat(companions ?? Array.Empty<OnnxProvider>()).Append(OnnxProvider.CPU))
            if (!chain.Contains(p)) chain.Add(p);
        return chain.AsReadOnly();
    }
    public override string ToString() => string.Join(" → ", Devices.Select(d => $"{d.Provider}({d.Kind})"));
}

/// <summary>Turns a selection into ranked candidate plans using the host's device ranking. Platform independent.</summary>
public static class OnnxPlanner {
    public static IReadOnlyList<OnnxExecutionPlan> Plan(IReadOnlyList<OnnxDevice> devices, OnnxSelection selection) {
        if (selection.Preference is not { } preference) {
            var chosen = new List<OnnxDevice>();
            foreach (var provider in selection.Providers) {
                var matches = devices.Where(d => d.Provider == provider).ToArray();
                if (matches.Length == 0) throw new OnnxFailure(OnnxFailureKind.Unsupported, $"此平台未提供 {provider} 推論後端。");
                // First available device of that provider in host rank order; report the best one's reason otherwise.
                chosen.Add(matches.FirstOrDefault(d => d.Available) ?? throw new OnnxFailure(OnnxFailureKind.Unsupported,
                    $"推論後端 {provider} 目前無法使用：{matches[0].Reason ?? "未提供原因"}"));
            }
            return new[] { new OnnxExecutionPlan(chosen) };
        }
        var candidates = devices.Where(d => d.Available && d.Automatic && preference switch {
            OnnxPreference.GPU => d.Kind == OnnxDeviceKind.GPU,
            OnnxPreference.NPU => d.Kind == OnnxDeviceKind.NPU,
            _ => true
        }).GroupBy(d => d.Provider).Select(g => g.First()).ToList();
        if (preference == OnnxPreference.Auto) {
            // CPU always closes the Auto chain, even when a host ranked it elsewhere.
            var cpu = candidates.FirstOrDefault(d => d.Provider == OnnxProvider.CPU) ?? devices.FirstOrDefault(d => d.Provider == OnnxProvider.CPU && d.Available);
            candidates.RemoveAll(d => d.Provider == OnnxProvider.CPU); if (cpu != null) candidates.Add(cpu);
        }
        if (candidates.Count == 0) throw new OnnxFailure(OnnxFailureKind.Unsupported, preference switch {
            OnnxPreference.GPU => "找不到可用的圖形處理器（GPU）推論後端。",
            OnnxPreference.NPU => "找不到可用的神經處理器（NPU）推論後端。",
            _ => "此平台沒有可用的推論後端。"
        });
        return candidates.Select(d => new OnnxExecutionPlan(d)).ToArray();
    }
}

/// <summary>Records which candidates were tried; useful for diagnostics and tests.</summary>
public sealed record OnnxSelectionAttempt(OnnxExecutionPlan Plan, OnnxFailure? Failure);

/// <summary>
/// Core selection policy shared by every host. Explicit provider lists are strict: an unavailable or failing
/// provider is an error and never becomes a different session. Preferences try ranked candidates in order and
/// move to the next one only when a provider fails to initialize on this machine.
/// </summary>
public static class OnnxSessionFactory {
    public static async ValueTask<IOnnxSession> Create(IOnnxBackend backend, ReadOnlyMemory<byte> model, OnnxSelection selection,
        CancellationToken cancellation = default, Action<OnnxSelectionAttempt>? observe = null) {
        if (backend.ContractVersion != OnnxContract.Version)
            throw new OnnxFailure(OnnxFailureKind.Unsupported, $"推論配接器契約版本 {backend.ContractVersion} 與核心版本 {OnnxContract.Version} 不相容。");
        OnnxTensor.ValidateModel(model); cancellation.ThrowIfCancellationRequested();
        var plans = OnnxPlanner.Plan(backend.GetDevices(), selection);
        var failures = new List<(OnnxExecutionPlan Plan, OnnxFailure Failure)>();
        foreach (var plan in plans) {
            try {
                var session = await backend.CreateSession(model, plan, cancellation).ConfigureAwait(false);
                observe?.Invoke(new(plan, null)); return session;
            } catch (OnnxFailure failure) when (selection.Preference != null && failure.Kind is OnnxFailureKind.Engine or OnnxFailureKind.Unsupported) {
                cancellation.ThrowIfCancellationRequested();
                observe?.Invoke(new(plan, failure)); failures.Add((plan, failure));
            }
        }
        if (failures.Count == 1) throw failures[0].Failure;
        throw new OnnxFailure(OnnxFailureKind.Engine, "所有候選推論後端都無法初始化：" +
            string.Join("；", failures.Select(f => $"{f.Plan.Primary.Provider}（{f.Failure.Message}）")), failures[^1].Failure);
    }
}

public static class OnnxBackendExtensions {
    public static ValueTask<IOnnxSession> CreateSession(this IOnnxBackend backend, ReadOnlyMemory<byte> model, OnnxSelection selection,
        CancellationToken cancellation = default) => OnnxSessionFactory.Create(backend, model, selection, cancellation);
    /// <summary>True when the selection can produce at least one plan on this host right now.</summary>
    public static bool IsSupported(this IOnnxBackend backend, OnnxSelection selection) {
        try { return OnnxPlanner.Plan(backend.GetDevices(), selection).Count > 0; }
        catch (OnnxFailure e) when (e.Kind is OnnxFailureKind.Unsupported) { return false; }
    }
    public static bool IsSupported(this IOnnxBackend backend, OnnxProvider provider) => backend.IsSupported(OnnxSelection.Explicit(provider));
}
