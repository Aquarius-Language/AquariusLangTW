using System.Numerics;
using AquariusLang.Graphics;

namespace AquariusLang.Desktop.Graphics;

internal sealed class SilkWgpuBackend : IWgpuBackend {
    private readonly Func<int, int, string, DesktopWgpuWindow>? windowFactory;
    internal SilkWgpuBackend(Func<int, int, string, DesktopWgpuWindow>? windowFactory = null) { this.windowFactory = windowFactory; }
    public string Name => "wgpu-native";
    public IWgpuDevice CreateDevice() => new DeviceAdapter(null);
    public IWgpuWindow CreateWindow(int width, int height, string title) =>
        windowFactory?.Invoke(width, height, title) ?? throw new NotSupportedException("This host cannot create wgpu windows.");
    public IWgpuDevice CreateDevice(IWgpuWindow window) {
        if (window is not DesktopWgpuWindow w || w.Disposed) throw new ArgumentException("Expected a live window from this host.");
        if (w.Device != null && !w.Device.Disposed) throw new InvalidOperationException("This window already has a wgpu device; dispose it first.");
        var device = new DeviceAdapter(w); w.Device = device.Native; return device;
    }
    private sealed class DeviceAdapter : IWgpuDevice {
        internal readonly WgpuDevice Native;
        private readonly DesktopWgpuWindow? window;
        internal DeviceAdapter(DesktopWgpuWindow? window) { this.window = window; Native = window == null ? new() : new(window.Handle); }
        public string Backend => Native.Backend;
        public IWgpuBuffer CreateBuffer(int length) => new BufferAdapter(this, length);
        public IWgpuVertexBuffer CreateVertexBuffer(float[] values) => new VertexBufferAdapter(this, values);
        public void Present(IWgpuRenderTarget target) {
            if (window == null) throw new InvalidOperationException("This wgpu device has no window surface; create it with WGPU.CreateDevice(window).");
            if (target is not TargetAdapter t || t.Owner != this) throw new ArgumentException("Expected a render target from this wgpu device.");
            if (window.Disposed) throw new InvalidOperationException("The window has been disposed.");
            window.FramebufferSize(out int w, out int h); t.Present(w, h);
        }
        public void RequireLive() => Native.RequireLive();
        public void Poll() { Native.Flush(); Native.Poll(); }
        public IWgpuBuffer CreateBuffer(float[] data) => new BufferAdapter(this, data);
        public IWgpuShader CreateShader(string vertex, string fragment) => new ShaderAdapter(this, vertex, fragment);
        public IWgpuTexture CreateTexture(WgpuTextureDescriptor descriptor, byte[] bytes) => new TextureAdapter(this,descriptor,bytes);
        public IWgpuRenderTarget CreateRenderTarget(int width, int height, string format = "rgba8unorm") => new TargetAdapter(this, width, height, format);
        public void Dispatch(string source, IWgpuBuffer buffer, uint groups) {
            if (buffer is not BufferAdapter b || b.Owner != this) throw new ArgumentException("Buffer belongs to another wgpu device.");
            Native.Dispatch(source, b.Native, groups);
        }
        public void Dispose() => Native.Dispose();
    }
    private sealed class BufferAdapter : IWgpuBuffer {
        internal readonly WgpuBuffer Native;
        public IWgpuDevice Owner { get; }
        internal BufferAdapter(DeviceAdapter device, float[] values) { Owner = device; Native = new(device.Native, values); }
        internal BufferAdapter(DeviceAdapter device, int length) { Owner = device; Native = new(device.Native, WgpuLimits.BufferLength(length)); }
        public int Length => Native.Length;
        public void Write(float[] values) => Native.Write(values);
        public float[] Read() => Native.Read();
        public void WriteRange(int offset, float[] values) => Native.WriteRange(offset,values);
        public float[] ReadRange(int offset, int count) => Native.ReadRange(offset,count);
        public void Dispose() => Native.Dispose();
    }
    private sealed class ShaderAdapter : IWgpuShader {
        internal readonly WgpuShader Native;
        public IWgpuDevice Owner { get; }
        public bool Disposed => Native.Disposed;
        internal ShaderAdapter(DeviceAdapter device, string vertex, string fragment) { Owner = device; Native = new(device.Native, vertex, fragment); }
        public void Set(string name, float[] values) => Native.Uniforms.Set(name, values);
        public void SetInt(string name, int value) => Native.Uniforms.Set(name, new float[1], true, value);
        public void SetTexture(string name, IWgpuTexture texture) {
            if(texture is not TextureAdapter t||t.Owner!=Owner||t.Disposed)throw new ArgumentException("Expected a live texture from this device.");
            Native.SetTexture(name,t.Native);
        }
        public void SetBuffer(string name, IWgpuBuffer buffer) {
            if(buffer is not BufferAdapter b||b.Owner!=Owner)throw new ArgumentException("Expected a live storage buffer from this device.");
            Native.SetBuffer(name,b.Native);
        }
        public void Dispose() => Native.Dispose();
    }
    private sealed class TextureAdapter : IWgpuTexture {
        internal readonly WgpuTexture Native;
        public IWgpuDevice Owner { get; }
        public WgpuTextureDescriptor Descriptor { get; }
        public bool Disposed => Native.Disposed;
        internal TextureAdapter(DeviceAdapter owner,WgpuTextureDescriptor descriptor,byte[] bytes) {
            Owner=owner;Descriptor=descriptor;Native=new(owner.Native,descriptor,bytes);
        }
        private readonly Action? releaseTarget;
        internal TextureAdapter(DeviceAdapter owner,WgpuTarget target) {Owner=owner;Native=target.Color;Descriptor=Native.Descriptor!;releaseTarget=target.Dispose;}
        public void Dispose(){if(releaseTarget!=null)releaseTarget();else Native.Dispose();}
    }
    private sealed class VertexBufferAdapter : IWgpuVertexBuffer {
        internal readonly WgpuVertexBuffer Native;
        public IWgpuDevice Owner { get; }
        internal VertexBufferAdapter(DeviceAdapter device, float[] values) { Owner = device; Native = new(device.Native, values); }
        public int Length => Native.Length;
        public bool Disposed => Native.Disposed;
        public void Write(float[] values) => Native.Write(values);
        public void WriteRange(int offset, float[] values) => Native.WriteRange(offset, values);
        public void Dispose() => Native.Dispose();
    }
    private sealed class TargetAdapter : IWgpuRenderTarget {
        private readonly DeviceAdapter owner;
        internal DeviceAdapter Owner => owner;
        internal void Present(int width, int height) { Live(); owner.Native.Present(target, width, height); }
        public void SetState(WgpuDrawState state) { Live(); target.State = state.Validate(); }
        private WgpuShader LiveShader(IWgpuShader shader) =>
            shader is ShaderAdapter s && s.Owner == owner && !s.Disposed ? s.Native : throw new ArgumentException("Expected a live shader from this wgpu device.");
        public void DrawBuffer(IWgpuShader shader, IWgpuVertexBuffer buffer, int first, int count) {
            Live(); var s = LiveShader(shader);
            if (buffer is not VertexBufferAdapter b || b.Owner != owner || b.Disposed) throw new ArgumentException("Expected a live vertex buffer from this wgpu device.");
            WgpuLimits.VertexRange(b.Length / 12, first, count); if (count > 0) target.DrawRetained(s, b.Native, first, count);
        }
        public void DrawProcedural(IWgpuShader shader, int first, int count) {
            Live(); var s = LiveShader(shader); WgpuLimits.ProceduralRange(first, count); if (count > 0) target.DrawRetained(s, null, first, count);
        }
        private readonly WgpuTarget target;
        internal TargetAdapter(DeviceAdapter owner, int width, int height, string format) { this.owner = owner; target = new(owner.Native, width, height, WgpuTarget.Format(format)); }
        public int Width => target.Width;
        public int Height => target.Height;
        public IWgpuTexture GetTexture() {Live();return new TextureAdapter(owner,target);}
        public void Clear(Vector4 color) { Live(); target.Clear(color); }
        private void Live() { owner.RequireLive(); if (target.Disposed) throw new InvalidOperationException("wgpu render target has been disposed."); }
        public void Draw(IWgpuShader shader, float[] vertices) {
            Live(); if (shader is not ShaderAdapter s || s.Owner != owner || s.Disposed) throw new ArgumentException("Expected a live shader from this wgpu device.");
            var uniforms = new float[WgpuShaderAbi.UniformBytes / 4];
            for (int m = 0; m < 4; m++) for (int i = 0; i < 4; i++) uniforms[m * 16 + i * 5] = 1;
            var state = target.State;
            target.Draw(s.Native, vertices, uniforms, 4, state.BlendCode, false, null, null, 0, state.DepthCode, state.CullCode);
        }
        public byte[] ReadPixels() { Live(); return target.ReadPixels(); }
        public void Dispose() => target.Dispose();
    }
}
