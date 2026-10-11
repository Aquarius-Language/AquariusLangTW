using System;

namespace AquariusLang.Audio;

/// <summary>Host output boundary. Engines, native handles and browser objects never enter core.</summary>
public interface IAudioBackend {
    IAudioDevice CreateDevice(bool offline);
}
public interface IAudioDevice : IDisposable {
    bool Offline { get; }
    void Start();
    void Suspend();
    IAudioSound CreateTone(float frequency, float seconds);
    IAudioSound CreateBuffer(float[] interleavedStereo);
    IAudioSound Load(byte[] encoded);
    float[] Render(int frames);
}
public interface IAudioSound : IDisposable {
    /// <summary>Scalar controls use FloatBits; Seek uses PCM frames, Loop uses 0/1.</summary>
    void Command(AudioSoundCommand command, int value = 0);
    int Query(AudioSoundQuery query);
}
public enum AudioSoundCommand { Play = 0, Pause = 1, Stop = 2, SetVolume = 3, SetPan = 4, SetPitch = 5, SetLooping = 6, Seek = 7 }
public enum AudioSoundQuery { IsPlaying = 0, DurationFrames = 1, PositionFrames = 2 }

/// <summary>Fixed portable PCM format and bounded resources shared with the library ABI.</summary>
public static class AudioFormat {
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int MaxSounds = 64;
    public const int MaxEncodedBytes = 32 * 1024 * 1024;
    public const int MaxFrames = SampleRate * 180;
    public static int FloatBits(float value) => BitConverter.SingleToInt32Bits(value);
}
