package org.aquariuslang.android;

import android.media.*;
import java.nio.*;
import java.util.HashMap;

/** PCM output only. Decoding, mixing and sound state stay in the shared miniaudio Wasm library. */
final class AudioBackend implements AutoCloseable {
  private final HashMap<Integer, AudioTrack> outputs = new HashMap<>();
  private int next;
  private boolean paused;
  private final java.util.Set<Integer> running = new java.util.HashSet<>();

  synchronized int call(String operation, int id, String pcm) {
    if (operation.equals("create")) {
      int bytes =
          Math.max(
              32768,
              AudioTrack.getMinBufferSize(
                  48000, AudioFormat.CHANNEL_OUT_STEREO, AudioFormat.ENCODING_PCM_FLOAT));
      AudioTrack track =
          new AudioTrack.Builder()
              .setAudioAttributes(
                  new AudioAttributes.Builder()
                      .setUsage(AudioAttributes.USAGE_GAME)
                      .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                      .build())
              .setAudioFormat(
                  new AudioFormat.Builder()
                      .setSampleRate(48000)
                      .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                      .setEncoding(AudioFormat.ENCODING_PCM_FLOAT)
                      .build())
              .setBufferSizeInBytes(bytes)
              .setTransferMode(AudioTrack.MODE_STREAM)
              .build();
      if (track.getState() != AudioTrack.STATE_INITIALIZED) {
        track.release();
        throw new IllegalStateException("Audio output unavailable");
      }
      int handle = ++next;
      outputs.put(handle, track);
      return handle;
    }
    AudioTrack track = outputs.get(id);
    if (track == null) throw new IllegalStateException("Audio output disposed");
    switch (operation) {
      case "start":
        running.add(id);
        if (!paused) track.play();
        break;
      case "suspend":
        running.remove(id);
        track.pause();
        track.flush();
        break;
      case "write":
        {
          if (paused) break;
          FloatBuffer data =
              ByteBuffer.wrap(NativeBridge.unbase64(pcm))
                  .order(ByteOrder.LITTLE_ENDIAN)
                  .asFloatBuffer();
          float[] samples = new float[data.remaining()];
          data.get(samples);
          int offset = 0;
          while (offset < samples.length) {
            int count =
                track.write(samples, offset, samples.length - offset, AudioTrack.WRITE_BLOCKING);
            if (count < 1) throw new IllegalStateException("Audio output write failed");
            offset += count;
          }
          break;
        }
      case "dispose":
        running.remove(id);
        outputs.remove(id);
        track.release();
        break;
      default:
        throw new IllegalArgumentException("Unknown audio output operation");
    }
    return 0;
  }

  synchronized void pause() {
    paused = true;
    for (int id : running) {
      outputs.get(id).pause();
      outputs.get(id).flush();
    }
  }

  synchronized void resume() {
    paused = false;
    for (int id : running) outputs.get(id).play();
  }

  public synchronized void close() {
    for (AudioTrack t : outputs.values()) t.release();
    outputs.clear();
    running.clear();
  }
}
