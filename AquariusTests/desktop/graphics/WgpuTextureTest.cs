using AquariusLang.Graphics;
using System.Numerics;
using Silk.NET.WebGPU;

namespace AquariusLang.Desktop.Graphics;

public class WgpuTextureGpuTest {
    [WgpuFact] public void CubeMipBindingsAndHdrTargetsPreserveLinearRadiance() {
        using var device=new WgpuDevice();
        // Each cube face is red at mip 0, green at mip 1. Radiance is above 1.
        var values=new List<byte>();
        foreach(int size in new[]{2,1})for(int face=0;face<6;face++)for(int texel=0;texel<size*size;texel++)
            foreach(float component in size==2?new[]{4f,0f,0f,1f}:new[]{0f,2f,0f,1f})values.AddRange(BitConverter.GetBytes((Half)component));
        using var texture=new WgpuTexture(device,new(2,2,"rgba16float","cube",2),values.ToArray());
        const string fragment="@group(2) @binding(0) var probe:texture_cube<f32>;@group(2) @binding(8) var s:sampler;@fragment fn fs_main(i:VertexOutput)->@location(0) vec4<f32>{return textureSampleLevel(probe,s,vec3<f32>(1.0,0.0,0.0),1.0);}";
        using var shader=new WgpuShader(device,WgpuShaders.Vertex,fragment);
        shader.SetTexture("probe",texture);
        using var hdr=new WgpuTarget(device,4,4,TextureFormat.Rgba16float);
        using var display=new WgpuTarget(device,4,4);
        using var tone=new WgpuShader(device,WgpuShaders.Vertex,"@group(2) @binding(0) var colorMap:texture_2d<f32>;@group(2) @binding(8) var s:sampler;@fragment fn fs_main(i:VertexOutput)->@location(0) vec4<f32>{return vec4<f32>(textureSample(colorMap,s,i.uv).rgb*0.25,1.0);}");
        tone.SetTexture("colorMap",hdr.Color);
        var u=new float[WgpuShaderAbi.UniformBytes/4];for(int m=0;m<4;m++)for(int i=0;i<4;i++)u[m*16+i*5]=1;
        float[] triangle={-1,-1,0,0,0,1,0,0,1,1,1,1, 3,-1,0,0,0,1,2,0,1,1,1,1, -1,3,0,0,0,1,0,2,1,1,1,1};
        hdr.Draw(shader,triangle,u,4,4,false,null);
        display.Draw(tone,triangle,u,4,4,false,null);
        var pixel=display.ReadPixels();Assert.InRange(pixel[1],127,128);Assert.Equal(0,pixel[0]);
        Assert.Throws<InvalidOperationException>(()=>hdr.ReadPixels());
        Assert.Throws<ArgumentException>(()=>hdr.Draw(tone,triangle,u,4,4,false,null));
        using var wrong=new WgpuTexture(device,new(1,1,"rgba8unorm","2d",1),new byte[]{255,255,255,255});
        Assert.Throws<ArgumentException>(()=>shader.SetTexture("probe",wrong));
        texture.Dispose();Assert.Throws<InvalidOperationException>(()=>display.Draw(shader,triangle,u,4,4,false,null));
    }
    [WgpuFact] public void RenderTargetTextureViewsShareOwnershipAndLifetime() {
        using var device=new SilkWgpuBackend().CreateDevice();
        using var target=device.CreateRenderTarget(2,2,"rgba16float");
        var texture=target.GetTexture();Assert.Same(device,texture.Owner);
        Assert.Equal("rgba16float",texture.Descriptor.Format);Assert.False(texture.Disposed);
        texture.Dispose();Assert.True(texture.Disposed);Assert.Throws<InvalidOperationException>(()=>target.Clear(Vector4.One));
    }
}
