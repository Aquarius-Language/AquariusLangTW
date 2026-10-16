package org.aquariuslang.android;

import android.app.Activity;
import android.os.Bundle;
import android.view.KeyEvent;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.TextView;

/** Native activity and surface. A single runtime actor owns Wasmtime and GPU commands. */
public final class AquariusActivity extends Activity implements SurfaceHolder.Callback {
  private NativeBridge bridge;
  private NativeRuntime runtime;
  private SurfaceView surface;
  private EditText editor;

  @Override
  public void onCreate(Bundle state) {
    super.onCreate(state);
    FrameLayout layout = new FrameLayout(this);
    layout.setBackgroundColor(0xff0d1624);
    if (android.os.Build.VERSION.SDK_INT < 30) {
      View decor = getWindow().getDecorView();
      decor.setSystemUiVisibility(
          decor.getSystemUiVisibility()
              & ~(View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR));
      getWindow().setStatusBarColor(0xff0d1624);
      getWindow().setNavigationBarColor(0xff0d1624);
    }
    surface = new SurfaceView(this);
    surface.getHolder().addCallback(this);
    surface.setFocusableInTouchMode(true);
    layout.addView(surface);
    editor = new KeyboardView(this);
    editor.setSingleLine(false);
    editor.setAlpha(0.01f);
    layout.addView(editor, new FrameLayout.LayoutParams(1, 1));
    setContentView(layout);
    bridge = new NativeBridge(this, editor);
    ((KeyboardView) editor).bridge = bridge;
    surface.setOnTouchListener(
        (v, event) -> {
          bridge.touch(event);
          return true;
        });
    layout.setOnApplyWindowInsetsListener(
        (v, insets) -> {
          bridge.insets(insets);
          if (android.os.Build.VERSION.SDK_INT >= 30) {
            WindowInsetsController controller = getWindow().getInsetsController();
            if (controller != null)
              controller.setSystemBarsAppearance(
                  0,
                  WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS
                      | WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS);
            var safe =
                insets.getInsets(
                    WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
            v.setPadding(safe.left, safe.top, safe.right, safe.bottom);
          }
          return insets;
        });
    if (android.os.Build.VERSION.SDK_INT < 30) layout.setFitsSystemWindows(true);
    runtime =
        new NativeRuntime(
            bridge,
            error ->
                runOnUiThread(
                    () -> {
                      TextView message = new TextView(this);
                      message.setText(error);
                      message.setPadding(24, 24, 24, 24);
                      layout.addView(message);
                    }));
    if (android.os.Build.VERSION.SDK_INT >= 33)
      getOnBackInvokedDispatcher()
          .registerOnBackInvokedCallback(
              android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, () -> bridge.back());
  }

  public void surfaceCreated(SurfaceHolder holder) {
    bridge.surface(holder.getSurface(), surface.getWidth(), surface.getHeight());
    runtime.start(getIntent().getIntExtra("frames", 0));
  }

  public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
    bridge.surface(holder.getSurface(), width, height);
  }

  public void surfaceDestroyed(SurfaceHolder holder) {
    bridge.surface(null, 0, 0);
  }

  @Override
  public boolean onKeyDown(int code, KeyEvent event) {
    if (bridge.key(code, event, true)) return true;
    return super.onKeyDown(code, event);
  }

  @Override
  public boolean onKeyUp(int code, KeyEvent event) {
    if (bridge.key(code, event, false)) return true;
    return super.onKeyUp(code, event);
  }

  @Override
  public void onBackPressed() {
    bridge.back();
  }

  @Override
  protected void onPause() {
    super.onPause();
    if (bridge != null) bridge.pause();
  }

  @Override
  protected void onResume() {
    super.onResume();
    if (bridge != null) bridge.resume();
  }

  @Override
  protected void onDestroy() {
    if (runtime != null) runtime.close();
    if (bridge != null) bridge.close();
    super.onDestroy();
  }

  @Override
  public void onRequestPermissionsResult(int request, String[] permissions, int[] grants) {
    super.onRequestPermissionsResult(request, permissions, grants);
    bridge.permissionResult(request, grants);
  }

  @Override
  protected void onActivityResult(int request, int result, android.content.Intent data) {
    super.onActivityResult(request, result, data);
    bridge.pickerResult(request, result, data);
  }
}
