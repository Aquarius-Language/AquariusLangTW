using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using AquariusLang.Wasm;
using Wasmtime;

namespace AquariusLang.Desktop.runtime;

/// <summary>Desktop engine adapter. Each Wasm session owns its store and capability bindings.</summary>
public sealed class WasmtimeEngine : IWasmEngine
{
    private static readonly Engine Engine = CreateEngine();
    private static Engine CreateEngine()
    {
        using var config = new Config();
        // Wasmtime owns validation and cache keys, including target and compiler settings.
        // Read-only machines can run without an on-disk compilation cache.
        try { config.WithCacheConfig(null!); }
        catch (WasmtimeException) { }
        return new Engine(config);
    }
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
        private readonly Dictionary<string, Function> functions = new();
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
                        case Func<int, int, int, int, int, int, int, int> f: linker.DefineFunction(WasmAbi.ImportModule, item.Key, f); break;
                        default: throw new InvalidOperationException($"Unsupported host signature: {item.Key}");
                    }
                }
                instance = linker.Instantiate(store, module);
            }
            catch (WasmtimeException e) { Dispose(); throw new InvalidDataException($"Invalid Aquarius WebAssembly module: {e.Message}", e); }
            catch { Dispose(); throw; }
        }
        public int Invoke(string name, params int[] arguments)
        {
            if (!functions.TryGetValue(name, out var function)) functions.Add(name, function = instance.GetFunction(name) ?? throw new InvalidDataException($"Missing Wasm runtime export: {name}"));
            var values = new ValueBox[arguments.Length]; for (int i = 0; i < arguments.Length; i++) values[i] = arguments[i];
            try { return Convert.ToInt32(function.Invoke(values)); }
            catch (WasmtimeException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        public Span<byte> Memory(int address, int length) => (instance.GetMemory("memory") ?? throw new InvalidDataException("Missing Wasm memory.")).GetSpan(address, length);
        public void Dispose() { linker.Dispose(); store.Dispose(); ownedModule?.Dispose(); }
    }
}
