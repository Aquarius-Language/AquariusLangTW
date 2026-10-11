using System.Runtime.InteropServices;
using AquariusLang.Audio;
using AquariusLang.Desktop.runtime;

namespace AquariusLang.Desktop.Audio;

internal sealed class DesktopAudioBackend : IAudioBackend {
    public IAudioDevice CreateDevice(bool offline) => new DesktopAudioDevice(offline);
}

/// <summary>One Wasm mixer per device. The realtime native callback consumes a bounded SPSC PCM ring;
/// it never calls managed code, takes a managed lock, decodes, or allocates.</summary>
internal sealed class DesktopAudioDevice : IAudioDevice {
    private readonly MiniaudioEngine engine = new(new WasmtimeEngine());
    private readonly HashSet<Sound> sounds = new();
    private IntPtr output;
    private CancellationTokenSource? cancellation;
    private Task? pump;
    private volatile Exception? outputError;
    private bool disposed;
    public bool Offline { get; }
    internal DesktopAudioDevice(bool offline) { Offline = offline; }
    private void Live() {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (outputError != null) throw new InvalidOperationException("Audio output failed.", outputError);
    }
    public void Start() {
        Live(); if (Offline) throw new InvalidOperationException("Offline devices have no output. Use Render.");
        if (pump != null) return;
        if (output == IntPtr.Zero) {
            output = AudioNative.aqua_audio_open();
            if (output == IntPtr.Zero) throw new InvalidOperationException("No audio output device is available.");
        }
        if (AudioNative.aqua_audio_start(output) != 0) throw new InvalidOperationException("Audio output could not start.");
        cancellation = new(); var token = cancellation.Token;
        pump = Task.Factory.StartNew(() => {
            var samples = new float[512];
            try {
                while (!token.IsCancellationRequested) {
                    // Keep at most 1024 frames queued (~21 ms). The ring has headroom for scheduler jitter.
                    if (AudioNative.aqua_audio_available(output) >= 4096 - 1024 + 256) {
                        engine.Render(samples);
                        if (AudioNative.aqua_audio_write(output, samples, 256) != 256) throw new InvalidOperationException("Audio queue write failed.");
                    } else token.WaitHandle.WaitOne(2);
                }
            } catch (Exception e) { outputError = e; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public void Suspend() {
        ObjectDisposedException.ThrowIf(disposed, this);
        StopPump();
        if (output != IntPtr.Zero && AudioNative.aqua_audio_stop(output) != 0) throw new InvalidOperationException("Audio output could not suspend.");
    }
    private void StopPump() {
        cancellation?.Cancel(); pump?.GetAwaiter().GetResult(); pump = null;
        cancellation?.Dispose(); cancellation = null;
    }
    private IAudioSound Own(int id) { var sound = new Sound(this, id); sounds.Add(sound); return sound; }
    public IAudioSound CreateTone(float frequency, float seconds) { Live(); return Own(engine.Tone(frequency, seconds)); }
    public IAudioSound CreateBuffer(float[] samples) { Live(); return Own(engine.Buffer(samples)); }
    public IAudioSound Load(byte[] encoded) { Live(); return Own(engine.Decode(encoded)); }
    public float[] Render(int frames) { Live(); if (!Offline) throw new InvalidOperationException("Render requires an offline device."); return engine.Render(frames); }
    public void Dispose() {
        if (disposed) return; disposed = true;
        try { StopPump(); }
        finally {
            try { if (output != IntPtr.Zero) AudioNative.aqua_audio_close(output); }
            finally { output = IntPtr.Zero; sounds.Clear(); engine.Dispose(); }
        }
    }
    private sealed class Sound(DesktopAudioDevice owner, int id) : IAudioSound {
        private bool disposed;
        private void Live() { owner.Live(); if (disposed || !owner.sounds.Contains(this)) throw new InvalidOperationException("Audio sound has been disposed."); }
        public void Command(AudioSoundCommand command, int value = 0) { Live(); owner.engine.Command(id, (int)command, value); }
        public int Query(AudioSoundQuery query) { Live(); return owner.engine.Query(id, (int)query); }
        public void Dispose() { if (disposed) return; disposed = true; if (owner.sounds.Remove(this) && !owner.disposed) owner.engine.Release(id); }
    }
}

internal static class AudioNative {
    private const string Library = "aquarius_audio";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr aqua_audio_open();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aqua_audio_start(IntPtr output);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aqua_audio_stop(IntPtr output);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aqua_audio_available(IntPtr output);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aqua_audio_write(IntPtr output, float[] samples, int frames);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_audio_close(IntPtr output);
}
