using System.Diagnostics;
using System.Globalization;
using AquariusLang.Compiler;
using AquariusLang.Wasm;
using Xunit.Abstractions;

namespace AquariusTests.Compiler;

// Opt-in measurements: correctness never depends on machine speed or a timing threshold.
public class BenchmarkTest {
    private readonly ITestOutputHelper output;
    public BenchmarkTest(ITestOutputHelper output) { this.output = output; }

    /// <summary>Runs <paramref name="body"/> (a function body whose final expression is the result) once, then 12 more
    /// times timed by the program itself with 目前時間, so the runtime's start-up is not measured. Returns the first
    /// result and the timings of the last 9 runs (the first 3 warm up), sorted.</summary>
    internal static (string Result, double[] Milliseconds) Measure(string body) {
        var run = WasmTestHost.Run($$"""
            變數 工作 = 函式() { {{body}} };
            變數 結果 = 工作();
            變數 次 = [];
            迴圈 (變數 r = 0; r < 12; r++) { 變數 開始 = 目前時間(); 工作(); 次 = push(次, 目前時間() - 開始); };
            印出(結果);
            迴圈 (變數 r = 3; r < 12; r++) { 印出(次[r]); };
            """).Succeeded();
        return (run.Lines[0], run.Lines.Skip(1).Select(t => double.Parse(t, CultureInfo.InvariantCulture)).Order().ToArray());
    }

    [Fact]
    public void MeasureCompilationAndCompiledExecution() {
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_WASM_BENCHMARKS") != "1" || !WasmTestHost.Available) return;
        var programs = new Dictionary<string, (string Source, int Expected)> {
            ["integer loop"] = ("變數 總數 = 0; 迴圈 (變數 i = 0; i < 50000; i++) { 總數 += i; } 總數;", 1249975000),
            ["recursive fibonacci"] = ("變數 費氏 = 函式(n) { 如果 (n < 2) { 回傳 n; } 回傳 費氏(n-1) + 費氏(n-2); }; 費氏(20);", 6765),
            ["closures and arrays"] = ("變數 建立 = 函式(x) { 函式(y) { x + y; }; }; 變數 加 = 建立(3); 變數 a = [0]; 迴圈 (變數 i = 0; i < 20000; i++) { a[0] = 加(a[0]); } a[0];", 60000)
        };
        foreach (var sample in programs) {
            var compileClock = Stopwatch.StartNew();
            new WasmCompiler().Compile(new LoweringCompiler().Compile(sample.Value.Source)); compileClock.Stop();
            var (result, times) = Measure(sample.Value.Source);
            Assert.Equal(sample.Value.Expected.ToString(CultureInfo.InvariantCulture), result);
            output.WriteLine($"{sample.Key}: run median {times[4]:F3} ms, parse + compile {compileClock.Elapsed.TotalMilliseconds:F2} ms");
        }
        // Starting the runtime is part of every application run.
        var program = new WasmCompiler().Compile("0;");
        WasmTestHost.Run(program).Succeeded();
        var starts = Enumerable.Range(0, 9).Select(_ => { var clock = Stopwatch.StartNew(); WasmTestHost.Run(program).Succeeded(); return clock.Elapsed.TotalMilliseconds; }).Order().ToArray();
        output.WriteLine($"runtime start and exit: median {starts[4]:F2} ms");
    }
}
