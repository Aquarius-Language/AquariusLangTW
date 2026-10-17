using System;
using System.Collections.Generic;
using AquariusLang.Object;

namespace AquariusLang.runtime;

/// <summary>Metadata and transport tokens only. All array algorithms execute in core Wasm.</summary>
public static class ArrayLibrary {
    // IDs 6..19 are shared with runtime/arrays.h and hosting/array-library.mjs.
    public static IReadOnlyList<LibraryFunction> Functions { get; } = Array.AsReadOnly(new LibraryFunction[] {
        new("Array", "Make", "建立", 2, 3), new("Array", "Len", "長度", 1, 1),
        new("Array", "Cap", "容量", 1, 1), new("Array", "Grow", "擴充", 2, 2),
        new("Array", "Append", "追加", 2, 2), new("Array", "AppendArray", "追加陣列", 2, 2),
        new("Array", "Copy", "複製", 2, 5), new("Array", "Clone", "克隆", 1, 1),
        new("Array", "Slice", "切片", 3, 3), new("Array", "Fill", "填入", 2, 2),
        new("Array", "Delete", "刪除", 3, 3), new("Array", "Reverse", "反轉", 1, 1),
        new("Array", "Index", "尋找", 2, 2), new("Array", "Contains", "包含", 2, 2)
    });
    public static bool IsName(string name) => name is "Array" or "陣列";
    public static BuiltinObj? Resolve(int id) => id is >= 6 and <= 19
        ? BuiltinObj.FromPortable(id, _ => new ErrorObj("Array functions require compiled Wasm execution.")) : null;
    public static ModuleObj CreateModule() => PortableModules.Create(Functions, 6, Resolve);
}
