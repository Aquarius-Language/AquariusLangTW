using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using static AquariusLang.Wasm.WasmEncoding;

namespace AquariusLang.Wasm;

/// <summary>The stencils of the desktop runtime JIT (runtime/platform/desktop/jit.c): typed variants of loops of untyped
/// code (<see cref="WasmNativeFunction.Stencil"/>), stored in the <c>aquarius.jit</c> custom section, which every other
/// engine ignores. When a loop gets hot the generic code asks the host (rt_jitRequest) for its next variant; the JIT wraps
/// that variant's body in a module importing the application's memory, table, native-tier globals and runtime helpers,
/// compiles it into the same store and returns its table index. A variant's entry checks the types it assumes and
/// answers -2 otherwise, so the generic code asks for the next one; results never differ from the generic code's.
///
/// Section layout (unsigned LEB128 integers, strings as length and UTF-8 bytes): version 1, the stencil count, then per
/// stencil its loop number, its function type index, its imported runtime functions (count, then name and type index
/// each, in function index order) and its body (length, then the code section entry without its size).</summary>
internal static class WasmJit
{
    internal const string Section = "aquarius.jit";
    internal const string SignalExport = "aqua_signal", ExecutionExport = "aqua_execution";
    internal const int Version = 1;

    internal static void WriteSection(BinaryWriter w, IReadOnlyList<WasmNativeFunction.Stencil> stencils, WasmRuntimeImage runtime, Func<string, int> text)
    {
        Name(w, Section);
        U(w, Version); U(w, stencils.Count);
        var types = FunctionTypes(runtime);
        foreach (var stencil in stencils) {
            var imports = new ImportMap(runtime.Functions);
            using var bytes = new MemoryStream();
            // In the stencil's module the imported globals come first: the signal is global 0, the execution global 1.
            using (var code = new BinaryWriter(bytes, Encoding.UTF8, true))
                stencil.Region.EmitImplementation(code, new Dictionary<int, (int, WasmNativeFunction)>(), imports, 0, 1, text: text);
            U(w, stencil.Loop); U(w, runtime.TypeCount + 1);
            U(w, imports.Names.Count);
            foreach (string name in imports.Names) { Name(w, name); U(w, types[runtime.Functions[name]]); }
            U(w, (int)bytes.Length); w.Write(bytes.ToArray());
        }
    }

    private static void Name(BinaryWriter w, string name) { byte[] bytes = Encoding.UTF8.GetBytes(name); U(w, bytes.Length); w.Write(bytes); }

    /// <summary>The type index of each function of the runtime image: its imports, then its own functions.</summary>
    private static int[] FunctionTypes(WasmRuntimeImage runtime)
    {
        var types = new List<int>();
        using (var imports = new BinaryReader(new MemoryStream(runtime.Sections[2]))) {
            uint count = WasmRuntimeImage.ReadUnsigned(imports);
            for (int i = 0; i < count; i++) {
                imports.ReadBytes(checked((int)WasmRuntimeImage.ReadUnsigned(imports)));
                imports.ReadBytes(checked((int)WasmRuntimeImage.ReadUnsigned(imports)));
                byte kind = imports.ReadByte();
                switch (kind) {
                    case 0: types.Add(checked((int)WasmRuntimeImage.ReadUnsigned(imports))); break;
                    case 1: imports.ReadByte(); Limits(imports); break;
                    case 2: Limits(imports); break;
                    case 3: imports.ReadByte(); imports.ReadByte(); break;
                    default: throw new InvalidDataException("Unknown import kind in the runtime image.");
                }
            }
        }
        using (var functions = new BinaryReader(new MemoryStream(runtime.Sections[3]))) {
            uint count = WasmRuntimeImage.ReadUnsigned(functions);
            for (int i = 0; i < count; i++) types.Add(checked((int)WasmRuntimeImage.ReadUnsigned(functions)));
        }
        return types.ToArray();
        static void Limits(BinaryReader r) { byte flags = r.ReadByte(); WasmRuntimeImage.ReadUnsigned(r); if ((flags & 1) != 0) WasmRuntimeImage.ReadUnsigned(r); }
    }

    /// <summary>Numbers the runtime functions a stencil calls in order of first use: they are its imports.</summary>
    private sealed class ImportMap(IReadOnlyDictionary<string, int> runtime) : IReadOnlyDictionary<string, int>
    {
        internal readonly List<string> Names = new();
        private readonly Dictionary<string, int> indices = new(StringComparer.Ordinal);
        public int this[string key] {
            get {
                if (!runtime.ContainsKey(key)) throw new KeyNotFoundException(key);
                if (!indices.TryGetValue(key, out int index)) { index = Names.Count; Names.Add(key); indices.Add(key, index); }
                return index;
            }
        }
        public bool ContainsKey(string key) => runtime.ContainsKey(key);
        public bool TryGetValue(string key, out int value) { value = ContainsKey(key) ? this[key] : 0; return ContainsKey(key); }
        public IEnumerable<string> Keys => runtime.Keys;
        public IEnumerable<int> Values => Keys.Select(k => this[k]);
        public int Count => runtime.Count;
        public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => Keys.Select(k => new KeyValuePair<string, int>(k, this[k])).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
