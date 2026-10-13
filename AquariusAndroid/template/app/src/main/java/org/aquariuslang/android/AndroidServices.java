package org.aquariuslang.android;

import android.content.*;
import android.graphics.*;
import android.graphics.pdf.PdfDocument;
import android.os.*;
import android.print.*;
import java.io.*;
import java.util.concurrent.*;
import org.json.*;

/** Android document output and clipboard services, separate from the Wasm engine. */
final class AndroidServices {
  private final NativeBridge bridge;

  AndroidServices(NativeBridge bridge) {
    this.bridge = bridge;
  }

  private Bitmap bitmap(JSONObject image) throws JSONException {
    int width = image.getInt("width"), height = image.getInt("height");
    byte[] pixels = NativeBridge.unbase64(image.getString("pixels"));
    if (width < 1
        || height < 1
        || (long) width * height > 16 * 1024 * 1024
        || pixels.length != (long) width * height * 4)
      throw new IllegalArgumentException("Invalid clipboard/print image");
    int[] argb = new int[width * height];
    for (int i = 0; i < argb.length; i++)
      argb[i] =
          ((pixels[i * 4 + 3] & 255) << 24)
              | ((pixels[i * 4] & 255) << 16)
              | ((pixels[i * 4 + 1] & 255) << 8)
              | (pixels[i * 4 + 2] & 255);
    return Bitmap.createBitmap(argb, width, height, Bitmap.Config.ARGB_8888);
  }

  JSONObject call(String operation, JSONObject args) {
    CountDownLatch done = new CountDownLatch(1);
    JSONObject[] result = {new JSONObject()};
    Throwable[] error = {null};
    bridge.activity.runOnUiThread(
        () -> {
          try {
            ClipboardManager clipboard =
                (ClipboardManager) bridge.activity.getSystemService(Context.CLIPBOARD_SERVICE);
            if (operation.equals("clipboardRead")) {
              String text = "";
              String image = "";
              ClipData clip = clipboard.getPrimaryClip();
              if (clip != null && clip.getItemCount() > 0) {
                ClipData.Item item = clip.getItemAt(0);
                if (item.getText() != null) text = item.getText().toString();
                if (item.getUri() != null) {
                  String type = bridge.activity.getContentResolver().getType(item.getUri());
                  if (type != null && type.startsWith("image/"))
                    image =
                        NativeBridge.base64(
                            NativeBridge.readBounded(
                                bridge.activity.getContentResolver().openInputStream(item.getUri()),
                                64 * 1024 * 1024));
                }
              }
              result[0] = NativeBridge.object("text", text, "image", image);
            } else if (operation.equals("clipboardWrite")) {
              String text = args.optString("text", "");
              if (args.has("image")) {
                Bitmap bitmap = bitmap(args.getJSONObject("image"));
                byte[] encoded;
                try {
                  ByteArrayOutputStream output = new ByteArrayOutputStream();
                  bitmap.compress(Bitmap.CompressFormat.PNG, 100, output);
                  encoded = output.toByteArray();
                } finally {
                  bitmap.recycle();
                }
                var uri = ImageProvider.store(bridge.activity, encoded);
                clipboard.setPrimaryClip(
                    new ClipData(
                        "Aquarius",
                        new String[] {"image/png", "text/plain"},
                        new ClipData.Item(text, null, uri)));
              } else clipboard.setPrimaryClip(ClipData.newPlainText("Aquarius", text));
            } else if (operation.equals("print")) {
              Bitmap image = bitmap(args);
              PrintManager manager =
                  (PrintManager) bridge.activity.getSystemService(Context.PRINT_SERVICE);
              manager.print(
                  "Aquarius image",
                  new PrintDocumentAdapter() {
                    public void onLayout(
                        PrintAttributes oldAttributes,
                        PrintAttributes attributes,
                        CancellationSignal cancellation,
                        LayoutResultCallback callback,
                        Bundle extras) {
                      if (cancellation.isCanceled()) callback.onLayoutCancelled();
                      else
                        callback.onLayoutFinished(
                            new PrintDocumentInfo.Builder("image.pdf")
                                .setContentType(PrintDocumentInfo.CONTENT_TYPE_PHOTO)
                                .setPageCount(1)
                                .build(),
                            true);
                    }

                    public void onWrite(
                        PageRange[] pages,
                        ParcelFileDescriptor destination,
                        CancellationSignal cancellation,
                        WriteResultCallback callback) {
                      if (cancellation.isCanceled()) {
                        callback.onWriteCancelled();
                        return;
                      }
                      PdfDocument document = new PdfDocument();
                      try (FileOutputStream output =
                          new FileOutputStream(destination.getFileDescriptor())) {
                        PdfDocument.Page page =
                            document.startPage(
                                new PdfDocument.PageInfo.Builder(595, 842, 1).create());
                        float scale = Math.min(555f / image.getWidth(), 802f / image.getHeight());
                        float width = image.getWidth() * scale, height = image.getHeight() * scale;
                        page.getCanvas()
                            .drawBitmap(
                                image,
                                null,
                                new RectF(
                                    (595 - width) / 2,
                                    (842 - height) / 2,
                                    (595 + width) / 2,
                                    (842 + height) / 2),
                                new Paint(Paint.FILTER_BITMAP_FLAG));
                        document.finishPage(page);
                        document.writeTo(output);
                        callback.onWriteFinished(new PageRange[] {PageRange.ALL_PAGES});
                      } catch (Exception e) {
                        callback.onWriteFailed(e.getMessage());
                      } finally {
                        document.close();
                      }
                    }

                    public void onFinish() {
                      image.recycle();
                    }
                  },
                  null);
            } else throw new IllegalArgumentException("Unknown Android service");
          } catch (Throwable e) {
            error[0] = e;
          } finally {
            done.countDown();
          }
        });
    try {
      if (!done.await(30, TimeUnit.SECONDS))
        throw new IllegalStateException("Android service timeout");
    } catch (InterruptedException e) {
      Thread.currentThread().interrupt();
      throw new IllegalStateException("Cancelled", e);
    }
    if (error[0] != null) throw new IllegalStateException(error[0].getMessage(), error[0]);
    return result[0];
  }
}
