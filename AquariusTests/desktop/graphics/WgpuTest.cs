using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using AquariusLang.Graphics;
using System.Text;
using AquariusLang.Object;
using AquariusLang.lexer;
using AquariusLang.parser;
using AquariusLang.Desktop.runtime;
using Xunit;

namespace AquariusLang.Desktop.Graphics;

public sealed class WgpuFactAttribute : FactAttribute {
    public WgpuFactAttribute() {
        if(System.Environment.GetEnvironmentVariable("AQUARIUS_WGPU_TESTS")!="1")
            Skip="Set AQUARIUS_WGPU_TESTS=1 with a wgpu adapter; Processing tests also need the GLFW bridge.";
    }
}

public sealed class WgpuTheoryAttribute : TheoryAttribute {
    public WgpuTheoryAttribute() {
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_WGPU_TESTS") != "1")
            Skip = "Set AQUARIUS_WGPU_TESTS=1 with a wgpu adapter and GLFW bridge.";
    }
}

public class WgpuTest {
    [Fact] public void ProcessingUniformsPreserveMatrixLayoutAndIndependentSnapshots() {
        using var runtime = new GraphicsRuntime();
        var canvas = new ProcessingCanvas(runtime, 640, 480, true) {
            Model = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(5, 6, 7),
            View = Matrix4x4.CreateTranslation(8, 9, 10),
            Projection = Matrix4x4.CreatePerspectiveFieldOfView(.7f, 1.5f, .1f, 100)
        };
        var first = WgpuShaders.Uniforms(canvas, false, false, false);
        Assert.Equal(WgpuShaderAbi.UniformBytes / sizeof(float), first.Length);
        var matrices = MemoryMarshal.Cast<float, Matrix4x4>(first.AsSpan(0, 64));
        Assert.Equal(canvas.Model, matrices[0]);
        Assert.Equal(canvas.View, matrices[1]);
        Assert.Equal(canvas.Projection, matrices[2]);
        Assert.True(Matrix4x4.Invert(canvas.Model * canvas.View, out var inverse));
        Assert.Equal(Matrix4x4.Transpose(inverse), matrices[3]);

        var originalModel = canvas.Model;
        canvas.Model = Matrix4x4.Identity;
        var second = WgpuShaders.Uniforms(canvas, true, true, false);
        Assert.Equal(originalModel, MemoryMarshal.Cast<float, Matrix4x4>(first.AsSpan(0, 16))[0]);
        Assert.Equal(Matrix4x4.Identity, MemoryMarshal.Cast<float, Matrix4x4>(second.AsSpan(0, 16))[0]);
        Assert.Equal(0f, first[64]); Assert.Equal(1f, second[64]);
        Assert.Equal(0f, first[66]); Assert.Equal(1f, second[66]);
    }

