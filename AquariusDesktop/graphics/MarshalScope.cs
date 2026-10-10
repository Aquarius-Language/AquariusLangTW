using System.Runtime.InteropServices;
using AquariusLang.Object;

namespace AquariusLang.Desktop.Graphics;

internal sealed class MarshalScope : IDisposable {
    private readonly GraphicsRuntime runtime;
    private readonly List<IntPtr> allocated = new();
    private readonly List<Action> outputs = new();
    internal MarshalScope(GraphicsRuntime runtime) { this.runtime = runtime; }
    private IntPtr Allocate(int bytes) { var p = Marshal.AllocHGlobal(Math.Max(1, bytes)); allocated.Add(p); return p; }
    private IntPtr String(string text) { var p = Marshal.StringToCoTaskMemUTF8(text); strings.Add(p); return p; }
    private readonly List<IntPtr> strings = new();
    internal object ConvertArgument(IObject value, string type, string function) {
        if (!type.Contains('*') && type != "GLsync") return Scalar(value, type);
        if (value is DataObject data) {
            if (data.Owner != runtime || data.Handle == IntPtr.Zero) throw new ArgumentException("Buffer is freed or belongs to another runtime.");
            return data.Handle;
        }
        if (value is PointerObject pointer) {
            if (pointer.Owner != runtime) throw new ArgumentException("Pointer belongs to another runtime.");
            return pointer.Handle;
        }
        if (value is INumberObj) {
            double number = GraphicsRuntime.Number(value);
            if (number != Math.Truncate(number)) throw new ArgumentException("Pointer offsets must be integers.");
            long n = checked((long)number);
            bool offset = function is "glVertexAttribPointer" or "glVertexAttribIPointer" or "glDrawElements" or "glDrawElementsInstanced"
                or "glDrawElementsBaseVertex" or "glDrawElementsInstancedBaseVertex" or "glDrawRangeElements" or "glDrawRangeElementsBaseVertex"
                or "glMultiDrawElements" or "glMultiDrawElementsBaseVertex";
            if (n != 0 && !offset) throw new ArgumentException("Use a typed graphics buffer for pointer arguments; only 0 is accepted as a null pointer.");
            if (n < 0) throw new ArgumentException("Buffer offsets must be nonnegative.");
            return new IntPtr(n);
        }
        if (value is StringObj text && type.Contains("GLchar") && type.Count(c => c == '*') == 1) return String(text.Value);
        var array = GraphicsRuntime.Array(value);
        if (type.Contains("GLchar") && type.Count(c => c == '*') == 2) {
            IntPtr p = Allocate(checked(array.Elements.Length * IntPtr.Size));
            for (int i = 0; i < array.Elements.Length; i++) Marshal.WriteIntPtr(p, i * IntPtr.Size, String(GraphicsRuntime.Text(array.Elements[i])));
            return p;
        }
        if (type.Count(c => c == '*') == 2) {
            IntPtr pointers = Allocate(checked(array.Elements.Length * IntPtr.Size));
            for (int i = 0; i < array.Elements.Length; i++)
                Marshal.WriteIntPtr(pointers, i * IntPtr.Size, (IntPtr)ConvertArgument(array.Elements[i], "void *", function));
            if (!type.Contains("const")) outputs.Add(() => {
                for (int i = 0; i < array.Elements.Length; i++)
                    array.Elements[i] = new PointerObject(Marshal.ReadIntPtr(pointers, i * IntPtr.Size), runtime);
            });
            return pointers;
        }
        string element = type.Replace("const", "").Replace("*", "").Trim();
        if (element == "void")
            throw new ArgumentException("Use GL.FloatData, GL.UIntData, GL.ByteData or GL.Allocate for void* arguments.");
        Type elementType = ClrType(element);
        int size = Marshal.SizeOf(elementType);
        IntPtr buffer = Allocate(checked(size * array.Elements.Length));
        for (int i = 0; i < array.Elements.Length; i++) Marshal.StructureToPtr(Scalar(array.Elements[i], element), IntPtr.Add(buffer, size * i), false);
        if (!type.Contains("const")) outputs.Add(() => {
            for (int i = 0; i < array.Elements.Length; i++) {
                object item = Marshal.PtrToStructure(IntPtr.Add(buffer, size * i), elementType)!;
                array.Elements[i] = item is long or ulong ? new GraphicsIntegerObject(item) : new DoubleObj(Convert.ToDouble(item));
            }
        });
        return buffer;
    }
    internal static Type ClrType(string type) => type switch {
        "GLfloat" => typeof(float), "GLdouble" => typeof(double), "GLenum" or "GLuint" or "GLbitfield" => typeof(uint),
        "GLint" or "GLsizei" => typeof(int), "GLboolean" or "GLubyte" or "GLchar" => typeof(byte),
        "GLbyte" => typeof(sbyte), "GLshort" => typeof(short), "GLushort" => typeof(ushort),
        "GLint64" => typeof(long), "GLuint64" => typeof(ulong), "GLintptr" or "GLsizeiptr" => typeof(IntPtr),
        _ => throw new ArgumentException($"Unsupported native scalar: {type}")
    };
    internal static object Scalar(IObject value, string type) {
        if (type == "GLboolean" && value is BooleanObj b) return (byte)(b.Value ? 1 : 0);
        if (value is GraphicsIntegerObject exact) {
            if (type == "GLuint64") return Convert.ToUInt64(exact.Value);
            if (type == "GLint64") return Convert.ToInt64(exact.Value);
        }
        double n = GraphicsRuntime.Number(value);
        if (type != "GLfloat" && type != "GLdouble" && n != Math.Truncate(n)) throw new ArgumentException("Expected an integral OpenGL argument.");
        return type switch {
            "GLfloat" => (object)checked((float)n), "GLdouble" => n,
            "GLenum" or "GLuint" or "GLbitfield" => checked((uint)n),
            "GLint" or "GLsizei" => checked((int)n), "GLboolean" or "GLubyte" or "GLchar" => checked((byte)n),
            "GLbyte" => checked((sbyte)n), "GLshort" => checked((short)n), "GLushort" => checked((ushort)n),
            "GLint64" => checked((long)n), "GLuint64" => checked((ulong)n),
            "GLintptr" or "GLsizeiptr" => new IntPtr(checked((long)n)),
            _ => throw new ArgumentException($"Unsupported OpenGL type: {type}")
        };
    }
    internal void CopyBack() { foreach (var output in outputs) output(); }
    public void Dispose() {
        foreach (var p in allocated) Marshal.FreeHGlobal(p);
        foreach (var p in strings) Marshal.FreeCoTaskMem(p);
    }
}
