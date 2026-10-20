using System;
using System.Numerics;

namespace AquariusLang.Graphics;

/// <summary>Host graphics boundary. No native handles or Silk.NET types cross into core.</summary>
public interface IWgpuBackend {
    string Name { get; }
    IWgpuDevice CreateDevice();
    /// <summary>Opens a host window whose surface a device created by <see cref="CreateDevice(IWgpuWindow)"/> presents to.</summary>
    IWgpuWindow CreateWindow(int width, int height, string title) =>
        throw new NotSupportedException("This host cannot create wgpu windows / 此平台不支援 wgpu 視窗。");
    IWgpuDevice CreateDevice(IWgpuWindow window) =>
        throw new NotSupportedException("This host cannot present wgpu devices to windows / 此平台不支援 wgpu 視窗呈現。");
}
public interface IWgpuDevice : IDisposable {
    string Backend { get; }
    void RequireLive();
    void Poll();
    IWgpuBuffer CreateBuffer(float[] values);
    IWgpuShader CreateShader(string vertex, string fragment);
    IWgpuTexture CreateTexture(WgpuTextureDescriptor descriptor, byte[] bytes);
    IWgpuRenderTarget CreateRenderTarget(int width, int height, string format = "rgba8unorm");
    void Dispatch(string source, IWgpuBuffer buffer, uint workgroups);
    /// <summary>Zero-initialized storage buffer, avoiding a large zero array crossing the language boundary.</summary>
    IWgpuBuffer CreateBuffer(int length) => CreateBuffer(new float[WgpuLimits.BufferLength(length)]);
    /// <summary>GPU-resident vertices in the shared 12-float layout (position, normal, UV, RGBA).</summary>
    IWgpuVertexBuffer CreateVertexBuffer(float[] values) =>
        throw new NotSupportedException("This host does not support retained vertex buffers / 此平台不支援常駐頂點緩衝區。");
    /// <summary>Scales a render target onto the device's window surface and presents it.</summary>
    void Present(IWgpuRenderTarget target) =>
        throw new InvalidOperationException("This wgpu device has no window surface / 此 wgpu 裝置沒有視窗表面。");
}
public interface IWgpuBuffer : IDisposable {
    IWgpuDevice Owner { get; }
    int Length { get; }
    void Write(float[] values);
    float[] Read();
    void WriteRange(int offset, float[] values) {
        WgpuBufferRange.Validate(Length, offset, values.Length);
        var data = Read(); values.CopyTo(data, offset); Write(data);
    }
    float[] ReadRange(int offset, int count) {
        WgpuBufferRange.Validate(Length, offset, count);
        return Read().AsSpan(offset, count).ToArray();
    }
}
public static class WgpuBufferRange {
    public static void Validate(int length, int offset, int count) {
        if (offset < 0 || count < 1 || count > length || offset > length - count)
            throw new ArgumentException("Invalid buffer range / 緩衝區範圍無效。");
    }
}
public interface IWgpuShader : IDisposable {
    IWgpuDevice Owner { get; }
    bool Disposed { get; }
    void Set(string name, float[] values);
    void SetInt(string name, int value);
    void SetTexture(string name, IWgpuTexture texture);
    /// <summary>Binds a storage buffer declared as <c>@group(2) @binding(9..15) var&lt;storage, read&gt;</c>.</summary>
    void SetBuffer(string name, IWgpuBuffer buffer) =>
        throw new NotSupportedException("This host does not support shader storage buffers / 此平台不支援著色器儲存緩衝區。");
}
public interface IWgpuVertexBuffer : IDisposable {
    IWgpuDevice Owner { get; }
    /// <summary>Length in floats; always a multiple of 12.</summary>
    int Length { get; }
    bool Disposed { get; }
    void Write(float[] values);
    void WriteRange(int offset, float[] values);
}
public interface IWgpuTexture : IDisposable {
    IWgpuDevice Owner { get; }
    WgpuTextureDescriptor Descriptor { get; }
    bool Disposed { get; }
}
public interface IWgpuRenderTarget : IDisposable {
    int Width { get; }
    int Height { get; }
    IWgpuTexture GetTexture();
    void Clear(Vector4 color);
    void Draw(IWgpuShader shader, float[] vertices);
    byte[] ReadPixels();
    /// <summary>Pipeline state for later draws on this target.</summary>
    void SetState(WgpuDrawState state) => throw new NotSupportedException("This host does not support draw state / 此平台不支援繪製狀態。");
    /// <summary>Draws <paramref name="count"/> vertices of a retained buffer, starting at vertex <paramref name="first"/>.</summary>
    void DrawBuffer(IWgpuShader shader, IWgpuVertexBuffer buffer, int first, int count) =>
        throw new NotSupportedException("This host does not support retained vertex buffers / 此平台不支援常駐頂點緩衝區。");
    /// <summary>Draws without vertex attributes; the shader reads <c>@builtin(vertex_index)</c> and storage buffers.</summary>
    void DrawProcedural(IWgpuShader shader, int first, int count) =>
        throw new NotSupportedException("This host does not support procedural draws / 此平台不支援程序化繪製。");
}

