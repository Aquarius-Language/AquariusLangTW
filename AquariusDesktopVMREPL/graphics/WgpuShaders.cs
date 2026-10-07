using System.Numerics;
using System.Text.RegularExpressions;

namespace AquariusREPL.Graphics;

// All Processing pipelines share this explicitly aligned WebGPU uniform ABI.
internal static class WgpuShaders {
    internal const int UniformBytes = 64 * 4 + 6 * 16 + 8 * 6 * 16;
    internal const string Header = """
struct Light {
    colorKind: vec4<f32>, position: vec4<f32>, direction: vec4<f32>,
    falloff: vec4<f32>, specular: vec4<f32>, spot: vec4<f32>,
};
struct ProcessingUniforms {
    model: mat4x4<f32>, view: mat4x4<f32>, projection: mat4x4<f32>, normalMatrix: mat4x4<f32>,
    flags: vec4<f32>, ambient: vec4<f32>, specular: vec4<f32>, emissive: vec4<f32>,
    material: vec4<f32>, counts: vec4<f32>, lights: array<Light, 8>,
};
@group(0) @binding(0) var<uniform> processing: ProcessingUniforms;
@group(0) @binding(1) var surface: texture_2d<f32>;
@group(0) @binding(2) var surfaceSampler: sampler;
struct VertexInput {
    @location(0) position: vec3<f32>, @location(1) normal: vec3<f32>,
    @location(2) texcoord: vec2<f32>, @location(3) color: vec4<f32>,
};
struct VertexOutput {
    @builtin(position) position: vec4<f32>, @location(0) vertexColor: vec4<f32>,
    @location(1) uv: vec2<f32>, @location(2) normal: vec3<f32>, @location(3) eyePosition: vec3<f32>,
};
""";
    internal const string Vertex = """
@vertex fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    let eye = processing.view * processing.model * vec4<f32>(input.position, 1.0);
    var clip = processing.projection * eye;
    // Processing's public matrices retain the OpenGL -1..1 depth convention.
    clip.z = (clip.z + clip.w) * 0.5;
    output.position = clip;
    output.eyePosition = eye.xyz;
    output.normal = (processing.normalMatrix * vec4<f32>(input.normal, 0.0)).xyz;
    output.uv = input.texcoord; output.vertexColor = input.color;
    return output;
}
""";
    internal const string Fragment = """
@fragment fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    var c = input.vertexColor;
    if (processing.flags.x > 0.5) {
        var tex = textureSample(surface, surfaceSampler, input.uv);
        if (processing.flags.z > 0.5 && tex.a > 0.0) { tex = vec4<f32>(tex.rgb / tex.a, tex.a); }
        c *= tex;
    }
    if (processing.flags.y > 0.5) {
        let n = normalize(input.normal); let v = normalize(-input.eyePosition);
        var diffuseLight = vec3<f32>(0.0); var specularLight = vec3<f32>(0.0);
        for (var i = 0u; i < u32(processing.counts.x); i++) {
            let light = processing.lights[i]; let delta = light.position.xyz - input.eyePosition;
            var l = normalize(-light.direction.xyz); var attenuation = 1.0;
            if (light.colorKind.w > 0.5) {
                l = normalize(delta); let d = length(delta);
                attenuation = 1.0 / max(dot(light.falloff.xyz, vec3<f32>(1.0, d, d*d)), 0.0001);
            }
            if (light.colorKind.w > 1.5) {
                let spot = dot(normalize(light.direction.xyz), -l);
                attenuation *= select(0.0, pow(max(spot, 0.0), light.spot.y), spot >= light.spot.x);
            }
            let lambert = max(dot(n, l), 0.0);
            diffuseLight += light.colorKind.xyz * lambert * attenuation;
            var highlight = 0.0;
            if (lambert > 0.0) { highlight = pow(max(dot(n, normalize(l+v)), 0.0), processing.material.w); }
            specularLight += light.specular.xyz * highlight * attenuation;
        }
        c = vec4<f32>(c.rgb * (processing.ambient.xyz * processing.material.xyz + diffuseLight)
            + processing.specular.xyz * specularLight + processing.emissive.xyz, c.a);
    }
    if (processing.flags.w > 0.5) { c = vec4<f32>(c.rgb*c.a, c.a); }
    return c;
}
""";
    internal const string Blit = """
@group(0) @binding(0) var image: texture_2d<f32>;
@group(0) @binding(1) var imageSampler: sampler;
struct Output { @builtin(position) position: vec4<f32>, @location(0) uv: vec2<f32> };
@vertex fn vs_main(@builtin(vertex_index) index: u32) -> Output {
    let x = f32((index << 1u) & 2u); let y = f32(index & 2u);
    var o: Output; o.position = vec4<f32>(x*2.0-1.0, 1.0-y*2.0, 0.0, 1.0); o.uv = vec2<f32>(x,y); return o;
}
@fragment fn fs_main(i: Output) -> @location(0) vec4<f32> { return textureSample(image, imageSampler, i.uv); }
""";
    internal static float[] Uniforms(ProcessingCanvas c, bool textured, bool premultiplied, bool lit) {
        var data = new float[UniformBytes/4]; int offset=0;
        void Matrix(Matrix4x4 m) { foreach(float v in new[]{m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44}) data[offset++]=v; }
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

// Custom uniforms occupy @group(1) @binding(0), in a struct named UserUniforms.
// Restrict the supported field types so host uploads have a provable WGSL layout.
internal sealed class WgpuUniformLayout {
    internal readonly Dictionary<string,(int Offset,int Count,string Type)> Fields=new();
    internal readonly byte[] Bytes=new byte[1024];
    internal WgpuUniformLayout(string source) {
        source=Regex.Replace(source,@"/\*[\s\S]*?\*/|//[^\r\n]*", "");
        var declaration=Regex.Match(source,@"struct\s+UserUniforms\s*\{([^}]*)\}");
        if(!declaration.Success)return;
        int offset=0;
        foreach(string field in declaration.Groups[1].Value.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)) {
            var match=Regex.Match(field,@"^\s*(\w+)\s*:\s*(f32|i32|u32|vec[234]<f32>|mat4x4<f32>)\s*$");
            if(!match.Success)throw new ArgumentException("UserUniforms supports f32, i32, u32, vec2/3/4<f32> and mat4x4<f32> fields.");
            string type=match.Groups[2].Value;int count=type.StartsWith("mat")?16:type.StartsWith("vec")?type[3]-'0':1;
            int alignment=count==1?4:count==2?8:16;offset=(offset+alignment-1)/alignment*alignment;
            if(offset+count*4>Bytes.Length||!Fields.TryAdd(match.Groups[1].Value,(offset,count,type)))throw new ArgumentException("Duplicate or oversized UserUniforms field.");
            offset+=count*4;
        }
    }
    internal void Set(string name,float[] values,bool integer=false,int intValue=0) {
        if(!Fields.TryGetValue(name,out var field))throw new ArgumentException($"Unknown UserUniforms field: {name}");
        if(values.Length!=field.Count)throw new ArgumentException($"Uniform {name} needs {field.Count} values.");
        bool integral=field.Type is "i32" or "u32";
        if(integer!=integral)throw new ArgumentException($"Uniform {name} requires {(integral?"setInt":"set")}().");
        if(integer) {
            if(field.Type=="u32"&&intValue<0)throw new ArgumentException("u32 uniform must be nonnegative.");
            BitConverter.GetBytes(intValue).CopyTo(Bytes,field.Offset);
        } else for(int i=0;i<values.Length;i++)BitConverter.GetBytes(values[i]).CopyTo(Bytes,field.Offset+i*4);
    }
}
