package org.aquariuslang.android;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import java.io.File;
import java.io.FileNotFoundException;
import java.nio.file.Files;

/** Read-only clipboard images, shared through explicit temporary URI grants. */
public final class ImageProvider extends ContentProvider {
  static Uri store(Context context, byte[] bytes) throws java.io.IOException {
    File directory = new File(context.getCacheDir(), "clipboard-images");
    if (!directory.isDirectory() && !directory.mkdirs())
      throw new java.io.IOException("Clipboard cache unavailable");
    File image = new File(directory, java.util.UUID.randomUUID() + ".png");
    Files.write(image.toPath(), bytes);
    return new Uri.Builder()
        .scheme("content")
        .authority(context.getPackageName() + ".images")
        .appendPath(image.getName())
        .build();
  }

  private File file(Uri uri) throws FileNotFoundException {
    String name = uri.getLastPathSegment();
    if (name == null || !name.matches("[a-f0-9-]{36}\\.png"))
      throw new FileNotFoundException("Invalid image URI");
    return new File(new File(getContext().getCacheDir(), "clipboard-images"), name);
  }

  public boolean onCreate() {
    return true;
  }

  public String getType(Uri uri) {
    return "image/png";
  }

  public Cursor query(Uri uri, String[] projection, String selection, String[] args, String order) {
    try {
      File image = file(uri);
      String[] columns =
          projection == null
              ? new String[] {OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE}
              : projection;
      MatrixCursor cursor = new MatrixCursor(columns);
      Object[] values = new Object[columns.length];
      for (int i = 0; i < columns.length; i++)
        values[i] =
            columns[i].equals(OpenableColumns.DISPLAY_NAME)
                ? image.getName()
                : columns[i].equals(OpenableColumns.SIZE) ? image.length() : null;
      cursor.addRow(values);
      return cursor;
    } catch (FileNotFoundException e) {
      return null;
    }
  }

  public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
    if (!mode.equals("r")) throw new FileNotFoundException("Read-only clipboard image");
    return ParcelFileDescriptor.open(file(uri), ParcelFileDescriptor.MODE_READ_ONLY);
  }

  public Uri insert(Uri uri, ContentValues values) {
    throw new UnsupportedOperationException();
  }

  public int delete(Uri uri, String selection, String[] args) {
    throw new UnsupportedOperationException();
  }

  public int update(Uri uri, ContentValues values, String selection, String[] args) {
    throw new UnsupportedOperationException();
  }
}
