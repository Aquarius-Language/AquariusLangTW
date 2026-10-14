package org.aquariuslang.android;

import android.app.Activity;
import android.os.SystemClock;
import android.view.Choreographer;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/** One outstanding frame request; no continuously polling UI callback. */
final class FrameClock implements AutoCloseable {
  private final Activity activity;
  private volatile boolean closed;
  private volatile CountDownLatch pending;

  FrameClock(Activity activity) {
    this.activity = activity;
  }

  void await(double deadline) {
    if (!Double.isFinite(deadline)) throw new IllegalArgumentException("Invalid frame deadline");
    CountDownLatch ready = new CountDownLatch(1);
    pending = ready;
    activity.runOnUiThread(
        () -> {
          Choreographer choreographer = Choreographer.getInstance();
          choreographer.postFrameCallback(
              new Choreographer.FrameCallback() {
                public void doFrame(long frameTimeNanos) {
                  // elapsedRealtime also includes device sleep, matching native.now().
                  if (closed || SystemClock.elapsedRealtimeNanos() / 1e6 >= deadline)
                    ready.countDown();
                  else choreographer.postFrameCallback(this);
                }
              });
        });
    try {
      while (!ready.await(100, TimeUnit.MILLISECONDS)) {
        if (closed) throw new IllegalStateException("Runtime closed");
      }
      if (closed) throw new IllegalStateException("Runtime closed");
    } catch (InterruptedException e) {
      Thread.currentThread().interrupt();
      throw new IllegalStateException("Cancelled", e);
    } finally {
      pending = null;
    }
  }

  public void close() {
    closed = true;
    CountDownLatch ready = pending;
    if (ready != null) ready.countDown();
  }
}
