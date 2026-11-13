using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AquariusLang.ast;
using AquariusLang.Compiler;
using static AquariusLang.Wasm.WasmEncoding;

namespace AquariusLang.Wasm;

/// <summary>The <c>aquarius.debug</c> custom section: what the runtime needs to say where a runtime error happened, with
/// the source line and a call stack (runtime/host/diagnostics.c). Nothing reads it while the program runs normally:
/// compiled code stores the failing instruction into its frame only on the error path, and the host reads this section
/// only after an error.
///
/// Layout (LEB128 unsigned numbers; a string is its UTF-8 byte length, then the bytes):
/// <code>
/// u8 version (1)
/// modules:   count, then per module: path, source text ("" when it is not known)
/// functions: count (every compiled body, by index), then per function:
///            module index + 1 (0: none, the guest prelude), name ("" when anonymous; a module's own body is ""),
///            block count, then each block's first instruction (a suspended frame's pc is a block index: the call that
///            suspended it is the instruction before that block),
///            instruction count, then per instruction: UTF-8 byte offset of its source range + 1 (0: unknown), its length
/// </code></summary>
internal static class WasmDebugInfo
{
    internal const string SectionName = "aquarius.debug";
    internal const byte Version = 1;

    internal static void Write(BinaryWriter w, IReadOnlyList<(string Path, LoweredProgram Program)> modules,
        IReadOnlyList<LoweredProgram> bodies, IReadOnlyDictionary<int, int> moduleOf, IReadOnlyList<WasmScopePlan> plans)
    {
        String(w, SectionName);
        w.Write(Version);
        U(w, modules.Count);
        var offsets = new int[modules.Count][];
        for (int m = 0; m < modules.Count; m++) {
            string source = modules[m].Program.Source ?? "";
            String(w, modules[m].Path); String(w, source);
            offsets[m] = Utf8Offsets(source);
        }
        U(w, bodies.Count);
        for (int f = 0; f < bodies.Count; f++) {
            var body = bodies[f];
            int module = moduleOf.TryGetValue(f, out int found) ? found : -1;
            U(w, module + 1);
            String(w, body.Name ?? "");
            var starts = WasmBodyEmitter.BlockStarts(body, plans[f]);
            U(w, starts.Length); foreach (int start in starts) U(w, start);
            U(w, body.Code.Length);
            var bytes = module >= 0 ? offsets[module] : null;
            for (int pc = 0; pc < body.Code.Length; pc++) {
                var span = body.SpanAt(pc);
                if (bytes == null || !span.IsKnown || span.End > bytes.Length - 1) { U(w, 0); U(w, 0); continue; }
                int start = bytes[span.Start], end = bytes[span.End];
                U(w, start + 1); U(w, end - start);
            }
        }
    }

    /// <summary>The UTF-8 byte offset of every UTF-16 position of <paramref name="text"/> (and of its end).</summary>
    private static int[] Utf8Offsets(string text)
    {
        var offsets = new int[text.Length + 1];
        int bytes = 0;
        for (int i = 0; i < text.Length; i++) {
            offsets[i] = bytes;
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { offsets[++i] = bytes; bytes += 4; }
            else bytes += c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
        }
        offsets[text.Length] = bytes;
        return offsets;
    }

    private static void String(BinaryWriter w, string value)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(value); U(w, encoded.Length); w.Write(encoded);
    }
}
