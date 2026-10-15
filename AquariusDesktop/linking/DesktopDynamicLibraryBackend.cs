using System.Runtime.InteropServices;
using AquariusLang.Linking;
using Wasmtime;
namespace AquariusLang.Desktop.Linking;

/// <summary>Native loading is an explicit OS capability; Wasm modules execute independently.</summary>
public sealed class DesktopDynamicLibraryBackend(Func<string, string>? resolvePath = null) : IDynamicLibraryBackend {
    public string Platform => OperatingSystem.IsWindows() ? "windows" : "linux";
    public bool IsSupported(string kind) => kind == "wasm" || kind == "native" || kind == (OperatingSystem.IsWindows() ? "dll" : "so");
    public IDynamicLibrary Load(string path) {
        path = Path.GetFullPath((resolvePath ?? (p => p))(path));
        if (Path.GetExtension(path).Equals(".wasm", StringComparison.OrdinalIgnoreCase)) return new WasmLibrary(path);
        if (!Path.GetExtension(path).Equals(OperatingSystem.IsWindows() ? ".dll" : ".so", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Unsupported library format / 不支援的函式庫格式。");
        return new NativeLibraryHandle(path);
    }
    private sealed class NativeLibraryHandle : IDynamicLibrary {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Version();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Call(IntPtr input, int length, IntPtr output, int capacity);
        private readonly object gate = new();
        private IntPtr handle;
        internal NativeLibraryHandle(string path) {
            handle = NativeLibrary.Load(path);
            try {
                if (Marshal.GetDelegateForFunctionPointer<Version>(NativeLibrary.GetExport(handle, "aqua_library_abi"))() != DynamicLibraryAbi.Version)
                    throw new InvalidDataException("Unsupported library ABI / 不支援的函式庫介面版本。");
            } catch { Dispose(); throw; }
        }
        private void Live() { if (handle == IntPtr.Zero) throw new ObjectDisposedException("DynamicLibrary"); }
        public bool HasSymbol(string name) { DynamicLibraryAbi.Symbol(name); lock (gate) { Live(); return NativeLibrary.TryGetExport(handle, name, out _); } }
        public byte[] Invoke(string name, byte[] input) {
            DynamicLibraryAbi.Symbol(name); DynamicLibraryAbi.Buffer(input.Length);
            lock (gate) {
                Live(); var fn = Marshal.GetDelegateForFunctionPointer<Call>(NativeLibrary.GetExport(handle, name));
                var output = new byte[DynamicLibraryAbi.MaxBytes];
                var i = GCHandle.Alloc(input, GCHandleType.Pinned); var o = GCHandle.Alloc(output, GCHandleType.Pinned);
                try { int count = DynamicLibraryAbi.Result(fn(i.AddrOfPinnedObject(), input.Length, o.AddrOfPinnedObject(), output.Length)); return output[..count]; }
                finally { o.Free(); i.Free(); }
            }
        }
        public void Dispose() { lock (gate) { if (handle != IntPtr.Zero) NativeLibrary.Free(handle); handle = IntPtr.Zero; } }
    }
    private sealed class WasmLibrary : IDynamicLibrary {
        private readonly Engine engine;
        private readonly Store store;
        private readonly Wasmtime.Module module;
        private readonly Linker linker;
        private readonly Instance instance;
        private readonly object gate = new();
        private bool disposed;
        internal WasmLibrary(string path) {
            engine = new Engine(); store = new Store(engine); linker = new Linker(engine);
            try {
                module = Wasmtime.Module.FromFile(engine, path);
                if (module.Imports.Any()) throw new InvalidDataException("Dynamic Wasm libraries must be import-free / 動態 Wasm 函式庫不得匯入主機函式。");
                instance = linker.Instantiate(store, module);
                if (Get("aqua_library_abi").Invoke() is not int version || version != DynamicLibraryAbi.Version || instance.GetMemory("memory") == null)
                    throw new InvalidDataException("Unsupported library ABI / 不支援的函式庫介面版本。");
                _ = Get("aqua_alloc"); _ = Get("aqua_free");
            } catch { module?.Dispose(); linker.Dispose(); store.Dispose(); engine.Dispose(); throw; }
        }
        private Function Get(string name) => instance.GetFunction(name) ?? throw new EntryPointNotFoundException(name);
        private void Live() { if (disposed) throw new ObjectDisposedException("DynamicLibrary"); }
        public bool HasSymbol(string name) { DynamicLibraryAbi.Symbol(name); lock (gate) { Live(); return instance.GetFunction(name) != null; } }
        public byte[] Invoke(string name, byte[] input) {
            DynamicLibraryAbi.Symbol(name); DynamicLibraryAbi.Buffer(input.Length);
            lock (gate) {
                Live(); var function = Get(name); int source = 0, output = 0;
                try {
                    source = (int)Get("aqua_alloc").Invoke(Math.Max(1, input.Length))!;
                    output = (int)Get("aqua_alloc").Invoke(DynamicLibraryAbi.MaxBytes)!;
                    if (source <= 0 || output <= 0) throw new InvalidDataException("Library allocation failed / 函式庫記憶體配置失敗。");
                    if ((long)source < (long)output + DynamicLibraryAbi.MaxBytes && (long)output < (long)source + Math.Max(1, input.Length))
                        throw new InvalidDataException("Overlapping library buffers / 函式庫緩衝區重疊。");
                    var memory = instance.GetMemory("memory")!;
                    _ = memory.GetSpan(output, DynamicLibraryAbi.MaxBytes);
                    input.CopyTo(memory.GetSpan(source, input.Length));
                    int count = DynamicLibraryAbi.Result((int)function.Invoke(source, input.Length, output, DynamicLibraryAbi.MaxBytes)!);
                    return memory.GetSpan(output, count).ToArray();
                } finally {
                    try { if (output > 0) Get("aqua_free").Invoke(output); }
                    finally { if (source > 0) Get("aqua_free").Invoke(source); }
                }
            }
        }
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; linker.Dispose(); store.Dispose(); module.Dispose(); engine.Dispose(); } }
    }
}
