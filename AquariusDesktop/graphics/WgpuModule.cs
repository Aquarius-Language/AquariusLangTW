using System.Runtime.InteropServices;
using AquariusLang.Graphics;
namespace AquariusLang.Desktop.Graphics;
internal sealed partial class GraphicsRuntime {
    private WgpuRuntime sharedWgpu = null!;
    private readonly HashSet<DesktopWgpuWindow> wgpuWindows = new();
    private void RegisterWgpu() => sharedWgpu = new(new SilkWgpuBackend(CreateWgpuWindow), WriteImage, path => File.ReadAllBytes(ResourcePath(path)));
    internal bool HasWgpuWindows => wgpuWindows.Count != 0;
    /// <summary>Opens a GLFW_NO_API window for the WGPU module. GLFW stays owned by this runtime.</summary>
    private DesktopWgpuWindow CreateWgpuWindow(int width, int height, string title) {
        if (disposed) throw new InvalidOperationException("Graphics runtime has been disposed.");
        if (sketchWindow != null || windows.Count != 0) throw new InvalidOperationException("Close Processing sketches and GLFW windows before creating a WGPU window.");
        if (wgpuWindows.Count != 0) throw new InvalidOperationException("This runtime supports one WGPU window at a time.");
        CallModule("GLFW", "Init");
        Native.aqua_hint(0x00022001, 0); // GLFW_CLIENT_API = GLFW_NO_API: wgpu owns the surface.
        Native.aqua_hint(0x00020003, 1); // GLFW_RESIZABLE
        IntPtr handle = Native.aqua_window(width, height, title);
        if (handle == IntPtr.Zero) { string error = Marshal.PtrToStringUTF8(Native.aqua_error()) ?? "GLFW window creation failed."; if (windows.Count == 0 && sketchWindow == null) Terminate(); throw new InvalidOperationException(error); }
        var window = new DesktopWgpuWindow(handle, ReleaseWgpuWindow); wgpuWindows.Add(window); return window;
    }
    private void ReleaseWgpuWindow(DesktopWgpuWindow window) {
        if (!wgpuWindows.Remove(window)) return;
        Native.aqua_destroy(window.Handle);
        if (wgpuWindows.Count == 0 && windows.Count == 0 && sketchWindow == null) Terminate();
    }
}

