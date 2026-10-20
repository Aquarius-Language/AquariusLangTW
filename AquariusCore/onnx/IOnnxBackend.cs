using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AquariusLang.Onnx;

/// <summary>
/// Contract version 2 host boundary. Core owns values, the provider catalog and selection policy;
/// adapters only enumerate their ranked execution devices and open sessions for a concrete plan.
/// </summary>
/// <remarks>
/// This mirrors ONNX Runtime's own layering: a host exposes <em>EP devices</em> (an execution provider bound to a
/// CPU, GPU or NPU), and a session is created from an ordered provider list. ORT's graph partitioner assigns each
/// node to the first provider that claims it and always keeps the CPU provider as the implicit final fallback.
/// Policy (Auto/GPU/NPU preference, ranking, fallback on initialization failure) lives in
/// <see cref="OnnxSessionFactory"/> so every platform behaves identically. Adding a platform means implementing
/// these two members; adding a provider to an existing platform means declaring one more ranked device.
/// </remarks>
public interface IOnnxBackend {
    /// <summary>Contract version implemented by the host adapter.</summary>
    int ContractVersion => OnnxContract.Version;
    /// <summary>Every device this host implements, best first (the order used by <see cref="OnnxPreference.Auto"/>).
    /// Unavailable devices stay listed with a reason so scripts and diagnostics can explain the choice.</summary>
    IReadOnlyList<OnnxDevice> GetDevices();
    /// <summary>Creates a session for exactly this plan. Failure never substitutes another provider;
    /// fallback between candidates is a core policy decision.</summary>
    ValueTask<IOnnxSession> CreateSession(ReadOnlyMemory<byte> model, OnnxExecutionPlan plan, CancellationToken cancellation = default);
}

public static class OnnxContract {
    public const int Version = 2;
}

public interface IOnnxSession : IDisposable {
    /// <summary>The plan this session was created for. <see cref="OnnxExecutionPlan.Primary"/> names the main provider.</summary>
    OnnxExecutionPlan Plan { get; }
    /// <summary>The effective provider chain handed to the engine, including companions and the implicit CPU fallback.</summary>
    IReadOnlyList<OnnxProvider> Providers { get; }
    OnnxProvider Provider => Plan.Primary.Provider;
    OnnxDeviceKind Device => Plan.Primary.Kind;
    IReadOnlyList<OnnxValueInfo> Inputs { get; }
    IReadOnlyList<OnnxValueInfo> Outputs { get; }
    ValueTask<IReadOnlyDictionary<string, OnnxTensor>> Run(IReadOnlyDictionary<string, OnnxTensor> inputs,
        IReadOnlyList<string> outputs, CancellationToken cancellation = default);
}

/// <summary>Negative dimensions denote dynamic axes. Unsupported/non-tensor metadata uses type "unsupported".</summary>
public sealed record OnnxValueInfo(string Name, string Type, IReadOnlyList<long> Shape);

public enum OnnxFailureKind { InvalidArgument, Unsupported, Closed, Cancelled, Engine }
public sealed class OnnxFailure(OnnxFailureKind kind, string message, Exception? inner = null) : Exception(message, inner) {
    public OnnxFailureKind Kind { get; } = kind;
}

/// <summary>Owned, immutable CPU snapshot. GPU handles never cross this contract.</summary>
public sealed class OnnxTensor {
    public const int MaxElements = 16 * 1024 * 1024;
    public const int MaxModelBytes = 256 * 1024 * 1024;
    public const int MaxSessions = 8;
    public const int MaxRank = 16;
    private readonly Array data;
    public string Type { get; }
    public IReadOnlyList<long> Shape { get; }
    public int Length => data.Length;
    public OnnxTensor(string type, IReadOnlyList<long> shape, Array values) : this(type, shape, values, copy: true) { }
    private OnnxTensor(string type, IReadOnlyList<long> shape, Array values, bool copy) {
        bool matches = type switch {
            "float32" => values is float[], "float64" => values is double[], "int32" => values is int[],
            "uint8" => values is byte[], "bool" => values is bool[], _ => false
        };
        if (!matches) throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "不支援的張量型別或資料陣列。");
        if (shape.Count > MaxRank || shape.Any(d => d < 0 || d > MaxElements))
            throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "張量維度必須是非負整數，最多十六個維度。");
        long count = 1;
        try { foreach (long dimension in shape) count = checked(count * dimension); }
        catch (OverflowException) { throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "張量尺寸超過上限。"); }
        if (count > MaxElements || count != values.LongLength)
            throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "張量資料長度與形狀不符或超過上限。");
        Type = type; Shape = Array.AsReadOnly(shape.ToArray()); data = copy ? (Array)values.Clone() : values;
    }
    /// <summary>Adopts a freshly materialized engine output without another copy. The caller must not retain
    /// or mutate <paramref name="values"/>; adapters use this right after copying native output memory.</summary>
    public static OnnxTensor TakeOwnership(string type, IReadOnlyList<long> shape, Array values) => new(type, shape, values, copy: false);
    public Array CopyData() => (Array)data.Clone();
    /// <summary>Read-only zero-copy view for engine adapters that bind inputs directly to ORT values.
    /// Engines read but never write input tensors, preserving the snapshot guarantee.</summary>
    public ReadOnlyMemory<T> AsMemory<T>() where T : unmanaged =>
        data is T[] typed ? typed : throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "張量元素型別與要求不符。");
    public ReadOnlySpan<byte> AsBytes() => data switch {
        float[] f => MemoryMarshal.AsBytes(f.AsSpan()), double[] d => MemoryMarshal.AsBytes(d.AsSpan()),
        int[] i => MemoryMarshal.AsBytes(i.AsSpan()), byte[] b => b, bool[] z => MemoryMarshal.AsBytes(z.AsSpan()),
        _ => throw new InvalidOperationException()
    };
    public static int ElementSize(string type) => type switch {
        "float32" or "int32" => 4, "float64" => 8, "uint8" or "bool" => 1,
        _ => throw new OnnxFailure(OnnxFailureKind.Unsupported, "不支援的張量型別。")
    };
    public static void ValidateModel(ReadOnlyMemory<byte> model) {
        if (model.Length is < 1 or > MaxModelBytes) throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "模型必須是非空的 ONNX 資料，最多二百五十六 MiB。");
    }
    public static void ValidateRun(IReadOnlyList<OnnxValueInfo> metadata, IReadOnlyList<OnnxValueInfo> outputMetadata,
        IReadOnlyDictionary<string, OnnxTensor> inputs, IReadOnlyList<string> outputs) {
        if (inputs.Count != metadata.Count || metadata.Any(m => !inputs.ContainsKey(m.Name)))
            throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "必須提供每個模型輸入且不得包含未知名稱。");
        if (outputs.Count == 0 || outputs.Distinct(StringComparer.Ordinal).Count() != outputs.Count || outputs.Any(n => !outputMetadata.Any(m => m.Name == n)))
            throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "輸出名稱必須存在、不得重複且至少指定一個。");
        foreach (var m in metadata) {
            var tensor = inputs[m.Name];
            if (tensor.Type != m.Type || tensor.Shape.Count != m.Shape.Count || m.Shape.Where((d, i) => d >= 0 && d != tensor.Shape[i]).Any())
                throw new OnnxFailure(OnnxFailureKind.InvalidArgument, $"輸入「{m.Name}」的型別或形狀與模型不符。");
        }
    }
}
