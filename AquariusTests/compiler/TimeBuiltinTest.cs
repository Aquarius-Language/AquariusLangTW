using System.Diagnostics;
using AquariusLang.Desktop;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Wasm;

namespace AquariusTests.Compiler;

public class TimeBuiltinTest {
    [Fact]
    public void 目前日期採主機本地西元日期且拒絕參數() {
        using var runtime = new WasmRuntime();
        var before = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var actual = Assert.IsType<StringObj>(runtime.Execute(new WasmCompiler().Compile("目前日期();"))).Value;
        var after = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(actual == before || actual == after);
        Assert.IsType<ErrorObj>(runtime.Execute(new WasmCompiler().Compile("currentDate(1);")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompiledSubtractionMeasuresMillisecondsWithEitherHost(bool desktop) {
        var builtins = desktop ? new DesktopBuiltins() : new Builtins();
        try {
            Assert.Same(builtins.BuiltinFuncs["currentTime"], builtins.BuiltinFuncs["目前時間"]);
            long workStart = 0, workEnd = 0;
            builtins.DefineFunction(new BuiltinObj(_ => {
                workStart = Stopwatch.GetTimestamp();
                Thread.Sleep(20);
                workEnd = Stopwatch.GetTimestamp();
                return new IntegerObj(42);
            }), englishName: "work");
            using var runtime = new WasmRuntime(builtins);
            var program = new WasmCompiler().Compile("""
                變數 start = 目前時間();
                work();
                變數 end = currentTime();
                [start, end, end - start, (end - start) / 1000.0d];
                """);
            long outerStart = Stopwatch.GetTimestamp();
            var values = Assert.IsType<ArrayObj>(runtime.Execute(program)).Elements.Cast<DoubleObj>().Select(v => v.Value).ToArray();
            long outerEnd = Stopwatch.GetTimestamp();
            Assert.All(values, value => Assert.True(double.IsFinite(value) && value >= 0));
            Assert.Equal(values[1] - values[0], values[2]);
            Assert.Equal(values[2] / 1000, values[3]);
            Assert.InRange(values[2], Stopwatch.GetElapsedTime(workStart, workEnd).TotalMilliseconds - 0.001,
                Stopwatch.GetElapsedTime(outerStart, outerEnd).TotalMilliseconds + 0.001);
        } finally { (builtins as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void ReadingsShareAnOriginAcrossBuiltinsAndExecutions() {
        using var first = new WasmRuntime();
        var program = new WasmCompiler().Compile("currentTime();");
        double start = Assert.IsType<DoubleObj>(first.Execute(program)).Value;
        Thread.Sleep(20);
        using var second = new WasmRuntime(new Builtins(new Dictionary<string, BuiltinObj>()));
        double end = Assert.IsType<DoubleObj>(second.Execute(program)).Value;
        Assert.True(end >= start);
        Assert.True(Assert.IsType<DoubleObj>(first.Execute(program)).Value >= end);
    }

    [Theory]
    [InlineData("currentTime")]
    [InlineData("目前時間")]
    public void ExtraArgumentsFailBeforeLaterSideEffects(string name) {
        using var runtime = new WasmRuntime();
        var environment = AquariusLang.Object.Environment.NewEnvironment();
        var result = runtime.Execute(new WasmCompiler().Compile($"變數 n = 0; {name}(1); n++;"), environment);
        Assert.Contains("Expected 0 arguments, got 1", Assert.IsType<ErrorObj>(result).Message);
        Assert.Equal(0, Assert.IsType<IntegerObj>(environment.GetOwned("n")).Value);
    }

    [Fact]
    public void ClockFunctionsCanBeStoredAndShadowed() {
        using var runtime = new WasmRuntime();
        Assert.IsType<DoubleObj>(runtime.Execute(new WasmCompiler().Compile("變數 read = 目前時間; read();")));
        Assert.Equal("[7, 9]", runtime.Execute(new WasmCompiler().Compile("""
            變數 currentTime = 函式() { 7; };
            變數 f = 函式(目前時間) { 目前時間(); };
            [currentTime(), f(函式() { 9; })];
            """)).Inspect());
    }
}
