using System;
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
        if (!IsIdentifier(name) || name.StartsWith("aqua_", StringComparison.Ordinal))
            throw new ArgumentException("Invalid or reserved symbol / 無效或保留的符號名稱。");
    }
    // Equivalent to \A[A-Za-z_][A-Za-z0-9_]{0,127}\z without a regex evaluation on every library call.
    private static bool IsIdentifier(string? name) {
        if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
        for (int i = 0; i < name.Length; i++) {
            char c = name[i];
            bool letter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
            if (!letter && (i == 0 || c < '0' || c > '9')) return false;
        }
        return true;
    }
    public static void Buffer(int length) {
        if (length < 0 || length > MaxBytes) throw new ArgumentException("Buffer exceeds 1 MiB / 緩衝區超過一 MiB。");
    }
    public static int Result(int length) {
        if (length < 0) throw new InvalidOperationException($"Library call failed ({length}) / 函式庫呼叫失敗。");
        Buffer(length); return length;
    }
}
