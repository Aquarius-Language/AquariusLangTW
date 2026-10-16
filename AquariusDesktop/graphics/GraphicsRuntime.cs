using System.Reflection;
using System.Runtime.InteropServices;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.Desktop.Graphics;

internal sealed partial class GraphicsRuntime : IDisposable {
    internal Func<string, string> ResourcePath { get; set; } = path => path;
    private static readonly object Gate = new();
    private static GraphicsRuntime? active;
    private readonly Dictionary<string, ModuleObj> modules = new();
    private readonly Dictionary<string, Delegate> functions = new();
    private readonly HashSet<WindowObject> windows = new();
    private readonly HashSet<DataObject> buffers = new();
    private WindowObject? current;
    private bool loaded, disposed;
    private readonly AquariusLang.Network.NetworkRuntime network;
    private readonly AquariusLang.Terminal.TerminalRuntime terminal;
    private readonly AquariusLang.Audio.AudioRuntime audio;
    private readonly AquariusLang.Webcam.WebcamRuntime webcam;
    private readonly AquariusLang.Camera.CameraRuntime camera;
    private readonly AquariusLang.Input.AndroidInputRuntime androidInput = new(new AquariusLang.Input.UnavailableAndroidInputBackend());
    private readonly AquariusLang.Onnx.OnnxRuntime onnx;
    private readonly AquariusLang.Linking.DynamicLibraryRuntime dynamicLibraries;
    private readonly AquariusLang.MediaPipe.PoseRuntime pose;
    internal readonly Application.DesktopApplicationHost ApplicationHost;
    internal readonly AquariusLang.Application.ApplicationRuntime Application;
    internal AquaEnvironment GlEnvironment = AquaEnvironment.NewEnvironment();

