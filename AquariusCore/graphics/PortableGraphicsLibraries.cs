using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AquariusLang.Compiler;
using AquariusLang.Object;
using AquariusLang.runtime;

namespace AquariusLang.Graphics;

/// <summary>Core source libraries linked as ordinary compiled Wasm functions. No algorithm host adapter.</summary>
public static class PortableGraphicsLibraries {
    public static IReadOnlyDictionary<string, string> Modules { get; } = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["Models"] = ".aquarius/Models.aqua", ["模型"] = ".aquarius/Models.aqua",
        ["PBR"] = ".aquarius/PBR.aqua",
        ["GPUMath"] = ".aquarius/GPUMath.aqua", ["GPU數學"] = ".aquarius/GPUMath.aqua"
    };
    public static string ReadSource(string path) {
        using var stream = typeof(PortableGraphicsLibraries).Assembly.GetManifestResourceStream("AquariusLang.Graphics." + Path.GetFileName(path))
            ?? throw new InvalidDataException("Missing core graphics library: " + path);
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    // Embedded sources never change and lowered programs are immutable, so each library is lowered once per process.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<LoweredProgram>> lowered = new(StringComparer.Ordinal);
    private static LoweredProgram Lowered(string path) =>
        lowered.GetOrAdd(path, p => new Lazy<LoweredProgram>(() => new LoweringCompiler().Compile(ReadSource(p)))).Value;
    public static IReadOnlyDictionary<string, LoweredProgram> Link(IReadOnlyDictionary<string, LoweredProgram> input) {
        if (input.Keys.Any(p => p.StartsWith(".aquarius/", StringComparison.OrdinalIgnoreCase)))
            throw new CompilationException("The .aquarius module directory is reserved for core libraries.");
        var result = new Dictionary<string, LoweredProgram>(input, StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<LoweredProgram>(input.Values);
        while (pending.TryDequeue(out var body)) foreach (var item in body.Constants) {
            if (item is FunctionCode function) pending.Enqueue(function.BodyCode);
            else if (item is LoweredProgram nested) pending.Enqueue(nested);
            else if (item is StringObj text && Modules.TryGetValue(text.Value, out var path) && !result.ContainsKey(path)) {
                var library = Lowered(path); result.Add(path, library); pending.Enqueue(library);
            }
        }
        return result;
    }
    public static IReadOnlyList<LibraryFunction> Functions { get; } = new LibraryFunction[] {
        new("GPUMath", "CreateDevice", "建立裝置", 0, 0, "WGPU.Device"),
        new("GPUMath", "CreateKernel", "建立核心", 3, 3),
        new("GPUMath", "Expression", "運算式", 3, 3),
        new("GPUMath", "CreateBuffer", "建立緩衝區", 3, 3, "WGPU.Buffer"),
        new("GPUMath", "Dispatch", "派送", 3, 3),
        new("GPUMath", "ReadResult", "讀取結果", 2, 2),
        new("GPUMath", "WriteInput", "寫入輸入", 4, 4),
        new("GPUMath", "Calculate", "計算", 3, 3),
        new("GPUMath", "DispatchWGSL", "派送WGSL", 4, 4),
        new("Models", "ParseOBJ", "解析OBJ", 1, 1), new("Models", "ParseMTL", "解析MTL", 1, 1),
        new("Models", "LoadOBJ", "載入OBJ", 1, 1), new("Models", "LoadMTL", "載入MTL", 1, 1),
        new("Models", "LoadAssetOBJ", "載入資產OBJ", 1, 1), new("Models", "LoadAssetMTL", "載入資產MTL", 1, 1),
        new("PBR", "CreateShader", "建立著色器", 1, 1, "Processing.Shader"),
        new("PBR", "Material", "材質", 3, 3), new("PBR", "SetMaterial", "設定材質", 2, 2),
        new("PBR", "SetLight", "設定光源", 3, 3), new("PBR", "CreateMesh", "建立網格", 2, 2),
        new("PBR", "SetEnvironment", "設定環境", 5, 5),
        new("PBR", "CreateMeshes", "建立網格群", 2, 2)
    };
}
