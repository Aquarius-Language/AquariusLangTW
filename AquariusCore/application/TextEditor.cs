using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AquariusLang.Application;

public enum TextPositionUnit { Grapheme, Utf16, UnicodeScalar }
public sealed record TextSelection(int Anchor, int Caret) { public int Start => Math.Min(Anchor, Caret); public int End => Math.Max(Anchor, Caret); }
public sealed record TextComposition(string Text, int CursorScalar);
/// <summary>Selection and editing positions use extended graphemes; composition is never inserted until committed.</summary>
public sealed class TextEditor {
    public string Text { get; private set; }
    public TextSelection Selection { get; private set; } = new(0, 0);
    public TextComposition? Composition { get; private set; }
    private int[] offsets;
    public int Length => offsets.Length - 1;
    public TextEditor(string text = "") { ValidateText(text); Text = text; offsets = Boundaries(text); }
    private static int[] Boundaries(string text) => StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToArray();
    private static void ValidateText(string text) { if (text.Length > 1024 * 1024) throw new ApplicationFailure(FailureKind.LimitExceeded, "Editor text exceeds 1 Mi UTF-16 units."); TextEncoding.Get("utf-8").GetByteCount(text); }
    public void Select(int anchor, int caret) {
        if (anchor < 0 || caret < 0 || anchor > Length || caret > Length) throw new ArgumentOutOfRangeException(nameof(caret));
        Selection = new(anchor, caret);
    }
    public int Convert(int position, TextPositionUnit from, TextPositionUnit to) {
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to)) throw new ArgumentException("Unknown text-position unit.");
        int utf16 = from switch {
            TextPositionUnit.Grapheme => position >= 0 && position <= Length ? offsets[position] : throw new ArgumentOutOfRangeException(nameof(position)),
            TextPositionUnit.Utf16 => position >= 0 && position <= Text.Length ? position : throw new ArgumentOutOfRangeException(nameof(position)),
            TextPositionUnit.UnicodeScalar => ScalarOffset(position), _ => throw new ArgumentException("Unknown position unit.")
        };
        if (utf16 > 0 && utf16 < Text.Length && char.IsLowSurrogate(Text[utf16]) && char.IsHighSurrogate(Text[utf16 - 1])) throw new ArgumentException("Position splits a Unicode scalar.");
        if (to == TextPositionUnit.Utf16) return utf16;
        if (to == TextPositionUnit.UnicodeScalar) return Text[..utf16].EnumerateRunes().Count();
        int found = Array.BinarySearch(offsets, utf16); return found >= 0 ? found : throw new ArgumentException("Position splits a grapheme.");
    }
    private int ScalarOffset(int position) {
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position)); int count = 0, offset = 0;
        foreach (var rune in Text.EnumerateRunes()) { if (count++ == position) return offset; offset += rune.Utf16SequenceLength; }
        return position == count ? offset : throw new ArgumentOutOfRangeException(nameof(position));
    }
    public string SelectedText => Text[offsets[Selection.Start]..offsets[Selection.End]];
    public void Insert(string text) {
        ValidateText(text); int start = offsets[Selection.Start];
        var updated = Text[..start] + text + Text[offsets[Selection.End]..]; ValidateText(updated);
        Text = updated; offsets = Boundaries(Text); int end = start + text.Length;
        int caret = Array.BinarySearch(offsets, end); if (caret < 0) caret = ~caret; Select(caret, caret);
    }
    public void Backspace() { if (Composition != null) return; if (Selection.Start == Selection.End && Selection.Caret > 0) Select(Selection.Caret - 1, Selection.Caret); Insert(""); }
    public void Delete() { if (Composition != null) return; if (Selection.Start == Selection.End && Selection.Caret < Length) Select(Selection.Caret, Selection.Caret + 1); Insert(""); }
    public void Move(int direction, string unit = "grapheme", bool extend = false) {
        if (direction is not -1 and not 1) throw new ArgumentException("Direction must be -1 or 1.");
        int next = Selection.Caret;
        if (!extend && Selection.Start != Selection.End && unit == "grapheme") next = direction < 0 ? Selection.Start : Selection.End;
        else if (unit == "grapheme") next = Math.Clamp(next + direction, 0, Length);
        else if (unit == "line") {
            if (direction < 0) { while (next > 0 && !IsLineBreak(next - 1)) next--; }
            else { while (next < Length && !IsLineBreak(next)) next++; }
        } else if (unit == "word") {
            if (direction > 0) { while (next < Length && Word(next)) next++; while (next < Length && !Word(next)) next++; }
            else { while (next > 0 && !Word(next - 1)) next--; while (next > 0 && Word(next - 1)) next--; }
        } else throw new ArgumentException("Navigation unit must be grapheme, word or line.");
        Select(extend ? Selection.Anchor : next, next);
    }
    private bool Word(int index) { var rune = Rune.GetRuneAt(Text, offsets[index]); return Rune.IsLetterOrDigit(rune) || rune.Value == '_'; }
    private bool IsLineBreak(int index) => Text[offsets[index]] is '\r' or '\n';
    public static int ConvertPosition(string text, int position, TextPositionUnit from, TextPositionUnit to) => new TextEditor(text).Convert(position, from, to);
    public void SetComposition(string text, int cursorScalar, TextPositionUnit unit = TextPositionUnit.UnicodeScalar) {
        cursorScalar = ConvertPosition(text, cursorScalar, unit, TextPositionUnit.UnicodeScalar);
        ValidateText(text); if (cursorScalar < 0 || cursorScalar > text.EnumerateRunes().Count()) throw new ArgumentOutOfRangeException(nameof(cursorScalar));
        Composition = new(text, cursorScalar);
    }
    public void CancelComposition() => Composition = null;
    public void Commit(string text) { Composition = null; Insert(text); }
    public async ValueTask Copy(IClipboard clipboard, bool cut = false, CancellationToken cancellation = default) {
        if (Selection.Start == Selection.End) return;
        await clipboard.Write(new(SelectedText), cancellation).ConfigureAwait(false); if (cut) Insert("");
    }
    public async ValueTask Paste(IClipboard clipboard, CancellationToken cancellation = default) {
        var content = await clipboard.Read(cancellation).ConfigureAwait(false); cancellation.ThrowIfCancellationRequested(); if (content.Text != null) Commit(content.Text);
    }
    /// <summary>Only physical shortcut keys reach this method. Committed text comes from the existing IME/text event stream.</summary>
    public async ValueTask<bool> Shortcut(string key, KeyModifiers modifiers, IClipboard clipboard, CancellationToken cancellation = default) {
        if (Composition != null) return false;
        bool command = (modifiers & (KeyModifiers.Control | KeyModifiers.Super)) != 0;
        if (command) switch (key.ToLowerInvariant()) {
            case "a": Select(0, Length); return true; case "c": await Copy(clipboard, cancellation: cancellation).ConfigureAwait(false); return true;
            case "x": await Copy(clipboard, true, cancellation).ConfigureAwait(false); return true; case "v": await Paste(clipboard, cancellation).ConfigureAwait(false); return true;
        }
        bool extend = (modifiers & KeyModifiers.Shift) != 0;
        switch (key) {
            case "ArrowLeft": Move(-1, command ? "word" : "grapheme", extend); return true;
            case "ArrowRight": Move(1, command ? "word" : "grapheme", extend); return true;
            case "Home": Move(-1, "line", extend); return true; case "End": Move(1, "line", extend); return true;
            case "Backspace": Backspace(); return true; case "Delete": Delete(); return true;
        }
        return false;
    }
}
