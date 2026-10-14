using AquariusLang.Graphics;
using System.IO.Compression;

namespace AquariusTests.Graphics;

public class WgpuTextureTest {
    [Fact] public void MipPayloadsIncludeAllCubeLayersAndNonSquareTails() {
        Assert.Equal(1008,new WgpuTextureDescriptor(4,4,"rgba16float","cube",3).ByteLength);
        Assert.Equal(11,new WgpuTextureDescriptor(4,2,"r8unorm","2d",3).ByteLength);
    }
    [Theory]
    [InlineData(0,1,"rgba8unorm","2d",1)]
    [InlineData(8,4,"rgba16float","cube",1)]
    [InlineData(4,4,"rgba32float","2d",1)]
    [InlineData(4,4,"rgba16float","3d",1)]
    [InlineData(4,4,"rgba16float","2d",4)]
    [InlineData(8192,8192,"rgba16float","2d",1)]
    public void InvalidDescriptorsFailBeforeAllocation(int w,int h,string format,string dimension,int levels) =>
        Assert.Throws<ArgumentException>(()=>new WgpuTextureDescriptor(w,h,format,dimension,levels).ByteLength);
    [Fact] public void HalfFloatsAndCompressedLengthAreValidated() {
        var descriptor=new WgpuTextureDescriptor(1,1,"rgba16float","2d",1);
        Assert.Throws<ArgumentException>(()=>descriptor.Validate(new byte[7]));
        Assert.Throws<ArgumentException>(()=>descriptor.Validate(new byte[]{0,0x7c,0,0,0,0,0,0}));
        using var output=new MemoryStream();using(var gzip=new GZipStream(output,CompressionLevel.Fastest,true))gzip.Write(new byte[9]);
        Assert.Throws<ArgumentException>(()=>descriptor.Decode(output.ToArray()));
    }
    [Fact] public void NamedBindingsSupportBothAttributeOrdersAndIgnoreComments() {
        var layout=new WgpuTextureLayout("@binding(7) @group(2) var normal: texture_2d<f32>; @group(2) @binding(0) var probe: texture_cube<f32>; @group(2) @binding(8) var s: sampler; // @group(2) @binding(7) var wrong: texture_2d<f32>;");
        Assert.Equal((7,"2d"),layout.Fields["normal"]);Assert.Equal((0,"cube"),layout.Fields["probe"]);
        Assert.Throws<ArgumentException>(()=>new WgpuTextureLayout("@group(2) @binding(0) var a:texture_2d<f32>;@group(2) @binding(0) var b:texture_2d<f32>;"));
        Assert.Throws<ArgumentException>(()=>new WgpuTextureLayout("@group(2) @binding(9) var a:texture_2d<f32>;"));
        Assert.Throws<ArgumentException>(()=>new WgpuTextureLayout("@group(2) @binding(0) var a:texture_3d<f32>;"));
    }
}
