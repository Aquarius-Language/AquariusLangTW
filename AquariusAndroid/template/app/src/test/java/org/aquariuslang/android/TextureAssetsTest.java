package org.aquariuslang.android;

import static org.junit.Assert.*;

import java.io.*;
import java.util.zip.GZIPOutputStream;
import org.junit.Test;

public class TextureAssetsTest {
  private byte[] gzip(byte[] bytes) throws IOException {
    var output = new ByteArrayOutputStream();
    try (var gzip = new GZIPOutputStream(output)) {
      gzip.write(bytes);
    }
    return output.toByteArray();
  }

  @Test
  public void nativeBufferPreservesRawAndGzipBytes() throws Exception {
    byte[] bytes = {0, 1, 2, 3, 4, 5, 6, 7};
    for (byte[] input : new byte[][] {bytes, gzip(bytes)}) {
      var result = TextureAssets.decode(new ByteArrayInputStream(input), 8, false);
      assertTrue(result.isDirect());
      byte[] actual = new byte[8];
      result.get(actual);
      assertArrayEquals(bytes, actual);
    }
  }

  @Test
  public void rejectsIncorrectSizesChecksumAndHalfFloats() throws Exception {
    for (int size : new int[] {7, 9})
      assertThrows(
          IOException.class,
          () -> TextureAssets.decode(new ByteArrayInputStream(gzip(new byte[8])), size, false));
    byte[] corrupt = gzip(new byte[8]);
    corrupt[corrupt.length - 8] ^= 1;
    assertThrows(
        IOException.class, () -> TextureAssets.decode(new ByteArrayInputStream(corrupt), 8, false));
    assertThrows(
        IOException.class,
        () ->
            TextureAssets.decode(
                new ByteArrayInputStream(gzip(new byte[] {0, 124, 0, 0, 0, 0, 0, 0})), 8, true));
    assertThrows(
        IllegalArgumentException.class,
        () ->
            TextureAssets.decode(new ByteArrayInputStream(new byte[0]), 129 * 1024 * 1024, false));
  }

  @Test
  public void validatesHalfFloatsAcrossOddStreamChunkBoundaries() throws Exception {
    var source =
        new ByteArrayInputStream(new byte[] {0, 124, 0, 0}) {
          public synchronized int read(byte[] b, int off, int len) {
            return super.read(b, off, Math.min(len, 1));
          }
        };
    assertThrows(IOException.class, () -> TextureAssets.decode(source, 4, true));
  }
}
