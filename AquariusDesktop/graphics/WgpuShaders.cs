using System.Numerics;
using System.Text.RegularExpressions;

namespace AquariusLang.Desktop.Graphics;

// All Processing pipelines share this explicitly aligned WebGPU uniform ABI.
internal static class WgpuShaders {
    internal const int UniformBytes = AquariusLang.Graphics.WgpuShaderAbi.UniformBytes;
    internal const string Header = AquariusLang.Graphics.WgpuShaderAbi.Header;
    internal const string Vertex = AquariusLang.Graphics.WgpuShaderAbi.Vertex;
    internal const string Fragment = AquariusLang.Graphics.WgpuShaderAbi.Fragment;
    internal const string Blit = AquariusLang.Graphics.WgpuShaderAbi.Blit;
    internal static float[] Uniforms(ProcessingCanvas c, bool textured, bool premultiplied, bool lit) {
        var data = new float[UniformBytes/4]; Uniforms(data, c, textured, premultiplied, lit); return data;
    }
    /// <summary>Writes the uniform block into a caller-owned buffer of UniformBytes/4 floats (cleared first).</summary>
    internal static void Uniforms(Span<float> data, ProcessingCanvas c, bool textured, bool premultiplied, bool lit) {
        data.Clear(); int offset=0;
        static void Matrix(Span<float> data, ref int offset, Matrix4x4 m) {
            data[offset++]=m.M11;data[offset++]=m.M12;data[offset++]=m.M13;data[offset++]=m.M14;
            data[offset++]=m.M21;data[offset++]=m.M22;data[offset++]=m.M23;data[offset++]=m.M24;
            data[offset++]=m.M31;data[offset++]=m.M32;data[offset++]=m.M33;data[offset++]=m.M34;
            data[offset++]=m.M41;data[offset++]=m.M42;data[offset++]=m.M43;data[offset++]=m.M44;
        }
        static void Vec(Span<float> data, ref int offset, Vector3 v,float w=0) { data[offset++]=v.X;data[offset++]=v.Y;data[offset++]=v.Z;data[offset++]=w; }
        Matrix(data,ref offset,c.Model);Matrix(data,ref offset,c.View);Matrix(data,ref offset,c.Projection);
        Matrix4x4.Invert(c.Model*c.View,out var inverse); Matrix(data,ref offset,Matrix4x4.Transpose(inverse));
        Vec(data,ref offset,new(textured?1:0,c.Style.Lit&&lit?1:0,premultiplied?1:0),c.BlendMode==4?1:0);
        Vec(data,ref offset,c.Style.Ambient);Vec(data,ref offset,c.Style.Specular);Vec(data,ref offset,c.Style.Emissive);Vec(data,ref offset,c.Style.MaterialAmbient,c.Style.Shininess);
        Vec(data,ref offset,new(c.Style.Lights.Count,0,0));
        foreach(var light in c.Style.Lights) {
            Vec(data,ref offset,light.Color,light.Kind);Vec(data,ref offset,light.Position);Vec(data,ref offset,light.Direction);Vec(data,ref offset,light.Falloff);Vec(data,ref offset,light.Specular);Vec(data,ref offset,new(light.Cutoff,light.Concentration,0));
        }
    }
}
