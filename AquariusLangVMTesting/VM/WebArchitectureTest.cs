using System.Text.Json;
using System.Diagnostics;
using AquariusLang.Graphics;
using AquariusLang.Physics;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.VM;
using AquariusLang.Web;

namespace AquariusLangVMTesting.VM;

public class WebArchitectureTest {
    private static string Root {
        get { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !File.Exists(Path.Combine(d.FullName, "AquariusLang.sln"))) d = d.Parent; return d!.FullName; }
    }
    [Fact]
    public void CoreHasNoNativeGraphicsOrPhysicsDependencies() {
        var dependencies = typeof(IWgpuBackend).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain(dependencies, n => n!.StartsWith("Silk.NET") || n.StartsWith("JoltPhysics"));
        Assert.Same(typeof(IWgpuBackend).Assembly, typeof(PhysicsRuntime).Assembly);
    }
    [Fact]
    public void BrowserAndCoreShareSurfaceResizeSemantics() {
        var initial = new GraphicsSurfaceSize(640, 480, 640, 480);
        var updates = new[] {
            new[] { 960, 600, 1920, 1200 }, // Logical resize plus density.
            new[] { 960, 600, 1200, 750 },  // Fractional density only.
            new[] { 960, 600, 1200, 750 },  // No change.
            new[] { 0, 0, 0, 0 },         // Minimized: retain layout/camera.
            new[] { 0, 600, 0, 750 },      // Partially zero surfaces cannot draw.
            new[] { 960, 600, 1200, 750 }, // Restore.
            new[] { 480, 960, 600, 1200 }, // Aspect ratio change.
        };
        var size = initial;
        var expected = updates.Select(d => size = size.Resize(d[0], d[1], d[2], d[3])).ToArray();
        Assert.False(expected[3].Drawable);
        Assert.Equal(960, expected[3].Width);
        Assert.Equal(600, expected[4].Height);
        Assert.True(expected[5].Drawable);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string file = Path.Combine(Path.GetTempPath(), "aquarius-surface-" + Guid.NewGuid().ToString("N") + ".json");
        try {
            File.WriteAllText(file, JsonSerializer.Serialize(new { initial, updates }, options));
            var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(Root, "AquariusWebCompiler", "tests", "surface-parity.mjs"));start.ArgumentList.Add(file);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();var error = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(15000), "Surface parity test timed out");
            Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
            Assert.Equal(JsonSerializer.Serialize(expected, options), output.GetAwaiter().GetResult().Trim());
        } finally { File.Delete(file); }
    }
    [Fact]
    public void ImportsAndInvalidArgumentsDoNotLoadNativeBackends() {
        var backend = new UnavailablePhysics();
        using var physics = new PhysicsRuntime(backend);
        Assert.True(physics.TryImport("Jolt", out var a));
        Assert.True(physics.TryImport("JoltPhysics", out var b));Assert.Same(a, b);
        Assert.False(physics.TryImport("Unknown", out _));
        var call = (BuiltinObj)a._Environment.Get("CreateWorld", out _);
        Assert.IsType<ErrorObj>(call.Fn(System.Array.Empty<IObject>()));
        Assert.IsType<ErrorObj>(call.Fn(new IObject[] { new IntegerObj(1) }));
        Assert.Equal(0, backend.Calls);
    }
    [Theory]
    [InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(double.NegativeInfinity)][InlineData(1000001)][InlineData(-1000001)]
    public void InvalidGravityNeverCrossesPhysicsBoundary(double value) {
        var backend = new UnavailablePhysics();using var runtime = new PhysicsRuntime(backend);runtime.TryImport("Jolt", out var module);
        var call = (BuiltinObj)module._Environment.Get("CreateWorld", out _);
        var result = call.Fn(new IObject[] { new ArrayObj(new IObject[] { new DoubleObj(value), new IntegerObj(0), new IntegerObj(0) }) });
        Assert.IsType<ErrorObj>(result);Assert.Equal(0, backend.Calls);
    }
    private sealed class UnavailablePhysics : IPhysicsBackend {
        public int Calls;public string Name => "test";
        public IPhysicsWorld CreateWorld(System.Numerics.Vector3 gravity) { Calls++;throw new InvalidOperationException("unavailable"); }
    }
    [Fact]
    public void SharedShaderLayoutAlignsAndPacksChineseUniforms() {
        var layout = new WgpuUniformLayout("struct UserUniforms { 視點: vec4<f32>, 模式: f32, 次數: u32, }");
        Assert.Equal(0, layout.Fields["視點"].Offset);Assert.Equal(16, layout.Fields["模式"].Offset);Assert.Equal(20, layout.Fields["次數"].Offset);
        layout.Set("視點", new float[] { 1, 2, 3, 4 });layout.Set("次數", new float[1], true, 42);
        Assert.Equal(42, BitConverter.ToInt32(layout.Bytes, 20));Assert.Equal(4, BitConverter.ToSingle(layout.Bytes, 12));
    }
    [Fact]
    public void EveryExampleCompilesIntoAWebsiteWithAssetsAndSharedAbi() {
        var dir = Path.Combine(Path.GetTempPath(), "aquarius-web-" + Guid.NewGuid().ToString("N"));
        try {
            WebsiteCompiler.Build(Path.Combine(Root, "examples"), dir, "wgpu_compute/main.aqua");
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "program.json")));
            var json = document.RootElement;
            Assert.Equal(Directory.GetFiles(Path.Combine(Root, "examples"), "*.aqua", SearchOption.AllDirectories).Length, json.GetProperty("modules").EnumerateObject().Count());
            Assert.Equal(WgpuShaderAbi.Header, json.GetProperty("graphics").GetProperty("header").GetString());
            Assert.True(json.GetProperty("assets").TryGetProperty("opengl_cube/checker.ppm", out _));
            Assert.True(File.Exists(Path.Combine(dir, "vendor-jolt.wasm")));
            Assert.True(File.Exists(Path.Combine(dir, "host.mjs")));
        } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    public static IEnumerable<object[]> ParitySources() {
        foreach (var source in new[] {
            "1+2*3;", "7/2;", "7/2.0d;", "1.5f+2.25d;", "1.1f;", "1.1f+2.2f;", "2147483647*2;", "1/0;", "變數 n=0; n-=2.8d; n;",
            "變數 n=1; [n++,++n,n];", "變數 n=0; 迴圈(變數 i=0;i<10;i++){n+=i;} n;",
            "變數 n=0; 迴圈(變數 i=0;i<10;i++){如果(i==3){中斷;} n+=i;} n;",
            "變數 a=[1,2]; a[1]=4; a;", "變數 a={\"x\":3}; a[\"x\"];",
            "如果(真){41+1;}否則{0;};", "!0;", "0==0.0d;", "\"甲\"+\"乙\";",
            "變數 f=函式(x){回傳 x+1;}; f(41);",
            "變數 f=函式(x){回傳 函式(y){回傳 x+y;};}; 變數 g=f(20); g(22);",
            "變數 n=0; 變數 f=函式(){n++; 回傳 n;}; [f(),f()];",
            "變數 f=函式(n){如果(n==0){回傳 0;} 回傳 f(n-1)+1;}; f(1000);",
            "變數 a=[]; a[1];", "[真&&假,真||假,真==真];"
        }) yield return new object[] { source };
    }
    [Theory, MemberData(nameof(ParitySources))]
    public void BrowserAndCoreExecuteTheSameCompiledInstructions(string source) {
        var program = new VmCompiler().Compile(source);var result = new VirtualMachine().Execute(program);
        var file = Path.Combine(Path.GetTempPath(), "aquarius-parity-" + Guid.NewGuid().ToString("N") + ".json");
        try {
            File.WriteAllText(file, WebBytecodeSerializer.Serialize(program));
            var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
            start.ArgumentList.Add(Path.Combine(Root, "AquariusWebCompiler", "tests", "execute-bytecode.mjs"));start.ArgumentList.Add(file);
            using var process = Process.Start(start)!;var output = process.StandardOutput.ReadToEndAsync();var error = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(15000), "Browser VM parity test timed out");Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
            Assert.Equal(result?.Inspect() ?? "", output.GetAwaiter().GetResult().TrimEnd());
        } finally { File.Delete(file); }
    }
}
