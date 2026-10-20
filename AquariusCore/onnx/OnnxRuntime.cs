using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.Onnx;

/// <summary>Bilingual binding, validation and per-runtime ownership; engines are injected by hosts.</summary>
public sealed class OnnxRuntime : IDisposable {
    private readonly IOnnxBackend backend;
    private readonly Func<string, byte[]> readModel;
    private readonly ModuleObj module = Record();
    private readonly HashSet<IOnnxSession> sessions = new();
    private readonly ConditionalWeakTable<AquaEnvironment, OnnxTensor> tensors = new();
    private bool disposed;
    /// <summary>Optional observer for selection attempts (diagnostics).</summary>
    public Action<OnnxSelectionAttempt>? SelectionObserver { get; init; }
    public OnnxRuntime(IOnnxBackend backend, Func<string, byte[]> readModel) {
        this.backend = backend; this.readModel = readModel;
        foreach (var provider in OnnxProviders.All) Field(module, provider.EnglishName, provider.ChineseName, new StringObj(provider.Provider.Id));
        foreach (var (_, english, chinese) in OnnxProviders.Preferences) Field(module, english, chinese, new StringObj(english));
        foreach (var (english, chinese, type) in new[] { ("Float32", "浮點32", "float32"), ("Float64", "浮點64", "float64"), ("Int32", "整數32", "int32"), ("UInt8", "無號整數8", "uint8"), ("Bool", "布林", "bool") })
            Field(module, english, chinese, new StringObj(type));
        Bind(module, "ONNX", "IsSupported", a => new BooleanObj(backend.IsSupported(Selection(a[0]))));
        Bind(module, "ONNX", "GetProviders", _ => new ArrayObj(backend.GetDevices().Select(d => (IObject)Device(d)).ToArray()));
        Bind(module, "ONNX", "CreateTensor", a => Tensor(new OnnxTensor(Text(a[0]), Numbers(a[1]).Select(Dimension).ToArray(), Data(Text(a[0]), a[2]))));
        Bind(module, "ONNX", "CreateSession", a => {
            var selection = a.Length > 1 ? Selection(a[1]) : OnnxSelection.Auto; string path = Text(a[0]);
            if (sessions.Count >= OnnxTensor.MaxSessions) throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "最多只能同時建立八個推論工作階段。");
            byte[] bytes = readModel(path); OnnxTensor.ValidateModel(bytes);
            var session = OnnxSessionFactory.Create(backend, bytes, selection, observe: SelectionObserver).AsTask().GetAwaiter().GetResult(); sessions.Add(session);
            var m = Record(); Field(m, "Backend", "後端", new StringObj(session.Provider.Id));
            Field(m, "Device", "裝置", new StringObj(OnnxProviders.DeviceName(session.Device)));
            Field(m, "Providers", "後端清單", new ArrayObj(session.Providers.Select(p => (IObject)new StringObj(p.Id)).ToArray()));
            void Live() { if (!sessions.Contains(session)) throw new OnnxFailure(OnnxFailureKind.Closed, "推論工作階段已關閉。"); }
            Bind(m, "ONNX.Session", "GetInputs", _ => { Live(); return Metadata(session.Inputs); });
            Bind(m, "ONNX.Session", "GetOutputs", _ => { Live(); return Metadata(session.Outputs); });
            Bind(m, "ONNX.Session", "Dispose", _ => { if (sessions.Remove(session)) session.Dispose(); return RepeatedPrimitives.NULL; });
            Bind(m, "ONNX.Session", "Run", args => {
                Live(); var names = Array(args[0]).Select(Text).ToArray(); var values = Array(args[1]); var outputs = Array(args[2]).Select(Text).ToArray();
                if (names.Length != values.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                    throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "輸入名稱與張量數量必須相同，且名稱不得重複。");
                var feeds = new Dictionary<string, OnnxTensor>(StringComparer.Ordinal);
                for (int i = 0; i < names.Length; i++) {
                    if (values[i] is not ModuleObj t || !tensors.TryGetValue(t._Environment, out var tensor))
                        throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "必須使用同一執行環境建立的張量。");
                    feeds.Add(names[i], tensor);
                }
                OnnxTensor.ValidateRun(session.Inputs, session.Outputs, feeds, outputs);
                var result = session.Run(feeds, outputs).AsTask().GetAwaiter().GetResult();
                return new ArrayObj(outputs.Select(name => (IObject)Tensor(result[name], name)).ToArray());
            }); return m;
        });
    }
    public bool TryImport(string name, out ModuleObj result) { result = module; return name is "ONNX" or "ONNXRuntime" or "推論引擎"; }
    /// <summary>Accepts a provider/preference string or an ordered array of provider strings.</summary>
    internal static OnnxSelection Selection(IObject value) => value switch {
        StringObj s => OnnxSelection.Parse(s.Value),
        ArrayObj a => OnnxSelection.Parse(a.Elements.Select(Text).ToArray()),
        _ => throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "推論後端必須是字串或字串陣列。")
    };
    private static ModuleObj Device(OnnxDevice device) {
        var m = Record(); Field(m, "Provider", "後端", new StringObj(device.Provider.Id));
        Field(m, "Device", "裝置", new StringObj(OnnxProviders.DeviceName(device.Kind))); Field(m, "Name", "名稱", new StringObj(device.Name));
        Field(m, "Available", "可用", new BooleanObj(device.Available)); Field(m, "Automatic", "自動選用", new BooleanObj(device.Available && device.Automatic));
        Field(m, "Reason", "原因", new StringObj(device.Reason ?? "")); return m;
    }
    private ModuleObj Tensor(OnnxTensor tensor, string name = "") {
        var m = Record(); tensors.Add(m._Environment, tensor); Field(m, "Name", "名稱", new StringObj(name));
        Bind(m, "ONNX.Tensor", "GetType", _ => new StringObj(tensor.Type));
        Bind(m, "ONNX.Tensor", "GetShape", _ => new ArrayObj(tensor.Shape.Select(n => (IObject)new IntegerObj((int)n)).ToArray()));
        Bind(m, "ONNX.Tensor", "GetData", _ => new ArrayObj(tensor.CopyData().Cast<object>().Select(v => v switch {
            bool b => (IObject)new BooleanObj(b), byte b => new IntegerObj(b), int n => new IntegerObj(n),
            float f => new DoubleObj(f), double d => new DoubleObj(d), _ => throw new InvalidOperationException()
        }).ToArray())); return m;
    }
    private static ArrayObj Metadata(IReadOnlyList<OnnxValueInfo> info) => new(info.Select(v => {
        var m = Record(); Field(m, "Name", "名稱", new StringObj(v.Name)); Field(m, "Type", "型別", new StringObj(v.Type));
        Field(m, "Shape", "形狀", new ArrayObj(v.Shape.Select(d => (IObject)new IntegerObj(checked((int)d))).ToArray())); return (IObject)m;
    }).ToArray());
    private void Bind(ModuleObj target, string library, string name, Func<IObject[], IObject> action) {
        var spec = OnnxCatalog.Functions.Single(f => f.Library == library && f.EnglishName == name);
        var fn = new BuiltinObj(args => {
            try {
                if (disposed) throw new OnnxFailure(OnnxFailureKind.Closed, "推論執行環境已關閉。");
                if (args.Length < spec.MinimumArguments || args.Length > spec.MaximumArguments)
                    throw new OnnxFailure(OnnxFailureKind.InvalidArgument, spec.MinimumArguments == spec.MaximumArguments
                        ? $"需要 {spec.MinimumArguments} 個參數。" : $"需要 {spec.MinimumArguments} 至 {spec.MaximumArguments} 個參數。");
                return action(args);
            } catch (Exception e) when (e is OnnxFailure or OperationCanceledException or IOException or UnauthorizedAccessException or OverflowException) {
                return new ErrorObj($"推論引擎.{spec.TraditionalChineseName}：{e.Message}");
            }
        }) { RetainedEnvironment = target._Environment };
        FunctionRegistration.Define(target._Environment, fn, spec.TraditionalChineseName, name);
    }
    private static ModuleObj Record() => new(AquaEnvironment.NewEnvironment());
    private static void Field(ModuleObj m, string english, string chinese, IObject value) { m._Environment.Create(english, value); if (chinese != english) m._Environment.Create(chinese, value); }
    private static string Text(IObject o) => o is StringObj s ? s.Value : throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "預期字串參數。");
    private static IObject[] Array(IObject o) => o is ArrayObj a ? a.Elements : throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "預期陣列參數。");
    private static double[] Numbers(IObject o) => Array(o).Select(v => v is INumberObj n ? n.GetNumValue() : throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "預期數字陣列。")).ToArray();
    private static long Dimension(double d) => double.IsFinite(d) && d == Math.Truncate(d) && d >= 0 && d <= OnnxTensor.MaxElements ? (long)d : throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "張量維度必須是非負整數。");
    private static System.Array Data(string type, IObject o) {
        var values = Array(o); if (values.Length > OnnxTensor.MaxElements) throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "張量資料超過上限。");
        if (type == "bool") return values.Select(v => v is BooleanObj b ? b.Value : throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "布林張量必須使用布林值。")).ToArray();
        var numbers = Numbers(o);
        if (numbers.Any(n => !double.IsFinite(n))) throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "輸入張量必須使用有限數值。");
        switch (type) {
            case "float32": if (numbers.Any(n => !float.IsFinite((float)n))) break; return numbers.Select(n => (float)n).ToArray();
            case "float64": return numbers;
            case "int32": if (numbers.Any(n => n != Math.Truncate(n) || n < int.MinValue || n > int.MaxValue)) break; return numbers.Select(n => (int)n).ToArray();
            case "uint8": if (numbers.Any(n => n != Math.Truncate(n) || n < 0 || n > 255)) break; return numbers.Select(n => (byte)n).ToArray();
        }
        throw new OnnxFailure(OnnxFailureKind.InvalidArgument, "張量型別不支援或數值超出型別範圍。");
    }
    public void Dispose() {
        if (disposed) return; disposed = true; var errors = new List<Exception>();
        foreach (var session in sessions) try { session.Dispose(); } catch (Exception e) { errors.Add(e); }
        sessions.Clear(); tensors.Clear(); if (errors.Count > 0) throw new AggregateException("推論資源釋放失敗。", errors);
    }
}
