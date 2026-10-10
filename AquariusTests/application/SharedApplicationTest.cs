using System.IO.Compression;
using System.Text;
using AquariusLang.Application;
using AquariusLang.Desktop.Application;
using AquariusLang.Object;
using AquariusLang.Compiler;
using AquariusLang.Desktop;

namespace AquariusTests.Application;

public class SharedApplicationTest {
    [Theory]
    [InlineData("a/../b//./c", "b/c")]
    [InlineData("../a/../../b", "../../b")]
    [InlineData("C:\\a\\..\\b", "C:/b")]
    [InlineData("//server/share/../../a", "//server/share/a")]
    public void PathsAreLexicalAndPortable(string input, string expected) => Assert.Equal(expected, PortablePath.Normalize(input));
    [Fact] public void PathOperationsRetainRootsAndRelativeSemantics() {
        Assert.Equal("a/b", PortablePath.Join("a", "b")); Assert.Equal("/b", PortablePath.Join("a", "/b"));
        Assert.Equal("../b/c", PortablePath.Relative("/a", "/b/c")); Assert.Equal("C:/", PortablePath.Parent("C:/file"));
        Assert.Equal("", PortablePath.Extension(".hidden")); Assert.Throws<ArgumentException>(() => PortablePath.Normalize("C:file"));
    }
    [Fact] public void EditorSeparatesCompositionAndUsesExtendedGraphemes() {
        var editor = new TextEditor("e\u0301👨‍👩‍👧‍👦繁"); Assert.Equal(3, editor.Length); editor.Select(3,3); editor.Backspace(); Assert.Equal("e\u0301👨‍👩‍👧‍👦", editor.Text);
        editor.SetComposition("中文",1); editor.Backspace(); Assert.Equal(2,editor.Length); Assert.Equal("中文", editor.Composition!.Text); editor.Commit("字"); Assert.Equal(3,editor.Length);
        Assert.Equal(2,editor.Convert(1,TextPositionUnit.Grapheme,TextPositionUnit.Utf16)); Assert.Throws<ArgumentException>(()=>editor.Convert(1,TextPositionUnit.Utf16,TextPositionUnit.Grapheme));
        editor.Select(0,2); editor.Insert("甲"); Assert.Equal("甲字",editor.Text); editor.Move(-1,"word"); Assert.Equal(0,editor.Selection.Caret);
    }
    [Theory] [InlineData("utf-8")] [InlineData("utf-16le")] [InlineData("utf-16be")]
    public void TextEncodingsRoundTripWithoutBom(string encoding) { var codec=TextEncoding.Get(encoding); Assert.Equal("繁😀",codec.GetString(codec.GetBytes("繁😀"))); Assert.Throws<EncoderFallbackException>(()=>codec.GetBytes("\ud800")); }
    [Fact] public void DocumentsRejectCorruptionAndUnknownVersions() {
        var bytes=DocumentSerialization.Binary(new { text="繁😀", values=new[]{1,2,3} },2); var parsed=DocumentSerialization.ReadBinary(bytes); Assert.Equal(2u,parsed.Schema); Assert.Equal("繁😀",parsed.Value.GetProperty("text").GetString());
        bytes[^1]^=1; Assert.Throws<ApplicationFailure>(()=>DocumentSerialization.ReadBinary(bytes)); Assert.Throws<ApplicationFailure>(()=>DocumentSerialization.ReadBinary("AQD9"u8.ToArray()));
        Assert.Throws<ArgumentException>(()=>DocumentSerialization.Json(double.NaN));
    }
    [Theory] [InlineData(CompressionFormat.Gzip)] [InlineData(CompressionFormat.Zlib)] [InlineData(CompressionFormat.Deflate)] [InlineData(CompressionFormat.Brotli)]
    public async Task CompressionRoundTripsAndStopsExpansion(CompressionFormat format) {
        var original=new byte[10000]; var compressed=DocumentCompression.Compress(original,format); Assert.Equal(original,await DocumentCompression.Decompress(compressed,format));
        await Assert.ThrowsAsync<ApplicationFailure>(async()=>await DocumentCompression.Decompress(compressed,format,new(MaxBytes:1000)));
        using var source=new CancellationTokenSource();source.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await DocumentCompression.Decompress(compressed,format,cancellation:source.Token));
    }
    [Fact] public async Task ArchiveValidatesNamesSizesAndChecksums() {
        var entries=new Dictionary<string,byte[]> { ["metadata.aqd"]=DocumentSerialization.Binary(new {schema="example"},1),["images/繁.bin"]=new byte[]{1,2,3} };
        var bytes=DocumentArchive.Create(entries); var output=await DocumentArchive.Read(bytes); Assert.Equal(entries["images/繁.bin"],output["images/繁.bin"]);
        Assert.Throws<ApplicationFailure>(()=>DocumentArchive.Create(new Dictionary<string,byte[]> { ["../escape"]=new byte[0] }));
        await Assert.ThrowsAsync<ApplicationFailure>(async()=>await DocumentArchive.Read(bytes,new(MaxBytes:bytes.Length,MaxEntries:1)));
    }
    [Fact] public async Task DesktopResourcesPreserveSavedOutputOnCancellationAndOwnStreams() {
        var provider=new DesktopFiles();await using var temp=await provider.Temporary(true);var resource=provider.Resolve(Path.Combine(temp.Resource.Id,"document.txt"));var service=new FileService(provider);
        Assert.True(await service.SaveText(resource,"繁😀","utf-8",true)); Assert.Equal("繁😀",await service.ReadText(resource,"utf-8"));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await service.SaveText(resource,"lost","utf-8",true,cancelled.Token));Assert.Equal("繁😀",await service.ReadText(resource,"utf-8"));
        await using(var stream=await provider.Open(resource,ResourceOpenMode.Read)){Assert.True(stream.CanSeek);stream.Seek(3,SeekOrigin.Begin);Assert.Equal(0xf0,stream.ReadByte());}
        await Assert.ThrowsAsync<ArgumentException>(async()=>await new DesktopFiles().Open(resource,ResourceOpenMode.Read));
    }
    [Fact] public async Task SettingsPersistAndMigrateWithoutChangingTheStorageProvider() {
        var files=new DesktopFiles();await using var temp=await files.Temporary(true);var storage=new DesktopStorage(files,"test",temp.Resource.Id);var v1=new Preferences(storage);await v1.Save("preferences",new { theme="dark" });
        var v2=new Preferences(storage,2);v2.AddMigration(1,value=>System.Text.Json.JsonSerializer.SerializeToElement(new {theme=value.GetProperty("theme").GetString(),size=12}));var settings=await v2.Load("preferences");Assert.Equal(12,settings!.Value.GetProperty("size").GetInt32());
        await Assert.ThrowsAsync<ArgumentException>(async()=>await storage.Read("../escape"));
    }
    [Fact] public void LanguageModulesUseSharedContractsAndCloseOwnedResources() {
        using var host=new DesktopBuiltins();var vm=new CompiledRuntime(host);var result=vm.Execute(new LoweringCompiler().Compile("變數 p=匯入(\"Paths\");變數 s=匯入(\"Serialization\"); [p.Join([\"a\",\"b\"]),s.ParseJson(s.Json({\"中文\":[1,真]}))];"));Assert.IsType<ArrayObj>(result);
        Assert.DoesNotContain(typeof(IApplicationHost).Assembly.GetReferencedAssemblies(),a=>a.Name!.StartsWith("Magick")||a.Name.StartsWith("SixLabors")||a.Name.StartsWith("Silk")||a.Name.StartsWith("Microsoft.Win32"));
    }
    [Theory] [InlineData(ImageFormat.Png)] [InlineData(ImageFormat.Jpeg)] [InlineData(ImageFormat.Bmp)] [InlineData(ImageFormat.Gif)] [InlineData(ImageFormat.Tiff)]
    public async Task RealCodecsReturnRequestedFormatAndExplicitFrames(ImageFormat format) {
        var codec=new DesktopImages();var source=new PixelImage(2,1,new byte[]{255,0,0,255,0,255,0,128});var encoded=await codec.Encode(source,format,new(Background:0xffffff),new());Assert.Equal(format,ImageCodecRegistry.Identify(encoded));
        var info=await codec.Inspect(encoded,new());Assert.Equal(2,info.Width);Assert.Equal(1,info.Frames);var decoded=await codec.Decode(encoded,0,new());Assert.Equal(8,decoded.Pixels.Length);
        if(format==ImageFormat.Png||format==ImageFormat.Tiff)Assert.Equal(source.Pixels.ToArray(),decoded.Pixels.ToArray());
        if(format==ImageFormat.Gif)Assert.NotEmpty(decoded.Metadata.Palette!);
        await Assert.ThrowsAnyAsync<ArgumentException>(async()=>await codec.Decode(encoded,1,new()));
    }
    [Fact] public async Task CodecRejectsSilentTransparencyLossAndMalformedImages() {
        var codec=new DesktopImages();var source=new PixelImage(1,1,new byte[]{1,2,3,4});await Assert.ThrowsAsync<ApplicationFailure>(async()=>await codec.Encode(source,ImageFormat.Jpeg,new(),new()));
        await Assert.ThrowsAsync<ApplicationFailure>(async()=>await codec.Decode(new byte[]{137,80,78,71,13,10,26,10},0,new()));
        Assert.Throws<ApplicationFailure>(()=>new PixelImage(9000,1,Array.Empty<byte>()));
    }
    [Fact] public void ImageMetadataNormalizesSingleAxisAndRequiresPermissionForGifResolutionLoss() {
        var image=new PixelImage(1,1,new byte[]{1,2,3,255},metadata:new(DpiY:144));
        Assert.Equal(144,image.Metadata.DpiX);Assert.Equal(144,image.Metadata.DpiY);
        Assert.Throws<ApplicationFailure>(()=>new ImageEncodingOptions().Prepare(image,ImageFormat.Gif));
        Assert.Equal(image.Pixels.ToArray(),new ImageEncodingOptions(AllowMetadataLoss:true).Prepare(image,ImageFormat.Gif));
    }
}
