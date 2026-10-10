using System.Diagnostics;
using AquariusLang.Object;
using AquariusLang.Compiler;
using Xunit.Abstractions;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusTests.Compiler;

// Opt-in measurements: correctness never depends on machine speed or a timing threshold.
public class BenchmarkTest {
    private readonly ITestOutputHelper output;
    public BenchmarkTest(ITestOutputHelper output) { this.output = output; }

    [Fact]
    public void MeasureCompilationAndCompiledExecution() {
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_WASM_BENCHMARKS") != "1") return;
        var programs = new Dictionary<string, (string Source, int Expected)> {
            ["integer loop"] = ("變數 總數 = 0; 迴圈 (變數 i = 0; i < 50000; i++) { 總數 += i; } 總數;", 1249975000),
            ["recursive fibonacci"] = ("變數 費氏 = 函式(n) { 如果 (n < 2) { 回傳 n; } 回傳 費氏(n-1) + 費氏(n-2); }; 費氏(20);", 6765),
            ["closures and arrays"] = ("變數 建立 = 函式(x) { 函式(y) { x + y; }; }; 變數 加 = 建立(3); 變數 a = [0]; 迴圈 (變數 i = 0; i < 20000; i++) { a[0] = 加(a[0]); } a[0];", 60000)
        };
        foreach (var sample in programs) {
            var compileClock = Stopwatch.StartNew();
            var program = new LoweringCompiler().Compile(sample.Value.Source); compileClock.Stop();
            var machine = new CompiledRuntime();
            Func<IObject> execute = () => machine.Execute(program, AquaEnvironment.NewEnvironment());
            Assert.Equal(sample.Value.Expected, Assert.IsType<IntegerObj>(execute()).Value);
            for (int i = 0; i < 3; i++) execute();
            var vmTimes = new List<double>();
            for (int i = 0; i < 9; i++) {
                vmTimes.Add(Measure(execute));
            }
            vmTimes.Sort();
            output.WriteLine($"{sample.Key}: VM median {vmTimes[4]:F2} ms, parse + compile {compileClock.Elapsed.TotalMilliseconds:F2} ms");
        }
    }

    private static double Measure(Func<IObject> action) {
        var clock = Stopwatch.StartNew(); action(); clock.Stop(); return clock.Elapsed.TotalMilliseconds;
    }
}
