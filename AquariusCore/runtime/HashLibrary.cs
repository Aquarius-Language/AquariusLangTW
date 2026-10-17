using System;
using System.Collections.Generic;
using AquariusLang.Object;

namespace AquariusLang.runtime;

/// <summary>Metadata and transport tokens only. All hash algorithms execute in core Wasm (runtime/hashes.h).</summary>
public static class HashLibrary {
    // IDs 20..29 are shared with runtime/hashes.h and hosting/hash-library.mjs.
    public const int FirstId = 20;
    public static IReadOnlyList<LibraryFunction> Functions { get; } = Array.AsReadOnly(new LibraryFunction[] {
        new("Hash", "Keys", "鍵列表", 1, 1), new("Hash", "Values", "值列表", 1, 1),
        new("Hash", "Has", "包含鍵", 2, 2), new("Hash", "Get", "取得", 3, 3),
        new("Hash", "Delete", "刪除鍵", 2, 2), new("Hash", "Len", "長度", 1, 1),
        new("Hash", "Clone", "克隆", 1, 1), new("Hash", "Clear", "清空", 1, 1),
        new("Hash", "Merge", "合併", 2, 2), new("Hash", "Entries", "項目列表", 1, 1)
    });
    public static bool IsName(string name) => name is "Hash" or "雜湊";
    public static BuiltinObj? Resolve(int id) => id >= FirstId && id < FirstId + Functions.Count
        ? BuiltinObj.FromPortable(id, _ => new ErrorObj("Hash functions require compiled Wasm execution.")) : null;
    public static ModuleObj CreateModule() => PortableModules.Create(Functions, FirstId, Resolve);
}

/// <summary>Builds a module whose members are portable builtin tokens with bilingual aliases.</summary>
internal static class PortableModules {
    internal static ModuleObj Create(IReadOnlyList<LibraryFunction> functions, int firstId, Func<int, BuiltinObj?> resolve) {
        var environment = AquariusLang.Object.Environment.NewEnvironment();
        for (int i = 0; i < functions.Count; i++) {
            var token = resolve(firstId + i)!;
            foreach (var name in functions[i].Names) environment.Create(name, token);
        }
        return new ModuleObj(environment);
    }
}