    [Fact] public void ModulesImportWithoutInitializingGpuAndKeepOpenGl() {
        using var runtime=new GraphicsRuntime();
        foreach(string name in new[]{"WGPU","wgpu","Processing","GL","GLAD","GLFW","GLM","STBImage"})Assert.True(runtime.TryImport(name,out _));
        runtime.TryImport("WGPU",out var gpu);runtime.TryImport("wgpu",out var alias);Assert.Same(gpu,alias);
        Assert.IsType<BuiltinObj>(gpu._Environment.Get("CreateDevice",out _));
        runtime.TryImport("Processing",out var processing);Assert.Equal("wgpu",Assert.IsType<StringObj>(processing._Environment.Get("backend",out _)).Value);
        Assert.Contains("Expected 0",Assert.IsType<ErrorObj>(ProcessingTest.Evaluate("變數 gpu=匯入(\"WGPU\");gpu.CreateDevice(1);")).Message);
    }
    [Fact] public void CustomUniformLayoutRespectsWgslAlignmentAndValueTypes() {
        var layout=new WgpuUniformLayout("struct UserUniforms { time: f32, uv: vec2<f32>, color: vec3<f32>, mode: i32, matrix: mat4x4<f32>, };");
        Assert.Equal(0,layout.Fields["time"].Offset);Assert.Equal(8,layout.Fields["uv"].Offset);Assert.Equal(16,layout.Fields["color"].Offset);
        Assert.Equal(28,layout.Fields["mode"].Offset);Assert.Equal(32,layout.Fields["matrix"].Offset);
        layout.Set("time",new[]{2.5f});layout.Set("mode",new float[1],true,7);
        Assert.Equal(2.5f,BitConverter.ToSingle(layout.Bytes,0));Assert.Equal(7,BitConverter.ToInt32(layout.Bytes,28));
        Assert.Throws<ArgumentException>(()=>layout.Set("missing",new[]{0f}));
        Assert.Throws<ArgumentException>(()=>layout.Set("color",new[]{0f}));
        Assert.Throws<ArgumentException>(()=>layout.Set("mode",new[]{0f}));
        Assert.Throws<ArgumentException>(()=>new WgpuUniformLayout("struct UserUniforms { x: array<f32,4>, }"));
        Assert.Throws<ArgumentException>(()=>new WgpuUniformLayout("struct UserUniforms { x: f32, x: f32, }"));
    }
    [Fact] public void ScissorClampsNegativeAndOversizedRectangles() {
        Assert.Equal((0,2,8,6),WgpuTarget.Scissor(8,8,new Vector4(-4,2,20,20)));
        Assert.Equal((8,8,0,0),WgpuTarget.Scissor(8,8,new Vector4(20,20,5,5)));
        Assert.Equal((0,0,8,8),WgpuTarget.Scissor(8,8,null));
    }
    [Theory]
    [InlineData("wgpu_compute")][InlineData("wgpu_triangle")][InlineData("processing_wgpu")][InlineData("processing_showcase")]
    public void ExamplesParseAndMirrorsMatch(string name) {
        string path=Path.Combine(AppContext.BaseDirectory,"examples",name,"main.aqua");
        var lexer=Lexer.NewInstance(File.ReadAllText(path));var parser=Parser.NewInstance(lexer);parser.ParseAST();Assert.Empty(lexer.Errors);Assert.Empty(parser.Errors);
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root!=null&&!File.Exists(Path.Combine(root.FullName,"AquariusLang.sln")))root=root.Parent;
        if(root!=null)Assert.Equal(File.ReadAllText(Path.Combine(root.FullName,"examples",name,"main.aqua")).Replace("\r\n","\n"),File.ReadAllText(path).Replace("\r\n","\n"));
    }
}

