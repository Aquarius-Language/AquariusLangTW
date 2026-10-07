using AquariusLang.Object;
using AquariusLang.VM;
using Xunit;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusREPL.Graphics;

public class ColorMappingExampleTest {
    internal static string ExamplePath => Path.Combine(AppContext.BaseDirectory, "examples/color_mapping/main.aqua");

    // Load the application's real functions without starting its interactive loop.
    internal static string Definitions() {
        string source = File.ReadAllText(ExamplePath);
        int start = source.LastIndexOf("畫.run(設定, 繪製);", StringComparison.Ordinal);
        Assert.True(start >= 0);
        return source[..start];
    }

    private static double[] Numbers(string expression) => Assert.IsType<ArrayObj>(
        ProcessingTest.Evaluate(Definitions() + expression)).Elements.Select(GraphicsRuntime.Number).ToArray();

    [Fact]
    public void RangeClampingGammaAndReversalHaveKnownResults() {
        Assert.Equal(new double[] { 0, 0, .5, 1, 1, .25, .75 }, Numbers("""
            [正規化(-10,20,80,1,假,假,8), 正規化(20,20,80,1,假,假,8),
             正規化(50,20,80,1,假,假,8), 正規化(80,20,80,1,假,假,8),
             正規化(110,20,80,1,假,假,8), 正規化(50,20,80,2,假,假,8),
             正規化(50,20,80,2,真,假,8)];
            """));
    }

    [Fact]
    public void BandsHaveEqualWidthAndIncludeTheUpperEndpoint() {
        Assert.Equal(new double[] { 0, 0, 1.0 / 3, 2.0 / 3, 1, 1, 1 }, Numbers("""
            [正規化(0,0,100,1,假,真,4), 正規化(24.99d,0,100,1,假,真,4),
             正規化(25,0,100,1,假,真,4), 正規化(50,0,100,1,假,真,4),
             正規化(75,0,100,1,假,真,4), 正規化(100,0,100,1,假,真,4),
             正規化(0,0,100,1,真,真,4)];
            """));
    }

    [Fact]
    public void PaletteEndpointsMidpointsAndHexCodesMatchRgbColors() {
        Assert.Equal(new double[] { 12, 28, 67, 233, 249, 216, 24, 151, 167 }, Numbers("""
            變數 a = 色帶(0,-1); 變數 b = 色帶(0,2); 變數 c = 色帶(0,0.5d);
            [畫.red(a),畫.green(a),畫.blue(a),畫.red(b),畫.green(b),畫.blue(b),畫.red(c),畫.green(c),畫.blue(c)];
            """));
        Assert.Equal("#0C1C43", Assert.IsType<StringObj>(ProcessingTest.Evaluate(
            Definitions() + "色碼(色帶(0,0));")).Value);
    }

