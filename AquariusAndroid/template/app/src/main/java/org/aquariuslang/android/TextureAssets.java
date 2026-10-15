package org.aquariuslang.android;

import java.io.BufferedInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.util.zip.GZIPInputStream;

/** Bounded native texture decode; the direct buffer is used only during JNI upload. */
final class TextureAssets {
  static ByteBuffer decode(InputStream source, int expected, boolean halfFloat) throws IOException {
    try (var buffered = new BufferedInputStream(source, 65536)) {
      if (expected < 1 || expected > 128 * 1024 * 1024 || halfFloat && expected % 2 != 0)
        throw new IllegalArgumentException("Invalid texture size");
      buffered.mark(2);
      int a = buffered.read(), b = buffered.read();
      buffered.reset();
      try (InputStream input =
          a == 31 && b == 139 ? new GZIPInputStream(buffered, 65536) : buffered) {
        ByteBuffer output = ByteBuffer.allocateDirect(expected);
        byte[] chunk = new byte[65536];
        int offset = 0, count;
        while ((count = input.read(chunk)) != -1) {
          if (count > expected - offset) throw new IOException("Invalid texture data length");
          if (halfFloat)
            for (int i = 0; i < count; i++)
              if ((offset + i) % 2 == 1 && (chunk[i] & 0x7c) == 0x7c)
                throw new IOException("Texture components must be finite half floats");
          output.put(chunk, 0, count);
          offset += count;
        }
        if (offset != expected) throw new IOException("Invalid texture data length");
        output.flip();
        return output;
      }
    }
  }
}
