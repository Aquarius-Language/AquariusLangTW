using System.Collections.Generic;
using AquariusLang.runtime;

namespace AquariusLang.Onnx;

public static class OnnxCatalog {
    public static IReadOnlyList<LibraryFunction> Functions { get; } = System.Array.AsReadOnly(new[] {
        new LibraryFunction("ONNX", "IsSupported", "是否支援", 1, 1),
        new("ONNX", "GetProviders", "取得後端清單", 0, 0),
        new("ONNX", "CreateSession", "建立工作階段", 1, 2, "ONNX.Session"),
        new("ONNX", "CreateTensor", "建立張量", 3, 3, "ONNX.Tensor"),
        new("ONNX.Session", "GetInputs", "取得輸入資訊", 0, 0),
        new("ONNX.Session", "GetOutputs", "取得輸出資訊", 0, 0),
        new("ONNX.Session", "Run", "推論", 3, 3),
        new("ONNX.Session", "Dispose", "釋放", 0, 0),
        new("ONNX.Tensor", "GetData", "取得資料", 0, 0),
        new("ONNX.Tensor", "GetShape", "取得形狀", 0, 0),
        new("ONNX.Tensor", "GetType", "取得型別", 0, 0)
    });
}
