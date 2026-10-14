package org.aquariuslang.android;

import android.Manifest;
import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.os.SystemClock;
import android.os.VibrationEffect;
import android.os.Vibrator;
import android.util.Base64;
import android.util.Log;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.Surface;
import android.view.WindowInsets;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import com.caoccao.javet.annotations.V8Function;
import com.caoccao.javet.values.reference.V8ValueTypedArray;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.SecureRandom;
import java.util.UUID;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import org.json.JSONArray;
import org.json.JSONObject;

/** Native services expose data, never Android objects, to the capability runtime. */
public final class NativeBridge implements AutoCloseable {
  final Activity activity;
  private final EditText editor;
  private final FrameClock frameClock;
  private final CameraBackend cameras;
  private final OnnxBackend onnx = new OnnxBackend(this);
  private final AudioBackend audio = new AudioBackend();
  private final NativeFonts fonts = new NativeFonts();
  private final SecureRandom random = new SecureRandom();
  private final java.util.Set<Long> gpuDevices = new java.util.HashSet<>();
  private final AndroidServices services = new AndroidServices(this);
  private final ConcurrentLinkedQueue<JSONObject> events = new ConcurrentLinkedQueue<>();
  private volatile int width = 1, height = 1;
  private volatile boolean paused, closed;
  private volatile boolean keyboardVisible, runtimeFinished;
  private volatile JSONObject safeInsets = new JSONObject();
  private volatile CountDownLatch permission;
  private volatile CountDownLatch pickerReady;
  private volatile JSONArray selected = new JSONArray();
  private final Object lifecycle = new Object();

  public NativeBridge(Activity activity, EditText editor) {
    this.activity = activity;
    this.editor = editor;
    this.frameClock = new FrameClock(activity);
    this.cameras = new CameraBackend(this);
  }

  static byte[] readBounded(java.io.InputStream input, int limit) throws java.io.IOException {
    try (input;
        var output = new ByteArrayOutputStream()) {
      byte[] buffer = new byte[16384];
      int count;
      while ((count = input.read(buffer)) != -1) {
        if ((long) output.size() + count > limit)
          throw new java.io.IOException("LimitExceeded: Resource too large");
        output.write(buffer, 0, count);
      }
      return output.toByteArray();
    }
  }

  String source(String name) {
    try {
      return new String(
          readBounded(activity.getAssets().open("aquarius/" + name), 256 * 1024 * 1024),
          StandardCharsets.UTF_8);
    } catch (Exception e) {
      throw new IllegalStateException("Missing runtime module: " + name, e);
    }
  }

  private static JSONObject json(String text) {
    try {
      return new JSONObject(text);
    } catch (Exception e) {
      throw new IllegalArgumentException(e);
    }
  }

  static JSONObject object(Object... values) {
    try {
      JSONObject result = new JSONObject();
      for (int i = 0; i < values.length; i += 2) result.put((String) values[i], values[i + 1]);
      return result;
    } catch (Exception e) {
      throw new IllegalStateException(e);
    }
  }

  static String base64(byte[] bytes) {
    return Base64.encodeToString(bytes, Base64.NO_WRAP);
  }

  static byte[] unbase64(String text) {
    return Base64.decode(text, Base64.DEFAULT);
  }

  byte[] assetBytes(String name, int limit) {
    if (!name.matches("[A-Za-z0-9_.-]+"))
      throw new IllegalArgumentException("Invalid runtime asset path");
    try {
      return readBounded(activity.getAssets().open("aquarius/" + name), limit);
    } catch (Exception e) {
      throw new IllegalStateException("Asset not found: " + name, e);
    }
  }

  @V8Function
  public String readJson() {
    return source("program.json");
  }

  @V8Function
  public String readAsset(String name) {
    if (!name.matches("[A-Za-z0-9_.-]+"))
      throw new IllegalArgumentException("Invalid runtime asset path");
    try {
      return base64(readBounded(activity.getAssets().open("aquarius/" + name), 256 * 1024 * 1024));
    } catch (Exception e) {
      throw new IllegalStateException("Asset not found: " + name, e);
    }
  }

  @V8Function
  public String utf8(String bytes) {
    return new String(unbase64(bytes), StandardCharsets.UTF_8);
  }

