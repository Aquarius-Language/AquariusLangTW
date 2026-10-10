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
        var data = new float[UniformBytes/4]; int offset=0;
        void Matrix(Matrix4x4 m) {
            data[offset++]=m.M11;data[offset++]=m.M12;data[offset++]=m.M13;data[offset++]=m.M14;
            data[offset++]=m.M21;data[offset++]=m.M22;data[offset++]=m.M23;data[offset++]=m.M24;
            data[offset++]=m.M31;data[offset++]=m.M32;data[offset++]=m.M33;data[offset++]=m.M34;
            data[offset++]=m.M41;data[offset++]=m.M42;data[offset++]=m.M43;data[offset++]=m.M44;
        }
        void Vec(Vector3 v,float w=0) { data[offset++]=v.X;data[offset++]=v.Y;data[offset++]=v.Z;data[offset++]=w; }
        Matrix(c.Model);Matrix(c.View);Matrix(c.Projection);
        Matrix4x4.Invert(c.Model*c.View,out var inverse); Matrix(Matrix4x4.Transpose(inverse));
        Vec(new(textured?1:0,c.Style.Lit&&lit?1:0,premultiplied?1:0),c.BlendMode==4?1:0);
        Vec(c.Style.Ambient);Vec(c.Style.Specular);Vec(c.Style.Emissive);Vec(c.Style.MaterialAmbient,c.Style.Shininess);
        Vec(new(c.Style.Lights.Count,0,0));
        foreach(var light in c.Style.Lights) {
            Vec(light.Color,light.Kind);Vec(light.Position);Vec(light.Direction);Vec(light.Falloff);Vec(light.Specular);Vec(new(light.Cutoff,light.Concentration,0));
        }
        return data;
    }
}
