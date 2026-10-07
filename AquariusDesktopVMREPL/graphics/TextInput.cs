using System.Globalization;
using System.Runtime.InteropServices;
using AquariusLang.Object;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusREPL.Graphics;

internal readonly record struct TextInputEvent(string Type, string Text, int Cursor) {
    internal ModuleObj ToObject() {
        var env = AquaEnvironment.NewEnvironment();
        env.Create("type", new StringObj(Type));
        env.Create("text", new StringObj(Text));
        env.Create("cursor", new IntegerObj(Cursor));
        return new ModuleObj(env);
    }
}

internal sealed partial class GraphicsRuntime {
    private void RegisterTextInput() {
        var env = Module("TextInput");
        Bind(env, "Start", 1, a => { SetTextInput(a[0], true); return Null(); });
        Bind(env, "Stop", 1, a => { SetTextInput(a[0], false); return Null(); });
        Bind(env, "SetCaretRect", 5, a => { SetTextInputRect(a[0], a[1..]); return Null(); });
        Bind(env, "Poll", 1, a => {
            IntPtr handle = Window(a[0]);
            if (a[0] == sketchWindow) throw new InvalidOperationException("Processing owns text events for its sketch; use Processing.on().");
            return new ArrayObj(ReadTextInput(handle).Select(e => (IObject)e.ToObject()).ToArray());
        });
        Bind(env, "SupportsComposition", 0, _ => new BooleanObj(Native.aqua_text_composition_supported() != 0));
        Bind(env, "Backspace", 1, a => new StringObj(RemoveLastTextElement(Text(a[0]))));
    }
    internal static string RemoveLastTextElement(string text) {
        int[] offsets = StringInfo.ParseCombiningCharacters(text);
        return offsets.Length == 0 ? "" : text[..offsets[^1]];
    }
    private void SetTextInput(IObject value, bool enabled) {
        IntPtr handle = Window(value);
        Native.aqua_text_input(handle, enabled ? 1 : 0);
        ((WindowObject)value).TextInputEnabled = enabled;
    }
    private void SetTextInputRect(IObject value, IObject[] rect) {
        IntPtr handle = Window(value);
        int x = Int(rect[0]), y = Int(rect[1]), width = Int(rect[2]), height = Int(rect[3]);
        if (width < 0 || height < 0) throw new ArgumentException("Caret dimensions must be nonnegative.");
        checked { _ = x + width; _ = y + height; }
        Native.aqua_text_rect(handle, x, y, width, height);
    }
    private static List<TextInputEvent> ReadTextInput(IntPtr window) {
        var events = new List<TextInputEvent>();
        while (true) {
            IntPtr pointer = Native.aqua_text_event(window, out int kind, out int cursor);
            if (kind == 0) return events;
            if (kind == -1) throw new InvalidOperationException("Text input queue exceeded 1 MiB or allocation failed; poll input more often.");
            string type = kind switch { 1 => "textInput", 2 => "compositionStarted", 3 => "compositionUpdated", 4 => "compositionEnded", _ => throw new InvalidOperationException("Unknown native text input event.") };
            events.Add(new TextInputEvent(type, Marshal.PtrToStringUTF8(pointer) ?? "", cursor));
        }
    }
}
