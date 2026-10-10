using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using AquariusLang.Wasm;
using Wasmtime;

namespace AquariusLang.Desktop.runtime;

/// <summary>Desktop engine adapter. A store and its host bindings belong to exactly one execution.</summary>
public sealed class WasmtimeEngine : IWasmEngine
{
    private static readonly Engine Engine = new();
    private static readonly ConditionalWeakTable<byte[], Wasmtime.Module> Modules = new();
    [ModuleInitializer]
    public static void Register() => WasmRuntime.EngineFactory ??= () => new WasmtimeEngine();
    public IWasmInstance Instantiate(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, Delegate> imports) => new Execution(bytes, imports);
    private sealed class Execution : IWasmInstance
    {
        private readonly Store store = new(Engine);
        private readonly Linker linker = new(Engine);
        private Wasmtime.Module? ownedModule;
        private readonly Instance instance;
        private readonly Dictionary<int, Func<int, int>> functions = new();
        internal Execution(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, Delegate> imports)
        {
            try
            {
                Wasmtime.Module module;
                if (MemoryMarshal.TryGetArray(bytes, out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length)
                    module = Modules.GetValue(segment.Array, key => Wasmtime.Module.FromBytes(Engine, "aquarius", key));
                else module = ownedModule = Wasmtime.Module.FromBytes(Engine, "aquarius", bytes.Span);
                foreach (var item in imports)
                {
                    switch (item.Value)
                    {
                        case Action a: linker.DefineFunction(WasmAbi.ImportModule, item.Key, a); break;
                        case Action<int> a: linker.DefineFunction(WasmAbi.ImportModule, item.Key, a); break;
                        case Func<int> f: linker.DefineFunction(WasmAbi.ImportModule, item.Key, f); break;
                        default: throw new InvalidOperationException($"Unsupported host signature: {item.Key}");
                    }
                }
                instance = linker.Instantiate(store, module);
            }
            catch (WasmtimeException e) { Dispose(); throw new InvalidDataException($"Invalid Aquarius WebAssembly module: {e.Message}", e); }
            catch { Dispose(); throw; }
        }
        public Func<int, int> GetFunction(int index)
        {
            if (functions.TryGetValue(index, out var result)) return result;
            var fn = instance.GetFunction<int, int>($"aqua_f{index}") ?? throw new InvalidDataException("Missing compiled function export.");
            result = pc =>
            {
                try { return fn(pc); }
                catch (WasmtimeException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
                catch (WasmtimeException e) { throw new InvalidDataException($"WebAssembly execution failed: {e.Message}", e); }
            };
            functions.Add(index, result); return result;
        }
        public void Dispose() { linker.Dispose(); store.Dispose(); ownedModule?.Dispose(); }
    }
}
