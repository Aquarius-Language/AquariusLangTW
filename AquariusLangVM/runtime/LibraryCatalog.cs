using System;
using System.Collections.Generic;
using System.Linq;

namespace AquariusLang.runtime;

/// <summary>Portable metadata for host libraries; querying this never loads native code.</summary>
public sealed record LibraryFunction(string Library, string EnglishName, string TraditionalChineseName,
    int MinimumArguments, int MaximumArguments, string? ReturnLibrary = null) {
    public string Detail => $"{Library}.{TraditionalChineseName} / {EnglishName} — " +
        (MinimumArguments == MaximumArguments ? $"{MinimumArguments} arguments." : $"{MinimumArguments}..{MaximumArguments} arguments.");
    public bool HasName(string name) => name == EnglishName || name == TraditionalChineseName;
}

public static partial class LibraryCatalog {
    public static IReadOnlyList<LibraryFunction> Functions { get; }
    private static readonly IReadOnlyDictionary<string, string> chineseNames;
    static LibraryCatalog() {
        Functions = Array.AsReadOnly(definitions);
        chineseNames = definitions.GroupBy(f => f.EnglishName)
            .ToDictionary(g => g.Key, g => g.First().TraditionalChineseName, StringComparer.Ordinal);
    }
    public static IReadOnlyList<LibraryFunction> GlobalFunctions { get; } = Array.AsReadOnly(new[] {
        new LibraryFunction("", "len", "長度", 1, 1), new("", "last", "最後一個", 1, 1),
        new("", "rest", "其餘", 1, 1), new("", "push", "加入", 2, 2),
        new("", "print", "印出", 0, int.MaxValue), new("", "import", "匯入", 1, 1),
        new("", "isOSWindows", "是Windows", 0, 0), new("", "isOSLinux", "是Linux", 0, 0),
        new("", "isOSMacOS", "是MacOS", 0, 0), new("", "execFile", "執行檔案", 2, 2)
    });
    public static string? TraditionalChinese(string englishName) => chineseNames.TryGetValue(englishName, out var name) ? name : null;
    public static string CanonicalLibrary(string name) => name switch {
        "wgpu" => "WGPU", "JoltPhysics" => "Jolt", _ => name
    };
    public static IEnumerable<LibraryFunction> Members(string library) => Functions.Where(f => f.Library == CanonicalLibrary(library));
    public static LibraryFunction? Find(string library, string name) => Members(library).FirstOrDefault(f => f.HasName(name));
}
