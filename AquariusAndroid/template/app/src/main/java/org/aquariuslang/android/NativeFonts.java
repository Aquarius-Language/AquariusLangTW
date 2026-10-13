package org.aquariuslang.android;

import android.graphics.Typeface;
import android.graphics.fonts.SystemFonts;
import java.util.LinkedHashMap;
import java.util.Map;
import org.json.JSONArray;

/** Public Android font inventory. Enumerated names resolve to the same face when measured. */
final class NativeFonts {
  private final Map<String, Typeface> faces = new LinkedHashMap<>();
  private boolean initialized;

  private synchronized void initialize() {
    if (initialized) return;
    for (String family : new String[] {"sans-serif", "serif", "monospace"}) {
      faces.put(family, Typeface.create(family, Typeface.NORMAL));
    }
    for (var font : SystemFonts.getAvailableFonts()) {
      if (font.getFile() == null || faces.size() >= 512) continue;
      String name = font.getFile().getName() + ":" + font.getTtcIndex();
      try {
        faces.putIfAbsent(
            name,
            new Typeface.Builder(font.getFile())
                .setTtcIndex(font.getTtcIndex())
                .setWeight(font.getStyle().getWeight())
                .setItalic(font.getStyle().getSlant() != 0)
                .build());
      } catch (IllegalArgumentException ignored) {
      }
    }
    initialized = true;
  }

  JSONArray enumerate() {
    initialize();
    var result = new JSONArray();
    for (var entry : faces.entrySet())
      result.put(
          NativeBridge.object(
              "family",
              entry.getKey(),
              "style",
              entry.getValue().isItalic()
                  ? "Italic"
                  : entry.getValue().isBold() ? "Bold" : "Regular"));
    return result;
  }

  boolean available(String family) {
    initialize();
    return faces.containsKey(family);
  }

  Typeface resolve(String family) {
    // Standard Processing family names can use Android fallback without scanning the inventory.
    if (family.equals("sans-serif") || family.equals("serif") || family.equals("monospace"))
      return Typeface.create(family, Typeface.NORMAL);
    initialize();
    return faces.getOrDefault(family, Typeface.create(family, Typeface.NORMAL));
  }
}