/// <summary>GLFW window backing a WGPU device surface. Input arrives through the bridge's application
/// callback during <see cref="Poll"/>, so short key taps between frames are not lost.</summary>
internal sealed class DesktopWgpuWindow : IWgpuWindow {
    internal IntPtr Handle;
    internal WgpuDevice? Device;
    private readonly Action<DesktopWgpuWindow> release;
    private readonly Native.ApplicationCallback callback;
    private readonly bool[] keys = new bool[512], keyPressed = new bool[512], keyReleased = new bool[512];
    private readonly bool[] pendingPressed = new bool[512], pendingReleased = new bool[512];
    private readonly bool[] buttons = new bool[8], buttonPressed = new bool[8], buttonReleased = new bool[8];
    private readonly bool[] pendingButtonPressed = new bool[8], pendingButtonReleased = new bool[8];
    private double wheel, lastX, lastY; private bool haveLast, focused = true, locked, closeRequested;
    public WgpuWindowState State { get; private set; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, true, false, false, false);
    public bool Disposed => Handle == IntPtr.Zero;
    internal DesktopWgpuWindow(IntPtr handle, Action<DesktopWgpuWindow> release) {
        Handle = handle; this.release = release; callback = OnEvent;
        Native.aqua_application_subscribe(handle, callback);
        Native.aqua_window_size(handle, out int w, out int h); Native.aqua_framebuffer(handle, out int pw, out int ph);
        Native.aqua_cursor(handle, out lastX, out lastY);
        State = new(w, h, pw, ph, lastX, lastY, 0, 0, 0, true, false, false, false);
    }
    private void OnEvent(IntPtr window, int kind, int a, int b, int c, double x, double y, IntPtr text) {
        switch (kind) {
            case 1 when a is >= 0 and < 512: // key: a=key, x=action (0 release, 1 press, 2 repeat)
                if (x == 1) { keys[a] = true; pendingPressed[a] = true; } else if (x == 0) { keys[a] = false; pendingReleased[a] = true; }
                break;
            case 3 when a is >= 0 and < 8: // mouse button: b=action
                if (b == 1) { buttons[a] = true; pendingButtonPressed[a] = true; } else { buttons[a] = false; pendingButtonReleased[a] = true; }
                break;
            case 4: wheel += y; break;
            case 5: focused = a != 0; if (!focused) { ReleaseHeldInput(); if (locked) SetPointerLock(false); } break;
            case 9: closeRequested = true; break;
        }
    }
    private void ReleaseHeldInput() {
        for (int i = 0; i < keys.Length; i++) if (keys[i]) { keys[i] = false; pendingReleased[i] = true; }
        for (int i = 0; i < buttons.Length; i++) if (buttons[i]) { buttons[i] = false; pendingButtonReleased[i] = true; }
    }
    private void Live() { if (Disposed) throw new InvalidOperationException("WGPU window has been disposed."); }
    internal void FramebufferSize(out int width, out int height) { Live(); Native.aqua_framebuffer(Handle, out width, out height); }
    public void Poll() {
        Live(); Native.aqua_poll();
        if (Disposed) return;
        Array.Copy(pendingPressed, keyPressed, keyPressed.Length); Array.Clear(pendingPressed);
        Array.Copy(pendingReleased, keyReleased, keyReleased.Length); Array.Clear(pendingReleased);
        Array.Copy(pendingButtonPressed, buttonPressed, buttonPressed.Length); Array.Clear(pendingButtonPressed);
        Array.Copy(pendingButtonReleased, buttonReleased, buttonReleased.Length); Array.Clear(pendingButtonReleased);
        Native.aqua_window_size(Handle, out int w, out int h); Native.aqua_framebuffer(Handle, out int pw, out int ph);
        // A minimized window presents nothing, so nothing paces the loop; avoid spinning a CPU core.
        if (pw == 0 || ph == 0) Thread.Sleep(50);
        Native.aqua_cursor(Handle, out double x, out double y);
        double dx = haveLast ? x - lastX : 0, dy = haveLast ? y - lastY : 0; lastX = x; lastY = y; haveLast = true;
        bool resized = w != State.Width || h != State.Height || pw != State.PixelWidth || ph != State.PixelHeight;
        bool close = closeRequested || Native.aqua_should_close(Handle) != 0;
        State = new(w, h, pw, ph, x, y, dx, dy, wheel, focused, locked, close, resized); wheel = 0;
    }
    private static bool Valid(int code, int length) => code >= 0 && code < length;
    public bool KeyDown(int key) => Valid(key, keys.Length) && keys[key];
    public bool KeyPressed(int key) => Valid(key, keys.Length) && keyPressed[key];
    public bool KeyReleased(int key) => Valid(key, keys.Length) && keyReleased[key];
    public bool MouseDown(int button) => Valid(button, buttons.Length) && buttons[button];
    public bool MousePressed(int button) => Valid(button, buttons.Length) && buttonPressed[button];
    public bool MouseReleased(int button) => Valid(button, buttons.Length) && buttonReleased[button];
    public void SetPointerLock(bool value) {
        Live(); if (value && !focused) return;
        Native.aqua_input(Handle, 0x00033001, value ? 0x00034003 : 0x00034001); // GLFW_CURSOR: DISABLED / NORMAL
        if (value && OperatingSystem.IsWindows()) Native.aqua_input(Handle, 0x00033005, 1); // GLFW_RAW_MOUSE_MOTION
        locked = value; haveLast = false;
        State = State with { PointerLocked = value, MouseDeltaX = 0, MouseDeltaY = 0 };
    }
    public void SetTitle(string title) { Live(); Native.aqua_title(Handle, title); }
    public void Close() { Live(); closeRequested = true; State = State with { ShouldClose = true }; }
    public void Dispose() {
        if (Disposed) return;
        // The device surface must be released before GLFW destroys the native window.
        if (Device != null && !Device.Disposed) Device.Dispose();
        release(this); Handle = IntPtr.Zero; GC.KeepAlive(callback);
    }
}
