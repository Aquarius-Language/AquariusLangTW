using AquariusLang.Object;

namespace AquariusREPL.Graphics;

internal sealed partial class GraphicsRuntime {
    private void RegisterProcessingTextInput() {
        ResetProcessingTextInput();
        PAction(processing, "startTextInput", 0, 0, _ => {
            RequireSketch();
            if(sketchWindow!.TextInputEnabled)return;
            SetTextInput(sketchWindow, true);
            ResetProcessingTextInput();
            processing.Create("textInputEnabled", new BooleanObj(true));
        });
        PAction(processing, "stopTextInput", 0, 0, _ => {
            RequireSketch(); SetTextInput(sketchWindow!, false); ResetProcessingTextInput();
        });
        PAction(processing, "textInputRect", 4, 4, a => {
            RequireSketch(); SetTextInputRect(sketchWindow!, a);
        });
    }
    private void ResetProcessingTextInput() {
        processing.Create("textInputEnabled", new BooleanObj(false));
        processing.Create("inputText", new StringObj(""));
        ClearProcessingComposition(false);
    }
    private void ClearProcessingComposition(bool composing) {
        processing.Create("compositionText", new StringObj(""));
        processing.Create("compositionCursor", N(0));
        processing.Create("isComposing", new BooleanObj(composing));
    }
    internal void DispatchTextInput(TextInputEvent input) {
        switch (input.Type) {
        case "textInput":
            redraw = true;
            processing.Create("inputText", new StringObj(input.Text));
            processing.Create("compositionText", new StringObj(""));
            processing.Create("compositionCursor", N(0));
            Event("textInput");
            // Preserve keyTyped's one Unicode scalar per callback contract.
            foreach(var rune in input.Text.EnumerateRunes()) {
                processing.Create("key", new StringObj(rune.ToString())); Event("keyTyped");
            }
            return;
        case "compositionStarted": ClearProcessingComposition(true); break;
        case "compositionUpdated":
            processing.Create("compositionText", new StringObj(input.Text));
            processing.Create("compositionCursor", N(input.Cursor));
            processing.Create("isComposing", new BooleanObj(true));
            break;
        case "compositionEnded": ClearProcessingComposition(false); break;
        default: throw new ArgumentException("Unknown text input event.");
        }
        Event(input.Type);
        redraw = true;
    }
}
