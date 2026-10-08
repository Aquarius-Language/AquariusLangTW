using AquariusLang.Application;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace AquariusREPL.Application;

internal static class DesktopActivation {
    // Registration advertises a handler; it never overrides the user's protected default-app choice.
    internal static void Register(FileAssociation association) {
        if (!OperatingSystem.IsWindows()) throw new ApplicationFailure(FailureKind.Unsupported, "File association registration requires a Windows adapter.");
        string extension = association.Extension.TrimStart('.');
        if (extension.Length is < 1 or > 32 || extension.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new ArgumentException("Invalid file association extension.");
        Preferences.ValidateKey(association.ApplicationId); string exe = Path.GetFullPath(association.Executable);
        if (!File.Exists(exe)) throw new FileNotFoundException("Association executable does not exist.", exe);
        string programId = "Aquarius." + association.ApplicationId + "." + extension;
        string command = string.Join(" ", new[] { Quote(exe) }.Concat(association.Arguments.Select(Quote)).Append("\"%1\""));
        using (var key = Registry.CurrentUser.CreateSubKey("Software\\Classes\\" + programId)) { key.SetValue("", association.Description); using var open = key.CreateSubKey("shell\\open\\command"); open.SetValue("", command); }
        using (var key = Registry.CurrentUser.CreateSubKey("Software\\Classes\\." + extension + "\\OpenWithProgids")) key.SetValue(programId, Array.Empty<byte>(), RegistryValueKind.None);
        SHChangeNotify(0x8000000, 0, IntPtr.Zero, IntPtr.Zero);
    }
    private static string Quote(string value) {
        if (value.Contains('\0')) throw new ArgumentException("Command argument contains NUL.");
        var result = new System.Text.StringBuilder("\""); int slashes = 0;
        foreach (char c in value) { if (c == '\\') { slashes++; continue; } if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c); else result.Append('\\', slashes).Append(c); slashes = 0; }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint kind, uint flags, IntPtr a, IntPtr b);
}
