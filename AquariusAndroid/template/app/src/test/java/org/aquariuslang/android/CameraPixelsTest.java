package org.aquariuslang.android;

import static org.junit.Assert.*;

import java.nio.ByteBuffer;
import org.junit.Test;

public class CameraPixelsTest {
  private int[] convert(byte[] y, byte[] u, byte[] v) {
    return CameraPixels.rgba(
        2,
        2,
        new ByteBuffer[] {ByteBuffer.wrap(y), ByteBuffer.wrap(u), ByteBuffer.wrap(v)},
        new int[] {2, 1, 1},
        new int[] {1, 1, 1});
  }

  @Test
  public void blackAndWhiteAreOpaque() {
    assertArrayEquals(
        new int[] {0xff000000, 0xffffffff, 0xff000000, 0xffffffff},
        convert(
            new byte[] {16, (byte) 235, 16, (byte) 235},
            new byte[] {(byte) 128},
            new byte[] {(byte) 128}));
  }

  @Test
  public void chromaChannelsAreNotSwapped() {
    int red = convert(new byte[] {81, 81, 81, 81}, new byte[] {90}, new byte[] {(byte) 240})[0];
    assertTrue((red >> 16 & 255) > 240);
    assertTrue((red & 255) < 10);
    assertEquals(255, red >>> 24);
  }

  @Test
  public void paddedInterleavedPlanesAndBufferPositions() {
    ByteBuffer y = ByteBuffer.wrap(new byte[] {99, 16, (byte) 235, 99, 99, 16, (byte) 235});
    y.position(1);
    ByteBuffer u = ByteBuffer.wrap(new byte[] {99, (byte) 128, 99});
    u.position(1);
    ByteBuffer v = ByteBuffer.wrap(new byte[] {99, (byte) 128, 99});
    v.position(1);
    assertArrayEquals(
        new int[] {0xff000000, 0xffffffff, 0xff000000, 0xffffffff},
        CameraPixels.rgba(
            2, 2, new ByteBuffer[] {y, u, v}, new int[] {4, 2, 2}, new int[] {1, 2, 2}));
  }

  @Test
  public void conversionDoesNotConsumeSharedPlaneBuffers() {
    ByteBuffer y = ByteBuffer.wrap(new byte[] {16, 16, 16, 16}),
        u = ByteBuffer.wrap(new byte[] {(byte) 128}),
        v = u.duplicate();
    CameraPixels.rgba(2, 2, new ByteBuffer[] {y, u, v}, new int[] {2, 1, 1}, new int[] {1, 1, 1});
    assertEquals(0, y.position());
    assertEquals(0, u.position());
  }

  @Test
  public void snapshotsDoNotSharePixelArrays() {
    int[] first =
        convert(new byte[] {16, 16, 16, 16}, new byte[] {(byte) 128}, new byte[] {(byte) 128});
    first[0] = 0;
    assertEquals(
        0xff000000,
        convert(new byte[] {16, 16, 16, 16}, new byte[] {(byte) 128}, new byte[] {(byte) 128})[0]);
  }

  @Test
  public void portraitAndLandscapeRotations() {
    assertEquals(90, CameraPixels.rotation(90, 0, false));
    assertEquals(0, CameraPixels.rotation(90, 90, false));
    assertEquals(180, CameraPixels.rotation(90, 270, false));
  }

  @Test
  public void frontSensorRotationKeepsUnmirroredInferencePixels() {
    assertEquals(270, CameraPixels.rotation(270, 0, true));
    assertEquals(0, CameraPixels.rotation(270, 90, true));
    assertEquals(180, CameraPixels.rotation(270, 270, true));
  }

  @Test(expected = IllegalArgumentException.class)
  public void truncatedPlaneIsRejected() {
    convert(new byte[] {16}, new byte[] {0}, new byte[] {0});
  }

  @Test(expected = IllegalArgumentException.class)
  public void excessiveDimensionsAreRejected() {
    CameraPixels.rgba(20000, 20000, new ByteBuffer[3], new int[3], new int[3]);
  }

  @Test(expected = IllegalArgumentException.class)
  public void zeroStrideIsRejected() {
    CameraPixels.rgba(
        1,
        1,
        new ByteBuffer[] {ByteBuffer.allocate(1), ByteBuffer.allocate(1), ByteBuffer.allocate(1)},
        new int[] {1, 1, 1},
        new int[] {0, 1, 1});
  }
}
