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
    IWgpuRenderTarget CreateRenderTarget(int width, int height);
    void Dispatch(string source, IWgpuBuffer buffer, uint workgroups);
}
public interface IWgpuBuffer : IDisposable {
    IWgpuDevice Owner { get; }
    int Length { get; }
    void Write(float[] values);
    float[] Read();
}
public interface IWgpuShader : IDisposable {
    IWgpuDevice Owner { get; }
    bool Disposed { get; }
    void Set(string name, float[] values);
    void SetInt(string name, int value);
}
public interface IWgpuRenderTarget : IDisposable {
    int Width { get; }
    int Height { get; }
    void Clear(Vector4 color);
    void Draw(IWgpuShader shader, float[] vertices);
    byte[] ReadPixels();
}
