using System;

namespace AquariusLang.Onnx;

/// <summary>
/// Minimal ONNX IR 8 / opset 13 model: a 1×1 convolution with weight 2 over float32[1,1,1,1] (input "X", output "Y").
/// Hosts create and run it once to verify that an execution provider really works on this machine — driver,
/// runtime libraries (for example CUDA, cuDNN or Core ML), device creation and kernel dispatch — instead of trusting
/// name-only availability lists. A convolution is used because it reaches the vendor DNN library on every GPU/NPU
/// provider. The Android adapter embeds the same bytes.
/// </summary>
public static class OnnxProbeModel {
    public const string Base64 = "CAgSDmFxdWFyaXVzLXByb2JlOmcKDwoBWAoBVxIBWSIEQ29udhIFcHJvYmUqEwgBCAEIAQgBEAFCAVdKBAAAAEBaGwoBWBIWChQIARIQCgIIAQoCCAEKAggBCgIIAWIbCgFZEhYKFAgBEhAKAggBCgIIAQoCCAEKAggBQgQKABAN";
    public const string Input = "X", Output = "Y";
    public const float Weight = 2;
    private static readonly byte[] Data = Convert.FromBase64String(Base64);
    public static ReadOnlyMemory<byte> Bytes => Data;
    public static byte[] ToArray() => (byte[])Data.Clone();
}
