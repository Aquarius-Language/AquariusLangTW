using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AquariusLang.External;
using AquariusLang.Wasm;

namespace AquariusLang.Audio;

/// <summary>Typed library ABI adapter. No DSP is reimplemented in managed code.</summary>
public sealed class MiniaudioEngine : IDisposable {
    private readonly IWasmInstance wasm;
    private readonly object gate = new();
    private bool disposed;
    public MiniaudioEngine(IWasmEngine engine) {
        wasm = engine.Instantiate(ExternalLibraries.ReadAsset(ExternalLibraries.Audio, "audio.wasm"), new Dictionary<string, Delegate>());
        try {
            wasm.Invoke("_initialize");
            if (wasm.Invoke("audio_abi") != 1) throw new NotSupportedException("Unsupported audio library ABI.");
            Check(wasm.Invoke("audio_init"));
        } catch { wasm.Dispose(); throw; }
    }
    private static int Check(int result) => result < 0 ? throw new InvalidOperationException($"miniaudio operation failed ({result}).") : result;
    private T Locked<T>(Func<T> action) { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); return action(); } }
    private int Upload(ReadOnlySpan<byte> bytes, string operation, int count) {
        int pointer = wasm.Invoke("audio_alloc", bytes.Length);
        if (pointer == 0) throw new InvalidOperationException("Audio memory limit exceeded.");
        try { bytes.CopyTo(wasm.Memory(pointer, bytes.Length)); return Check(wasm.Invoke(operation, pointer, count)); }
        finally { wasm.Invoke("audio_free", pointer); }
    }
    public int Tone(float frequency, float seconds) => Locked(() => {
        if (!float.IsFinite(frequency) || frequency < 20 || frequency > 20000 || !float.IsFinite(seconds) || seconds < .001f || seconds > 60)
            throw new ArgumentException("Tone expects 20..20000 Hz and 0.001..60 seconds.");
        return Check(wasm.Invoke("audio_tone", AudioFormat.FloatBits(frequency), (int)Math.Round((double)seconds * AudioFormat.SampleRate, MidpointRounding.AwayFromZero)));
    });
    public int Buffer(float[] samples) => Locked(() => {
        if (samples.Length < 2 || samples.Length % 2 != 0 || samples.Length > AudioFormat.MaxFrames * 2)
            throw new ArgumentException("Expected interleaved stereo samples.");
        return Upload(MemoryMarshal.AsBytes(samples.AsSpan()), "audio_buffer", samples.Length / 2);
    });
    public int Decode(byte[] encoded) => Locked(() => {
        if (encoded.Length == 0 || encoded.Length > AudioFormat.MaxEncodedBytes) throw new ArgumentException("Encoded audio must be 1..32 MiB.");
        return Upload(encoded, "audio_decode", encoded.Length);
    });
    public void Command(int id, int command, int value) => Locked(() => Check(wasm.Invoke("audio_command", id, command, value)));
    public int Query(int id, int query) => Locked(() => Check(wasm.Invoke("audio_query", id, query)));
    public void Release(int id) => Locked(() => Check(wasm.Invoke("audio_release", id)));
    public float[] Render(int frames) {
        if (frames < 1 || frames > 4096) throw new ArgumentException("Render expects 1..4096 stereo frames.");
        var output = new float[frames * 2]; Render(output); return output;
    }
    public void Render(float[] output) => Locked(() => {
        if (output.Length < 2 || output.Length % 2 != 0 || output.Length > 8192) throw new ArgumentException("Render expects 1..4096 stereo frames.");
        int pointer = wasm.Invoke("audio_render", output.Length / 2);
        if (pointer == 0) throw new InvalidOperationException("Audio render failed.");
        wasm.Memory(pointer, output.Length * 4).CopyTo(MemoryMarshal.AsBytes(output.AsSpan())); return true;
    });
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; try { wasm.Invoke("audio_dispose"); } finally { wasm.Dispose(); } } }
}
