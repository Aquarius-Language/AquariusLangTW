package org.aquariuslang.android;

import java.nio.ByteBuffer;

/** Camera plane conversion independent of Camera2 handles and UI lifetimes. */
final class CameraPixels {
  static int rotation(int sensor, int display, boolean front) {
    return (sensor + (front ? display : -display) + 360) % 360;
  }

  static int[] rgba(int width, int height, ByteBuffer[] planes, int[] rows, int[] strides) {
    if (width < 1
        || height < 1
        || (long) width * height > 1920 * 1080
        || planes.length != 3
        || rows.length != 3
        || strides.length != 3) {
      throw new IllegalArgumentException("Invalid camera planes");
    }
    for (int i = 0; i < 3; i++) {
      if (rows[i] < 1 || strides[i] < 1)
        throw new IllegalArgumentException("Invalid camera stride");
      int w = i == 0 ? width : (width + 1) / 2, h = i == 0 ? height : (height + 1) / 2;
      if ((long) (h - 1) * rows[i] + (long) (w - 1) * strides[i] >= planes[i].remaining()) {
        throw new IllegalArgumentException("Truncated camera plane");
      }
    }
    int[] pixels = new int[width * height];
    for (int y = 0; y < height; y++)
      for (int x = 0; x < width; x++) {
        int yy = sample(planes[0], rows[0], strides[0], x, y) - 16;
        int u = sample(planes[1], rows[1], strides[1], x / 2, y / 2) - 128;
        int v = sample(planes[2], rows[2], strides[2], x / 2, y / 2) - 128;
        int r = clamp((298 * yy + 409 * v + 128) >> 8);
        int g = clamp((298 * yy - 100 * u - 208 * v + 128) >> 8);
        int b = clamp((298 * yy + 516 * u + 128) >> 8);
        pixels[y * width + x] = 0xff000000 | (r << 16) | (g << 8) | b;
      }
    return pixels;
  }

  private static int sample(ByteBuffer plane, int row, int stride, int x, int y) {
    return plane.get(plane.position() + y * row + x * stride) & 255;
  }

  private static int clamp(int value) {
    return Math.max(0, Math.min(255, value));
  }
}
