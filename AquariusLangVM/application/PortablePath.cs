using System;
using System.Collections.Generic;
using System.Linq;

namespace AquariusLang.Application;

/// <summary>Lexical slash paths. No filesystem access, symlink resolution or package authorization.</summary>
public static class PortablePath {
    public static string Normalize(string path) {
        if (path.IndexOf('\0') >= 0) throw new ArgumentException("Path contains NUL.");
        path = path.Replace('\\', '/'); string root = "";
        if (path.StartsWith("//", StringComparison.Ordinal)) {
            var pieces = path[2..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length < 2) throw new ArgumentException("UNC path requires server and share.");
            root = "//" + pieces[0] + "/" + pieces[1] + "/"; path = string.Join("/", pieces.Skip(2));
        } else if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') {
            if (path.Length < 3 || path[2] != '/') throw new ArgumentException("Drive-relative paths are ambiguous.");
            root = char.ToUpperInvariant(path[0]) + ":/"; path = path[3..];
        } else if (path.StartsWith('/')) { root = "/"; path = path.TrimStart('/'); }
        var result = new List<string>();
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            if (part == ".") continue;
            if (part == "..") {
                if (result.Count > 0 && result[^1] != "..") result.RemoveAt(result.Count - 1);
                else if (root.Length == 0) result.Add(part);
            } else result.Add(part);
        }
        return root + string.Join("/", result) is var normalized && normalized.Length > 0 ? normalized : ".";
    }
    private static string Root(string path) => path.StartsWith("//") ? string.Join("/", path.Split('/').Take(4)) + "/" :
        path.StartsWith('/') ? "/" : path.Length >= 3 && path[1] == ':' ? path[..3] : "";
    public static string Join(params string[] paths) {
        string result = "";
        foreach (var p in paths) { var n = Normalize(p); result = Root(n).Length > 0 || result.Length == 0 ? n : result + "/" + n; }
        return Normalize(result);
    }
    public static string Name(string path) { path = Normalize(path); return path == Root(path) || path == "." ? "" : path[(path.LastIndexOf('/') + 1)..]; }
    public static string Extension(string path) { var n = Name(path); int i = n.LastIndexOf('.'); return i <= 0 ? "" : n[i..]; }
    public static string Parent(string path) {
        path = Normalize(path); string root = Root(path); if (path == root) return path;
        int i = path.LastIndexOf('/'); return i < 0 ? "." : i < root.Length ? root : path[..i];
    }
    public static string Relative(string fromDirectory, string target) {
        var from = Normalize(fromDirectory); target = Normalize(target);
        if (Root(from) != Root(target)) throw new ArgumentException("Paths have different roots.");
        var a = from.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(x => x != ".").ToArray();
        var b = target.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(x => x != ".").ToArray(); int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        var result = string.Join("/", Enumerable.Repeat("..", a.Length - i).Concat(b.Skip(i))); return result.Length == 0 ? "." : result;
    }
}