    internal GraphicsRuntime(Application.DesktopApplicationHost? applicationHost = null, AquariusLang.Webcam.IWebcamBackend? webcamBackend = null, AquariusLang.Onnx.IOnnxBackend? onnxBackend = null) {
        network = new(new Network.DesktopNetworkBackend()) { Invoke = (fn, args) => InvokeAqua?.Invoke(fn, args) ?? new ErrorObj("Network callbacks unavailable"), ResourcePath = path => ResourcePath(path) };
        audio = new(new Audio.DesktopAudioBackend(), path => File.ReadAllBytes(ResourcePath(path)));
        ApplicationHost = applicationHost ?? new();
        Application = new(ApplicationHost, path => {string resolved=ResourcePath(path);new AquariusLang.Application.ApplicationLimits().Bytes(new FileInfo(resolved).Length);return File.ReadAllBytes(resolved);});
        webcam = new(webcamBackend ?? new Webcam.DesktopWebcamBackend(), pixels => Application.Image(pixels));
        camera = new(new AquariusLang.Camera.UnavailableCameraBackend(), pixels => Application.Image(pixels));
        var inferenceBackend = onnxBackend ?? new Onnx.DesktopOnnxBackend();
        byte[] ReadModel(string path) {
            string resolved = ResourcePath(path);
            if (new FileInfo(resolved).Length > AquariusLang.Onnx.OnnxTensor.MaxModelBytes) throw new AquariusLang.Onnx.OnnxFailure(AquariusLang.Onnx.OnnxFailureKind.InvalidArgument, "模型大小超過上限。");
            return File.ReadAllBytes(resolved);
        }
        onnx = new(inferenceBackend, ReadModel);
        terminal = new(new Terminal.DesktopTerminalBackend(), path => ResourcePath(path));
        dynamicLibraries = new(new Linking.DesktopDynamicLibraryBackend(path => ResourcePath(path)));
        pose = new(new runtime.WasmtimeEngine(), inferenceBackend, ReadModel, Application.GetImage);
        modules["GL"] = new ModuleObj(GlEnvironment);
        modules["Thread"] = modules["執行緒"] = AquariusLang.Threading.ThreadCatalog.CreateModule();
        RegisterGl();
        RegisterGlHelpers();
        RegisterGlfw();
        RegisterTextInput();
        RegisterMath();
        RegisterImages();
        RegisterProcessing();
        modules["繪圖"] = modules["Processing"];
        RegisterWgpu();
        RegisterApplicationGraphics();
    }
    internal bool TryImport(string name, out ModuleObj module) =>
        network.TryImport(name, out module) || dynamicLibraries.TryImport(name, out module) || Application.TryImport(name, out module) || pose.TryImport(name, out module) || onnx.TryImport(name, out module) || camera.TryImport(name, out module) || androidInput.TryImport(name, out module) || webcam.TryImport(name, out module) || audio.TryImport(name, out module) || terminal.TryImport(name, out module) || sharedWgpu.TryImport(name, out module) || modules.TryGetValue(name, out module!);
    private AquaEnvironment Module(string name) {
        var env = AquaEnvironment.NewEnvironment(); modules[name] = new ModuleObj(env); return env;
    }
    private void Bind(AquaEnvironment env, string name, int count, Func<IObject[], IObject> fn) {
        var function = new BuiltinObj(args => {
            try {
                if (disposed) throw new InvalidOperationException("Graphics runtime has been disposed.");
                if (args.Length != count) throw new ArgumentException($"Expected {count} arguments, got {args.Length}.");
                return fn(args);
            } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DllNotFoundException
                or EntryPointNotFoundException or BadImageFormatException or OverflowException or IOException) {
                return new ErrorObj($"{name}: {ex.Message}");
            }
        });
        FunctionRegistration.Define(env, function, LibraryCatalog.TraditionalChinese(name), name);
    }
    internal static double Number(IObject value) {
        if (value is INumberObj number) {
            double n = number.GetNumValue();
            if (double.IsFinite(n)) return n;
        }
        throw new ArgumentException("Expected a finite number.");
    }
    internal static int Int(IObject value) => checked((int)Integral(value));
    private static double Integral(IObject value) {
        double n = Number(value);
        if (n != Math.Truncate(n)) throw new ArgumentException("Expected an integer.");
        return n;
    }
    internal static string Text(IObject value) => value is StringObj s ? s.Value : throw new ArgumentException("Expected a string.");
    internal static ArrayObj Array(IObject value) => value as ArrayObj ?? throw new ArgumentException("Expected an array.");
    internal static ArrayObj Numbers(params double[] values) => new(values.Select(x => (IObject)new DoubleObj(x)).ToArray());
    private static IObject Null() => RepeatedPrimitives.NULL;
    private void RequireInit() {
        if (active != this) throw new InvalidOperationException("Call GLFW.Init() first.");
    }
    private void RequireGl() {
        RequireInit();
        if (current == null || current.Handle == IntPtr.Zero || !loaded)
            throw new InvalidOperationException("Make a window current and call GLAD.Load() first.");
    }
    private IntPtr Window(IObject value) {
        RequireInit();
        if (value is not WindowObject window || window.Owner != this || !windows.Contains(window) || window.Handle == IntPtr.Zero)
            throw new ArgumentException("Expected a live GLFW window owned by this runtime.");
        return window.Handle;
    }
    private DataObject Data(IObject value) {
        if (value is not DataObject data || data.Owner != this || data.Handle == IntPtr.Zero)
            throw new ArgumentException("Expected a live graphics buffer owned by this runtime.");
        return data;
    }
    private void BindGl(string name, Type signature, string resultType, string[] argumentTypes) {
        Bind(GlEnvironment, name, argumentTypes.Length, args => {
            RequireGl();
            if (!functions.TryGetValue(name, out Delegate? function)) {
                IntPtr address = Native.aqua_proc(name);
                if (address == IntPtr.Zero) throw new InvalidOperationException($"OpenGL function unavailable: {name}");
                function = Marshal.GetDelegateForFunctionPointer(address, signature);
                functions[name] = function;
            }
            using var scope = new MarshalScope(this);
            ValidateGlArguments(name, args, argumentTypes);
            object[] nativeArgs = args.Select((arg, i) => scope.ConvertArgument(arg, argumentTypes[i], name)).ToArray();
            object? result;
            try { result = function.DynamicInvoke(nativeArgs); }
            catch (TargetInvocationException ex) { throw new InvalidOperationException(ex.InnerException?.Message ?? ex.Message); }
            scope.CopyBack();
            return FromNative(result, resultType);
        });
    }
    private static void ValidateGlArguments(string name, IObject[] args, string[] types) {
        // GL has no bounds checks for host pointers. Catch the common count/size
        // mistakes before a driver can read or write outside managed storage.
        if (name.StartsWith("glGen", StringComparison.Ordinal) || name.StartsWith("glDelete", StringComparison.Ordinal)) {
            if (args.Length == 2 && types[1].Contains('*')) RequireCapacity(args[1], Int(args[0]), 4);
        }
        if (name == "glBufferData") RequireCapacity(args[2], Int(args[1]), 1, allowNull: true);
        if (name is "glBufferSubData" or "glGetBufferSubData") RequireCapacity(args[3], Int(args[2]), 1);
        if (name == "glShaderSource") {
            RequireCapacity(args[2], Int(args[1]), IntPtr.Size);
            if (args[3] is ArrayObj) RequireCapacity(args[3], Int(args[1]), 4);
        }
        if (name.StartsWith("glUniformMatrix", StringComparison.Ordinal)) {
            string dimension = name.Substring("glUniformMatrix".Length).Split('f')[0];
            int entries = dimension.Contains('x') ? (dimension[0] - '0') * (dimension[2] - '0') : (dimension[0] - '0') * (dimension[0] - '0');
            RequireCapacity(args[3], checked(Int(args[1]) * entries), 4);
        }
        if (name.StartsWith("glUniform", StringComparison.Ordinal) && name.EndsWith("v", StringComparison.Ordinal)
            && !name.StartsWith("glUniformMatrix", StringComparison.Ordinal)) {
            int components = name["glUniform".Length] - '0';
            if (components is >= 1 and <= 4) RequireCapacity(args[2], checked(Int(args[1]) * components), 4);
        }
    }
    private static void RequireCapacity(IObject buffer, int count, int size, bool allowNull = false) {
        if (count < 0) throw new ArgumentException("OpenGL count/size must be nonnegative.");
        if (allowNull && buffer is INumberObj n && n.GetNumValue() == 0) return;
        if (buffer is ArrayObj array && array.Elements.Length < count)
            throw new ArgumentException($"Pointer array needs at least {count} elements.");
        if (buffer is DataObject data && data.Size < checked(count * size))
            throw new ArgumentException($"Native buffer needs at least {count * size} bytes.");
    }
    private IObject FromNative(object? result, string type) {
        if (result == null) return Null();
        if (result is IntPtr pointer) {
            if (type.Contains("GLubyte") || type.Contains("GLchar")) return new StringObj(Marshal.PtrToStringUTF8(pointer) ?? "");
            if (type == "GLintptr" || type == "GLsizeiptr") return new GraphicsIntegerObject(pointer.ToInt64());
            return new PointerObject(pointer, this);
        }
        if (type == "GLboolean") return new BooleanObj((byte)result != 0);
        if (result is long or ulong) return new GraphicsIntegerObject(result);
        return new DoubleObj(Convert.ToDouble(result));
    }
    public void Dispose() {
        if (disposed) return;
        network.Dispose();
        dynamicLibraries.Dispose();
        webcam.Dispose();
        camera.Dispose();
        onnx.Dispose();
        pose.Dispose();
        audio.Dispose();
        DisposeProcessing();
        sharedWgpu.Dispose();
        foreach (var buffer in buffers) buffer.Dispose();
        buffers.Clear();
        Terminate(); disposed = true;
        Application.Dispose();
    }
    private void Terminate() {
        lock (Gate) {
            if (active != this) return;
            // The wgpu surface must be released before GLFW destroys its native window.
            if(sketchWindow!=null) {DisposeProcessing();sketchWindow=null;sketchClock.Stop();ResetProcessingTextInput();}
            foreach (var window in windows) { Native.aqua_destroy(window.Handle); window.Handle = IntPtr.Zero; }
            windows.Clear(); current = null; loaded = false; functions.Clear();
            Native.aqua_terminate(); active = null;
        }
    }
}
