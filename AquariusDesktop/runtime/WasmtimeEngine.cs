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
    public static void Register() {
        WasmRuntime.EngineFactory ??= () => new WasmtimeEngine();
        WasmRuntime.ThreadBackendFactory ??= () => new DesktopThreadBackend();
    }
    public IWasmInstance Instantiate(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, Delegate> imports) => new Execution(bytes, imports);
    private sealed class Execution : IWasmInstance
    {
        private readonly Store store = new(Engine);
        private readonly Linker linker = new(Engine);
        private Wasmtime.Module? ownedModule;
        private readonly Instance instance;
        // Desktop fast path: typed Wasmtime wrappers avoid boxing every host-to-guest call, and the
        // memory export is resolved once per instance instead of on every access.
        private readonly Dictionary<string, Func<int[], int>> functions = new();
        private Memory? memory;
        private IntPtr memoryBase;
        private long memoryLength;
        private bool disposed;
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
                        case Action a: linker.DefineFunction(WasmAbi.ImportModule, item.Key, () => { RefreshMemory(); a(); }); break;
                        case Action<int> a: linker.DefineFunction(WasmAbi.ImportModule, item.Key, (int n) => { RefreshMemory(); a(n); }); break;
                        case Func<int> f: linker.DefineFunction(WasmAbi.ImportModule, item.Key, () => { RefreshMemory(); return f(); }); break;
                        case Func<int, int, int, int, int, int, int, int> f:
                            linker.DefineFunction(WasmAbi.ImportModule, item.Key, (int a, int b, int c, int d, int e, int g, int h) => { RefreshMemory(); return f(a, b, c, d, e, g, h); }); break;
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
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!functions.TryGetValue(name, out var invoke)) functions.Add(name, invoke = Bind(instance.GetFunction(name) ?? throw new InvalidDataException($"Missing Wasm runtime export: {name}"), arguments.Length));
            try { return invoke(arguments); }
            catch (WasmtimeException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
            finally { RefreshMemory(); }
        }
        private Function ExportFunction(string name) {
            ObjectDisposedException.ThrowIf(disposed, this);
            return instance.GetFunction(name) ?? throw new InvalidDataException($"Missing Wasm runtime export: {name}");
        }
        // Same exception and memory-refresh contract as Invoke, without the name lookup and argument array.
        private Exception Unwrap(WasmtimeException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); return e; }
        public Func<int> Export(string name) {
            var f = ExportFunction(name).WrapFunc<int>(); if (f == null) return () => Invoke(name);
            return () => { ObjectDisposedException.ThrowIf(disposed, this); try { return f(); } catch (WasmtimeException e) when (e.InnerException != null) { throw Unwrap(e); } finally { RefreshMemory(); } };
        }
        public Func<int, int> Export1(string name) {
            var f = ExportFunction(name).WrapFunc<int, int>(); if (f == null) return a => Invoke(name, a);
            return a => { ObjectDisposedException.ThrowIf(disposed, this); try { return f(a); } catch (WasmtimeException e) when (e.InnerException != null) { throw Unwrap(e); } finally { RefreshMemory(); } };
        }
        public Func<int, int, int> Export2(string name) {
            var f = ExportFunction(name).WrapFunc<int, int, int>(); if (f == null) return (a, b) => Invoke(name, a, b);
            return (a, b) => { ObjectDisposedException.ThrowIf(disposed, this); try { return f(a, b); } catch (WasmtimeException e) when (e.InnerException != null) { throw Unwrap(e); } finally { RefreshMemory(); } };
        }
        public Func<int, int, int, int> Export3(string name) {
            var f = ExportFunction(name).WrapFunc<int, int, int, int>(); if (f == null) return (a, b, c) => Invoke(name, a, b, c);
            return (a, b, c) => { ObjectDisposedException.ThrowIf(disposed, this); try { return f(a, b, c); } catch (WasmtimeException e) when (e.InnerException != null) { throw Unwrap(e); } finally { RefreshMemory(); } };
        }
        public Func<int, int, int, int, int> Export4(string name) {
            var f = ExportFunction(name).WrapFunc<int, int, int, int, int>(); if (f == null) return (a, b, c, d) => Invoke(name, a, b, c, d);
            return (a, b, c, d) => { ObjectDisposedException.ThrowIf(disposed, this); try { return f(a, b, c, d); } catch (WasmtimeException e) when (e.InnerException != null) { throw Unwrap(e); } finally { RefreshMemory(); } };
        }
        private static Func<int[], int> Bind(Function function, int arity)
        {
            // Every runtime export takes i32 arguments and returns one i32; other shapes use the boxed path.
            switch (arity) {
                case 0: if (function.WrapFunc<int>() is { } f0) return _ => f0(); break;
                case 1: if (function.WrapFunc<int, int>() is { } f1) return a => f1(a[0]); break;
                case 2: if (function.WrapFunc<int, int, int>() is { } f2) return a => f2(a[0], a[1]); break;
                case 3: if (function.WrapFunc<int, int, int, int>() is { } f3) return a => f3(a[0], a[1], a[2]); break;
                case 4: if (function.WrapFunc<int, int, int, int, int>() is { } f4) return a => f4(a[0], a[1], a[2], a[3]); break;
            }
            return arguments => {
                var values = new ValueBox[arguments.Length]; for (int i = 0; i < arguments.Length; i++) values[i] = arguments[i];
                return Convert.ToInt32(function.Invoke(values));
            };
        }
        private void RefreshMemory()
        {
            // Guest code (including a trapping call) can grow memory. Refresh before callbacks and
            // after every guest return, including reentrant calls, rather than per primitive read.
            if (disposed || instance == null) return;
            memory ??= instance.GetMemory("memory");
            if (memory == null) return;
            memoryBase = memory.GetPointer(); memoryLength = memory.GetLength();
        }
        public unsafe Span<byte> Memory(int address, int length)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (memory == null) RefreshMemory();
            if (memory == null) throw new InvalidDataException("Missing Wasm memory.");
            if (address < 0 || length < 0 || (long)address + length > memoryLength) throw new ArgumentOutOfRangeException(nameof(address));
            return new Span<byte>((byte*)memoryBase + address, length);
        }
        public void Dispose() { if (disposed) return; disposed = true; memoryBase = IntPtr.Zero; memoryLength = 0; linker.Dispose(); store.Dispose(); ownedModule?.Dispose(); }
    }
}
