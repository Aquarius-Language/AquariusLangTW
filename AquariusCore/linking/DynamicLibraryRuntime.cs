using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;
namespace AquariusLang.Linking;

public static class DynamicLibraryCatalog {
    public static IReadOnlyList<LibraryFunction> Functions { get; } = Array.AsReadOnly(new[] {
        new LibraryFunction("DynamicLibrary", "Load", "載入", 1, 1, "DynamicLibrary.Handle"),
        new("DynamicLibrary", "IsSupported", "是否支援", 1, 1),
        new("DynamicLibrary.Handle", "HasSymbol", "具有符號", 1, 1),
        new("DynamicLibrary.Handle", "Invoke", "呼叫位元組", 2, 2),
        new("DynamicLibrary.Handle", "Call", "呼叫", 2, 2),
        new("DynamicLibrary.Handle", "Dispose", "釋放", 0, 0)
    });
}
/// <summary>Core owns bilingual registration, validation and per-host library ownership.</summary>
public sealed class DynamicLibraryRuntime : IDisposable {
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly ModuleObj module = Record();
    private readonly HashSet<IDynamicLibrary> libraries = new();
    private readonly List<IDynamicLibrary> creationOrder = new();
    private bool disposed;
    public DynamicLibraryRuntime(IDynamicLibraryBackend backend) {
        Field(module, "Platform", "平台", new StringObj(backend.Platform));
        Field(module, "AbiVersion", "介面版本", new IntegerObj(DynamicLibraryAbi.Version));
        Bind(module, "DynamicLibrary", "IsSupported", a => new BooleanObj(backend.IsSupported(Text(a[0]))));
        Bind(module, "DynamicLibrary", "Load", a => {
            if (libraries.Count >= DynamicLibraryAbi.MaxLibraries) throw new InvalidOperationException("Too many libraries / 函式庫數量超過上限。");
            string path;
            if (a[0] is HashObj map) {
                var paths = map.Pairs.Values.ToDictionary(p => Text(p.Key), p => Text(p.Value), StringComparer.Ordinal);
                if (!paths.TryGetValue(backend.Platform, out path!) && !paths.TryGetValue("wasm", out path!))
                    throw new ArgumentException("No library for this platform / 缺少此平台的函式庫。");
            } else path = Text(a[0]);
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) throw new ArgumentException("Invalid library path / 無效的函式庫路徑。");
            var library = backend.Load(path); libraries.Add(library); creationOrder.Add(library);
            var handle = Record(); Field(handle, "Path", "路徑", new StringObj(path));
            void Live() { if (!libraries.Contains(library)) throw new InvalidOperationException("Library is closed / 函式庫已關閉。"); }
            string Symbol(IObject arg) { Live(); string name = Text(arg); DynamicLibraryAbi.Symbol(name); return name; }
            byte[] Invoke(string name, byte[] input) {
                DynamicLibraryAbi.Buffer(input.Length);
                byte[] output = library.Invoke(name, input); DynamicLibraryAbi.Buffer(output.Length); return output;
            }
            Bind(handle, "DynamicLibrary.Handle", "HasSymbol", args => new BooleanObj(library.HasSymbol(Symbol(args[0]))));
            Bind(handle, "DynamicLibrary.Handle", "Invoke", args => {
                string name = Symbol(args[0]);
                if (args[1] is not ArrayObj values) throw new ArgumentException("Expected byte array / 預期位元組陣列。");
                DynamicLibraryAbi.Buffer(values.Elements.Length);
                var bytes = values.Elements.Select(v => v is INumberObj n && n.GetNumValue() is var d && double.IsFinite(d) && d == Math.Truncate(d) && d >= 0 && d <= 255
                    ? (byte)d : throw new ArgumentException("Expected bytes in 0..255 / 位元組必須介於零至二五五。")).ToArray();
                return new ArrayObj(Invoke(name, bytes).Select(b => (IObject)new IntegerObj(b)).ToArray());
            });
            Bind(handle, "DynamicLibrary.Handle", "Call", args => {
                string name = Symbol(args[0]), text = Text(args[1]); DynamicLibraryAbi.Buffer(Utf8.GetByteCount(text));
                return new StringObj(Utf8.GetString(Invoke(name, Utf8.GetBytes(text))));
            });
            Bind(handle, "DynamicLibrary.Handle", "Dispose", _ => { if (libraries.Remove(library)) { creationOrder.Remove(library); library.Dispose(); } return RepeatedPrimitives.NULL; });
            return handle;
        });
    }
    public bool TryImport(string name, out ModuleObj result) { result = module; return name is "DynamicLibrary" or "動態函式庫"; }
    private void Bind(ModuleObj target, string group, string name, Func<IObject[], IObject> action) {
        var spec = DynamicLibraryCatalog.Functions.Single(f => f.Library == group && f.EnglishName == name);
        var fn = new BuiltinObj(args => {
            try {
                if (disposed) throw new ObjectDisposedException("DynamicLibrary / 動態函式庫");
                if (args.Length != spec.MinimumArguments) throw new ArgumentException($"Expected {spec.MinimumArguments} arguments / 參數數量錯誤。");
                return action(args);
            } catch (Exception e) { return new ErrorObj($"DynamicLibrary.動態函式庫.{name}：{e.Message}"); }
        }) { RetainedEnvironment = target._Environment };
        FunctionRegistration.Define(target._Environment, fn, spec.TraditionalChineseName, name);
    }
    private static string Text(IObject value) => value is StringObj s ? s.Value : throw new ArgumentException("Expected string / 預期字串。");
    private static ModuleObj Record() => new(AquaEnvironment.NewEnvironment());
    private static void Field(ModuleObj m, string english, string chinese, IObject value) { m._Environment.Create(english, value); m._Environment.Create(chinese, value); }
    public void Dispose() {
        if (disposed) return; disposed = true;
        var errors = new List<Exception>();
        foreach (var library in creationOrder.AsEnumerable().Reverse()) {
            try { library.Dispose(); } catch (Exception e) { errors.Add(e); }
        }
        libraries.Clear(); creationOrder.Clear();
        if (errors.Count != 0) throw new AggregateException(errors);
    }
}
