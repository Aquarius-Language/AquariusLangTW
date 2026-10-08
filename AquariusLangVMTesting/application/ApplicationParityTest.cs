using System.Diagnostics;
using System.Text.Json;
using AquariusLang.Application;
using AquariusREPL.Application;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Tiff;
using PortableFormat = AquariusLang.Application.ImageFormat;

namespace AquariusLangVMTesting.Application;

public class ApplicationParityTest {
    private static string Root { get { var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir!=null&&!File.Exists(Path.Combine(dir.FullName,"AquariusLang.sln")))dir=dir.Parent;return dir!.FullName; } }
    [Fact] public async Task NativeDocumentsContainersCompressionAndPixelsAreReadableInBrowserProvider() {
        var value=new {name="繁😀",items=new object?[]{1,true,null}};var document=DocumentSerialization.Binary(value,2);var image=new PixelImage(2,1,new byte[]{255,0,0,255,0,255,0,128});
        var entries=new Dictionary<string,byte[]>{["metadata.aqd"]=document,["images/繁.bin"]=new byte[]{1,2,3}};
        var compressed=new[]{CompressionFormat.Gzip,CompressionFormat.Zlib,CompressionFormat.Deflate}.ToDictionary(f=>f.ToString(),f=>DocumentCompression.Compress(new byte[]{1,2,3},f).Select(b=>(int)b).ToArray());
        string file=Path.Combine(Path.GetTempPath(),"aquarius-application-parity-"+Guid.NewGuid().ToString("N")+".json");
        try {
            var encoded=await new DesktopImages().Encode(image,PortableFormat.Tiff,new(),new());
            File.WriteAllText(file,JsonSerializer.Serialize(new{document=document.Select(b=>(int)b).ToArray(),archive=DocumentArchive.Create(entries).Select(b=>(int)b).ToArray(),image=encoded.Select(b=>(int)b).ToArray(),compressed}));
            var start=new ProcessStartInfo("node"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8};start.ArgumentList.Add(Path.Combine(Root,"AquariusWebCompiler","tests","application-parity.mjs"));start.ArgumentList.Add(file);
            using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();Assert.True(process.WaitForExit(30000),"Application parity test timed out");Assert.True(process.ExitCode==0,await errors);
            using var result=JsonDocument.Parse(await output);var json=result.RootElement;Assert.Equal(2,json.GetProperty("document").GetProperty("schema").GetInt32());Assert.Equal("繁😀",json.GetProperty("document").GetProperty("value").GetProperty("name").GetString());
            Assert.Equal(image.Pixels.ToArray(),json.GetProperty("pixels").EnumerateArray().Select(p=>(byte)p.GetInt32()).ToArray());Assert.Equal("../b/c",json.GetProperty("path").GetString());Assert.Equal(1,json.GetProperty("text").GetProperty("composition").GetProperty("cursorScalar").GetInt32());
            foreach(var f in compressed.Keys)Assert.Equal(new[]{1,2,3},json.GetProperty("compressed").GetProperty(f).EnumerateArray().Select(p=>p.GetInt32()).ToArray());
        } finally { File.Delete(file); }
    }
    [Theory] [InlineData(PortableFormat.Gif)] [InlineData(PortableFormat.Tiff)]
    public async Task MultipleFramesAndPagesRequireValidSelection(PortableFormat format) {
        using var document=new Image<Rgba32>(1,1);document[0,0]=new Rgba32(255,0,0,255);document.Frames.AddFrame(new Rgba32[]{new(0,0,255,255)});using var output=new MemoryStream();
        if(format==PortableFormat.Gif)document.Save(output,new GifEncoder());else document.Save(output,new TiffEncoder());
        var codec=new DesktopImages();var bytes=output.ToArray();Assert.Equal(2,(await codec.Inspect(bytes,new())).Frames);var second=await codec.Decode(bytes,1,new());Assert.Equal(new byte[]{0,0,255,255},second.Pixels.ToArray());
        await Assert.ThrowsAnyAsync<ArgumentException>(async()=>await codec.Decode(bytes,-1,new()));
    }
    [Theory] [InlineData(PortableFormat.Png)] [InlineData(PortableFormat.Jpeg)] [InlineData(PortableFormat.Tiff)]
    public async Task MetadataOrientationAndResolutionRemainExplicit(PortableFormat format) {
        var image=new PixelImage(1,1,new byte[]{1,2,3,255},metadata:new(6,144,96));var codec=new DesktopImages();var info=await codec.Inspect(await codec.Encode(image,format,new(),new()),new());
        Assert.Equal(6,info.Metadata.Orientation);Assert.InRange(info.Metadata.DpiX!.Value,143.9,144.1);
    }
}
