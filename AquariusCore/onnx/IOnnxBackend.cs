using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AquariusLang.Onnx;

/// <summary>Version 1 host boundary. Core owns values; adapters own engine sessions and devices.</summary>
public interface IOnnxBackend {
    bool IsSupported(OnnxProvider provider);
    ValueTask<IOnnxSession> CreateSession(ReadOnlyMemory<byte> model, OnnxSessionOptions options, CancellationToken cancellation = default);
}

public interface IOnnxSession : IDisposable {
    OnnxProvider Provider { get; }
    IReadOnlyList<OnnxValueInfo> Inputs { get; }
    IReadOnlyList<OnnxValueInfo> Outputs { get; }
    ValueTask<IReadOnlyDictionary<string, OnnxTensor>> Run(IReadOnlyDictionary<string, OnnxTensor> inputs,
        IReadOnlyList<string> outputs, CancellationToken cancellation = default);
}

public enum OnnxProvider { WebGPU, CPU }
public sealed record OnnxSessionOptions(OnnxProvider Provider = OnnxProvider.WebGPU);
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
    private readonly Array data;
    public string Type { get; }
    public IReadOnlyList<long> Shape { get; }
    public int Length => data.Length;
    public OnnxTensor(string type, IReadOnlyList<long> shape, Array values) {
        bool matches = type switch {
            "float32" => values is float[], "float64" => values is double[], "int32" => values is int[],
            "uint8" => values is byte[], "bool" => values is bool[], _ => false
        };
        if (!matches) throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "不支援的張量型別或資料陣列。");
        if (shape.Count > 16 || shape.Any(d => d < 0 || d > MaxElements))
            throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "張量維度必須是非負整數，最多十六個維度。");
        long count = 1;
        try { foreach (long dimension in shape) count = checked(count * dimension); }
        catch (OverflowException) { throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "張量尺寸超過上限。"); }
        if (count > MaxElements || count != values.LongLength)
            throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "張量資料長度與形狀不符或超過上限。");
        Type = type; Shape = Array.AsReadOnly(shape.ToArray()); data = (Array)values.Clone();
    }
    public Array CopyData() => (Array)data.Clone();
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
