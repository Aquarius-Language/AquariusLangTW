using System.Runtime.InteropServices;
using AquariusLang.Object;

namespace AquariusREPL.Graphics;

internal sealed class WindowObject : IObject {
    internal IntPtr Handle;
    internal readonly GraphicsRuntime Owner;
    internal WindowObject(IntPtr handle, GraphicsRuntime owner) { Handle = handle; Owner = owner; }
    public string Type() => "GLFW_WINDOW";
    public string Inspect() => Handle == IntPtr.Zero ? "closed GLFW window" : "GLFW window";
}

// Typed buffers keep void* uploads unambiguous; pointers are never language numbers.
internal sealed class DataObject : IObject, IDisposable {
    internal IntPtr Handle;
    internal int Size;
    internal readonly GraphicsRuntime Owner;
    internal DataObject(int size, GraphicsRuntime owner) {
        if (size < 0 || size > 256 * 1024 * 1024) throw new ArgumentException("Buffer size must be between 0 and 256 MiB.");
        Size = size; Owner = owner;
        Handle = Marshal.AllocHGlobal(Math.Max(1, size));
        Marshal.Copy(new byte[size], 0, Handle, size);
    }
    public void Dispose() { if (Handle != IntPtr.Zero) { Marshal.FreeHGlobal(Handle); Handle = IntPtr.Zero; } }
    public string Type() => "GL_DATA";
    public string Inspect() => $"graphics buffer ({Size} bytes)";
}

internal sealed class PointerObject : IObject {
    internal readonly IntPtr Handle;
    internal readonly GraphicsRuntime Owner;
    internal PointerObject(IntPtr handle, GraphicsRuntime owner) { Handle = handle; Owner = owner; }
    public string Type() => "GL_POINTER";
    public string Inspect() => "OpenGL pointer";
}

// Preserve exact 64-bit query results and GL_TIMEOUT_IGNORED without double rounding.
internal sealed class GraphicsIntegerObject : IObject {
    internal readonly object Value;
    internal GraphicsIntegerObject(object value) { Value = value; }
    public string Type() => "GL_INTEGER64";
    public string Inspect() => Value.ToString()!;
}