    [Fact]
    public void NarrowRangesPreserveIntermediatePaletteColors() {
        Assert.Equal(new double[] { 0, 64, 128, 191, 255 }, Numbers("""
            狀態[1] = 49.0d; 狀態[2] = 50.0d;
            [映色索引(49),映色索引(49.25d),映色索引(49.5d),映色索引(49.75d),映色索引(50)];
            """));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ScalarFieldsStayWithinTheirDeclaredRange(int dataset) {
        double[] values = Numbers($$"""
            變數 樣本 = [];
            迴圈(變數 y=0;y<=10;y++) {
                迴圈(變數 x=0;x<=10;x++) { 樣本 = 加入(樣本,取樣(x/10.0d,y/10.0d,{{dataset}})); }
            }
            樣本;
            """);
        Assert.All(values, value => Assert.InRange(value, 0, 100));
        Assert.True(values.Max() - values.Min() > 50);
    }

    [Fact]
    public void DraggingRangeHandlesNeverCrossesOrLeavesTheInputDomain() {
        Assert.Equal(new double[] { 99, 100, 0, 1 }, Numbers("""
            拖曳 = 1; 更新拖曳(10000); 拖曳 = 2; 更新拖曳(-10000);
            變數 a = 狀態[1]; 變數 b = 狀態[2];
            拖曳 = 1; 更新拖曳(-10000); 拖曳 = 2; 更新拖曳(-10000);
            [a,b,狀態[1],狀態[2]];
            """));
    }

    [Fact]
    public void GammaAndBandSlidersClampAndDisabledBandSliderIgnoresClicks() {
        Assert.Equal(new double[] { .25, 2.5, 3, 16, 0, 4 }, Numbers("""
            拖曳 = 3; 更新拖曳(-1000); 變數 a = 狀態[3]; 更新拖曳(1000); 變數 b = 狀態[3];
            拖曳 = 4; 更新拖曳(-1000); 變數 c = 狀態[6]; 更新拖曳(1000); 變數 d = 狀態[6];
            拖曳 = 0; 處理點擊(100,709); 變數 e = 拖曳;
            處理點擊(100,657); 處理點擊(100,709);
            [a,b,c,d,e,拖曳];
            """));
    }

    [Fact]
    public void MouseAndKeyboardControlsUpdateTheSameStateAndResetDefaults() {
        var results = Assert.IsType<ArrayObj>(ProcessingTest.Evaluate(Definitions() + """
            處理點擊(100,298); 處理點擊(1070,148); 處理點擊(270,412);
            變數 點擊狀態 = [狀態[0],狀態[7],狀態[4]];
            處理按鍵(52); 處理按鍵(68); 處理按鍵(66);
            變數 鍵盤狀態 = [狀態[0],狀態[7],狀態[5]];
            處理按鍵(48); 處理點擊(1050,60);
            [點擊狀態,鍵盤狀態,狀態,儲存];
            """));
        var clicked = Assert.IsType<ArrayObj>(results.Elements[0]).Elements;
        Assert.Equal(2, GraphicsRuntime.Number(clicked[0]));
        Assert.Equal(2, GraphicsRuntime.Number(clicked[1]));
        Assert.True(Assert.IsType<BooleanObj>(clicked[2]).Value);
        var keyed = Assert.IsType<ArrayObj>(results.Elements[1]).Elements;
        Assert.Equal(3, GraphicsRuntime.Number(keyed[0]));
        Assert.Equal(0, GraphicsRuntime.Number(keyed[1]));
        Assert.True(Assert.IsType<BooleanObj>(keyed[2]).Value);
        Assert.Equal("[0, 0, 100, 1, 假, 假, 8, 0]", results.Elements[2].Inspect());
        Assert.True(Assert.IsType<BooleanObj>(results.Elements[3]).Value);
    }

    [Fact]
    public void LetterboxedLayoutConvertsPointerCoordinatesBackToControls() {
        // Processing's initial logical size is 640x480 before setup opens a window.
        double[] values = Numbers("""
            更新版面();
            變數 x = 左邊 + 100 * 比例; 變數 y = 上邊 + 298 * 比例;
            處理點擊((x-左邊)/比例,(y-上邊)/比例);
            [比例,左邊,上邊,狀態[0]];
            """);
        Assert.Equal(640.0 / 1200, values[0], 8);
        Assert.Equal(0, values[1], 8);
        Assert.Equal((480 - 820 * (640.0 / 1200)) / 2, values[2], 8);
        Assert.Equal(2, values[3]);
    }
}

[Collection("Starship console output")]
public class ColorMappingExampleIntegrationTest {
    [WgpuFact]
    public void ControlsRecolorTheFramebufferAndSaveTheRenderedUi() {
        string capture = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        string saved = Path.Combine(Path.GetDirectoryName(ColorMappingExampleTest.ExamplePath)!, "color-mapping-0003.png");
        string? frames = System.Environment.GetEnvironmentVariable("AQUARIUS_GRAPHICS_FRAMES");
        string? oldCapture = System.Environment.GetEnvironmentVariable("AQUARIUS_GRAPHICS_CAPTURE");
        try {
            System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_FRAMES", "3");
            System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_CAPTURE", capture);
            string source = ColorMappingExampleTest.Definitions() + """
                變數 結果 = [];
                畫.run(設定,函式() {
                    如果(畫.frameCount == 2) {
                        處理按鍵(52); 處理按鍵(68); 處理按鍵(82); 處理按鍵(66);
                        拖曳 = 1; 更新拖曳(113); 拖曳 = 2; 更新拖曳(235); 拖曳 = 0;
                    }
                    如果(畫.frameCount == 3) { 處理按鍵(83); }
                    繪製();
                    # Read the center of raster cell (10,10), away from the probe.
                    變數 預期 = 查色表[映色索引(資料[10][10])];
                    結果 = 加入(結果,[畫.get(406,250),影像.get(10,10),預期]);
                    畫.redraw();
                });
                結果;
                """;
            using var builtins = new DesktopBuiltins();
            builtins.NewDefaultBuiltins(ColorMappingExampleTest.ExamplePath);
            var result = Assert.IsType<ArrayObj>(VmEvaluator.NewInstance(builtins).Evaluate(source, AquaEnvironment.NewEnvironment()));
            Assert.Equal(3, result.Elements.Length);
            foreach (var frame in result.Elements) {
                var values = Assert.IsType<ArrayObj>(frame).Elements;
                uint rendered = (uint)GraphicsRuntime.Number(values[0]);
                uint expected = (uint)GraphicsRuntime.Number(values[2]);
                Assert.Equal(expected, (uint)GraphicsRuntime.Number(values[1]));
                // The scaled PImage uses bilinear filtering; framebuffer pixel
                // centers can fall slightly to one side of the source texel center.
                foreach (int shift in new[] { 0, 8, 16, 24 }) {
                    Assert.InRange(Math.Abs((int)((rendered >> shift) & 255) - (int)((expected >> shift) & 255)), 0, 2);
                }
            }
            Assert.NotEqual(Assert.IsType<ArrayObj>(result.Elements[0]).Elements[0].Inspect(),
                Assert.IsType<ArrayObj>(result.Elements[1]).Elements[0].Inspect());
            byte[] bytes = File.ReadAllBytes(capture);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes.Take(8));
            Assert.True(bytes.Length > 10000);
            Assert.Equal(bytes, File.ReadAllBytes(saved));
        } finally {
            System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_FRAMES", frames);
            System.Environment.SetEnvironmentVariable("AQUARIUS_GRAPHICS_CAPTURE", oldCapture);
            File.Delete(capture);
            File.Delete(saved);
        }
    }
}
