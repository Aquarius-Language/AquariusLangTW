using System.Runtime.InteropServices;
using AquariusLang.Object;

namespace AquariusREPL.Graphics;

internal sealed partial class GraphicsRuntime {
    private void RegisterGlfw() {
        var env = Module("GLFW");
        var glad = Module("GLAD");
        Bind(env, "Init", 0, _ => {
            lock (Gate) {
                if (active == this) return new BooleanObj(true);
                if (active != null) throw new InvalidOperationException("Another Aquarius runtime owns GLFW.");
                if (Native.aqua_init() == 0) throw new InvalidOperationException(Marshal.PtrToStringUTF8(Native.aqua_error()));
                active = this;
                Native.aqua_hint(0x00022002, 3); Native.aqua_hint(0x00022003, 3);
                Native.aqua_hint(0x00022008, 0x00032001); Native.aqua_hint(0x00022006, 1);
                return new BooleanObj(true);
            }
        });
        Bind(env, "Terminate", 0, _ => { Terminate(); return Null(); });
        Bind(env, "WindowHint", 2, a => { RequireInit(); Native.aqua_hint(Int(a[0]), Int(a[1])); return Null(); });
        Bind(env, "CreateWindow", 3, a => {
            RequireInit();
            if (windows.Count != 0) throw new InvalidOperationException("This runtime supports one OpenGL window at a time.");
            int width = Int(a[0]), height = Int(a[1]);
            if (width <= 0 || height <= 0) throw new ArgumentException("Window dimensions must be positive.");
            IntPtr handle = Native.aqua_window(width, height, Text(a[2]));
            if (handle == IntPtr.Zero) throw new InvalidOperationException(Marshal.PtrToStringUTF8(Native.aqua_error()));
            var window = new WindowObject(handle, this); windows.Add(window); return window;
        });
        Bind(env, "DestroyWindow", 1, a => {
            IntPtr handle = Window(a[0]); var window = (WindowObject)a[0]; Native.aqua_destroy(handle);
            window.Handle = IntPtr.Zero; windows.Remove(window); current = null; loaded = false; functions.Clear(); return Null();
        });
        Bind(env, "MakeContextCurrent", 1, a => {
            Native.aqua_current(Window(a[0])); current = (WindowObject)a[0]; loaded = false; functions.Clear(); return Null();
        });
        Bind(glad, "Load", 0, _ => {
            RequireInit();
            if (current == null) throw new InvalidOperationException("Call GLFW.MakeContextCurrent(window) before GLAD.Load().");
            int version = Native.aqua_load();
            if (version < 30003) throw new InvalidOperationException("The GPU/driver must support OpenGL 3.3 core or later.");
            loaded = true; return new IntegerObj(version);
        });
        Bind(env, "WindowShouldClose", 1, a => new BooleanObj(Native.aqua_should_close(Window(a[0])) != 0));
        Bind(env, "SetWindowShouldClose", 2, a => {
            if (a[1] is not BooleanObj close) throw new ArgumentException("Expected a Boolean close flag.");
            Native.aqua_close(Window(a[0]), close.Value ? 1 : 0); return Null();
        });
        Bind(env, "SwapBuffers", 1, a => { RequireGl(); Native.aqua_swap(Window(a[0])); return Null(); });
        Bind(env, "PollEvents", 0, _ => { RequireInit(); Native.aqua_poll(); return Null(); });
        Bind(env, "SwapInterval", 1, a => { RequireGl(); Native.aqua_interval(Int(a[0])); return Null(); });
        Bind(env, "GetTime", 0, _ => { RequireInit(); return new DoubleObj(Native.aqua_time()); });
        Bind(env, "GetKey", 2, a => new IntegerObj(Native.aqua_key(Window(a[0]), Int(a[1]))));
        Bind(env, "GetMouseButton", 2, a => new IntegerObj(Native.aqua_mouse(Window(a[0]), Int(a[1]))));
        Bind(env, "GetCursorPos", 1, a => { Native.aqua_cursor(Window(a[0]), out double x, out double y); return Numbers(x, y); });
        Bind(env, "SetInputMode", 3, a => { Native.aqua_input(Window(a[0]), Int(a[1]), Int(a[2])); return Null(); });
        Bind(env, "GetFramebufferSize", 1, a => { Native.aqua_framebuffer(Window(a[0]), out int w, out int h); return Numbers(w, h); });
        Bind(env, "GetEnvironment", 1, a => new StringObj(System.Environment.GetEnvironmentVariable(Text(a[0])) ?? ""));
        var constants = new Dictionary<string, int> {
            ["GLFW_TRUE"] = 1, ["GLFW_FALSE"] = 0, ["GLFW_PRESS"] = 1, ["GLFW_RELEASE"] = 0, ["GLFW_REPEAT"] = 2,
            ["GLFW_CONTEXT_VERSION_MAJOR"] = 0x22002, ["GLFW_CONTEXT_VERSION_MINOR"] = 0x22003,
            ["GLFW_OPENGL_PROFILE"] = 0x22008, ["GLFW_OPENGL_CORE_PROFILE"] = 0x32001,
            ["GLFW_OPENGL_FORWARD_COMPAT"] = 0x22006, ["GLFW_VISIBLE"] = 0x20004, ["GLFW_RESIZABLE"] = 0x20003,
            ["GLFW_SAMPLES"] = 0x2100D, ["GLFW_KEY_ESCAPE"] = 256, ["GLFW_KEY_SPACE"] = 32,
            ["GLFW_KEY_LEFT"] = 263, ["GLFW_KEY_RIGHT"] = 262, ["GLFW_KEY_UP"] = 265, ["GLFW_KEY_DOWN"] = 264,
            ["GLFW_MOUSE_BUTTON_LEFT"] = 0, ["GLFW_MOUSE_BUTTON_RIGHT"] = 1,
            ["GLFW_CURSOR"] = 0x33001, ["GLFW_CURSOR_NORMAL"] = 0x34001, ["GLFW_CURSOR_DISABLED"] = 0x34003
        };
        for (char key = 'A'; key <= 'Z'; key++) constants["GLFW_KEY_" + key] = key;
        for (char key = '0'; key <= '9'; key++) constants["GLFW_KEY_" + key] = key;
        foreach (var constant in constants) env.Create(constant.Key, new IntegerObj(constant.Value));
    }
}
