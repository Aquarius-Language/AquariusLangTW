package org.aquariuslang.android;

import android.util.Base64;
import java.util.LinkedHashMap;
import org.json.JSONObject;

/** Per-runtime ownership. Only packaged JNI libraries, never writable downloaded code. */
final class DynamicLibraries implements AutoCloseable {
  static { System.loadLibrary("aquarius_android"); }
  private static native long open(String name);
  private static native boolean has(long handle, String symbol);
  private static native byte[] invoke(long handle, String symbol, byte[] input);
  private static native void release(long handle);
  private final LinkedHashMap<Long, Long> handles = new LinkedHashMap<>();
  private long next;
  private boolean closed;
  synchronized String command(String operation, String request) throws Exception {
    if (closed) throw new IllegalStateException("Library runtime is closed / 函式庫執行環境已關閉");
    JSONObject args = new JSONObject(request), result = new JSONObject();
    if (operation.equals("open")) {
      String name = args.getString("path");
      if (!name.matches("lib[A-Za-z0-9_][A-Za-z0-9_.-]*\\.so") || handles.size() >= 32)
        throw new IllegalArgumentException("Expected packaged lib*.so basename, at most 32 libraries / 預期套件內的函式庫名稱");
      long pointer = open(name), id = ++next; handles.put(id, pointer); return result.put("id", id).toString();
    }
    long id = args.getLong("id");
    if (operation.equals("dispose")) {
      Long pointer = handles.remove(id); if (pointer != null) release(pointer); return "{}";
    }
    Long pointer = handles.get(id);
    if (pointer == null) throw new IllegalStateException("Library is closed / 函式庫已關閉");
    String symbol = args.getString("symbol");
    if (!symbol.matches("[A-Za-z_][A-Za-z0-9_]{0,127}") || symbol.startsWith("aqua_")) throw new IllegalArgumentException("Invalid symbol / 無效的符號名稱");
    if (operation.equals("has")) return result.put("found", has(pointer, symbol)).toString();
    if (!operation.equals("invoke")) throw new IllegalArgumentException("Unknown library operation");
    String encoded = args.getString("input");
    if (encoded.length() > 1398104) throw new IllegalArgumentException("Input exceeds 1 MiB");
    return result.put("output", Base64.encodeToString(invoke(pointer, symbol, Base64.decode(encoded, Base64.NO_WRAP)), Base64.NO_WRAP)).toString();
  }
  public synchronized void close() {
    if (closed) return; closed = true;
    Long[] pointers = handles.values().toArray(new Long[0]); handles.clear();
    for (int i = pointers.length - 1; i >= 0; i--) release(pointers[i]);
  }
}