  @V8Function
  public String decodeText(String bytes, String encoding, boolean fatal, boolean ignoreBom) {
    try {
      var decoder = java.nio.charset.Charset.forName(encoding).newDecoder();
      decoder.onMalformedInput(
          fatal
              ? java.nio.charset.CodingErrorAction.REPORT
              : java.nio.charset.CodingErrorAction.REPLACE);
      decoder.onUnmappableCharacter(
          fatal
              ? java.nio.charset.CodingErrorAction.REPORT
              : java.nio.charset.CodingErrorAction.REPLACE);
      String result = decoder.decode(java.nio.ByteBuffer.wrap(unbase64(bytes))).toString();
      return !ignoreBom && result.startsWith("\ufeff") ? result.substring(1) : result;
    } catch (Exception e) {
      throw new IllegalArgumentException("Invalid text encoding or bytes", e);
    }
  }

  @V8Function
  public String segments(String text, String granularity) {
    android.icu.text.BreakIterator iterator =
        granularity.equals("word")
            ? android.icu.text.BreakIterator.getWordInstance(java.util.Locale.ROOT)
            : android.icu.text.BreakIterator.getCharacterInstance(java.util.Locale.ROOT);
    iterator.setText(text);
    JSONArray result = new JSONArray();
    int start = iterator.first();
    for (int end = iterator.next();
        end != android.icu.text.BreakIterator.DONE;
        start = end, end = iterator.next()) {
      String segment = text.substring(start, end);
      result.put(
          object(
              "segment",
              segment,
              "index",
              start,
              "input",
              text,
              "isWordLike",
              segment.codePoints().anyMatch(Character::isLetterOrDigit)));
    }
    return result.toString();
  }

  @V8Function
  public String encode(String text) {
    return base64(text.getBytes(StandardCharsets.UTF_8));
  }

  @V8Function
  public String uuid() {
    return UUID.randomUUID().toString();
  }

  @V8Function
  public String randomBytes(int length) {
    if (length < 0 || length > 65536)
      throw new IllegalArgumentException("Random byte request must be 0..65536 bytes.");
    byte[] bytes = new byte[length];
    random.nextBytes(bytes);
    return base64(bytes);
  }

  @V8Function
  public double now() {
    return SystemClock.elapsedRealtimeNanos() / 1e6;
  }

  @V8Function
  public void log(String text) {
    Log.i("Aquarius", text);
  }

  @V8Function
  public void fail(String message) {
    log("AQUARIUS_ANDROID_ERROR: " + message);
    activity.runOnUiThread(
        () -> {
          android.widget.TextView view = new android.widget.TextView(activity);
          view.setText("應用程式無法執行。\n" + message);
          view.setPadding(24, 24, 24, 24);
          activity.setContentView(view);
        });
  }

  @V8Function
  public String gpu(String operation, String data, V8ValueTypedArray bytes)
      throws com.caoccao.javet.exceptions.JavetException {
    // Javet exposes the ArrayBuffer as a direct view. JNI consumes it only
    // during this synchronous call; no V8 pointer escapes into pending GPU work.
    try (var buffer = bytes.getBuffer()) {
      String result =
          NativeGpu.command(
              operation,
              data,
              buffer.getByteBuffer(),
              bytes.getByteOffset(),
              bytes.getByteLength());
      if (operation.equals("device")) gpuDevices.add(Long.parseLong(result));
      if (operation.equals("disposeDevice")) gpuDevices.remove(json(data).optLong("device"));
      return result;
    }
  }

  @V8Function
  public byte[] gpuRead(double device, double resource, boolean texture) {
    // Javet converts byte[] to an owned Int8Array. The JS adapter views its
    // buffer as unsigned bytes; readback needs no base64 text or decoding loop.
    return NativeGpu.read((long) device, (long) resource, texture);
  }

  @V8Function
  public String viewport() {
    float density = activity.getResources().getDisplayMetrics().density;
    return object(
            "width",
            Math.max(1, Math.round(width / density)),
            "height",
            Math.max(1, Math.round(height / density)),
            "pixelWidth",
            paused ? 0 : width,
            "pixelHeight",
            paused ? 0 : height,
            "density",
            density)
        .toString();
  }

