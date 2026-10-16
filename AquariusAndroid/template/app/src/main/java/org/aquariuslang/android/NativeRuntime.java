package org.aquariuslang.android;

import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Consumer;

/** Wasmtime executes guest code; QuickJS provides the shared capability host. */
public final class NativeRuntime implements AutoCloseable {
  static { System.loadLibrary("aquarius_runtime"); }
  private final NativeBridge bridge;
  private final Consumer<String> failure;
  private final AtomicBoolean started = new AtomicBoolean();
  private volatile boolean closed;
  private long engine;
  private volatile Thread actor;

  private static native long create(NativeBridge bridge);
  private static native boolean step(long engine, int frames, boolean initialize);
  private static native void interrupt(long engine);
  private static native void destroy(long engine);

  public NativeRuntime(NativeBridge bridge, Consumer<String> failure) {
    this.bridge = bridge;
    this.failure = failure;
  }

  public void start(int frames) {
    if (!started.compareAndSet(false, true)) return;
    actor = new Thread(null, () -> {
      try {
        synchronized (this) { if (!closed) engine = create(bridge); }
        boolean initialize = true;
        while (!closed && !step(engine, frames, initialize)) {
          initialize = false;
          Thread.sleep(1);
        }
      } catch (Throwable e) {
        if (!closed) {
          bridge.log("AQUARIUS_ANDROID_ERROR: " + e);
          failure.accept("應用程式無法執行。\n" + e.getMessage());
        }
      } finally {
        synchronized (this) { if (engine != 0) destroy(engine); engine = 0; }
        bridge.releaseRuntime();
      }
    }, "Aquarius-Wasmtime", 8 * 1024 * 1024);
    actor.start();
  }

  public synchronized void close() {
    closed = true;
    if (engine != 0) interrupt(engine);
    if (actor != null) actor.interrupt();
  }
}
