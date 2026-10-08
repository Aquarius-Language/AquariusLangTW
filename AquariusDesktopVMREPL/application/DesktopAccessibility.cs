using System.Runtime.InteropServices;
using AquariusLang.Application;
using AquariusREPL.Graphics;

namespace AquariusREPL.Application;

/// <summary>Read-only MSAA mirror; rendering and application interactions stay with the application.</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class DesktopAccessibility : IAccessibleTree {
    private AccessibilityNode[] nodes = Array.Empty<AccessibilityNode>();
    private readonly Native.AccessibilityCallback callback;
    private readonly IntPtr hwnd;
    internal DesktopAccessibility(IntPtr window) {
        if (!OperatingSystem.IsWindows()) throw new ApplicationFailure(FailureKind.Unsupported, "MSAA adapter requires Windows.");
        Native.aqua_wgpu_handles(window, out _, out hwnd); callback = GetObject; Native.aqua_accessibility_subscribe(window, callback);
    }
    public void Update(IReadOnlyList<AccessibilityNode> values) {
        if (values.Count > 4096 || values.Any(n => string.IsNullOrEmpty(n.Id) || n.Role == null || n.Label == null) || values.Select(n => n.Id).Distinct().Count() != values.Count) throw new ArgumentException("Invalid accessibility node list.");
        nodes = values.ToArray(); NotifyWinEvent(0x8004, hwnd, -4, 0);
    }
    private IntPtr GetObject(nuint parameter, nint objectId) {
        if ((int)objectId != -4) return IntPtr.Zero;
        try { var id = typeof(IAccessibleTree).GUID; return LresultFromObject(ref id, parameter, this); } catch { return IntPtr.Zero; }
    }
    private AccessibilityNode Node(object child) { int index = child is int n ? n : throw new ArgumentException("Child must be an integer."); if (index < 1 || index > nodes.Length) throw new ArgumentException("Invalid accessible child."); return nodes[index - 1]; }
    public object? get_accParent() => null;
    public int get_accChildCount() => nodes.Length;
    public object? get_accChild(object child) { _ = Node(child); return null; }
    public string get_accName(object child) => child is int n && n == 0 ? "Aquarius application" : Node(child).Label;
    public string? get_accValue(object child) => child is int n && n == 0 ? null : Node(child).Value;
    public string? get_accDescription(object child) => null;
    public object get_accRole(object child) => child is int n && n == 0 ? 0xa : Node(child).Role switch { "button" => 0x2b, "textbox" => 0x2a, "img" => 0x28, "status" => 0x17, "checkbox" => 0x2c, "list" => 0x21, "listitem" => 0x22, _ => 0x29 };
    public object get_accState(object child) => child is int n && n == 0 ? 0 : Node(child).Disabled ? 1 : 0;
    public string? get_accHelp(object child) => null;
    public int get_accHelpTopic(out string? help, object child) { help = null; return -1; }
    public string? get_accKeyboardShortcut(object child) => null;
    public object? get_accFocus() => 0;
    public object? get_accSelection() => null;
    public string? get_accDefaultAction(object child) => null;
    public void accSelect(int flags, object child) => throw new COMException("Read-only accessibility mirror.", unchecked((int)0x80004001));
    public void accLocation(out int x, out int y, out int width, out int height, object child) { GetWindowRect(hwnd, out var rect); x = rect.Left; y = rect.Top; width = rect.Right - x; height = rect.Bottom - y; }
    public object? accNavigate(int direction, object child) { int n = child is int index ? index : 0; return direction switch { 7 when nodes.Length > 0 => 1, 8 when nodes.Length > 0 => nodes.Length, 5 when n < nodes.Length => n + 1, 6 when n > 1 => n - 1, _ => null }; }
    public object? accHitTest(int x, int y) => 0;
    public void accDoDefaultAction(object child) => throw new COMException("No default action.", unchecked((int)0x80004001));
    public void set_accName(object child, string name) => throw new COMException("Read-only accessibility mirror.", unchecked((int)0x80004001));
    public void set_accValue(object child, string value) => throw new COMException("Read-only accessibility mirror.", unchecked((int)0x80004001));
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern void NotifyWinEvent(uint kind, IntPtr window, int objectId, int child);
    [DllImport("oleacc.dll")] private static extern IntPtr LresultFromObject(ref Guid id, nuint parameter, [MarshalAs(UnmanagedType.Interface)] IAccessibleTree value);
}
[ComVisible(true), Guid("618736E0-3C3D-11CF-810C-00AA00389B71"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IAccessibleTree {
    [DispId(-5000)] object? get_accParent(); [DispId(-5001)] int get_accChildCount(); [DispId(-5002)] object? get_accChild(object child);
    [DispId(-5003)] string get_accName(object child); [DispId(-5004)] string? get_accValue(object child); [DispId(-5005)] string? get_accDescription(object child);
    [DispId(-5006)] object get_accRole(object child); [DispId(-5007)] object get_accState(object child); [DispId(-5008)] string? get_accHelp(object child);
    [DispId(-5009)] int get_accHelpTopic(out string? help, object child); [DispId(-5010)] string? get_accKeyboardShortcut(object child);
    [DispId(-5011)] object? get_accFocus(); [DispId(-5012)] object? get_accSelection(); [DispId(-5013)] string? get_accDefaultAction(object child);
    [DispId(-5014)] void accSelect(int flags, object child); [DispId(-5015)] void accLocation(out int x, out int y, out int width, out int height, object child);
    [DispId(-5016)] object? accNavigate(int direction, object child); [DispId(-5017)] object? accHitTest(int x, int y); [DispId(-5018)] void accDoDefaultAction(object child);
    [DispId(-5003)] void set_accName(object child, string name); [DispId(-5004)] void set_accValue(object child, string value);
}