  @V8Function
  public void waitFrame(int milliseconds) {
    synchronized (lifecycle) {
      try {
        while (paused && !closed) lifecycle.wait(100);
        if (closed) throw new IllegalStateException("Runtime closed");
        lifecycle.wait(Math.max(1, Math.min(100, milliseconds)));
      } catch (InterruptedException e) {
        Thread.currentThread().interrupt();
        throw new IllegalStateException("Cancelled", e);
      }
    }
  }

  @V8Function
  public void awaitFrame(double deadline) {
    synchronized (lifecycle) {
      try {
        while (paused && !closed) lifecycle.wait(100);
        if (closed) throw new IllegalStateException("Runtime closed");
      } catch (InterruptedException e) {
        Thread.currentThread().interrupt();
        throw new IllegalStateException("Cancelled", e);
      }
    }
    frameClock.await(deadline);
  }

  @V8Function
  public String poll() {
    JSONArray list = new JSONArray();
    JSONObject e;
    while ((e = events.poll()) != null) list.put(e);
    return object(
            "events",
            list,
            "insets",
            safeInsets,
            "orientation",
            width < height ? "portrait" : "landscape",
            "keyboardVisible",
            keyboardVisible)
        .toString();
  }

  @V8Function
  public void keyboard(boolean visible) {
    activity.runOnUiThread(
        () -> {
          InputMethodManager manager =
              (InputMethodManager) activity.getSystemService(Context.INPUT_METHOD_SERVICE);
          if (visible) {
            editor.requestFocus();
            manager.showSoftInput(editor, InputMethodManager.SHOW_IMPLICIT);
          } else {
            manager.hideSoftInputFromWindow(editor.getWindowToken(), 0);
            editor.clearFocus();
          }
        });
  }

  @V8Function
  public void vibrate(int duration) {
    if (duration < 1 || duration > 10000)
      throw new IllegalArgumentException("Vibration must be 1..10000 ms");
    ((Vibrator) activity.getSystemService(Context.VIBRATOR_SERVICE))
        .vibrate(VibrationEffect.createOneShot(duration, VibrationEffect.DEFAULT_AMPLITUDE));
  }

  @V8Function
  public void resolveBack(String decision) {
    if (decision.equals("Accept")) activity.runOnUiThread(activity::finish);
    else if (!decision.equals("Cancel") && !decision.equals("Defer"))
      throw new IllegalArgumentException("Invalid back decision");
  }

  @V8Function
  public String camera(String operation, String args) {
    return cameras.call(operation, json(args)).toString();
  }

  @V8Function
  public String inference(String operation, String args) {
    return onnx.call(operation, json(args)).toString();
  }

  @V8Function
  public int audio(String operation, int id, String pcm) {
    return audio.call(operation, id, pcm);
  }

  @V8Function
  public String service(String operation, String args) {
    return services.call(operation, json(args)).toString();
  }

  @V8Function
  public void title(String title) {
    activity.runOnUiThread(() -> activity.setTitle(title));
  }

  @V8Function
  public void accessibility(String nodes) {
    JSONArray list;
    try {
      list = new JSONArray(nodes);
    } catch (Exception e) {
      throw new IllegalArgumentException(e);
    }
    StringBuilder description = new StringBuilder();
    for (int i = 0; i < list.length(); i++)
      description.append(list.optJSONObject(i).optString("label")).append(". ");
    activity.runOnUiThread(
        () -> activity.getWindow().getDecorView().setContentDescription(description));
  }

