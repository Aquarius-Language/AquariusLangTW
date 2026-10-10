using System;
using System.Linq;
using AquariusLang.Object;
using static AquariusLang.Application.ApplicationValues;

namespace AquariusLang.Application;

public sealed partial class ApplicationRuntime {
    private void RegisterEditing() {
        var m = Module("TextEdit"); Bind(m, "Create", 1, 1, a => EditorObject(new(Text(a[0]))));
        Bind(m, "Convert", 4, 4, a => new IntegerObj(TextEditor.ConvertPosition(Text(a[0]), Integer(a[1]), Enum.Parse<TextPositionUnit>(Text(a[2]), true), Enum.Parse<TextPositionUnit>(Text(a[3]), true))));
        var clipboard = Module("Clipboard");
        Bind(clipboard, "Capabilities", 0, 0, _ => Record(host.Clipboard.Capabilities));
        Bind(clipboard, "Formats", 0, 0, _ => new ArrayObj(Await(host.Clipboard.Formats(cancellation)).Select(f => (IObject)new StringObj(f)).ToArray()));
        Bind(clipboard, "ReadText", 0, 0, _ => Await(host.Clipboard.Read(cancellation)).Text is string text ? new StringObj(text) : AquariusLang.runtime.RepeatedPrimitives.NULL);
        Bind(clipboard, "ReadImage", 0, 0, _ => Await(host.Clipboard.Read(cancellation)).Image is PixelImage image ? Image(image) : AquariusLang.runtime.RepeatedPrimitives.NULL);
        Action(clipboard, "WriteText", 1, 1, a => Await(host.Clipboard.Write(new(Text(a[0])), cancellation)));
        Action(clipboard, "WriteImage", 1, 2, a => Await(host.Clipboard.Write(new(a.Length == 2 ? Text(a[1]) : null, GetImage(a[0])), cancellation)));
        var fonts = Module("Fonts");
        Bind(fonts, "Enumerate", 0, 0, _ => new ArrayObj(Await(host.Fonts.Enumerate(cancellation)).Select(f => (IObject)Record(f)).ToArray()));
        Bind(fonts, "Available", 1, 1, a => new BooleanObj(host.Fonts.Available(Text(a[0]))));
        Bind(fonts, "Measure", 3, 3, a => Record(host.Fonts.Measure(Text(a[0]), Text(a[1]), Number(a[2]))));
        Bind(fonts, "Selection", 5, 5, a => new ArrayObj(host.Fonts.Selection(Text(a[0]), Integer(a[1]), Integer(a[2]), Text(a[3]), Number(a[4])).Select(r => (IObject)Record(r)).ToArray()));
    }
    private ModuleObj EditorObject(TextEditor editor) {
        var m = Record(new { positionUnit = "grapheme" }); objects.Add(m, editor);
        Bind(m, "State", 0, 0, _ => Record(new { editor.Text, editor.Length, editor.Selection, editor.Composition }));
        Action(m, "Select", 2, 2, a => editor.Select(Integer(a[0]), Integer(a[1]))); Action(m, "Insert", 1, 1, a => editor.Insert(Text(a[0])));
        Action(m, "Backspace", 0, 0, _ => editor.Backspace()); Action(m, "Delete", 0, 0, _ => editor.Delete());
        Action(m, "Move", 3, 3, a => editor.Move(Integer(a[0]), Text(a[1]), Boolean(a[2])));
        Bind(m, "Convert", 3, 3, a => new IntegerObj(editor.Convert(Integer(a[0]), Enum.Parse<TextPositionUnit>(Text(a[1]), true), Enum.Parse<TextPositionUnit>(Text(a[2]), true))));
        Action(m, "Composition", 2, 3, a => editor.SetComposition(Text(a[0]), Integer(a[1]), a.Length == 3 ? Enum.Parse<TextPositionUnit>(Text(a[2]), true) : TextPositionUnit.UnicodeScalar)); Action(m, "CancelComposition", 0, 0, _ => editor.CancelComposition());
        Action(m, "Commit", 1, 1, a => editor.Commit(Text(a[0]))); Action(m, "Copy", 0, 0, _ => Await(editor.Copy(host.Clipboard, cancellation: cancellation)));
        Action(m, "Cut", 0, 0, _ => Await(editor.Copy(host.Clipboard, true, cancellation))); Action(m, "Paste", 0, 0, _ => Await(editor.Paste(host.Clipboard, cancellation)));
        Bind(m, "Shortcut", 2, 2, a => new BooleanObj(Await(editor.Shortcut(Text(a[0]), (KeyModifiers)Integer(a[1]), host.Clipboard, cancellation))));
        return m;
    }
}
