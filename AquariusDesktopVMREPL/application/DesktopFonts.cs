using System.Runtime.InteropServices;
using AquariusLang.Application;
using AquariusREPL.Graphics;

namespace AquariusREPL.Application;

public sealed class DesktopFonts : IFontService {
    public ValueTask<IReadOnlyList<FontInfo>> Enumerate(CancellationToken cancellation = default) {
        cancellation.ThrowIfCancellationRequested(); if (!OperatingSystem.IsWindows()) throw new ApplicationFailure(FailureKind.Unsupported, "System font enumeration adapter unavailable; existing bitmap fallback remains usable.");
        var result = new HashSet<FontInfo>(); IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        try { var filter = new LogFont { CharSet = 1, FaceName = "" }; FontCallback callback = (font, metrics, type, data) => { if (cancellation.IsCancellationRequested) return 0; var f = Marshal.PtrToStructure<LogFont>(font); result.Add(new(f.FaceName, (f.Weight >= 700 ? "Bold" : "Regular") + (f.Italic != 0 ? " Italic" : ""))); return result.Count < 4096 ? 1 : 0; };
            EnumFontFamiliesExW(dc, ref filter, callback, IntPtr.Zero, 0); cancellation.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<FontInfo>>(result.OrderBy(f => f.Family).ThenBy(f => f.Style).ToArray());
        } finally { DeleteDC(dc); }
    }
    public bool Available(string family) => family is "sans-serif" or "monospace" || Enumerate().Result.Any(f => f.Family.Equals(family, StringComparison.OrdinalIgnoreCase));
    public TextRectangle Measure(string text, string family, double size) {
        if (!double.IsFinite(size) || size <= 0 || size > 512) throw new ArgumentException("Font size must be 0..512.");
        var measured = TextRaster.Measure(text, family, (float)size); return new(0, 0, text.Length == 0 ? 0 : measured.w - (OperatingSystem.IsWindows() ? 2 : 0), measured.h);
    }
    public IReadOnlyList<TextRectangle> Selection(string text, int startUtf16, int endUtf16, string family, double size) {
        var editor = new TextEditor(text); _ = editor.Convert(startUtf16, TextPositionUnit.Utf16, TextPositionUnit.Grapheme); _ = editor.Convert(endUtf16, TextPositionUnit.Utf16, TextPositionUnit.Grapheme);
        if (endUtf16 < startUtf16) throw new ArgumentException("Selection end precedes start."); var output = new List<TextRectangle>(); int offset = 0, lineIndex = 0;
        foreach (var line in text.Split('\n')) { int start = Math.Clamp(startUtf16 - offset, 0, line.Length), end = Math.Clamp(endUtf16 - offset, 0, line.Length);
            if (startUtf16 <= offset + line.Length && endUtf16 >= offset) { double x = Measure(line[..start], family, size).Width; output.Add(new(x, lineIndex * size * 1.2, Measure(line[..end], family, size).Width - x, size)); }
            offset += line.Length + 1; lineIndex++;
        }
        return output;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private struct LogFont { public int Height, Width, Escapement, Orientation, Weight; public byte Italic, Underline, StrikeOut, CharSet, OutPrecision, ClipPrecision, Quality, Pitch; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int FontCallback(IntPtr font, IntPtr metrics, uint type, IntPtr data);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int EnumFontFamiliesExW(IntPtr dc, ref LogFont filter, FontCallback callback, IntPtr data, uint flags);
}
