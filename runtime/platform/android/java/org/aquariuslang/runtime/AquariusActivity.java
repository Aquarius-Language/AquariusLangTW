package org.aquariuslang.runtime;

import android.app.NativeActivity;
import android.content.ActivityNotFoundException;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Bundle;
import android.text.InputType;
import android.view.KeyEvent;
import android.view.View;
import android.view.WindowInsets;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.view.inputmethod.InputConnectionWrapper;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.FrameLayout;

/**
 * The activity of every AquariusLang Android application. The runtime (libaquarius.so) runs on NativeActivity's
 * native thread; this class only forwards what Android delivers to Java alone: activity results, permission results,
 * window insets and input-method text. Everything else happens in native code (runtime/platform/android).
 */
public final class AquariusActivity extends NativeActivity {
    static {
        System.loadLibrary("aquarius");
    }

    private EditText editor;
    private volatile float insetLeft, insetTop, insetRight, insetBottom;
    private volatile boolean keyboardVisible;

    private static native void nativeActivityResult(int request, int result, Intent data);
    private static native void nativePermissionResult(int request, boolean granted);
    private static native void nativeText(String text);
    private static native void nativeComposition(String text, int cursor);
    private static native void nativeKey(int keyCode, boolean down);

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        editor = new InputEditor(this);
        addContentView(editor, new FrameLayout.LayoutParams(1, 1));
        getWindow().getDecorView().setOnApplyWindowInsetsListener((view, insets) -> {
            android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
            insetLeft = bars.left; insetTop = bars.top; insetRight = bars.right; insetBottom = bars.bottom;
            keyboardVisible = insets.isVisible(WindowInsets.Type.ime());
            return view.onApplyWindowInsets(insets);
        });
    }

    @Override
    protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        nativeActivityResult(request, result, data);
    }

    @Override
    public void onRequestPermissionsResult(int request, String[] permissions, int[] grants) {
        super.onRequestPermissionsResult(request, permissions, grants);
        nativePermissionResult(request, grants.length > 0 && grants[0] == PackageManager.PERMISSION_GRANTED);
    }

    // ---- Called from native code (any thread) ----

    public void startForResult(Intent intent, int request) {
        runOnUiThread(() -> {
            try { startActivityForResult(intent, request); }
            catch (ActivityNotFoundException | SecurityException e) { nativeActivityResult(request, RESULT_CANCELED, null); }
        });
    }

    public void requestPermission(String permission, int request) {
        runOnUiThread(() -> {
            if (checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED) nativePermissionResult(request, true);
            else requestPermissions(new String[] {permission}, request);
        });
    }

    /** Left, top, right, bottom system-bar and cutout insets in pixels, then 1 when the soft keyboard is visible. */
    public float[] insets() {
        return new float[] {insetLeft, insetTop, insetRight, insetBottom, keyboardVisible ? 1 : 0};
    }

    public void showKeyboard(boolean show) {
        runOnUiThread(() -> {
            InputMethodManager manager = (InputMethodManager) getSystemService(Context.INPUT_METHOD_SERVICE);
            if (show) {
                editor.requestFocus();
                manager.showSoftInput(editor, 0);
            } else {
                manager.hideSoftInputFromWindow(editor.getWindowToken(), 0);
                editor.clearFocus();
            }
        });
    }

    /** A 1x1 editor that owns the input-method connection: composition stays separate from committed text. */
    private static final class InputEditor extends EditText {
        private String composing = "";

        InputEditor(Context context) {
            super(context);
            setAlpha(0.01f);
            setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE);
            setFocusableInTouchMode(true);
        }

        @Override
        public InputConnection onCreateInputConnection(EditorInfo info) {
            InputConnection base = super.onCreateInputConnection(info);
            // The application draws its own editing UI; a fullscreen extracted editor would hide it.
            info.imeOptions |= EditorInfo.IME_FLAG_NO_EXTRACT_UI | EditorInfo.IME_FLAG_NO_FULLSCREEN;
            if (base == null) return null;
            return new InputConnectionWrapper(base, true) {
                @Override
                public boolean commitText(CharSequence text, int position) {
                    if (!composing.isEmpty()) nativeComposition(null, 0);
                    composing = "";
                    nativeText(text.toString());
                    getEditableText().clear();
                    return true;
                }

                @Override
                public boolean setComposingText(CharSequence text, int position) {
                    composing = text.toString();
                    nativeComposition(composing, composing.length());
                    return super.setComposingText(text, position);
                }

                @Override
                public boolean finishComposingText() {
                    if (!composing.isEmpty()) {
                        nativeComposition(null, 0);
                        nativeText(composing);
                    }
                    composing = "";
                    getEditableText().clear();
                    return super.finishComposingText();
                }

                @Override
                public boolean deleteSurroundingText(int before, int after) {
                    for (int i = 0; i < before; i++) { nativeKey(KeyEvent.KEYCODE_DEL, true); nativeKey(KeyEvent.KEYCODE_DEL, false); }
                    return true;
                }

                @Override
                public boolean deleteSurroundingTextInCodePoints(int before, int after) {
                    return deleteSurroundingText(before, after);
                }

                @Override
                public boolean sendKeyEvent(KeyEvent event) {
                    if (event.getAction() == KeyEvent.ACTION_DOWN || event.getAction() == KeyEvent.ACTION_UP)
                        nativeKey(event.getKeyCode(), event.getAction() == KeyEvent.ACTION_DOWN);
                    return true;
                }
            };
        }
    }
}