[Collection("Starship console output")]
public class WgpuIntegrationTest {
    [WgpuTheory, InlineData(false), InlineData(true)] public void ComputeDispatchAndWritesReachGpuStorageAndReadBack(bool chinese) {
        Assert.True(Assert.IsType<BooleanObj>(ScriptRunner.RunFile(Path.Combine(AppContext.BaseDirectory,"examples/wgpu_compute/main.aqua"))).Value);
        var result=Assert.IsType<ArrayObj>(ProcessingTest.Evaluate("""
            變數 gpu=匯入("WGPU");變數 device=gpu.CreateDevice();變數 buffer=device.CreateBuffer([1,2]);
            buffer.Write([7,9]);變數 result=buffer.Read();buffer.Dispose();buffer.Dispose();device.Dispose();device.Dispose();result;
            """, chinese));
        Assert.Equal(new double[]{7,9},result.Elements.Select(GraphicsRuntime.Number));
    }
    [WgpuFact] public void OddWidthTargetsReadBackPixelsAndRenderWgslTriangles() {
        string source="變數 gpu=匯入(\"WGPU\");變數 device=gpu.CreateDevice();變數 target=device.CreateRenderTarget(17,3);target.Clear([1,0,0,1]);變數 pixels=target.ReadPixels();device.Dispose();pixels;";
        var pixels=Assert.IsType<ArrayObj>(ProcessingTest.Evaluate(source));Assert.Equal(51,pixels.Elements.Length);
        Assert.All(pixels.Elements,pixel=>Assert.Equal(0xFFFF0000,GraphicsRuntime.Number(pixel)));
        string example=Path.Combine(AppContext.BaseDirectory,"examples/wgpu_triangle/main.aqua"),capture=Path.Combine(Path.GetDirectoryName(example)!,"wgpu-triangle.png");
        try {Assert.True(Assert.IsType<BooleanObj>(ScriptRunner.RunFile(example)).Value);Assert.True(File.ReadAllBytes(capture).Length>1000);}
        finally {File.Delete(capture);}
    }
    [WgpuFact] public void InvalidResourcesAndShadersBecomeLanguageErrorsAndCleanupIsRepeatable() {
        foreach(var (call,message) in new[]{
            ("device.CreateBuffer([]);","floats"),("device.CreateRenderTarget(0,1);","Dimension"),
            ("變數 b=device.CreateBuffer([1]);b.Write([1,2]);","1 floats"),
            ("變數 b=device.CreateBuffer([1]);b.Dispose();b.Read();","disposed"),
            ("device.CreateShader(\"bad shader\",\"bad shader\");","Shader compile failed"),
            ("device.Dispose();device.Poll();","disposed"),
            ("變數 b=device.CreateBuffer([1]);device.Dispatch(\"bad shader\",b,0);","Workgroup"),
            ("變數 other=gpu.CreateDevice();變數 b=other.CreateBuffer([1]);device.Dispatch(\"bad shader\",b,1);","this wgpu device")
        }) {
            var error=Assert.IsType<ErrorObj>(ProcessingTest.Evaluate("變數 gpu=匯入(\"WGPU\");變數 device=gpu.CreateDevice();"+call));Assert.Contains(message,error.Message);
        }
        Assert.True(Assert.IsType<BooleanObj>(ProcessingTest.Evaluate("變數 gpu=匯入(\"WGPU\");變數 d=gpu.CreateDevice();d.Dispose();真;")).Value);
    }
    [WgpuFact] public void ProcessingUsesNoOpenGlContextAndCanSwitchBackToRawOpenGl() {
        Assert.Contains("Make a window current",Assert.IsType<ErrorObj>(ProcessingTest.Evaluate("變數 p=匯入(\"Processing\");p.size(16,16);變數 gl=匯入(\"GL\");gl.glGetError();")).Message);
        var result=Assert.IsType<ArrayObj>(ProcessingTest.Evaluate("""
            變數 p=匯入("Processing");變數 gl=匯入("GL");變數 glfw=匯入("GLFW");變數 glad=匯入("GLAD");
            p.size(16,16);p.background(255,0,0);變數 pixel=p.get(2,2);p.close();
            glfw.Init();變數 window=glfw.CreateWindow(16,16,"OpenGL preserved");glfw.MakeContextCurrent(window);glad.Load();
            變數 error=gl.glGetError();glfw.Terminate();[p.backend,pixel,error];
            """));
        Assert.Equal("wgpu",Assert.IsType<StringObj>(result.Elements[0]).Value);Assert.Equal(0xFFFF0000,GraphicsRuntime.Number(result.Elements[1]));Assert.Equal(0,GraphicsRuntime.Number(result.Elements[2]));
        Assert.Equal(0xFF00FF00,GraphicsRuntime.Number(ProcessingTest.Evaluate("變數 p=匯入(\"Processing\");變數 glfw=匯入(\"GLFW\");p.size(16,16);glfw.Terminate();p.size(16,16);p.background(0,255,0);變數 pixel=p.get(4,4);p.close();pixel;")));
    }
    [WgpuTheory, InlineData(false), InlineData(true)] public void ProcessingShaderUniformsClipAndSmoothSwitchesPreservePixels(bool chinese) {
        var result=Assert.IsType<ArrayObj>(ProcessingTest.Evaluate("""
            變數 p=匯入("Processing");p.size(32,32);p.noStroke();p.background(0,0,255);p.noSmooth();
            變數 shader=p.createShader("@vertex fn vs_main(i: VertexInput)->VertexOutput {var o:VertexOutput;var clip=processing.projection*vec4<f32>(i.position,1.0);clip.z=(clip.z+clip.w)*0.5;o.position=clip;o.vertexColor=i.color;o.uv=i.texcoord;o.normal=i.normal;o.eyePosition=i.position;return o;}",
            "struct UserUniforms {color:vec4<f32>,}; @group(1) @binding(0) var<uniform> user:UserUniforms; @fragment fn fs_main(i:VertexOutput)->@location(0) vec4<f32>{return user.color;}");
            shader.set("color",[1,0,0,1]);p.shader(shader);p.clip(0,0,8,8);p.rect(0,0,32,32);p.noClip();p.resetShader();
            變數 red=p.get(4,4);p.smooth();變數 blue=p.get(24,24);p.noSmooth();變數 preserved=p.get(4,4);p.endFrame();p.close();[red,blue,preserved];
            """, chinese));
        Assert.Equal(new double[]{0xFFFF0000,0xFF0000FF,0xFFFF0000},result.Elements.Select(GraphicsRuntime.Number));
    }
}

