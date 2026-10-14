using System;
using System.Numerics;

namespace AquariusLang.Graphics;

/// <summary>Host graphics boundary. No native handles or Silk.NET types cross into core.</summary>
public interface IWgpuBackend {
    string Name { get; }
    IWgpuDevice CreateDevice();
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
}
