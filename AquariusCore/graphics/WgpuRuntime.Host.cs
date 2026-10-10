using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.Graphics;
public sealed partial class WgpuRuntime {
    private readonly IWgpuBackend backend;
    private readonly Action<string, int, int, byte[]> saveImage;
    private readonly Dictionary<string, ModuleObj> modules = new();
    private bool disposed;
    public WgpuRuntime(IWgpuBackend backend, Action<string, int, int, byte[]> saveImage) {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.saveImage = saveImage ?? throw new ArgumentNullException(nameof(saveImage));
        RegisterWgpu();
    }
    public bool TryImport(string name, out ModuleObj module) => modules.TryGetValue(name, out module!);
    private AquaEnvironment Module(string name) { var env = AquaEnvironment.NewEnvironment(); modules[name] = new(env); return env; }
    private void Bind(AquaEnvironment env, string name, int count, Func<IObject[], IObject> action) {
        FunctionRegistration.Define(env, new BuiltinObj(args => {
            try {
                if (disposed) throw new InvalidOperationException("Graphics runtime has been disposed.");
                if (args.Length != count) throw new ArgumentException($"Expected {count} arguments, got {args.Length}.");
                return action(args);
            } catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or OverflowException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return new ErrorObj($"{name}: {e.Message}"); }
        }), LibraryCatalog.TraditionalChinese(name), name);
    }
    private static ArrayObj Array(IObject o) => o as ArrayObj ?? throw new ArgumentException("Expected an array.");
    private static string Text(IObject o) => o is StringObj s ? s.Value : throw new ArgumentException("Expected a string.");
    private static double Number(IObject o) => o is INumberObj n && double.IsFinite(n.GetNumValue()) ? n.GetNumValue() : throw new ArgumentException("Expected a finite number.");
    private static float F(IObject o) { float f = (float)Number(o); return float.IsFinite(f) ? f : throw new ArgumentException("Number exceeds float range."); }
    private static int Int(IObject o) { double n = Number(o); if (n != Math.Truncate(n)) throw new ArgumentException("Expected an integer."); return checked((int)n); }
    private static int Dimension(IObject o) { int d = Int(o); return d is > 0 and <= 8192 ? d : throw new ArgumentException("Dimension must be 1..8192."); }
    private static IObject N(int n) => new IntegerObj(n);
    private static ArrayObj Numbers(double[] n) => new(n.Select(v => (IObject)new DoubleObj(v)).ToArray());
    private static ArrayObj PixelArray(byte[] bytes) => new(Enumerable.Range(0, bytes.Length / 4).Select(i => (IObject)new DoubleObj((uint)(bytes[i*4+3]<<24 | bytes[i*4]<<16 | bytes[i*4+1]<<8 | bytes[i*4+2]))).ToArray());
    private static IObject Null() => RepeatedPrimitives.NULL;
    public void Dispose() { if (disposed) return; foreach (var device in wgpuDevices.Values) device.Dispose(); wgpuDevices.Clear(); disposed = true; }
}
