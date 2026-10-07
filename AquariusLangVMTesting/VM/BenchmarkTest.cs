using System.Diagnostics;
using AquariusLang.evaluator;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using AquariusLang.VM;
using Xunit.Abstractions;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLangVMTesting.VM;

// Opt-in measurements: correctness never depends on machine speed or a timing threshold.
public class BenchmarkTest {
    private readonly ITestOutputHelper output;
    public BenchmarkTest(ITestOutputHelper output) { this.output = output; }

    [Fact]
    public void CompareCompiledExecutionWithTreeInterpretation() {
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_VM_BENCHMARKS") != "1") return;
        var programs = new Dictionary<string, string> {
            ["integer loop"] = "變數 總數 = 0; 迴圈 (變數 i = 0; i < 50000; i++) { 總數 += i; } 總數;",
            ["recursive fibonacci"] = "變數 費氏 = 函式(n) { 如果 (n < 2) { 回傳 n; } 回傳 費氏(n-1) + 費氏(n-2); }; 費氏(20);",
            ["closures and arrays"] = "變數 建立 = 函式(x) { 函式(y) { x + y; }; }; 變數 加 = 建立(3); 變數 a = [0]; 迴圈 (變數 i = 0; i < 20000; i++) { a[0] = 加(a[0]); } a[0];"
        };
        foreach (var sample in programs) {
            var parser = Parser.NewInstance(Lexer.NewInstance(sample.Value));
            var tree = parser.ParseAST(); Assert.Empty(parser.Errors);
            var compileClock = Stopwatch.StartNew();
            var program = new VmCompiler().Compile(tree); compileClock.Stop();
            var evaluator = AquariusLang.evaluator.Evaluator.NewInstance(new Builtins());
            var machine = new VirtualMachine();
            Func<IObject> interpret = () => evaluator.Eval(tree, AquaEnvironment.NewEnvironment());
            Func<IObject> execute = () => machine.Execute(program, AquaEnvironment.NewEnvironment());
            Assert.Equal(interpret().Inspect(), execute().Inspect());
            for (int i = 0; i < 3; i++) { interpret(); execute(); }
            var treeTimes = new List<double>(); var vmTimes = new List<double>();
            for (int i = 0; i < 9; i++) {
                if (i % 2 == 0) { treeTimes.Add(Measure(interpret)); vmTimes.Add(Measure(execute)); }
                else { vmTimes.Add(Measure(execute)); treeTimes.Add(Measure(interpret)); }
            }
            treeTimes.Sort(); vmTimes.Sort();
            output.WriteLine($"{sample.Key}: tree {treeTimes[4]:F2} ms, VM {vmTimes[4]:F2} ms, speedup {treeTimes[4] / vmTimes[4]:F2}x, compile {compileClock.Elapsed.TotalMilliseconds:F2} ms");
        }
    }

    private static double Measure(Func<IObject> action) {
        var clock = Stopwatch.StartNew(); action(); clock.Stop(); return clock.Elapsed.TotalMilliseconds;
    }
}
