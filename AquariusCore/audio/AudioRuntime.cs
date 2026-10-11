using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.Audio;

/// <summary>Language surface and ownership only. The host supplies output and packaged asset access.</summary>
public sealed class AudioRuntime : IDisposable {
    private readonly IAudioBackend backend;
    private readonly Func<string, byte[]> readAsset;
    private readonly HashSet<IAudioDevice> devices = new();
    private readonly ModuleObj module;
    private bool disposed;
    public AudioRuntime(IAudioBackend backend, Func<string, byte[]> readAsset) {
        this.backend = backend; this.readAsset = readAsset;
        var env = AquaEnvironment.NewEnvironment(); module = new(env);
        env.Create("取樣率", new IntegerObj(AudioFormat.SampleRate)); env.Create("SampleRate", env.Get("取樣率", out _));
        env.Create("聲道數", new IntegerObj(AudioFormat.Channels)); env.Create("Channels", env.Get("聲道數", out _));
        Bind(env, "Audio", "CreateDevice", 0, _ => Device(false));
        Bind(env, "Audio", "CreateOfflineDevice", 0, _ => Device(true));
    }
    public bool TryImport(string name, out ModuleObj result) { result = module; return name is "Audio" or "音訊"; }
    private void Bind(AquaEnvironment env, string library, string name, int count, Func<IObject[], IObject> fn) {
        var function = new BuiltinObj(args => {
            try {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (args.Length != count) throw new ArgumentException($"Expected {count} arguments, got {args.Length}.");
                return fn(args);
            } catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or
                NotSupportedException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or OverflowException) {
                return new ErrorObj($"Audio.{name}: {e.Message}");
            }
        });
        FunctionRegistration.Define(env, function, LibraryCatalog.Find(library, name)?.TraditionalChineseName, name);
    }
    private ModuleObj Device(bool offline) {
        var device = backend.CreateDevice(offline); devices.Add(device);
        var env = AquaEnvironment.NewEnvironment();
        void B(string name, int count, Func<IObject[], IObject> fn) => Bind(env, "Audio.Device", name, count, args => {
            if (!devices.Contains(device)) throw new InvalidOperationException("Audio device has been disposed.");
            return fn(args);
        });
        Bind(env, "Audio.Device", "Dispose", 0, _ => { if (devices.Remove(device)) device.Dispose(); return RepeatedPrimitives.NULL; });
        B("Start", 0, _ => { device.Start(); return RepeatedPrimitives.NULL; });
        B("Suspend", 0, _ => { device.Suspend(); return RepeatedPrimitives.NULL; });
        B("CreateTone", 2, a => Sound(device, device.CreateTone(Number(a[0], 20, 20000), Number(a[1], 0.001, 60))));
        B("CreateBuffer", 1, a => {
            if (a[0] is not ArrayObj array || array.Elements.Length < 2 || array.Elements.Length % 2 != 0 || array.Elements.Length > AudioFormat.MaxFrames * 2)
                throw new ArgumentException("Expected interleaved stereo samples (2..17280000 values).");
            return Sound(device, device.CreateBuffer(array.Elements.Select(v => Number(v, -1, 1)).ToArray()));
        });
        B("Load", 1, a => {
            if (a[0] is not StringObj path) throw new ArgumentException("Expected an audio asset path.");
            var bytes = readAsset(path.Value);
            if (bytes.Length == 0 || bytes.Length > AudioFormat.MaxEncodedBytes) throw new ArgumentException("Encoded audio must be 1..32 MiB.");
            return Sound(device, device.Load(bytes));
        });
        B("Render", 1, a => {
            if (a[0] is not INumberObj number || number.GetNumValue() != Math.Truncate(number.GetNumValue())) throw new ArgumentException("Expected an integer frame count.");
            float frames = Number(a[0], 1, 4096);
            if (frames != MathF.Truncate(frames)) throw new ArgumentException("Expected an integer frame count.");
            return new ArrayObj(device.Render((int)frames).Select(v => (IObject)new DoubleObj(v)).ToArray());
        });
        return new(env);
    }
    private ModuleObj Sound(IAudioDevice device, IAudioSound sound) {
        var env = AquaEnvironment.NewEnvironment(); bool released = false;
        void B(string name, int count, Func<IObject[], IObject> fn) => Bind(env, "Audio.Sound", name, count, args => {
            if (released || !devices.Contains(device)) throw new InvalidOperationException("Audio sound has been disposed.");
            return fn(args);
        });
        Bind(env, "Audio.Sound", "Dispose", 0, _ => { if (!released) { released = true; if (devices.Contains(device)) sound.Dispose(); } return RepeatedPrimitives.NULL; });
        foreach (var (name, command) in new[] { ("Play", AudioSoundCommand.Play), ("Pause", AudioSoundCommand.Pause), ("Stop", AudioSoundCommand.Stop) })
            B(name, 0, _ => { sound.Command(command); return RepeatedPrimitives.NULL; });
        foreach (var (name, command, min, max) in new[] { ("SetVolume", AudioSoundCommand.SetVolume, 0.0, 1.0), ("SetPan", AudioSoundCommand.SetPan, -1.0, 1.0), ("SetPitch", AudioSoundCommand.SetPitch, 0.25, 4.0) })
            B(name, 1, a => { sound.Command(command, AudioFormat.FloatBits(Number(a[0], min, max))); return RepeatedPrimitives.NULL; });
        B("SetLooping", 1, a => { if (a[0] is not BooleanObj b) throw new ArgumentException("Expected a boolean."); sound.Command(AudioSoundCommand.SetLooping, b.Value ? 1 : 0); return RepeatedPrimitives.NULL; });
        B("Seek", 1, a => { float seconds = Number(a[0], 0, sound.Query(AudioSoundQuery.DurationFrames) / (double)AudioFormat.SampleRate); sound.Command(AudioSoundCommand.Seek, (int)Math.Round((double)seconds * AudioFormat.SampleRate, MidpointRounding.AwayFromZero)); return RepeatedPrimitives.NULL; });
        B("IsPlaying", 0, _ => new BooleanObj(sound.Query(AudioSoundQuery.IsPlaying) != 0));
        B("GetDuration", 0, _ => new DoubleObj(sound.Query(AudioSoundQuery.DurationFrames) / (double)AudioFormat.SampleRate));
        B("GetPosition", 0, _ => new DoubleObj(sound.Query(AudioSoundQuery.PositionFrames) / (double)AudioFormat.SampleRate));
        return new(env);
    }
    private static float Number(IObject value, double min, double max) {
        if (value is not INumberObj number || !double.IsFinite(number.GetNumValue()) || number.GetNumValue() < min || number.GetNumValue() > max)
            throw new ArgumentException($"Expected a finite number in {min}..{max}.");
        return (float)number.GetNumValue();
    }
    public void Dispose() { if (disposed) return; disposed = true; try { foreach (var device in devices) device.Dispose(); } finally { devices.Clear(); } }
}
