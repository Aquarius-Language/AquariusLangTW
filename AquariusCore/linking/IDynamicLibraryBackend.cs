using System;
using System.Text.RegularExpressions;
namespace AquariusLang.Linking;

/// <summary>OS/module loading capability. Native modules implement Aquarius buffer ABI 1.</summary>
public interface IDynamicLibraryBackend {
    string Platform { get; }
    bool IsSupported(string kind);
    IDynamicLibrary Load(string path);
}
public interface IDynamicLibrary : IDisposable {
    bool HasSymbol(string name);
    byte[] Invoke(string name, byte[] input);
}
public static class DynamicLibraryAbi {
    public const int Version = 1, MaxBytes = 1024 * 1024, MaxLibraries = 32;
    public static void Symbol(string name) {
        if (!Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z") || name.StartsWith("aqua_", StringComparison.Ordinal))
            throw new ArgumentException("Invalid or reserved symbol / 無效或保留的符號名稱。");
    }
    public static void Buffer(int length) {
        if (length < 0 || length > MaxBytes) throw new ArgumentException("Buffer exceeds 1 MiB / 緩衝區超過一 MiB。");
    }
    public static int Result(int length) {
        if (length < 0) throw new InvalidOperationException($"Library call failed ({length}) / 函式庫呼叫失敗。");
        Buffer(length); return length;
    }
}