[Collection("Starship console output")]
public class WgpuCliSmokeTest {
    internal static (int Code,string Output,string Error) Run(params string[] args) {
        var start=new ProcessStartInfo(System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet") {
            WorkingDirectory=AppContext.BaseDirectory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
        };
        start.ArgumentList.Add(typeof(ScriptRunner).Assembly.Location);foreach(string arg in args)start.ArgumentList.Add(arg);
        start.Environment["AQUARIUS_GRAPHICS_FRAMES"]="2";
        start.Environment.Remove("AQUARIUS_GRAPHICS_CAPTURE");
        using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(60_000)){process.Kill(true);throw new TimeoutException("wgpu smoke test did not exit in 60 seconds.");}
        return(process.ExitCode,output.GetAwaiter().GetResult(),error.GetAwaiter().GetResult());
    }
    [Fact] public void WgpuExamplesCompileWithoutGpuInitialization() {
        foreach(string name in new[]{"wgpu_compute","wgpu_triangle","processing_wgpu"}) {
            var result=Run("--disassemble",Path.Combine("examples",name,"main.aqua"));Assert.Equal(0,result.Code);Assert.Equal("",result.Error);Assert.NotEmpty(result.Output);
        }
    }
    [WgpuTheory, InlineData(false), InlineData(true)] public void ExamplesExecuteAsSourceAndCompiledWasmsInSeparateProcesses(bool chinese) {
        foreach(string name in new[]{"wgpu_compute","wgpu_triangle","processing_wgpu"}) {
            using var directory=new ApplicationTestDirectory();
            string original=Path.Combine(AppContext.BaseDirectory,"examples",name,"main.aqua"),wasm=directory.FilePath("example.wasm");
            string text=File.ReadAllText(original);
            string example=directory.Write("main.aqua",chinese?AquariusTests.BilingualTestSource.Chinese(text):text);
            try {
                var source=Run(example);Assert.True(source.Code==0,source.Output+source.Error);Assert.Equal("",source.Error);Assert.Contains("真",source.Output);
                var compiled=Run("-c","--root",Path.GetDirectoryName(example)!,"-o",wasm,example);Assert.Equal(0,compiled.Code);Assert.Equal("",compiled.Error);
                File.Delete(example);
                var packaged=Run(wasm);Assert.True(packaged.Code==0,packaged.Output+packaged.Error);Assert.Equal("",packaged.Error);Assert.Contains("真",packaged.Output);
            } finally {File.Delete(wasm);File.Delete(Path.Combine(AppContext.BaseDirectory,"examples",name,"wgpu-triangle.png"));}
        }
    }
}