public static class WgpuLimits {
    public const int MaxFloats = 16 * 1024 * 1024;
    public static int BufferLength(int length) =>
        length is >= 1 and <= MaxFloats ? length : throw new ArgumentException("Buffer length must be 1..16777216 floats / 緩衝區長度必須為 1..16777216。");
    public static void VertexRange(int vertices, int first, int count) {
        if (first < 0 || count < 0 || first > vertices - count)
            throw new ArgumentException("Vertex range is outside the buffer / 頂點範圍超出緩衝區。");
    }
    public static void ProceduralRange(int first, int count) {
        if (first < 0 || count < 0 || count > 48 * 1024 * 1024 || first > int.MaxValue - count)
            throw new ArgumentException("Procedural vertex range must be nonnegative and at most 50331648 vertices / 程序化頂點範圍無效。");
    }
}

/// <summary>Per-target pipeline state. Depth: none, less (test and write), read (test only).
/// Blend: alpha, add, multiply, screen, replace, premultiplied. Cull: none, back, front (counter-clockwise front faces).</summary>
public sealed record WgpuDrawState(string Depth = "none", string Blend = "alpha", string Cull = "none") {
    public static readonly WgpuDrawState Default = new();
    public static readonly string[] DepthModes = { "none", "less", "read" };
    public static readonly string[] BlendModes = { "alpha", "add", "multiply", "screen", "replace", "premultiplied" };
    public static readonly string[] CullModes = { "none", "back", "front" };
    public int DepthCode => Code(DepthModes, Depth, "depth");
    /// <summary>Matches the existing Processing blend numbering; premultiplied is 5.</summary>
    public int BlendCode => Code(BlendModes, Blend, "blend");
    public int CullCode => Code(CullModes, Cull, "cull");
    public WgpuDrawState Validate() { _ = DepthCode; _ = BlendCode; _ = CullCode; return this; }
    private static int Code(string[] values, string value, string name) {
        int index = Array.IndexOf(values, value);
        return index >= 0 ? index : throw new ArgumentException($"Unknown {name} mode '{value}'; expected {string.Join(", ", values)}.");
    }
}

/// <summary>Snapshot taken by <see cref="IWgpuWindow.Poll"/>. Sizes are logical/physical pixels; deltas accumulate between polls.</summary>
public sealed record WgpuWindowState(int Width, int Height, int PixelWidth, int PixelHeight,
    double MouseX, double MouseY, double MouseDeltaX, double MouseDeltaY, double Wheel,
    bool Focused, bool PointerLocked, bool ShouldClose, bool Resized);

/// <summary>A host window and its input. Key codes use GLFW numbering on every host (A=65, Space=32, Escape=256, Left Shift=340).
/// Mouse buttons: 0 left, 1 right, 2 middle. Pressed/Released report transitions observed since the previous poll.</summary>
public interface IWgpuWindow : IDisposable {
    WgpuWindowState State { get; }
    bool Disposed { get; }
    void Poll();
    bool KeyDown(int key);
    bool KeyPressed(int key);
    bool KeyReleased(int key);
    bool MouseDown(int button);
    bool MousePressed(int button);
    bool MouseReleased(int button);
    void SetPointerLock(bool locked);
    void SetTitle(string title);
    void Close();
}
