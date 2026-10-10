using System.Runtime.InteropServices;
using System.Text;
using AquariusLang.Object;

namespace AquariusLang.Desktop.Graphics;

internal sealed partial class GraphicsRuntime {
    private IObject CallGl(string name, params IObject[] args) {
        var builtin = (BuiltinObj)GlEnvironment.Get(name, out _);
        IObject result = builtin.Fn(args);
        if (result is ErrorObj error) throw new InvalidOperationException(error.Message);
        return result;
    }
    private static IntegerObj N(int value) => new(value);
    private void RegisterGlHelpers() {
        GlEnvironment.Create("GL_TIMEOUT_IGNORED", new GraphicsIntegerObject(ulong.MaxValue));
        Bind(GlEnvironment, "ReadText", 1, a => new StringObj(File.ReadAllText(ResourcePath(Text(a[0])))));
        Bind(GlEnvironment, "ParseInteger", 1, a => {
            if (!int.TryParse(Text(a[0]), out int value)) throw new ArgumentException("Expected a decimal integer string.");
            return N(value);
        });
        Bind(GlEnvironment, "FloatData", 1, a => NewData(a[0], "GLfloat"));
        Bind(GlEnvironment, "UIntData", 1, a => NewData(a[0], "GLuint"));
        Bind(GlEnvironment, "ByteData", 1, a => NewData(a[0], "GLubyte"));
        Bind(GlEnvironment, "Allocate", 1, a => { var data = new DataObject(Int(a[0]), this); buffers.Add(data); return data; });
        Bind(GlEnvironment, "ByteLength", 1, a => N(Data(a[0]).Size));
        Bind(GlEnvironment, "FreeData", 1, a => { var data = Data(a[0]); data.Dispose(); buffers.Remove(data); return Null(); });
        Bind(GlEnvironment, "ReadBytes", 1, a => {
            var data = Data(a[0]); var bytes = new byte[data.Size]; Marshal.Copy(data.Handle, bytes, 0, bytes.Length);
            return new ArrayObj(bytes.Select(b => (IObject)N(b)).ToArray());
        });
        Bind(GlEnvironment, "CreateProgram", 2, a => {
            RequireGl(); string vertexSource = Text(a[0]), fragmentSource = Text(a[1]);
            IObject? vertex = null, fragment = null, program = null;
            try {
                vertex = Compile(0x8B31, vertexSource); fragment = Compile(0x8B30, fragmentSource);
                program = CallGl("glCreateProgram");
                CallGl("glAttachShader", program, vertex); CallGl("glAttachShader", program, fragment);
                CallGl("glLinkProgram", program);
                var status = Numbers(0); CallGl("glGetProgramiv", program, N(0x8B82), status);
                if (Number(status.Elements[0]) == 0) throw new InvalidOperationException("Program link failed: " + Log(program, false));
                return program;
            } catch {
                if (program != null) CallGl("glDeleteProgram", program);
                throw;
            } finally {
                if (vertex != null) CallGl("glDeleteShader", vertex);
                if (fragment != null) CallGl("glDeleteShader", fragment);
            }
        });
        Bind(GlEnvironment, "ShaderLog", 1, a => { RequireGl(); return new StringObj(Log(a[0], true)); });
        Bind(GlEnvironment, "ProgramLog", 1, a => { RequireGl(); return new StringObj(Log(a[0], false)); });
        Bind(GlEnvironment, "SavePPM", 3, a => {
            RequireGl(); string path = Text(a[0]); int width = Int(a[1]), height = Int(a[2]);
            if (width <= 0 || height <= 0) throw new ArgumentException("Capture dimensions must be positive.");
            using var pixels = new DataObject(checked(width * height * 3), this);
            var oldAlignment = Numbers(0); CallGl("glGetIntegerv", N(0x0D05), oldAlignment);
            CallGl("glPixelStorei", N(0x0D05), N(1));
            try { CallGl("glReadPixels", N(0), N(0), N(width), N(height), N(0x1907), N(0x1401), pixels); }
            finally { CallGl("glPixelStorei", N(0x0D05), oldAlignment.Elements[0]); }
            byte[] bytes = new byte[pixels.Size]; Marshal.Copy(pixels.Handle, bytes, 0, bytes.Length);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var output = File.Create(path);
            byte[] header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"); output.Write(header);
            for (int row = height - 1; row >= 0; row--) output.Write(bytes, row * width * 3, width * 3);
            return Null();
        });
    }
    private DataObject NewData(IObject input, string type) {
        var array = Array(input); int size = Marshal.SizeOf(MarshalScope.ClrType(type));
        // Validate before allocating so failed conversion cannot leak unmanaged memory.
        object[] values = array.Elements.Select(x => MarshalScope.Scalar(x, type)).ToArray();
        var data = new DataObject(checked(values.Length * size), this); buffers.Add(data);
        for (int i = 0; i < values.Length; i++) Marshal.StructureToPtr(values[i], IntPtr.Add(data.Handle, size * i), false);
        return data;
    }
    private IObject Compile(int type, string source) {
        IObject shader = CallGl("glCreateShader", N(type));
        try {
            CallGl("glShaderSource", shader, N(1), new ArrayObj(new IObject[] { new StringObj(source) }), N(0));
            CallGl("glCompileShader", shader);
            var status = Numbers(0); CallGl("glGetShaderiv", shader, N(0x8B81), status);
            if (Number(status.Elements[0]) == 0) throw new InvalidOperationException("Shader compile failed: " + Log(shader, true));
            return shader;
        } catch { CallGl("glDeleteShader", shader); throw; }
    }
    private string Log(IObject handle, bool shader) {
        var length = Numbers(0); CallGl(shader ? "glGetShaderiv" : "glGetProgramiv", handle, N(0x8B84), length);
        int size = Math.Max(1, Int(length.Elements[0]));
        using var data = new DataObject(size, this);
        CallGl(shader ? "glGetShaderInfoLog" : "glGetProgramInfoLog", handle, N(size), N(0), data);
        return Marshal.PtrToStringUTF8(data.Handle) ?? "";
    }
}
