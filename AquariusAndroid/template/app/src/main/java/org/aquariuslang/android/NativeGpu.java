package org.aquariuslang.android;

import android.view.Surface;
import java.nio.ByteBuffer;

final class NativeGpu {
  static {
    System.loadLibrary("aquarius_android");
  }

  static native String command(
      String operation, String json, ByteBuffer data, int offset, int length);

  static native byte[] read(long device, long resource, boolean texture);

  static native void surface(Surface surface);
}
