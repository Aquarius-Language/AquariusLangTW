using System;
using System.Collections.Generic;
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
    /// <summary>Undo snapshots are bounded; consecutive typing or deletion at the caret coalesces into one step.</summary>
    public const int MaximumHistory = 200;
    private readonly List<(string Text, TextSelection Selection)> undo = new(), redo = new();
    private string? editGroup;
    private int editGroupCaret = -1;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public void Select(int anchor, int caret) { SetSelection(anchor, caret); editGroup = null; }
    private void SetSelection(int anchor, int caret) {
        if (anchor < 0 || caret < 0 || anchor > Length || caret > Length) throw new ArgumentOutOfRangeException(nameof(caret));
        Selection = new(anchor, caret);
    }
    private bool Mergeable(string kind) => kind is "type" or "backspace" or "delete" && kind == editGroup && Selection.Start == Selection.End && Selection.Caret == editGroupCaret;
    private void Record(string kind, bool merge, TextSelection before) {
        if (!merge) { undo.Add((Text, before)); if (undo.Count > MaximumHistory) undo.RemoveAt(0); }
        redo.Clear(); editGroup = kind;
    }
    private void Restore((string Text, TextSelection Selection) state) {
        Text = state.Text; offsets = Boundaries(Text); Composition = null; SetSelection(state.Selection.Anchor, state.Selection.Caret); editGroup = null;
    }
    public bool Undo() { if (Composition != null || undo.Count == 0) return false; redo.Add((Text, Selection)); Restore(undo[^1]); undo.RemoveAt(undo.Count - 1); return true; }
    public bool Redo() { if (Composition != null || redo.Count == 0) return false; undo.Add((Text, Selection)); Restore(redo[^1]); redo.RemoveAt(redo.Count - 1); return true; }
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
        string kind = text.Length > 0 && StringInfo.ParseCombiningCharacters(text).Length == 1 && !string.IsNullOrWhiteSpace(text) ? "type" : "insert";
        Replace(text, kind, Mergeable(kind), Selection);
    }
    private void Replace(string text, string kind, bool merge, TextSelection before) {
        ValidateText(text); int start = offsets[Selection.Start];
        var updated = Text[..start] + text + Text[offsets[Selection.End]..]; ValidateText(updated);
        bool changed = updated != Text; if (changed) Record(kind, merge, before);
        Text = updated; offsets = Boundaries(Text); int end = start + text.Length;
        int caret = Array.BinarySearch(offsets, end); if (caret < 0) caret = ~caret; SetSelection(caret, caret); if (changed) editGroupCaret = caret;
    }
    public void Backspace() {
        if (Composition != null) return; var before = Selection; bool merge = Mergeable("backspace");
        if (Selection.Start == Selection.End) { if (Selection.Caret == 0) return; SetSelection(Selection.Caret - 1, Selection.Caret); }
        Replace("", "backspace", merge, before);
    }
    public void Delete() {
        if (Composition != null) return; var before = Selection; bool merge = Mergeable("delete");
        if (Selection.Start == Selection.End) { if (Selection.Caret == Length) return; SetSelection(Selection.Caret, Selection.Caret + 1); }
        Replace("", "delete", merge, before);
    }
    /// <summary>Deletes the selection, or from the caret to the previous/next word boundary.</summary>
    public void DeleteWord(int direction) {
        if (Composition != null) return;
        var before = Selection;
        if (Selection.Start == Selection.End) { int caret = Selection.Caret; Move(direction, "word", true); if (Selection.Caret == caret) return; }
        Replace("", "insert", false, before);
    }
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
        } else if (unit == "document") next = direction < 0 ? 0 : Length;
        else throw new ArgumentException("Navigation unit must be grapheme, word, line or document.");
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
        await clipboard.Write(new(SelectedText), cancellation).ConfigureAwait(false); if (cut) Replace("", "insert", false, Selection);
    }
    public async ValueTask Paste(IClipboard clipboard, CancellationToken cancellation = default) {
        var content = await clipboard.Read(cancellation).ConfigureAwait(false); cancellation.ThrowIfCancellationRequested(); if (content.Text != null) { Composition = null; Replace(content.Text, "insert", false, Selection); }
    }
    /// <summary>Only physical shortcut keys reach this method. Committed text comes from the existing IME/text event stream.</summary>
    public async ValueTask<bool> Shortcut(string key, KeyModifiers modifiers, IClipboard clipboard, CancellationToken cancellation = default) {
        if (Composition != null) return false;
        bool command = (modifiers & (KeyModifiers.Control | KeyModifiers.Super)) != 0;
        if (command) switch (key.ToLowerInvariant()) {
            case "a": Select(0, Length); return true; case "c": await Copy(clipboard, cancellation: cancellation).ConfigureAwait(false); return true;
            case "x": await Copy(clipboard, true, cancellation).ConfigureAwait(false); return true; case "v": await Paste(clipboard, cancellation).ConfigureAwait(false); return true;
            case "z": if ((modifiers & KeyModifiers.Shift) != 0) Redo(); else Undo(); return true; case "y": Redo(); return true;
        }
        bool extend = (modifiers & KeyModifiers.Shift) != 0;
        switch (key) {
            case "ArrowLeft": Move(-1, command ? "word" : "grapheme", extend); return true;
            case "ArrowRight": Move(1, command ? "word" : "grapheme", extend); return true;
            case "Home": Move(-1, command ? "document" : "line", extend); return true; case "End": Move(1, command ? "document" : "line", extend); return true;
            case "Backspace": if (command) DeleteWord(-1); else Backspace(); return true; case "Delete": if (command) DeleteWord(1); else Delete(); return true;
        }
        return false;
    }
}