  @V8Function
  public String picker(String operation, String args) {
    if (pickerReady != null)
      throw new IllegalStateException("Conflict: A picker is already active");
    JSONObject options = json(args);
    selected = new JSONArray();
    CountDownLatch ready = new CountDownLatch(1);
    pickerReady = ready;
    activity.runOnUiThread(
        () -> {
          android.content.Intent intent =
              new android.content.Intent(
                  operation.equals("save")
                      ? android.content.Intent.ACTION_CREATE_DOCUMENT
                      : operation.equals("directory")
                          ? android.content.Intent.ACTION_OPEN_DOCUMENT_TREE
                          : android.content.Intent.ACTION_OPEN_DOCUMENT);
          if (!operation.equals("directory")) {
            intent.addCategory(android.content.Intent.CATEGORY_OPENABLE);
            intent.setType(operation.equals("image") ? "image/*" : "*/*");
            intent.putExtra(
                android.content.Intent.EXTRA_ALLOW_MULTIPLE, options.optBoolean("multiple", false));
            if (operation.equals("save"))
              intent.putExtra(
                  android.content.Intent.EXTRA_TITLE,
                  options.optString("suggestedName", "document"));
          }
          intent.addFlags(
              android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION
                  | android.content.Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                  | android.content.Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
          activity.startActivityForResult(intent, 73);
        });
    try {
      while (!ready.await(100, TimeUnit.MILLISECONDS)) {
        if (closed) throw new IllegalStateException("Cancelled: Runtime closed");
      }
      return object("files", selected).toString();
    } catch (InterruptedException e) {
      throw new IllegalStateException("Cancelled", e);
    } finally {
      pickerReady = null;
    }
  }

  void pickerResult(int request, int result, android.content.Intent intent) {
    if (request != 73 || pickerReady == null) return;
    if (result == Activity.RESULT_OK && intent != null) {
      java.util.List<android.net.Uri> uris = new java.util.ArrayList<>();
      if (intent.getClipData() != null)
        for (int i = 0; i < intent.getClipData().getItemCount(); i++)
          uris.add(intent.getClipData().getItemAt(i).getUri());
      else if (intent.getData() != null) uris.add(intent.getData());
      for (android.net.Uri uri : uris) {
        try {
          activity
              .getContentResolver()
              .takePersistableUriPermission(
                  uri,
                  intent.getFlags()
                      & (android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION
                          | android.content.Intent.FLAG_GRANT_WRITE_URI_PERMISSION));
        } catch (SecurityException ignored) {
        }
        selected.put(describe(uri));
      }
    }
    pickerReady.countDown();
  }

  private JSONObject describe(android.net.Uri uri) {
    String name = "document";
    String kind = android.provider.DocumentsContract.isTreeUri(uri) ? "directory" : "file";
    android.net.Uri queryUri =
        android.provider.DocumentsContract.isTreeUri(uri)
                && !android.provider.DocumentsContract.isDocumentUri(activity, uri)
            ? android.provider.DocumentsContract.buildDocumentUriUsingTree(
                uri, android.provider.DocumentsContract.getTreeDocumentId(uri))
            : uri;
    try (var cursor =
        activity
            .getContentResolver()
            .query(
                queryUri,
                new String[] {
                  android.provider.OpenableColumns.DISPLAY_NAME,
                  android.provider.DocumentsContract.Document.COLUMN_MIME_TYPE
                },
                null,
                null,
                null)) {
      if (cursor != null && cursor.moveToFirst()) {
        name = cursor.getString(0);
        kind =
            android.provider.DocumentsContract.Document.MIME_TYPE_DIR.equals(cursor.getString(1))
                ? "directory"
                : "file";
      }
    } catch (Exception ignored) {
    }
    return object("uri", uri.toString(), "name", name, "kind", kind);
  }

  @V8Function
  public String document(String operation, String args) {
    JSONObject a = json(args);
    try {
      android.net.Uri uri = android.net.Uri.parse(a.getString("uri"));
      if (!uri.getScheme().equals("content"))
        throw new IllegalArgumentException("Invalid document URI");
      var resolver = activity.getContentResolver();
      if (operation.equals("read")) {
        try (var stream = resolver.openInputStream(uri)) {
          byte[] data = readBounded(stream, 64 * 1024 * 1024);
          if (data.length > 64 * 1024 * 1024)
            throw new IllegalArgumentException("LimitExceeded: Document too large");
          return object("bytes", base64(data)).toString();
        }
      }
      if (operation.equals("write")) {
        byte[] data = unbase64(a.getString("bytes"));
        if (data.length > 64 * 1024 * 1024)
          throw new IllegalArgumentException("LimitExceeded: Document too large");
        try (var stream = resolver.openOutputStream(uri, "wt")) {
          stream.write(data);
        }
        return "{}";
      }
      if (operation.equals("delete")) {
        android.provider.DocumentsContract.deleteDocument(resolver, uri);
        return "{}";
      }
      if (operation.equals("enumerate")) {
        String id =
            android.provider.DocumentsContract.isDocumentUri(activity, uri)
                ? android.provider.DocumentsContract.getDocumentId(uri)
                : android.provider.DocumentsContract.getTreeDocumentId(uri);
        android.net.Uri children =
            android.provider.DocumentsContract.buildChildDocumentsUriUsingTree(uri, id);
        JSONArray entries = new JSONArray();
        try (var cursor =
            resolver.query(
                children,
                new String[] {
                  android.provider.DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                  android.provider.DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                  android.provider.DocumentsContract.Document.COLUMN_MIME_TYPE
                },
                null,
                null,
                null)) {
          while (cursor != null && cursor.moveToNext()) {
            if (entries.length() >= 4096)
              throw new IllegalArgumentException("LimitExceeded: Directory entries");
            android.net.Uri child =
                android.provider.DocumentsContract.buildDocumentUriUsingTree(
                    uri, cursor.getString(0));
            entries.put(
                object(
                    "uri",
                    child.toString(),
                    "name",
                    cursor.getString(1),
                    "kind",
                    cursor
                            .getString(2)
                            .equals(android.provider.DocumentsContract.Document.MIME_TYPE_DIR)
                        ? "directory"
                        : "file"));
          }
        }
        return object("entries", entries).toString();
      }
      throw new IllegalArgumentException("Unknown document operation");
    } catch (Exception e) {
      throw new IllegalStateException(e);
    }
  }

  @V8Function
  public double measureText(String text, String font, double size) {
    Paint paint = paint(font, size);
    return paint.measureText(text);
  }

  @V8Function
  public String fonts() {
    return fonts.enumerate().toString();
  }

  @V8Function
  public boolean fontAvailable(String family) {
    return fonts.available(family);
  }

  private Paint paint(String font, double size) {
    if (!Double.isFinite(size) || size <= 0 || size > 8192)
      throw new IllegalArgumentException("Invalid text size");
    Paint p = new Paint(Paint.ANTI_ALIAS_FLAG);
    p.setTypeface(fonts.resolve(font));
    p.setTextSize((float) size);
    return p;
  }

  @V8Function
  public String raster(String args) {
    JSONObject a = json(args);
    try {
      String text = a.getString("text");
      float scale = (float) a.getDouble("density"),
          size = (float) a.getDouble("size"),
          leading = (float) a.getDouble("leading");
      Paint p = paint(a.getString("font"), size);
      String[] lines = text.split("\n", -1);
      float w = 1;
      for (String line : lines) w = Math.max(w, p.measureText(line));
      int logicalWidth = (int) Math.ceil(w) + 4,
          logicalHeight = (int) Math.ceil(lines.length * leading) + 4;
      int pw = (int) Math.ceil(logicalWidth * scale), ph = (int) Math.ceil(logicalHeight * scale);
      if (pw <= 0 || ph <= 0 || (long) pw * ph > 16 * 1024 * 1024)
        throw new IllegalArgumentException("Text image too large");
      Bitmap bitmap = Bitmap.createBitmap(pw, ph, Bitmap.Config.ARGB_8888);
      try {
        Canvas canvas = new Canvas(bitmap);
        canvas.scale(scale, scale);
        JSONArray color = a.getJSONArray("color");
        p.setARGB(
            (int) Math.round(color.getDouble(3) * 255),
            (int) Math.round(color.getDouble(0) * 255),
            (int) Math.round(color.getDouble(1) * 255),
            (int) Math.round(color.getDouble(2) * 255));
        for (int i = 0; i < lines.length; i++)
          canvas.drawText(lines[i], 2, i * leading + 2 - p.ascent(), p);
        return object(
                "width",
                logicalWidth,
                "height",
                logicalHeight,
                "pixelWidth",
                pw,
                "pixelHeight",
                ph,
                "bytes",
                base64(pixels(bitmap)))
            .toString();
      } finally {
        bitmap.recycle();
      }
    } catch (Exception e) {
      throw new IllegalArgumentException(e);
    }
  }

  static byte[] pixels(Bitmap bitmap) {
    int[] argb = new int[bitmap.getWidth() * bitmap.getHeight()];
    bitmap.getPixels(argb, 0, bitmap.getWidth(), 0, 0, bitmap.getWidth(), bitmap.getHeight());
    byte[] out = new byte[argb.length * 4];
    for (int i = 0; i < argb.length; i++) {
      out[i * 4] = (byte) (argb[i] >>> 16);
      out[i * 4 + 1] = (byte) (argb[i] >>> 8);
      out[i * 4 + 2] = (byte) argb[i];
      out[i * 4 + 3] = (byte) (argb[i] >>> 24);
    }
    return out;
  }

  @V8Function
  public String image(String operation, String args) {
    JSONObject a = json(args);
    try {
      if (operation.equals("decode") || operation.equals("inspect")) {
        byte[] encoded = unbase64(a.getString("bytes"));
        BitmapFactory.Options bounds = new BitmapFactory.Options();
        bounds.inJustDecodeBounds = true;
        BitmapFactory.decodeByteArray(encoded, 0, encoded.length, bounds);
        if (bounds.outWidth < 1
            || bounds.outHeight < 1
            || (long) bounds.outWidth * bounds.outHeight > 16 * 1024 * 1024)
          throw new IllegalArgumentException("Image dimensions invalid");
        if (operation.equals("inspect"))
          return object(
                  "width",
                  bounds.outWidth,
                  "height",
                  bounds.outHeight,
                  "frameCount",
                  1,
                  "format",
                  bounds.outMimeType)
              .toString();
        Bitmap bitmap = BitmapFactory.decodeByteArray(encoded, 0, encoded.length);
        try {
          return object(
                  "width",
                  bitmap.getWidth(),
                  "height",
                  bitmap.getHeight(),
                  "pixels",
                  base64(pixels(bitmap)),
                  "alpha",
                  "Straight")
              .toString();
        } finally {
          bitmap.recycle();
        }
      }
      int w = a.getInt("width"), h = a.getInt("height");
      byte[] bytes = unbase64(a.getString("pixels"));
      if (w < 1 || h < 1 || (long) w * h > 16 * 1024 * 1024 || bytes.length != (long) w * h * 4)
        throw new IllegalArgumentException("Invalid image pixels");
      int[] colors = new int[w * h];
      for (int i = 0; i < colors.length; i++)
        colors[i] =
            ((bytes[i * 4 + 3] & 255) << 24)
                | ((bytes[i * 4] & 255) << 16)
                | ((bytes[i * 4 + 1] & 255) << 8)
                | (bytes[i * 4 + 2] & 255);
      Bitmap bitmap = Bitmap.createBitmap(colors, w, h, Bitmap.Config.ARGB_8888);
      try {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        String format = a.getString("format");
        if (!format.equalsIgnoreCase("Png") && !format.equalsIgnoreCase("Jpeg"))
          throw new IllegalArgumentException("Unsupported: Android image encoding format");
        bitmap.compress(
            format.equalsIgnoreCase("Jpeg")
                ? Bitmap.CompressFormat.JPEG
                : Bitmap.CompressFormat.PNG,
            a.optInt("quality", 90),
            output);
        return object("bytes", base64(output.toByteArray())).toString();
      } finally {
        bitmap.recycle();
      }
    } catch (Exception e) {
      throw new IllegalArgumentException(e);
    }
  }

  @V8Function
  public String storage(String operation, String key, String value) {
    try {
      File folder = new File(activity.getFilesDir(), "aquarius-store");
      if (!folder.isDirectory() && !folder.mkdirs())
        throw new java.io.IOException("Storage directory unavailable");
      if (key.length() > 4096) throw new IllegalArgumentException("Storage key too long");
      byte[] digest =
          java.security.MessageDigest.getInstance("SHA-256")
              .digest(key.getBytes(StandardCharsets.UTF_8));
      StringBuilder id = new StringBuilder();
      for (byte b : digest) id.append(String.format(java.util.Locale.ROOT, "%02x", b & 255));
      File file = new File(folder, id + ".json");
      if (operation.equals("get"))
        return file.exists()
            ? new JSONObject(new String(Files.readAllBytes(file.toPath()), StandardCharsets.UTF_8))
                .get("value")
                .toString()
            : "null";
      if (operation.equals("put")) {
        File stage = File.createTempFile("write-", ".tmp", folder);
        try {
          String record =
              object("key", key, "value", new org.json.JSONTokener(value).nextValue()).toString();
          Files.write(stage.toPath(), record.getBytes(StandardCharsets.UTF_8));
          Files.move(
              stage.toPath(),
              file.toPath(),
              java.nio.file.StandardCopyOption.REPLACE_EXISTING,
              java.nio.file.StandardCopyOption.ATOMIC_MOVE);
        } finally {
          stage.delete();
        }
        return "null";
      }
      if (operation.equals("delete")) {
        Files.deleteIfExists(file.toPath());
        return "null";
      }
      if (operation.equals("keys")) {
        JSONArray keys = new JSONArray();
        File[] files = folder.listFiles();
        if (files != null)
          for (File f : files)
            if (f.getName().endsWith(".json"))
              keys.put(
                  new JSONObject(new String(Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8))
                      .getString("key"));
        return keys.toString();
      }
      throw new IllegalArgumentException("Unknown storage operation");
    } catch (Exception e) {
      throw new IllegalStateException(e);
    }
  }

  @V8Function
  public String clipboard(String operation, String value) {
    try {
      CountDownLatch latch = new CountDownLatch(1);
      String[] out = {""};
      activity.runOnUiThread(
          () -> {
            ClipboardManager cb =
                (ClipboardManager) activity.getSystemService(Context.CLIPBOARD_SERVICE);
            if (operation.equals("write"))
              cb.setPrimaryClip(ClipData.newPlainText("Aquarius", value));
            else if (cb.hasPrimaryClip())
              out[0] = cb.getPrimaryClip().getItemAt(0).coerceToText(activity).toString();
            latch.countDown();
          });
      if (!latch.await(10, TimeUnit.SECONDS)) throw new IllegalStateException("Clipboard timeout");
      return out[0];
    } catch (InterruptedException e) {
      throw new IllegalStateException(e);
    }
  }

  void surface(Surface s, int w, int h) {
    width = w;
    height = h;
    NativeGpu.surface(s);
  }

  void text(String text) {
    events.add(object("type", "textInput", "text", text, "timestamp", now() / 1000));
  }

  void composition(String type, String text) {
    events.add(object("type", type, "text", text, "timestamp", now() / 1000));
  }

  void touch(MotionEvent event) {
    float density = activity.getResources().getDisplayMetrics().density;
    int action = event.getActionMasked(), index = event.getActionIndex();
    for (int i = 0; i < event.getPointerCount(); i++) {
      if (action != MotionEvent.ACTION_MOVE && action != MotionEvent.ACTION_CANCEL && i != index)
        continue;
      String phase =
          action == MotionEvent.ACTION_DOWN || action == MotionEvent.ACTION_POINTER_DOWN
              ? "down"
              : action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_POINTER_UP
                  ? "up"
                  : action == MotionEvent.ACTION_CANCEL ? "cancel" : "move";
      JSONArray history = new JSONArray();
      for (int j = 0; j < event.getHistorySize(); j++)
        history.put(
            object(
                "x",
                event.getHistoricalX(i, j) / density,
                "y",
                event.getHistoricalY(i, j) / density,
                "pressure",
                event.getHistoricalPressure(i, j),
                "timestamp",
                event.getHistoricalEventTime(j) / 1000.0));
      events.add(
          object(
              "type",
              "touch",
              "phase",
              phase,
              "id",
              event.getPointerId(i),
              "x",
              event.getX(i) / density,
              "y",
              event.getY(i) / density,
              "pressure",
              event.getPressure(i),
              "tool",
              event.getToolType(i) == MotionEvent.TOOL_TYPE_STYLUS
                      || event.getToolType(i) == MotionEvent.TOOL_TYPE_ERASER
                  ? "pen"
                  : "touch",
              "eraser",
              event.getToolType(i) == MotionEvent.TOOL_TYPE_ERASER,
              "timestamp",
              event.getEventTime() / 1000.0,
              "history",
              history));
    }
  }

  boolean key(int code, KeyEvent event, boolean pressed) {
    if (code == KeyEvent.KEYCODE_BACK) return false;
    events.add(
        object(
            "type",
            pressed ? "keyPressed" : "keyReleased",
            "key",
            event.getUnicodeChar() == 0
                ? ""
                : new String(Character.toChars(event.getUnicodeChar())),
            "code",
            code,
            "physicalKey",
            KeyEvent.keyCodeToString(code),
            "repeat",
            pressed && event.getRepeatCount() > 0,
            "modifiers",
            (event.isShiftPressed() ? 1 : 0)
                | (event.isCtrlPressed() ? 2 : 0)
                | (event.isAltPressed() ? 4 : 0)
                | (event.isMetaPressed() ? 8 : 0)
                | (event.isCapsLockOn() ? 16 : 0)
                | (event.isNumLockOn() ? 32 : 0),
            "timestamp",
            event.getEventTime() / 1000.0));
    if (pressed
        && editor.hasFocus()
        && !event.isCtrlPressed()
        && !event.isAltPressed()
        && event.getUnicodeChar() >= 32)
      text(new String(Character.toChars(event.getUnicodeChar())));
    return true;
  }

  void back() {
    if (runtimeFinished) activity.finish();
    else events.add(object("type", "back", "timestamp", now() / 1000));
  }

  void insets(WindowInsets insets) {
    float density = activity.getResources().getDisplayMetrics().density;
    if (android.os.Build.VERSION.SDK_INT >= 30) {
      var safe =
          insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
      safeInsets =
          object(
              "left",
              safe.left / density,
              "top",
              safe.top / density,
              "right",
              safe.right / density,
              "bottom",
              safe.bottom / density);
      keyboardVisible = insets.isVisible(WindowInsets.Type.ime());
    } else {
      safeInsets =
          object(
              "left",
              insets.getSystemWindowInsetLeft() / density,
              "top",
              insets.getSystemWindowInsetTop() / density,
              "right",
              insets.getSystemWindowInsetRight() / density,
              "bottom",
              insets.getSystemWindowInsetBottom() / density);
      keyboardVisible = insets.getSystemWindowInsetBottom() / density > 100;
    }
  }

  void requestCameraPermission() {
    if (activity.checkSelfPermission(Manifest.permission.CAMERA)
        == PackageManager.PERMISSION_GRANTED) return;
    permission = new CountDownLatch(1);
    activity.runOnUiThread(
        () -> activity.requestPermissions(new String[] {Manifest.permission.CAMERA}, 71));
    try {
      if (!permission.await(30, TimeUnit.SECONDS)
          || activity.checkSelfPermission(Manifest.permission.CAMERA)
              != PackageManager.PERMISSION_GRANTED)
        throw new IllegalStateException("PermissionDenied: Camera permission denied");
    } catch (InterruptedException e) {
      throw new IllegalStateException("Cancelled", e);
    }
  }

  void permissionResult(int request, int[] grants) {
    if (request == 71 && permission != null) permission.countDown();
  }

  void pause() {
    paused = true;
    events.add(object("type", "inputCancelled", "timestamp", now() / 1000));
    events.add(object("type", "visibility", "state", false, "timestamp", now() / 1000));
    events.add(object("type", "activation", "state", false, "timestamp", now() / 1000));
    cameras.pause();
    audio.pause();
  }

  void resume() {
    events.add(object("type", "visibility", "state", true, "timestamp", now() / 1000));
    events.add(object("type", "activation", "state", true, "timestamp", now() / 1000));
    cameras.resume();
    audio.resume();
    synchronized (lifecycle) {
      paused = false;
      lifecycle.notifyAll();
    }
  }

  void releaseRuntime() {
    runtimeFinished = true;
    cameras.close();
    for (long device : gpuDevices)
      try {
        NativeGpu.command(
            "disposeDevice",
            object("device", device).toString(),
            java.nio.ByteBuffer.allocateDirect(0),
            0,
            0);
      } catch (IllegalStateException ignored) {
      }
    gpuDevices.clear();
    onnx.close();
    audio.close();
  }

  public void close() {
    frameClock.close();
    closed = true;
    if (permission != null) permission.countDown();
    if (pickerReady != null) pickerReady.countDown();
    synchronized (lifecycle) {
      lifecycle.notifyAll();
    }
    cameras.close();
  }
}
