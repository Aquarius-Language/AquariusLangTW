using System.Numerics;
using AquariusLang.Graphics;

namespace AquariusLang.Desktop.Graphics;

internal sealed class SilkWgpuBackend : IWgpuBackend {
    public string Name => "wgpu-native";
    public IWgpuDevice CreateDevice() => new DeviceAdapter();
    private sealed class DeviceAdapter : IWgpuDevice {
        internal readonly WgpuDevice Native = new();
        public string Backend => Native.Backend;
        public void RequireLive() => Native.RequireLive();
        public void Poll() { Native.Flush(); Native.Poll(); }
        public IWgpuBuffer CreateBuffer(float[] data) => new BufferAdapter(this, data);
        public IWgpuShader CreateShader(string vertex, string fragment) => new ShaderAdapter(this, vertex, fragment);
        public IWgpuRenderTarget CreateRenderTarget(int width, int height) => new TargetAdapter(this, width, height);
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
        public int Length => Native.Length;
        public void Write(float[] values) => Native.Write(values);
        public float[] Read() => Native.Read();
        public void Dispose() => Native.Dispose();
    }
    private sealed class ShaderAdapter : IWgpuShader {
        internal readonly WgpuShader Native;
        public IWgpuDevice Owner { get; }
        public bool Disposed => Native.Disposed;
        internal ShaderAdapter(DeviceAdapter device, string vertex, string fragment) { Owner = device; Native = new(device.Native, vertex, fragment); }
        public void Set(string name, float[] values) => Native.Uniforms.Set(name, values);
        public void SetInt(string name, int value) => Native.Uniforms.Set(name, new float[1], true, value);
        public void Dispose() => Native.Dispose();
    }
    private sealed class TargetAdapter : IWgpuRenderTarget {
        private readonly DeviceAdapter owner;
        private readonly WgpuTarget target;
        internal TargetAdapter(DeviceAdapter owner, int width, int height) { this.owner = owner; target = new(owner.Native, width, height); }
        public int Width => target.Width;
        public int Height => target.Height;
        public void Clear(Vector4 color) { Live(); target.Clear(color); }
        private void Live() { owner.RequireLive(); if (target.Disposed) throw new InvalidOperationException("wgpu render target has been disposed."); }
        public void Draw(IWgpuShader shader, float[] vertices) {
            Live(); if (shader is not ShaderAdapter s || s.Owner != owner || s.Disposed) throw new ArgumentException("Expected a live shader from this wgpu device.");
            var uniforms = new float[WgpuShaderAbi.UniformBytes / 4];
            for (int m = 0; m < 4; m++) for (int i = 0; i < 4; i++) uniforms[m * 16 + i * 5] = 1;
            target.Draw(s.Native, vertices, uniforms, 4, 0, false, null);
        }
        public byte[] ReadPixels() { Live(); return target.ReadPixels(); }
        public void Dispose() => target.Dispose();
    }
}
