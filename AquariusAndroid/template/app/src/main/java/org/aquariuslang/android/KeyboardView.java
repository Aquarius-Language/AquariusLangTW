package org.aquariuslang.android;

import android.content.Context;
import android.view.KeyEvent;
import android.view.inputmethod.*;
import android.widget.EditText;

/** IME composition is distinct from committed text, including Chinese and emoji input. */
final class KeyboardView extends EditText {
  NativeBridge bridge;
  private String composing = "";

  KeyboardView(Context context) {
    super(context);
    setSingleLine(false);
  }

  @Override
  public boolean onKeyDown(int code, KeyEvent event) {
    return bridge != null && bridge.key(code, event, true) || super.onKeyDown(code, event);
  }

  @Override
  public boolean onKeyUp(int code, KeyEvent event) {
    return bridge != null && bridge.key(code, event, false) || super.onKeyUp(code, event);
  }

  @Override
  public InputConnection onCreateInputConnection(EditorInfo info) {
    InputConnection base = super.onCreateInputConnection(info);
    // The renderer owns the editing UI; Android's extracted fullscreen editor would hide it.
    info.imeOptions |= EditorInfo.IME_FLAG_NO_EXTRACT_UI | EditorInfo.IME_FLAG_NO_FULLSCREEN;
    if (base == null) return null;
    return new InputConnectionWrapper(base, true) {
      @Override
      public boolean commitText(CharSequence text, int position) {
        if (bridge != null) {
          bridge.text(text.toString());
          bridge.composition("compositionEnded", "");
        }
        composing = "";
        return super.commitText(text, position);
      }

      @Override
      public boolean setComposingText(CharSequence text, int position) {
        if (bridge != null) {
          if (composing.isEmpty()) bridge.composition("compositionStarted", text.toString());
          bridge.composition("compositionUpdated", text.toString());
        }
        composing = text.toString();
        return super.setComposingText(text, position);
      }

      @Override
      public boolean finishComposingText() {
        if (bridge != null && !composing.isEmpty()) {
          bridge.text(composing);
          bridge.composition("compositionEnded", "");
        }
        composing = "";
        return super.finishComposingText();
      }

      @Override
      public boolean deleteSurroundingText(int before, int after) {
        if (bridge != null && before > 0) {
          bridge.key(
              KeyEvent.KEYCODE_DEL, new KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_DEL), true);
          bridge.key(
              KeyEvent.KEYCODE_DEL, new KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_DEL), false);
        }
        return super.deleteSurroundingText(before, after);
      }

      @Override
      public boolean sendKeyEvent(KeyEvent event) {
        return bridge != null
                && bridge.key(event.getKeyCode(), event, event.getAction() == KeyEvent.ACTION_DOWN)
            || super.sendKeyEvent(event);
      }

      @Override
      public boolean deleteSurroundingTextInCodePoints(int before, int after) {
        if (bridge != null && before > 0) {
          bridge.key(
              KeyEvent.KEYCODE_DEL, new KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_DEL), true);
          bridge.key(
              KeyEvent.KEYCODE_DEL, new KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_DEL), false);
        }
        return super.deleteSurroundingTextInCodePoints(before, after);
      }
    };
  }
}
