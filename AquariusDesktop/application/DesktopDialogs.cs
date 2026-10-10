using System.Runtime.InteropServices;
using AquariusLang.Application;

namespace AquariusLang.Desktop.Application;

internal static class DesktopDialogs {
    internal static IReadOnlyList<string> FileDialog(PickerOptions options, bool save, CancellationToken cancellation) {
        if (!OperatingSystem.IsWindows()) throw new ApplicationFailure(FailureKind.Unsupported, "Native file picker adapter unavailable on this OS.");
        cancellation.ThrowIfCancellationRequested(); if (options.SuggestedName.IndexOf('\0') >= 0) throw new ArgumentException("Filename contains NUL.");
        string filter = string.Join("\0", (options.Filters ?? new[] { new FileFilter("All files", new[] { "*" }) }).SelectMany(f => new[] { f.Name, string.Join(";", f.Extensions.Select(e => e == "*" ? "*.*" : "*." + ValidateExtension(e))) })) + "\0\0";
        IntPtr buffer = Marshal.AllocHGlobal(131072); IntPtr filterBuffer = Marshal.StringToHGlobalUni(filter);
        try {
            Marshal.Copy(new byte[131072], 0, buffer, 131072);
            if (options.SuggestedName.Length >= 65535) throw new ArgumentException("Suggested name too long."); Marshal.Copy((options.SuggestedName + '\0').ToCharArray(), 0, buffer, options.SuggestedName.Length + 1);
            var request = new OpenFileName { Size = Marshal.SizeOf<OpenFileName>(), File = buffer, MaxFile = 65536, Filter = filterBuffer, FilterIndex = 1, Title = options.Title, Flags = (uint)(0x80000 | 0x8 | 0x800 | (save ? 0x2 : 0x1000) | (!save && options.Multiple ? 0x200 : 0)) };
            bool accepted = save ? GetSaveFileNameW(ref request) : GetOpenFileNameW(ref request);
            if (!accepted) { uint error = CommDlgExtendedError(); if (error != 0) throw new ApplicationFailure(FailureKind.Unavailable, "File dialog error " + error); return Array.Empty<string>(); }
            cancellation.ThrowIfCancellationRequested(); var parts = new List<string>(); int offset = 0;
            while (offset < 65536) { string part = Marshal.PtrToStringUni(buffer + offset * 2) ?? ""; if (part.Length == 0) break; parts.Add(part); offset += part.Length + 1; }
            return parts.Count < 2 ? parts : parts.Skip(1).Select(p => Path.Combine(parts[0], p)).ToArray();
        } finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(filterBuffer); }
    }
    private static string ValidateExtension(string value) {
        value = value.TrimStart('.'); if (value.Length == 0 || value.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new ArgumentException("Filters require simple filename extensions."); return value;
    }
    internal static string? DirectoryDialog(CancellationToken cancellation) {
        if (!OperatingSystem.IsWindows()) throw new ApplicationFailure(FailureKind.Unsupported, "Native directory picker unavailable on this OS.");
        cancellation.ThrowIfCancellationRequested(); var request = new BrowseInfo { Title = "Select directory", Flags = 0x1 | 0x40 };
        IntPtr item = SHBrowseForFolderW(ref request); if (item == IntPtr.Zero) return null;
        try { var path = new System.Text.StringBuilder(32768); if (!SHGetPathFromIDListW(item, path)) throw new ApplicationFailure(FailureKind.Unsupported, "Selection has no filesystem path."); cancellation.ThrowIfCancellationRequested(); return path.ToString(); }
        finally { Marshal.FreeCoTaskMem(item); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OpenFileName {
        public int Size; public IntPtr Owner, Instance, Filter, CustomFilter; public int MaxCustomFilter, FilterIndex; public IntPtr File; public int MaxFile;
        public IntPtr FileTitle; public int MaxFileTitle; public string? InitialDirectory, Title; public uint Flags; public ushort FileOffset, FileExtension;
        public string? DefaultExtension; public IntPtr CustomData, Hook; public string? Template; public IntPtr Reserved; public uint Reserved2, FlagsEx;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct BrowseInfo { public IntPtr Owner, Root, DisplayName; public string Title; public uint Flags; public IntPtr Callback, Param; public int Image; }
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)] private static extern bool GetOpenFileNameW(ref OpenFileName request);
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)] private static extern bool GetSaveFileNameW(ref OpenFileName request);
    [DllImport("comdlg32.dll")] private static extern uint CommDlgExtendedError();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHBrowseForFolderW(ref BrowseInfo request);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool SHGetPathFromIDListW(IntPtr item, System.Text.StringBuilder path);
}
