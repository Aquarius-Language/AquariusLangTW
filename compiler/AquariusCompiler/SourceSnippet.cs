using System;
using System.Text;

namespace AquariusLang.Compiler;

/// <summary>A source line with part of it underlined, as compile errors show it:
/// <code>
///   |
/// 2 | 變數 a = (1 + ;
///   |               ~
/// </code>
/// Tabs expand to multiples of 4 columns and East Asian wide characters take two, the same rules as runtime errors
/// (runtime/host/diagnostics.c), so the marks stay under the text in a terminal.</summary>
public static class SourceSnippet
{
    public const int TabWidth = 4;

    /// <param name="start">Where the underline starts in <paramref name="line"/> (UTF-16 index).</param>
    /// <param name="length">How many UTF-16 units to underline (at least one column is marked; the line's end bounds it).</param>
    public static string Render(string line, int lineNumber, int start, int length)
    {
        start = Math.Clamp(start, 0, line.Length);
        int end = Math.Clamp(start + Math.Max(length, 0), start, line.Length);
        string number = lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), gutter = new(' ', number.Length);
        var text = new StringBuilder();
        text.Append(gutter).Append(" |\n");
        text.Append(number).Append(" | ").Append(Expand(line)).Append('\n');
        int lead = Columns(line, 0, start, 0), width = Math.Max(1, Columns(line, start, end, lead));
        text.Append(gutter).Append(" | ").Append(' ', lead).Append('~', width);
        return text.ToString();
    }

    private static string Expand(string line)
    {
        var text = new StringBuilder(); int column = 0;
        for (int i = 0; i < line.Length; i++) {
            if (line[i] == '\t') { int n = TabWidth - column % TabWidth; text.Append(' ', n); column += n; continue; }
            text.Append(line[i]);
            if (char.IsHighSurrogate(line[i]) && i + 1 < line.Length) { text.Append(line[++i]); column += Width(char.ConvertToUtf32(line[i - 1], line[i])); }
            else column += Width(line[i]);
        }
        return text.ToString();
    }

    private static int Columns(string line, int from, int to, int at)
    {
        int column = at;
        for (int i = from; i < to; i++) {
            if (line[i] == '\t') { column += TabWidth - column % TabWidth; continue; }
            if (char.IsHighSurrogate(line[i]) && i + 1 < line.Length) { column += Width(char.ConvertToUtf32(line[i], line[i + 1])); i++; }
            else column += Width(line[i]);
        }
        return column - at;
    }

    /// <summary>Terminal columns of a character: 2 for East Asian wide and full-width ones, 0 for combining marks.</summary>
    public static int Width(int c)
    {
        if (c is >= 0x0300 and <= 0x036f or >= 0x200b and <= 0x200f or >= 0xfe00 and <= 0xfe0f) return 0;
        if (c is >= 0x1100 and <= 0x115f or >= 0x2e80 and <= 0x303e or >= 0x3041 and <= 0x33ff or >= 0x3400 and <= 0x4dbf
            or >= 0x4e00 and <= 0x9fff or >= 0xa000 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or >= 0xf900 and <= 0xfaff
            or >= 0xfe30 and <= 0xfe4f or >= 0xff00 and <= 0xff60 or >= 0xffe0 and <= 0xffe6 or >= 0x1f300 and <= 0x1f64f
            or >= 0x1f900 and <= 0x1f9ff or >= 0x20000 and <= 0x3fffd) return 2;
        return 1;
    }
}
