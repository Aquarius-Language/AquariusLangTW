using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AquariusLang.Application;
using AquariusLang.Desktop.Application;
using AquariusLang.Desktop.Onnx;
using AquariusLang.Desktop.runtime;
using AquariusLang.Desktop;
using AquariusLang.Desktop.Graphics;
using AquariusLang.Compiler;
using AquariusLang.Object;
using AquariusLang.Webcam;
using AquariusLang.External;
using AquariusLang.MediaPipe;
using AquariusLang.Onnx;
using Xunit;

namespace AquariusTests.Onnx;

public class PoseTrackerTest {
    [Fact]
    public void RotatedRoiProjectsBackToOriginalLandscapeFrameAndClearsAfterLoss() {
        using var wasm=new WasmtimeEngine().Instantiate(ExternalLibraries.ReadAsset(ExternalLibraries.MediaPipe,"pose.wasm"),new Dictionary<string,Delegate>());
        float[] Read(int slot,int n)=>MemoryMarshal.Cast<byte,float>(wasm.Memory(wasm.Invoke("pose_ptr",slot),n*4)).ToArray();
        void Write(int slot,float[] values)=>MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(wasm.Memory(wasm.Invoke("pose_ptr",slot),values.Length*4));
        wasm.Invoke("pose_reset");Assert.Equal(1,wasm.Invoke("pose_begin",640,480,0));
        Write(3,Enumerable.Repeat(-100f,2254).ToArray());var scores=Read(3,2254);scores[0]=10;Write(3,scores);
        var boxes=new float[2254*12];boxes[2]=boxes[3]=30;
        // Anchor 0 = (4,4) in detector pixels. Hip (320,240), top point (420,240).
        boxes[4]=108;boxes[5]=108;boxes[6]=143;boxes[7]=108;Write(2,boxes);
        Assert.Equal(1,wasm.Invoke("pose_detect"));
        var landmarks=new float[195];for(int i=0;i<39;i++){landmarks[i*5]=128;landmarks[i*5+1]=0;landmarks[i*5+3]=landmarks[i*5+4]=10;}
        landmarks[33*5]=landmarks[33*5+1]=128;landmarks[34*5]=128;landmarks[34*5+1]=28;
        Write(5,landmarks);Write(6,Enumerable.Repeat(-100f,64*64*39).ToArray());Write(7,new[]{.99f});
        Assert.Equal(1,wasm.Invoke("pose_finish"));var output=Read(8,166);
        Assert.InRange(output[1],445f/640-.00001f,445f/640+.00001f);Assert.InRange(output[2],.49999f,.50001f);
        Assert.Equal(0,wasm.Invoke("pose_begin",640,480,BitConverter.SingleToInt32Bits(33)));
        Write(7,new[]{.1f});Assert.Equal(0,wasm.Invoke("pose_finish"));Assert.Equal(0,Read(8,166)[0]);
        Assert.Equal(1,wasm.Invoke("pose_begin",640,480,BitConverter.SingleToInt32Bits(200)));
    }
    [Fact]
    public async Task RealPersonAccuracyTrackingBlankRejectionAndWebGpuParity() {
        string? dir=System.Environment.GetEnvironmentVariable("AQUARIUS_POSE_FIXTURES");if(dir==null)return;
        string? model=System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_POSE_MODEL");Assert.NotNull(model);
        var image=await new DesktopImages().Decode(File.ReadAllBytes(Path.Combine(dir,"pose.jpg")),0,new ApplicationLimits());
        var expected=Regex.Matches(File.ReadAllText(Path.Combine(dir,"pose_landmarks.pbtxt")),@"x:\s*([-\d.e]+)\s+y:\s*([-\d.e]+)")
            .Take(33).Select(m=>(x:float.Parse(m.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture),y:float.Parse(m.Groups[2].Value,System.Globalization.CultureInfo.InvariantCulture))).ToArray();Assert.Equal(33,expected.Length);
        var reports=new List<object>();float[]? cpuPoints=null;
        foreach(var provider in System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_WEBGPU")=="1"?new[]{OnnxProvider.CPU,OnnxProvider.WebGPU}:new[]{OnnxProvider.CPU}) {
            using var tracker=new PoseTracker(new WasmtimeEngine(),new DesktopOnnxBackend(),File.ReadAllBytes(Path.Combine(dir,"人物偵測.onnx")),File.ReadAllBytes(model!),provider);
            var first=tracker.Analyze(image,0);Assert.True(first.HasPose);Assert.Equal(165,first.Landmarks.Length);
            var distances=expected.Select((p,i)=>Math.Sqrt(Math.Pow((first.Landmarks[i*5]-p.x)*image.Width,2)+Math.Pow((first.Landmarks[i*5+1]-p.y)*image.Height,2))).ToArray();
            double mean=distances.Average();Assert.True(mean<18,$"{provider}: mean landmark error {mean:F2}px");
            if(cpuPoints==null)cpuPoints=first.Landmarks;else Assert.InRange(Enumerable.Range(0,33).Max(i=>Math.Abs(cpuPoints[i*5]-first.Landmarks[i*5])+Math.Abs(cpuPoints[i*5+1]-first.Landmarks[i*5+1])),0,.015);
            var frames=Enumerable.Range(1,12).Select(i=>tracker.Analyze(image,i*33)).ToArray();Assert.All(frames,r=>Assert.True(r.HasPose));Assert.All(frames,r=>Assert.False(r.DetectionRan));
            var rotatedBytes=new byte[image.Pixels.Length];var source=image.Pixels.ToArray();
            for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++)source.AsSpan((y*image.Width+x)*4,4).CopyTo(rotatedBytes.AsSpan((x*image.Height+image.Height-1-y)*4,4));
            tracker.Reset();var rotated=tracker.Analyze(new PixelImage(image.Height,image.Width,rotatedBytes),450);Assert.True(rotated.HasPose);
            double rotatedError=expected.Select((p,i)=>Math.Sqrt(Math.Pow((rotated.Landmarks[i*5]-(1-p.y))*image.Height,2)+Math.Pow((rotated.Landmarks[i*5+1]-p.x)*image.Width,2))).Average();Assert.True(rotatedError<22,$"Rotated {provider}: {rotatedError:F2}px");
            var offcenterBytes=new byte[1400*900*4];for(int y=0;y<467;y++)for(int x=0;x<700;x++)source.AsSpan(((int)(y*image.Height/467.0)*image.Width+(int)(x*image.Width/700.0))*4,4).CopyTo(offcenterBytes.AsSpan(((y+100)*1400+x+600)*4,4));
            tracker.Reset();var offcenter=tracker.Analyze(new PixelImage(1400,900,offcenterBytes),480);Assert.True(offcenter.HasPose);
            double offcenterError=expected.Select((p,i)=>Math.Sqrt(Math.Pow(offcenter.Landmarks[i*5]*1400-(600+p.x*700),2)+Math.Pow(offcenter.Landmarks[i*5+1]*900-(100+p.y*467),2))).Average();Assert.True(offcenterError<22,$"Off-center {provider}: {offcenterError:F2}px");
            tracker.Reset();var blank=tracker.Analyze(new PixelImage(640,480,new byte[640*480*4]),500);Assert.False(blank.HasPose);Assert.Empty(blank.Landmarks);
            reports.Add(new{provider=provider.ToString(),meanErrorPixels=mean,maxErrorPixels=distances.Max(),rotatedMeanErrorPixels=rotatedError,offcenterMeanErrorPixels=offcenterError,firstFrameMs=first.TotalMilliseconds,trackedMedianMs=frames.Select(r=>r.TotalMilliseconds).Order().ElementAt(6),landmarks=first.Landmarks});
        }
        File.WriteAllText(Path.Combine(dir,"native-results.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
    }
}

[Collection("Starship console output")]
public class PoseApplicationTest {
    private sealed class Camera(byte[] pixels) : IWebcamBackend {
        public bool IsSupported=>true;
        public ValueTask<IReadOnlyList<WebcamDevice>> Enumerate(CancellationToken cancellation=default)=>new(Array.Empty<WebcamDevice>());
        public ValueTask<IWebcamSession> Open(string id,WebcamOptions options,CancellationToken cancellation=default)=>new(new Stream(pixels));
        private sealed class Stream(byte[] pixels):IWebcamSession {
            private int sequence;public bool IsOpen{get;private set;}=true;
            public ValueTask<WebcamFrame> Capture(CancellationToken cancellation=default)=>new(new WebcamFrame(640,480,pixels,++sequence,sequence*33));
            public void Dispose()=>IsOpen=false;
        }
    }
    [Fact]
    public async Task RealApplicationTracksCameraFramesRendersAndStops() {
        if(System.Environment.GetEnvironmentVariable("AQUARIUS_POSE_GUI")!="1")return;
        string dir=System.Environment.GetEnvironmentVariable("AQUARIUS_POSE_FIXTURES")!;
        string model=System.Environment.GetEnvironmentVariable("AQUARIUS_ONNX_POSE_MODEL")!;
        string root=Path.GetFullPath(Path.Combine(dir,"../..")),example=Path.Combine(root,"examples/mediapipe/星泉姿態觀測站.aqua");
        var image=await new DesktopImages().Decode(File.ReadAllBytes(Path.Combine(dir,"pose.jpg")),0,new ApplicationLimits());var original=image.Pixels.ToArray();
        var pixels=new byte[640*480*4];for(int i=3;i<pixels.Length;i+=4)pixels[i]=255;
        for(int y=0;y<426;y++)for(int x=0;x<640;x++)original.AsSpan(((int)(y*image.Height/426.0)*image.Width+(int)(x*image.Width/640.0))*4,4).CopyTo(pixels.AsSpan(((y+27)*640+x)*4,4));
        string? frames=System.Environment.GetEnvironmentVariable("AQUARIUS_GRAPHICS_FRAMES"),capture=System.Environment.GetEnvironmentVariable("AQUARIUS_GRAPHICS_CAPTURE");
        try {
            System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_FRAMES","30");System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_CAPTURE",Path.Combine(dir,"desktop.png"));
            using var graphics=new GraphicsRuntime(webcamBackend:new Camera(pixels));graphics.ResourcePath=p=>p.EndsWith("人物偵測.onnx")?Path.Combine(dir,"人物偵測.onnx"):p.EndsWith("姿態.onnx")?model:p;
            using var builtins=new DesktopBuiltins(graphics);builtins.NewDefaultBuiltins(example);
            string source=File.ReadAllText(example);source=source[..source.LastIndexOf("畫.執行(設定, 繪製);",StringComparison.Ordinal)]+"畫.執行(函式(){設定();啟動(真);},繪製);變數 結果=[模型.有人物,影格率,影格耗時];停止();結果;";
            foreach(string name in new[]{"中文介面.aqua","姿態模型.aqua"})source=source.Replace($"匯入(\"{name}\")",$"匯入(\"{Path.Combine(Path.GetDirectoryName(example)!,name).Replace('\\','/')}\")");
            var evaluated=CompiledEvaluator.NewInstance(builtins).Evaluate(source,AquariusLang.Object.Environment.NewEnvironment());Assert.True(evaluated is ArrayObj,evaluated.Inspect());var result=(ArrayObj)evaluated;
            File.WriteAllText(Path.Combine(dir,"desktop-ui-results.json"),JsonSerializer.Serialize(new{framesPerSecond=GraphicsRuntime.Number(result.Elements[1]),frameProcessingMs=GraphicsRuntime.Number(result.Elements[2])}));
            Assert.True(Assert.IsType<BooleanObj>(result.Elements[0]).Value,"Real camera fixture was not tracked.");Assert.True(GraphicsRuntime.Number(result.Elements[1])>20,$"Actual frame rate: {GraphicsRuntime.Number(result.Elements[1]):F2}");
            Assert.True(File.ReadAllBytes(Path.Combine(dir,"desktop.png")).Length>20000);
        }finally{System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_FRAMES",frames);System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_CAPTURE",capture);}
    }
}
