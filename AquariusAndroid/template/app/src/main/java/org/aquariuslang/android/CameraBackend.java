package org.aquariuslang.android;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.ImageFormat;
import android.graphics.Matrix;
import android.hardware.camera2.*;
import android.media.Image;
import android.media.ImageReader;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Size;
import java.nio.ByteBuffer;
import java.util.Arrays;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import org.json.JSONArray;
import org.json.JSONObject;

/**
 * Camera2 adapter. The latest complete frame replaces old frames; cancellation closes every handle.
 */
final class CameraBackend implements AutoCloseable {
  private final NativeBridge bridge;
  private final CameraManager manager;
  private final java.util.concurrent.ConcurrentHashMap<Integer, Session> sessions =
      new java.util.concurrent.ConcurrentHashMap<>();
  private volatile boolean paused;
  private int next;

  CameraBackend(NativeBridge bridge) {
    this.bridge = bridge;
    manager = (CameraManager) bridge.activity.getSystemService(Context.CAMERA_SERVICE);
  }

  JSONObject call(String op, JSONObject args) {
    try {
      if (op.equals("enumerate")) {
        JSONArray list = new JSONArray();
        for (String id : manager.getCameraIdList()) {
          CameraCharacteristics c = manager.getCameraCharacteristics(id);
          Integer facing = c.get(CameraCharacteristics.LENS_FACING);
          list.put(
              NativeBridge.object(
                  "id",
                  id,
                  "name",
                  "Camera " + id,
                  "facing",
                  facing != null && facing == CameraCharacteristics.LENS_FACING_FRONT
                      ? "Front"
                      : facing != null && facing == CameraCharacteristics.LENS_FACING_EXTERNAL
                          ? "External"
                          : "Back"));
        }
        return NativeBridge.object("devices", list);
      }
      if (op.equals("open")) {
        for (var entry : sessions.entrySet())
          if (entry.getValue().closed) {
            sessions.remove(entry.getKey());
            entry.getValue().close();
          }
        if (sessions.size() >= 4)
          throw new IllegalStateException("Unavailable: At most four camera sessions");
        bridge.requestCameraPermission();
        if (paused) throw new IllegalStateException("Cancelled: Camera paused");
        String id = args.getString("id");
        if (id.isEmpty()) {
          String[] ids = manager.getCameraIdList();
          if (ids.length == 0) throw new IllegalStateException("NotFound: No camera");
          id = ids[0];
          for (String candidate : ids) {
            Integer facing =
                manager.getCameraCharacteristics(candidate).get(CameraCharacteristics.LENS_FACING);
            if (facing != null && facing == CameraCharacteristics.LENS_FACING_BACK) {
              id = candidate;
              break;
            }
          }
        }
        int w = args.getInt("width"), h = args.getInt("height"), fps = args.getInt("fps");
        if (w < 1 || w > 1920 || h < 1 || h > 1080 || fps < 1 || fps > 60)
          throw new IllegalArgumentException("Invalid camera options");
        Session session = new Session(id, w, h, fps);
        int handle = ++next;
        synchronized (this) {
          if (paused) {
            session.close();
            throw new IllegalStateException("Cancelled: Camera paused");
          }
          sessions.put(handle, session);
        }
        return NativeBridge.object("id", handle);
      }
      int id = args.getInt("id");
      Session session = sessions.get(id);
      if (op.equals("dispose")) {
        if (session != null) {
          sessions.remove(id);
          session.close();
        }
        return new JSONObject();
      }
      if (op.equals("isOpen"))
        return NativeBridge.object("open", session != null && !session.closed);
      if (session == null || session.closed)
        throw new IllegalStateException("Closed: Camera session closed");
      if (op.equals("capture")) {
        JSONObject frame = session.frames.poll(10, TimeUnit.SECONDS);
        if (frame == null) {
          sessions.remove(id);
          session.close();
          throw new IllegalStateException("Timeout: Camera capture timed out");
        }
        return frame;
      }
      if (op.equals("isOpen")) return NativeBridge.object("open", !session.closed);
      throw new IllegalArgumentException("Unknown camera operation");
    } catch (Exception e) {
      throw new IllegalStateException(e.getMessage(), e);
    }
  }

  private final class Session implements AutoCloseable {
    final ArrayBlockingQueue<JSONObject> frames = new ArrayBlockingQueue<>(1);
    final HandlerThread thread = new HandlerThread("Aquarius-Camera");
    CameraDevice camera;
    CameraCaptureSession capture;
    ImageReader reader;
    volatile boolean closed;
    final java.util.concurrent.atomic.AtomicBoolean released =
        new java.util.concurrent.atomic.AtomicBoolean();
    long sequence;
    final int width, height, sensor;
    final String facing;

