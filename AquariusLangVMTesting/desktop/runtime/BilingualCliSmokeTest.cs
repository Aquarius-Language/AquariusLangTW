using AquariusLangVMTesting;
using Xunit;

namespace AquariusREPL.runtime;

public class BilingualCliSmokeTest {
    [Fact] public void DocumentedExampleRunsAsSourceAndSourceFreeBottle() {
        using var temp = new BottleTestDirectory();
        string folder = Path.Combine(AppContext.BaseDirectory, "examples/bilingual_library");
        string main = temp.Write("main.aqua", File.ReadAllText(Path.Combine(folder, "main.aqua")));
        string library = temp.Write("tools.aqua", File.ReadAllText(Path.Combine(folder, "tools.aqua")));
        var source = BottleCliSmokeTest.Run(temp.Path, main);
        Assert.Equal(0, source.Code); Assert.Equal("BILINGUAL_LIBRARY_OK\n真\n", source.Output); Assert.Empty(source.Error);
        Assert.Equal(0, BottleCliSmokeTest.Run(temp.Path, "-c", "main.aqua", "tools.aqua").Code);
        File.Delete(main); File.Delete(library);
        var bottle = BottleCliSmokeTest.Run(temp.Path, "main.bottle");
        Assert.Equal(0, bottle.Code); Assert.Equal(source.Output, bottle.Output); Assert.Empty(bottle.Error);
    }

    [Theory]
    [InlineData("計數", "計數")][InlineData("count", "count")]
    [InlineData("計數", "count")][InlineData("count", "計數")]
    public void ScriptLibrariesShareClosuresThroughEitherNameInSourceAndSourceFreeBottles(string first, string second) {
        using var temp = new BottleTestDirectory();
        string library = temp.Write("工具.aqua", """
            變數 n=40;
            變數 計數,count=函式(){n++;n;};
            變數 中文限定=函式(){7;};
            變數 englishOnly=函式(){9;};
            """);
        string main = temp.Write("main.aqua", $$"""
            變數 lib=import("工具.aqua");
            變數 saved=lib.{{second}};
            print("ALIAS_SMOKE",lib.{{first}}(),saved(),lib.{{second}}(),lib.中文限定(),lib.englishOnly());
            真;
            """);
        var source = BottleCliSmokeTest.Run(temp.Path, main);
        Assert.Equal(0, source.Code); Assert.Equal("ALIAS_SMOKE 41 42 43 7 9\n真\n", source.Output); Assert.Empty(source.Error);
        var compiled = BottleCliSmokeTest.Run(temp.Path, "-cr", "main.aqua", "工具.aqua");
        Assert.Equal(0, compiled.Code); Assert.EndsWith(source.Output, compiled.Output); Assert.Empty(compiled.Error);
        File.Delete(main); File.Delete(library);
        var bottle = BottleCliSmokeTest.Run(temp.Path, "main.bottle");
        Assert.Equal(0, bottle.Code); Assert.Equal(source.Output, bottle.Output); Assert.Empty(bottle.Error);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void NativeLibrariesAndReturnedObjectsWorkInSourceAndBottles(bool chinese) {
        using var temp = new BottleTestDirectory();
        const string script = """
            變數 m=匯入("GLM");變數 gl=匯入("GL");變數 text=匯入("TextInput");變數 p=匯入("Processing");
            變數 v=p.createVector(3,4);v.normalize();v.mult(10);
            變數 image=p.createImage(2,1);image.set(0,0,p.color(255,0,0));
            印出("NATIVE_ALIAS_SMOKE",m.Dot([1,2,3],[4,5,6]),gl.ParseInteger("42"),text.Backspace("繁體😀"),v.mag(),image.get(0,0),p.map(5,0,10,10,20));
            真;
            """;
        string file = temp.Write("main.aqua", chinese ? BilingualTestSource.Chinese(script) : script);
        var source = BottleCliSmokeTest.Run(temp.Path, "main.aqua");
        Assert.Equal(0, source.Code); Assert.Equal("NATIVE_ALIAS_SMOKE 32 42 繁體 10 4294901760 15\n真\n", source.Output); Assert.Empty(source.Error);
        Assert.Equal(0, BottleCliSmokeTest.Run(temp.Path, "-c", "main.aqua").Code);
        File.Delete(file); var bottle = BottleCliSmokeTest.Run(temp.Path, "main.bottle");
        Assert.Equal(0, bottle.Code); Assert.Equal(source.Output, bottle.Output); Assert.Empty(bottle.Error);
    }

    [Theory]
    [InlineData("中文")][InlineData("english")]
    public void AliasArgumentFailuresKeepSourceAndBottleExitCodesConsistent(string name) {
        using var temp = new BottleTestDirectory();
        temp.Write("main.aqua", $"變數 中文,english=函式(n){{n;}};{name}();");
        var source = BottleCliSmokeTest.Run(temp.Path, "main.aqua");
        Assert.Equal(1, source.Code); Assert.Contains("expects 1 arguments", source.Output);
        Assert.Equal(0, BottleCliSmokeTest.Run(temp.Path, "-c", "main.aqua").Code);
        var bottle = BottleCliSmokeTest.Run(temp.Path, "main.bottle");
        Assert.Equal(1, bottle.Code); Assert.Equal(source.Output, bottle.Output);
    }
}
