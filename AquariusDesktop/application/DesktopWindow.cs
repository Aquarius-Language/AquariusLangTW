using System.Runtime.InteropServices;
using AquariusLang.Application;
using AquariusLang.Desktop.Graphics;

namespace AquariusLang.Desktop.Application;

internal sealed class DesktopWindow : IWindowIntegration {
    private readonly Func<IntPtr> live;
    private readonly DesktopFiles files;
    private readonly Native.ApplicationCallback callback;
    private readonly Queue<ApplicationEvent> events = new();
    private readonly List<FileResource> drop = new();
    private bool overflow, closePending, destroyed;
    private KeyModifiers modifiers;
    internal DesktopWindow(Func<IntPtr> live, DesktopFiles files) {
        this.live = live; this.files = files; callback = OnEvent;
        if (Native.aqua_application_version() != 1) throw new ApplicationFailure(FailureKind.Unsupported, "Rebuild the native application bridge.");
        Native.aqua_application_subscribe(live(), callback);
    }
    public IReadOnlyDictionary<string, Capability> Capabilities { get; } = new Dictionary<string, Capability> {
        ["events"] = new(true), ["fileDrop"] = new(true), ["closeInterception"] = new(true), ["customCursor"] = new(true),
        ["capture"] = new(OperatingSystem.IsWindows(), "GLFW has no portable absolute pointer-capture API."), ["touch"] = new(false, "GLFW supplies mouse/keyboard events."), ["pen"] = new(false, "GLFW does not expose pen properties.")
    };
    private void OnEvent(IntPtr window, int type, int a, int b, int c, double x, double y, IntPtr text) {
        // Never allow managed exceptions to cross the C callback boundary.
        try {
            var now = Native.aqua_time(); ApplicationEvent? e = null;
            if (type is 1 or 3) modifiers = (KeyModifiers)c;
            if (type == 5 && a == 0) modifiers = KeyModifiers.None;
            switch (type) {
                case 1: e = new(x == 0 ? "keyReleased" : x == 2 ? "keyRepeat" : "keyPressed", now, LogicalKey(a, Marshal.PtrToStringUTF8(text)), "scan:" + b, (KeyModifiers)c); break;
                case 2: e = new("pointerMoved", now, Modifiers: modifiers, DeviceId: 0, Device: "mouse", Samples: new[] { new PointerSample(x, y, now) }); break;
                case 3: e = new(b == 0 ? "pointerReleased" : "pointerPressed", now, Modifiers: (KeyModifiers)c, DeviceId: 0, Device: "mouse", Button: a, Samples: new[] { new PointerSample(x, y, now) }, Contact: b != 0); break;
                case 4: e = new("wheel", now, Modifiers: modifiers, DeviceId: 0, Device: "mouse", WheelX: x, WheelY: -y, WheelUnit: "line"); break;
                case 5: e = new("focus", now, State: a != 0); if (a == 0) events.Enqueue(new("inputCancelled", now)); events.Enqueue(new("activation", now, State: a != 0)); break;
                case 6: e = new("visibility", now, State: a != 0); break;
                case 7: e = new(a == 0 ? "pointerLeft" : "pointerEntered", now, DeviceId: 0, Device: "mouse"); break;
                case 8: if (a == 0) drop.Clear(); drop.Add(files.Resolve(Marshal.PtrToStringUTF8(text)!)); if (a == b - 1) e = new("fileDrop", now, Files: drop.ToArray()); break;
                case 9: closePending = true; e = new("closeRequested", now); break;
                case 10: e = new("captureLost", now, DeviceId: 0); break;
                case 11: destroyed = true; e = new("destroyed", now); break;
                case 12: e = new("created", now); break;
            }
            if (events.Count >= 4096) { overflow = true; return; } if (e != null) events.Enqueue(e);
        } catch { overflow = true; }
    }
    private static string LogicalKey(int key, string? text) => key switch { 256 => "Escape", 257 => "Enter", 258 => "Tab", 259 => "Backspace", 261 => "Delete", 262 => "ArrowRight", 263 => "ArrowLeft", 264 => "ArrowDown", 265 => "ArrowUp", 268 => "Home", 269 => "End", 32 => " ", _ => text ?? "key:" + key };
    public IReadOnlyList<ApplicationEvent> Poll() { if (!destroyed) _ = live(); if (overflow) { overflow = false; events.Clear(); throw new ApplicationFailure(FailureKind.LimitExceeded, "Window event queue overflow; poll more often."); } var result = events.ToArray(); events.Clear(); return result; }
    public void SetTitle(string title) => Native.aqua_title(live(), title);
    public void SetCursor(CursorShape shape) {
        int value = shape switch { CursorShape.Arrow => 0x36001, CursorShape.Text => 0x36002, CursorShape.Crosshair => 0x36003, CursorShape.Hand => 0x36004, CursorShape.ResizeHorizontal => 0x36005, CursorShape.ResizeVertical => 0x36006, CursorShape.ResizeDiagonal1 => 0x36007, CursorShape.ResizeDiagonal2 => 0x36008, CursorShape.Move => 0x36009, CursorShape.NotAllowed => 0x3600a, _ => throw new ArgumentException("Invalid cursor.") };
        if (Native.aqua_standard_cursor(live(), value) == 0) throw new ApplicationFailure(FailureKind.Unsupported, "Cursor unavailable on this window system.");
    }
    public void SetCustomCursor(PixelImage image, int hotspotX, int hotspotY) {
        if (hotspotX < 0 || hotspotY < 0 || hotspotX >= image.Width || hotspotY >= image.Height) throw new ArgumentException("Hotspot must be inside cursor image.");
        if (Native.aqua_custom_cursor(live(), image.Width, image.Height, image.StraightPixels(), hotspotX, hotspotY) == 0) throw new ApplicationFailure(FailureKind.Unsupported, "Custom cursor unavailable.");
    }
    public void CapturePointer(long deviceId) { if (deviceId != 0) throw new ArgumentException("GLFW mouse id is 0."); if (Native.aqua_capture(live(), 1) == 0) throw new ApplicationFailure(FailureKind.Unsupported, "Pointer capture unavailable."); }
    public void ReleasePointer(long deviceId) { if (deviceId != 0) throw new ArgumentException("GLFW mouse id is 0."); if (Native.aqua_capture(live(), 0) == 0) throw new ApplicationFailure(FailureKind.Unavailable, "Pointer capture could not be released."); }
    public void ResolveClose(CloseDecision decision) { if (!Enum.IsDefined(decision)) throw new ArgumentException("Unknown close decision."); if (!closePending) throw new ApplicationFailure(FailureKind.Conflict, "No pending close request."); if (decision == CloseDecision.Defer) return; closePending = false; Native.aqua_close(live(), decision == CloseDecision.Accept ? 1 : 0); }
}