    Session(String id, int width, int height, int fps) throws Exception {
      this.width = width;
      this.height = height;
      CameraCharacteristics c = manager.getCameraCharacteristics(id);
      sensor = c.get(CameraCharacteristics.SENSOR_ORIENTATION);
      Integer f = c.get(CameraCharacteristics.LENS_FACING);
      facing =
          f != null && f == CameraCharacteristics.LENS_FACING_FRONT
              ? "Front"
              : f != null && f == CameraCharacteristics.LENS_FACING_EXTERNAL ? "External" : "Back";
      Size[] sizes =
          c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
              .getOutputSizes(ImageFormat.YUV_420_888);
      Size size =
          Arrays.stream(sizes)
              .filter(s -> s.getWidth() <= 1920 && s.getHeight() <= 1080)
              .min(
                  java.util.Comparator.comparingDouble(
                      s ->
                          Math.abs((double) s.getWidth() / s.getHeight() - (double) width / height)
                                  * 100000
                              + Math.abs(s.getWidth() * s.getHeight() - width * height)))
              .orElseThrow(() -> new IllegalStateException("Unavailable: No camera output size"));
      thread.start();
      Handler handler = new Handler(thread.getLooper());
      reader =
          ImageReader.newInstance(size.getWidth(), size.getHeight(), ImageFormat.YUV_420_888, 2);
      reader.setOnImageAvailableListener(
          r -> {
            try (Image image = r.acquireLatestImage()) {
              if (image == null || closed) return;
              JSONObject frame = convert(image);
              frames.poll();
              frames.offer(frame);
            } catch (Exception e) {
              close();
            }
          },
          handler);
      CountDownLatch ready = new CountDownLatch(1);
      String[] failure = {null};
      try {
        manager.openCamera(
            id,
            new CameraDevice.StateCallback() {
              public void onOpened(CameraDevice device) {
                synchronized (Session.this) {
                  if (closed) {
                    device.close();
                    ready.countDown();
                    return;
                  }
                  camera = device;
                }
                try {
                  device.createCaptureSession(
                      java.util.Collections.singletonList(reader.getSurface()),
                      new CameraCaptureSession.StateCallback() {
                        public void onConfigured(CameraCaptureSession session) {
                          synchronized (Session.this) {
                            if (closed) {
                              session.close();
                              ready.countDown();
                              return;
                            }
                            capture = session;
                          }
                          try {
                            CaptureRequest.Builder request =
                                device.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
                            request.addTarget(reader.getSurface());
                            android.util.Range<Integer>[] ranges =
                                c.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES);
                            if (ranges != null && ranges.length > 0)
                              request.set(
                                  CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE,
                                  Arrays.stream(ranges)
                                      .min(
                                          java.util.Comparator.comparingInt(
                                              range -> Math.abs(range.getUpper() - fps)))
                                      .get());
                            session.setRepeatingRequest(request.build(), null, handler);
                          } catch (Exception e) {
                            failure[0] = e.toString();
                          }
                          ready.countDown();
                        }

                        public void onConfigureFailed(CameraCaptureSession session) {
                          failure[0] = "Unavailable: Camera configuration failed";
                          ready.countDown();
                        }
                      },
                      handler);
                } catch (Exception e) {
                  failure[0] = e.toString();
                  ready.countDown();
                }
              }

              public void onDisconnected(CameraDevice device) {
                device.close();
                failure[0] = "Unavailable: Camera disconnected";
                close();
                ready.countDown();
              }

              public void onError(CameraDevice device, int error) {
                device.close();
                failure[0] = "Unavailable: Camera error " + error;
                close();
                ready.countDown();
              }
            },
            handler);
        if (!ready.await(10, TimeUnit.SECONDS))
          throw new IllegalStateException("Timeout: Camera open timed out");
        if (failure[0] != null) throw new IllegalStateException(failure[0]);
      } catch (Exception e) {
        close();
        throw e;
      }
    }

    JSONObject convert(Image image) {
      int w = image.getWidth(), h = image.getHeight();
      Image.Plane[] planes = image.getPlanes();
      int[] pixels =
          CameraPixels.rgba(
              w,
              h,
              new ByteBuffer[] {
                planes[0].getBuffer(), planes[1].getBuffer(), planes[2].getBuffer()
              },
              new int[] {
                planes[0].getRowStride(), planes[1].getRowStride(), planes[2].getRowStride()
              },
              new int[] {
                planes[0].getPixelStride(), planes[1].getPixelStride(), planes[2].getPixelStride()
              });
      Bitmap raw = Bitmap.createBitmap(pixels, w, h, Bitmap.Config.ARGB_8888);
      int display = bridge.activity.getWindowManager().getDefaultDisplay().getRotation() * 90;
      int rotation = CameraPixels.rotation(sensor, display, facing.equals("Front"));
      Matrix matrix = new Matrix();
      matrix.postRotate(rotation);
      Bitmap upright = Bitmap.createBitmap(raw, 0, 0, w, h, matrix, true);
      Bitmap scaled =
          Bitmap.createScaledBitmap(
              upright,
              rotation % 180 == 0 ? width : height,
              rotation % 180 == 0 ? height : width,
              true);
      try {
        return NativeBridge.object(
            "width",
            scaled.getWidth(),
            "height",
            scaled.getHeight(),
            "pixels",
            NativeBridge.base64(NativeBridge.pixels(scaled)),
            "sequence",
            ++sequence,
            "timestamp",
            image.getTimestamp() / 1e6,
            "facing",
            facing);
      } finally {
        if (scaled != upright) scaled.recycle();
        if (upright != raw) upright.recycle();
        raw.recycle();
      }
    }

    public synchronized void close() {
      if (!released.compareAndSet(false, true)) return;
      closed = true;
      if (capture != null) capture.close();
      if (camera != null) camera.close();
      if (reader != null) reader.close();
      thread.quitSafely();
      frames.clear();
    }
  }

  synchronized void pause() {
    paused = true;
    close();
  }

  void resume() {
    paused = false;
  }

  public synchronized void close() {
    for (Session s : sessions.values()) s.close();
    sessions.clear();
  }
}
