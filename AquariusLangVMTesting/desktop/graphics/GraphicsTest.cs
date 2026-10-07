using AquariusLang.VM;
using System.Globalization;
using AquariusLang.runtime;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using Xunit;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusREPL.Graphics;

public class GraphicsTest {
    private static IObject Evaluate(string source) {
        using var builtins = new DesktopBuiltins();
        var lexer = Lexer.NewInstance(source);
        var parser = Parser.NewInstance(lexer); var tree = parser.ParseAST();
        Assert.Empty(parser.Errors); Assert.Empty(lexer.Errors);
        return VmEvaluator.NewInstance(builtins).Eval(tree, AquaEnvironment.NewEnvironment());
    }
    [Fact]
    public void MathUsesOpenGlDepthAndColumnVectorComposition() {
        var result = Assert.IsType<ArrayObj>(Evaluate(@"
            變數 數學 = 匯入(""GLM"");
            變數 平移 = 數學.Translate([2, 3, 4]);
            變數 縮放 = 數學.Scale([2, 2, 2]);
            變數 合成 = 數學.Multiply(平移, 縮放);
            [數學.TransformPoint(合成, [1, 1, 1]), 數學.Perspective(數學.Radians(90), 1, 1, 10)];"));
        double[] point = Assert.IsType<ArrayObj>(result.Elements[0]).Elements.Select(GraphicsRuntime.Number).ToArray();
        Assert.Equal(new double[] { 4, 5, 6 }, point);
        double[] p = Assert.IsType<ArrayObj>(result.Elements[1]).Elements.Select(GraphicsRuntime.Number).ToArray();
        Assert.Equal(-1, (-p[10] + p[14]), 5); // near z=-1, w=1
        Assert.Equal(1, (-10 * p[10] + p[14]) / 10, 5); // far z=-10
    }
    [Theory]
    [InlineData("變數 圖形 = 匯入(\"GL\"); 圖形.glClear(圖形.GL_COLOR_BUFFER_BIT);", "GLFW.Init")]
    [InlineData("變數 數學 = 匯入(\"GLM\"); 數學.Normalize([0, 0, 0]);", "zero vector")]
    [InlineData("變數 數學 = 匯入(\"GLM\"); 數學.Perspective(1, 0, 1, 10);", "Perspective")]
    [InlineData("變數 圖形 = 匯入(\"GL\"); 圖形.FloatData([1, \"bad\"]);", "finite number")]
    [InlineData("變數 圖形 = 匯入(\"GL\"); 變數 資料 = 圖形.Allocate(4); 圖形.FreeData(資料); 圖形.ByteLength(資料);", "live graphics buffer")]
    [InlineData("變數 視窗 = 匯入(\"GLFW\"); 視窗.Init(1);", "Expected 0")]
    public void InvalidArgumentsAreLanguageErrors(string source, string message) {
        Assert.Contains(message, Assert.IsType<ErrorObj>(Evaluate(source)).Message);
    }
    [Fact]
    public void GeneratedBindingsCoverEveryCoreEntryPoint() {
        using var runtime = new GraphicsRuntime();
        int count = typeof(GraphicsRuntime).GetNestedTypes(System.Reflection.BindingFlags.NonPublic).Count(t => t.Name.StartsWith("D_gl"));
        Assert.Equal(344, count);
        Assert.True(runtime.TryImport("GL", out var module));
        Assert.IsType<BuiltinObj>(module._Environment.Get("glDrawElementsInstanced", out _));
        Assert.IsType<BuiltinObj>(module._Environment.Get("glFramebufferTexture2D", out _));
        Assert.IsType<GraphicsIntegerObject>(module._Environment.Get("GL_TIMEOUT_IGNORED", out _));
    }
    [Fact]
    public void TypedBuffersHaveExactByteLengthsAndContent() {
        var result = Assert.IsType<ArrayObj>(Evaluate(@"
            變數 圖形 = 匯入(""GL"");
            變數 頂點 = 圖形.FloatData([1, 2, 3]);
            變數 索引 = 圖形.UIntData([0, 1, 2]);
            變數 像素 = 圖形.ByteData([0, 127, 255]);
            [圖形.ByteLength(頂點), 圖形.ByteLength(索引), 圖形.ReadBytes(像素)];"));
        Assert.Equal(12, GraphicsRuntime.Number(result.Elements[0]));
        Assert.Equal(12, GraphicsRuntime.Number(result.Elements[1]));
        Assert.Equal(new double[] { 0, 127, 255 }, Assert.IsType<ArrayObj>(result.Elements[2]).Elements.Select(GraphicsRuntime.Number));
    }
    [Fact]
    public void CubeParsesWithoutNativeLibraries() {
        string path = Path.Combine(AppContext.BaseDirectory, "examples/opengl_cube/main.aqua");
        var lexer = Lexer.NewInstance(File.ReadAllText(path));
        var parser = Parser.NewInstance(lexer); parser.ParseAST();
        Assert.Empty(lexer.Errors); Assert.Empty(parser.Errors);
    }
}

public sealed class OpenGlFactAttribute : FactAttribute {
    public OpenGlFactAttribute() {
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_OPENGL_TESTS") != "1")
            Skip = "Set AQUARIUS_OPENGL_TESTS=1 on a desktop with the native library and OpenGL 3.3 driver.";
    }
}

[Collection("Starship console output")]
public class OpenGlIntegrationTest {
    [OpenGlFact]
    public void ShaderFailuresAreLanguageErrorsAndReleaseTheirContext() {
        using (var builtins = new DesktopBuiltins()) {
            var parser = Parser.NewInstance(Lexer.NewInstance(@"
                變數 視窗庫 = 匯入(""GLFW""); 變數 載入 = 匯入(""GLAD""); 變數 繪圖 = 匯入(""GL"");
                視窗庫.Init(); 視窗庫.WindowHint(視窗庫.GLFW_VISIBLE, 0);
                變數 視窗 = 視窗庫.CreateWindow(32, 32, ""shader error test"");
                視窗庫.MakeContextCurrent(視窗); 載入.Load();
                繪圖.CreateProgram(""invalid GLSL"", ""invalid GLSL"");"));
            var tree = parser.ParseAST(); Assert.Empty(parser.Errors);
            var result = VmEvaluator.NewInstance(builtins).Eval(tree, AquaEnvironment.NewEnvironment());
            Assert.Contains("Shader compile failed", Assert.IsType<ErrorObj>(result).Message);
        }
        using var next = new GraphicsRuntime(); Assert.True(next.TryImport("GLFW", out var glfw));
        Assert.True(Assert.IsType<BooleanObj>(((BuiltinObj)glfw._Environment.Get("Init", out _)).Fn(System.Array.Empty<IObject>())).Value);
    }
    [OpenGlFact]
    public void CubeCompilesShadersLoadsTextureRendersAndCleansUp() {
        string path = Path.Combine(AppContext.BaseDirectory, "examples/opengl_cube/main.aqua");
        string capture = Path.Combine(Path.GetTempPath(), "aquarius-cube-" + Guid.NewGuid() + ".ppm");
        string? oldFrames = System.Environment.GetEnvironmentVariable("AQUARIUS_GRAPHICS_FRAMES");
        string? oldCapture = System.Environment.GetEnvironmentVariable("AQUARIUS_GRAPHICS_CAPTURE");
        var originalOutput = Console.Out;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try {
            System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_FRAMES", "3");
            System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_CAPTURE", capture);
            Console.SetOut(output);
            Assert.True(Assert.IsType<BooleanObj>(AquariusREPL.runtime.ScriptRunner.RunFile(path)).Value);
            Assert.Contains("OpenGL error: 0", output.ToString());
            byte[] image = File.ReadAllBytes(capture);
            Assert.True(image.Length > 960 * 720 * 3);
            Assert.True(image.Distinct().Count() > 64, "A lit cube should have more colors than the cleared background.");
            // Script runner disposal permits another independent graphics session.
            using var runtime = new GraphicsRuntime(); Assert.True(runtime.TryImport("GLFW", out var glfw));
            var init = (BuiltinObj)glfw._Environment.Get("Init", out _);
            Assert.True(Assert.IsType<BooleanObj>(init.Fn(System.Array.Empty<IObject>())).Value);
        } finally {
            Console.SetOut(originalOutput);
            System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_FRAMES", oldFrames);
            System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_CAPTURE", oldCapture);
            if (File.Exists(capture)) File.Delete(capture);
        }
    }
}
