using AquariusLang.VM;
using System.Numerics;
using AquariusLang.runtime;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using Xunit;
using AquaEnvironment=AquariusLang.Object.Environment;

namespace AquariusREPL.Graphics;

public class ProcessingTest {
    internal static IObject Evaluate(string source, bool chinese = false) {
        if (chinese) source = AquariusLangVMTesting.BilingualTestSource.Chinese(source);
        using var builtins=new DesktopBuiltins();var lexer=Lexer.NewInstance(source);var parser=Parser.NewInstance(lexer);var tree=parser.ParseAST();
        Assert.Empty(lexer.Errors);Assert.Empty(parser.Errors);return VmEvaluator.NewInstance(builtins).Eval(tree,AquaEnvironment.NewEnvironment());
    }
    [Theory, InlineData(false), InlineData(true)] public void ColorsTransformsImagesVectorsAndMathWorkWithoutLoadingNativeGraphics(bool chinese) {
        var result=Assert.IsType<ArrayObj>(Evaluate(@"
            變數 p=匯入(""Processing"");p.colorMode(p.HSB,360,100,100,255);
            變數 red=p.color(0,100,100);p.translate(10,20);p.scale(2);p.pushMatrix();p.rotate(p.PI);p.popMatrix();
            變數 img=p.createImage(2,1,p.ARGB);img.set(0,0,red);img.loadPixels();img.updatePixels();
            變數 v=p.createVector(3,4);v.normalize();v.mult(10);
            [p.red(red),p.green(red),p.blue(red),p.modelX(2,3),p.modelY(2,3),img.get(0,0),v.mag(),p.map(5,0,10,10,20),p.bezierPoint(0,0,10,10,0.5d)];", chinese));
        Assert.Equal(new double[]{360,0,0,14,26,4294901760,10,15,5},result.Elements.Select(GraphicsRuntime.Number));
    }
    [Theory, InlineData(false), InlineData(true)] public void RandomAndNoiseSeedsAreReproducibleAndNoiseIsContinuous(bool chinese) {
        var result=Assert.IsType<ArrayObj>(Evaluate(@"變數 p=匯入(""Processing"");p.randomSeed(5);變數 r=p.random(100);p.randomSeed(5);p.noiseSeed(7);變數 n=p.noise(0.4d,0.2d);p.noiseSeed(7);[r==p.random(100),n==p.noise(0.4d,0.2d),n,p.noise(0.4001d,0.2d)];", chinese));
        Assert.True(Assert.IsType<BooleanObj>(result.Elements[0]).Value);Assert.True(Assert.IsType<BooleanObj>(result.Elements[1]).Value);
        Assert.InRange(GraphicsRuntime.Number(result.Elements[2]),0,1);Assert.InRange(Math.Abs(GraphicsRuntime.Number(result.Elements[2])-GraphicsRuntime.Number(result.Elements[3])),0,.002);
    }
    [Theory, InlineData(false), InlineData(true)] public void PixelEditsCopiesResizesAndFiltersUseImageContent(bool chinese) {
        var result=Assert.IsType<ArrayObj>(Evaluate(@"變數 p=匯入(""Processing"");變數 img=p.createImage(2,1);img.loadPixels();變數 pixels=img.pixels;pixels[p.floor(0.5d)]=p.color(255,0,0);pixels[p.ceil(0.5d)]=p.color(0,255,0);img.updatePixels();
            變數 copy=img.get();img.resize(4,0);變數 resized=img.get(1,0);copy.filter(p.INVERT);[img.width,img.height,resized,copy.get(0,0),copy.get(1,0)];", chinese));
        Assert.Equal(new double[]{4,2,0xFFFF0000,0xFF00FFFF,0xFFFF00FF},result.Elements.Select(GraphicsRuntime.Number));
    }
    [Theory]
    [InlineData("p.rect(0,0,10,10);","size()")]
    [InlineData("p.popMatrix();","stack is empty")]
    [InlineData("p.vertex(1,2);","beginShape")]
    [InlineData("p.endShape();","beginShape")]
    [InlineData("p.colorMode(p.RGB,0);","positive")]
    [InlineData("p.map(1,0,0,1,2);","range is zero")]
    [InlineData("p.noiseDetail(0);","octaves")]
    [InlineData("p.on(\"unknown\",函式(){});","Unknown event")]
    [InlineData("p.run(函式(x){},函式(){});","zero parameters")]
    [InlineData("p.createImage(-1,1);","Dimension")]
    public void InvalidCallsReturnAquaErrors(string call,string message) {
        foreach (bool chinese in new[] { false, true })
            Assert.Contains(message,Assert.IsType<ErrorObj>(Evaluate("變數 p=匯入(\"Processing\");"+call, chinese)).Message);
    }
    [Fact] public void ConcavePolygonsTriangulateInBothWindingsAndOnVerticalPlanes() {
        var points=new[]{new Vector3(0,0,0),new(4,0,0),new(4,4,0),new(2,2,0),new(0,4,0)};
        foreach(var p in new[]{points,points.Reverse().ToArray(),points.Select(v=>new Vector3(0,v.X,v.Y)).ToArray()}) {
            var ids=ProcessingGeometry.Triangulate(p);Assert.Equal(9,ids.Count);
            double area=0;for(int i=0;i<ids.Count;i+=3)area+=Vector3.Cross(p[ids[i+1]]-p[ids[i]],p[ids[i+2]]-p[ids[i]]).Length()/2;Assert.Equal(12,area,5);
        }
    }
    [Fact] public void ShowcaseParses() {
        var lexer=Lexer.NewInstance(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"examples/processing_showcase/main.aqua")));var parser=Parser.NewInstance(lexer);parser.ParseAST();Assert.Empty(lexer.Errors);Assert.Empty(parser.Errors);
    }
    [Fact] public void CanvasResizeTracksLogicalAndPhysicalSizesAndRecoversFromMinimizing() {
        using var runtime=new GraphicsRuntime();
        var canvas=new ProcessingCanvas(runtime,640,480,true);
        var projection=canvas.Projection;
        Assert.True(canvas.Resize(960,480,1920,960));
        Assert.Equal((960,480,1920,960),(canvas.Width,canvas.Height,canvas.PixelWidth,canvas.PixelHeight));
        Assert.NotEqual(projection,canvas.Projection);
        projection=canvas.Projection;
        Assert.True(canvas.Resize(960,480,960,480)); // Display density changed alone.
        Assert.Equal(projection,canvas.Projection);
        Assert.False(canvas.Resize(960,480,960,480));
        Assert.True(canvas.Resize(0,0,0,0));
        Assert.Equal((960,480),(canvas.Width,canvas.Height));
        Assert.Equal(projection,canvas.Projection);
        Assert.True(canvas.Resize(960,480,960,480));
    }
    [Fact] public void TextRasterResolutionFollowsTransformAndDisplayDensity() {
        using var runtime=new GraphicsRuntime();
        var canvas=new ProcessingCanvas(runtime,640,480,false);
        Assert.Equal(1d,GraphicsRuntime.TextRasterScale(canvas,20,30),3);
        canvas.Resize(640,480,1280,960);
        Assert.Equal(2d,GraphicsRuntime.TextRasterScale(canvas,20,30),3);
        canvas.Model=Matrix4x4.CreateScale(3)*Matrix4x4.CreateTranslation(10,20,0);
        Assert.Equal(6d,GraphicsRuntime.TextRasterScale(canvas,20,30),3);
    }
    [Fact] public void PngEncodingPreservesDimensionsAndPixelPayload() {
        string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".png");
        try{GraphicsRuntime.WriteImage(path,2,1,new byte[]{255,0,0,255,0,255,0,128});var bytes=File.ReadAllBytes(path);Assert.Equal(new byte[]{137,80,78,71,13,10,26,10},bytes.Take(8));Assert.Equal(2,bytes[19]);Assert.Equal(1,bytes[23]);Assert.True(bytes.Length>60);}finally{File.Delete(path);}
    }
}

[Collection("Starship console output")]
public class ProcessingIntegrationTest {
    [WgpuTheory, InlineData(false), InlineData(true)] public void CompletedOffscreenLayersCanBeReadAndSnapshottedWithoutBeginningAnotherDraw(bool chinese) {
        var result=Assert.IsType<ArrayObj>(ProcessingTest.Evaluate("""
            變數 p=匯入("Processing");p.size(16,16);
            變數 pg=p.createGraphics(8,4);pg.resize(8,4,16,8);
            pg.beginDraw();pg.noSmooth();pg.noStroke();pg.background(0,0,255);
            pg.fill(255,0,0);pg.rect(4,0,4,4);pg.endDraw();
            變數 snapshot=pg.get();變數 crop=pg.get(4,0,4,4);變數 pixel=pg.get(7,3);
            pg.loadPixels();變數 count=長度(pg.pixels);
            pg.beginDraw();pg.background(0,255,0);pg.endDraw();
            p.background(0);p.image(snapshot,0,0);
            變數 result=[snapshot.width,snapshot.height,snapshot.get(0,0),snapshot.get(7,3),
                crop.width,crop.height,crop.get(3,3),pixel,count,pg.get(7,3),p.get(7,3)];
            p.close();result;
            """,chinese));
        Assert.Equal(new double[]{8,4,0xFF0000FF,0xFFFF0000,4,4,0xFFFF0000,0xFFFF0000,128,0xFF00FF00,0xFFFF0000},result.Elements.Select(GraphicsRuntime.Number));
    }
    [WgpuFact] public void CompletedOffscreenLayersCanBeSavedButStillRejectDrawingOutsideBeginDraw() {
        string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".png");
        try {
            var result=ProcessingTest.Evaluate($$"""
                變數 p=匯入("Processing");p.size(16,16);變數 pg=p.createGraphics(2,1);
                pg.beginDraw();pg.background(255,0,0);pg.endDraw();
                pg.save("{{path.Replace("\\","/")}}");變數 saved=p.loadImage("{{path.Replace("\\","/")}}");
                pg.saveFrame("{{path.Replace("\\","/")}}");變數 frame=p.loadImage("{{path.Replace("\\","/")}}");
                變數 result=[saved.width,saved.height,saved.get(1,0),frame.get(0,0)];p.close();result;
                """);
            Assert.Equal(new double[]{2,1,0xFFFF0000,0xFFFF0000},Assert.IsType<ArrayObj>(result).Elements.Select(GraphicsRuntime.Number));
        } finally {File.Delete(path);}
        foreach(var call in new[]{"pg.rect(0,0,1,1);","pg.set(0,0,p.color(255));","pg.updatePixels();"})
            Assert.Contains("beginDraw()",Assert.IsType<ErrorObj>(ProcessingTest.Evaluate("變數 p=匯入(\"Processing\");p.size(16,16);變數 pg=p.createGraphics(2,1);pg.beginDraw();pg.endDraw();"+call)).Message);
    }
    [WgpuFact] public void ResizingPausedSketchRedrawsAtTheNewResolutionWithoutAnExplicitRedraw() {
        var result=Assert.IsType<ArrayObj>(ProcessingTest.Evaluate("""
            變數 p=匯入("Processing");變數 frames=0;變數 events=0;變數 dimensions=[];
            p.run(函式(){
                p.size(640,480);p.noLoop();
                p.on("windowResized",函式(){
                    events++;p.background(0,255,0);
                    dimensions=[p.width,p.height,p.pixelWidth,p.pixelHeight,p.get(p.width-1,p.height-1)];
                });
            },函式(){
                frames++;
                如果(frames==1){p.background(0);p.resize(800,600);}
                否則{p.exit();}
            });
            [frames,events,dimensions];
            """));
        Assert.Equal(2,GraphicsRuntime.Number(result.Elements[0]));
        Assert.True(GraphicsRuntime.Number(result.Elements[1])>=1);
        var dimensions=Assert.IsType<ArrayObj>(result.Elements[2]).Elements.Select(GraphicsRuntime.Number).ToArray();
        Assert.Equal(800,dimensions[0]);Assert.Equal(600,dimensions[1]);
        Assert.True(dimensions[2]>0&&dimensions[3]>0);
        Assert.Equal(0xFF00FF00,dimensions[4]);
    }
    [WgpuFact] public void OffscreenResizeChangesPhysicalPixelsAndRetainsLogicalDrawingCoordinates() {
        var result=Assert.IsType<ArrayObj>(ProcessingTest.Evaluate("""
            變數 p=匯入("Processing");p.size(64,48);p.noSmooth();p.noStroke();p.background(0);
            變數 pg=p.createGraphics(16,8);pg.resize(24,12,96,48);
            pg.beginDraw();pg.noSmooth();pg.noStroke();pg.background(0,255,0);
            pg.fill(255,0,0);pg.rect(12,0,12,12);pg.loadPixels();
            變數 count=長度(pg.pixels);變數 color=pg.get(23,11);pg.endDraw();
            p.image(pg,8,8);
            變數 rendered=p.get(30,10);變數 outside=p.get(40,10);
            變數 copy=p.createImage(2,1);copy.copy(pg,12,0,12,12,0,0,2,1);
            p.copy(pg,12,0,12,12,0,0,12,12);
            變數 result=[pg.width,pg.height,pg.pixelWidth,pg.pixelHeight,count,color,rendered,outside,copy.get(1,0),p.get(6,6)];
            pg.resize(24,12,12,6);pg.beginDraw();pg.background(0,255,0);pg.loadPixels();
            result=加入(result,長度(pg.pixels));pg.endDraw();
            copy.copy(pg,-1,0,1,1,0,0,1,1);result=加入(result,copy.get(0,0));
            p.close();result;
            """));
        Assert.Equal(new double[]{24,12,96,48,4608,0xFFFF0000,0xFFFF0000,0xFF000000,0xFFFF0000,0xFFFF0000,72,0},result.Elements.Select(GraphicsRuntime.Number));
    }
    [WgpuFact] public void OffscreenResizeRejectsInvalidDimensionsAndInvalidatesOldPixelArrays() {
        foreach(var call in new[]{"pg.resize(0,8);","pg.resize(8,8,16);","pg.resize(8,8,8192,8192);","pg.beginDraw();pg.resize(8,8);"})
            Assert.IsType<ErrorObj>(ProcessingTest.Evaluate("變數 p=匯入(\"Processing\");p.size(16,16);變數 pg=p.createGraphics(8,8);"+call));
        Assert.Contains("loadPixels()",Assert.IsType<ErrorObj>(ProcessingTest.Evaluate("""
            變數 p=匯入("Processing");p.size(16,16);變數 pg=p.createGraphics(8,8);
            pg.beginDraw();pg.loadPixels();pg.endDraw();pg.resize(16,16);pg.beginDraw();pg.updatePixels();
            """)).Message);
    }
    [WgpuTheory, InlineData(false), InlineData(true)] public void SketchCanRestartAndRejectsOldGraphicsObjects(bool chinese) {
        Assert.Equal(0xFF00FF00,GraphicsRuntime.Number(ProcessingTest.Evaluate(@"變數 p=匯入(""Processing"");p.size(16,16);p.close();p.size(16,16);p.background(0,255,0);變數 c=p.get(2,2);p.close();c;", chinese)));
        Assert.Contains("live PImage",Assert.IsType<ErrorObj>(ProcessingTest.Evaluate(@"變數 p=匯入(""Processing"");p.size(16,16);變數 img=p.createImage(2,2);p.close();p.size(16,16);p.image(img,0,0);", chinese)).Message);
        Assert.Contains("Shader compile failed",Assert.IsType<ErrorObj>(ProcessingTest.Evaluate(@"變數 p=匯入(""Processing"");p.size(16,16);p.createShader(""bad shader"",""bad shader"");", chinese)).Message);
    }
    [WgpuFact] public void TransparentOffscreenPixelsCompositeOnceAndRetainStraightArgb() {
        var result=Assert.IsType<ArrayObj>(ProcessingTest.Evaluate(@"變數 p=匯入(""Processing"");p.size(16,16);p.background(0,0,255);p.noStroke();
            變數 pg=p.createGraphics(16,16);pg.beginDraw();pg.clear();pg.noStroke();pg.fill(255,0,0,128);pg.rect(0,0,16,16);變數 rgba=pg.get(8,8);pg.endDraw();p.image(pg,0,0);變數 blended=p.get(8,8);
            p.loadPixels();p.updatePixels();變數 updated=p.get(8,8);p.close();[rgba,blended,updated];"));
        Assert.Equal(new double[]{0x80FF0000,0xFF80007F,0xFF80007F},result.Elements.Select(GraphicsRuntime.Number));
    }
    [WgpuFact] public void CanvasContoursImagesOffscreenShadersAndPixelOrientationRenderCorrectly() {
        var result=Assert.IsType<ArrayObj>(ProcessingTest.Evaluate(@"
            變數 p=匯入(""Processing"");p.size(64,64);p.noSmooth();p.noStroke();p.background(0);p.fill(255,0,0);p.rect(0,0,32,32);
            變數 top=p.get(4,4);變數 bottom=p.get(4,48);
            p.fill(0,255,0);p.beginShape();p.vertex(32,0);p.vertex(64,0);p.vertex(64,32);p.vertex(32,32);
            p.beginContour();p.vertex(40,8);p.vertex(56,8);p.vertex(56,24);p.vertex(40,24);p.endContour();p.endShape(p.CLOSE);
            變數 hole=p.get(48,16);變數 edge=p.get(36,4);
            變數 pg=p.createGraphics(16,16);pg.beginDraw();pg.noStroke();pg.background(0,0,255);pg.fill(255,255,0);pg.rect(0,0,16,8);pg.endDraw();
            p.image(pg,0,32,16,16);變數 pgTop=p.get(4,34);變數 pgBottom=p.get(4,46);
            p.blendMode(p.REPLACE);p.fill(100,50,200);p.rect(48,48,16,16);變數 blended=p.get(52,52);
            p.close();[top,bottom,hole,edge,pgTop,pgBottom,blended];"));
        Assert.Equal(new double[]{0xFFFF0000,0xFF000000,0xFF000000,0xFF00FF00,0xFFFFFF00,0xFF0000FF,0xFF6432C8},result.Elements.Select(GraphicsRuntime.Number));
    }
    [WgpuFact] public void CallbacksRunInTheirClosuresAndErrorCleanupAllowsAnotherHost() {
        var result=ProcessingTest.Evaluate(@"變數 p=匯入(""Processing"");變數 n=0;p.run(函式(){p.size(16,16);},函式(){n++;p.background(0);如果(n==2){p.exit();}});n;");
        Assert.Equal(2,GraphicsRuntime.Number(result));
        Assert.Contains("Identifier not found",Assert.IsType<ErrorObj>(ProcessingTest.Evaluate(@"變數 p=匯入(""Processing"");p.run(函式(){p.size(16,16);},函式(){未知變數;});")).Message);
        Assert.True(Assert.IsType<BooleanObj>(ProcessingTest.Evaluate(@"變數 p=匯入(""Processing"");p.size(16,16);p.close();真;")).Value);
    }
    [WgpuFact] public void ShowcaseRendersFiniteFramesAndCapturesAPng() {
        string path=Path.Combine(AppContext.BaseDirectory,"examples/processing_showcase/main.aqua"),capture=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".png");
        string? frames=System.Environment.GetEnvironmentVariable("AQUARIUS_GRAPHICS_FRAMES"),oldCapture=System.Environment.GetEnvironmentVariable("AQUARIUS_GRAPHICS_CAPTURE");
        try {System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_FRAMES","2");System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_CAPTURE",capture);
            string source=File.ReadAllText(path);
            using var builtins=new DesktopBuiltins();builtins.NewDefaultBuiltins(path);var parser=Parser.NewInstance(Lexer.NewInstance(source));var tree=parser.ParseAST();Assert.Empty(parser.Errors);
            Assert.True(Assert.IsType<BooleanObj>(VmEvaluator.NewInstance(builtins).Eval(tree,AquaEnvironment.NewEnvironment())).Value);var bytes=File.ReadAllBytes(capture);Assert.True(bytes.Length>10000);
        }finally{System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_FRAMES",frames);System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_CAPTURE",oldCapture);File.Delete(capture);}
    }
}
