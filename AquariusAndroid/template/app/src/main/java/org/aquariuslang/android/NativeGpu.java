package org.aquariuslang.android;

import android.view.Surface;

final class NativeGpu {
  static {
    System.loadLibrary("aquarius_android");
  }

  static native String command(String operation, String json, byte[] data);

  static native byte[] read(long device, long resource, boolean texture);

  static native void surface(Surface surface);
}
