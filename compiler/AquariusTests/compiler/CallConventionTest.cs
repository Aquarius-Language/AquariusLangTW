using AquariusLang.Compiler;
using AquariusLang.Wasm;
using Xunit.Abstractions;

namespace AquariusTests.Compiler;

/// <summary>Call conventions of compiled programs on the native runtime: argument windows, evaluation order, nested and
/// recursive calls inside arguments, and errors raised by builtins and host functions. (The C# tests of owned/borrowed
/// C# builtin argument arrays, host environments and C# value mirrors were removed with the C# runtime; host calls are
/// now runtime/host/calls.c.)</summary>
public class CallConventionTest {
    private readonly ITestOutputHelper output;
    public CallConventionTest(ITestOutputHelper output) { this.output = output; }

    [RuntimeFact]
    public void ArgumentsRemainIndependentAndMayBeRetained() {
        // A function keeping its arguments (the C# test kept a C# builtin's argument arrays): each call's stay its own.
        Assert.Equal(new TestValue("array", "[42, 99, 42, 假, 2]"), WasmTestHost.Eval("""
            變數 saved=[];變數 保存=函式(x){變數 arguments=[x];saved=push(saved,arguments);x;};
            保存(7);變數 result=保存(42);saved[0][0]=99;
            [result,saved[0][0],saved[1][0],saved[0]==saved[1],長度(saved)];
            """));
    }

    [RuntimeFact]
    public void RetainedArraysOutliveCollections() {
        // The C# test kept 300 arrays of 8192 elements in a host builtin; a program keeping them itself must still see
        // every one after the collections their allocation causes, and aliases stay the same array.
        Assert.Equal("[8192, 99, 99, 真, 300, 299]\n", WasmTestHost.Output("""
            變數 kept=陣列.建立(0,0,300);迴圈(變數 i=0;i<300;i++){陣列.追加(kept,陣列.建立(8192,i));}
            變數 first=kept[0];變數 copy=kept[0];first[0]=99;[陣列.長度(first),copy[0],first[0],first==copy,長度(kept),kept[299][0]];
            """));
    }

    [RuntimeFact]
    public void CallsSurviveReentryAndNestedCalls() {
        // A higher-order call whose callback recurses 500 deep, nested inside an expression with pending operands.
        Assert.Equal(new TestValue("integer", "42"), WasmTestHost.Eval("""
            變數 加 = 函式(x,y) { 回傳 x+y; };
            變數 遞迴 = 函式(n) { 如果(n==0){回傳 加(10,20);} 回傳 遞迴(n-1); };
            變數 回呼 = 函式(f, x) { 變數 before = x; 變數 result = f(x); 如果 (before != x) { 回傳 -1; } 回傳 result; };
            1 + 回呼(函式(n) { 回傳 n+遞迴(500); }, 加(3,4)) + 4;
            """));
    }

    [RuntimeFact]
    public void LargeArgumentWindowsAndResultAliasing() {
        // 257 arguments; the last one is an array returned as the same array (aliasing), not a copy.
        string parameters = string.Join(",", Enumerable.Range(0, 256).Select(i => "p" + i));
        string source = $"變數 末值=函式({parameters},last){{last;}};變數 a=[42]; 變數 b=末值(" + string.Join(",", Enumerable.Repeat("1", 256)) + ",a); b[0]=7; a[0];";
        Assert.Equal(new TestValue("integer", "7"), WasmTestHost.Eval(source));
        // A builtin given many arguments reports its arity instead of reading past them.
        Assert.True(WasmTestHost.Eval("長度(" + string.Join(",", Enumerable.Repeat("[1]", 256)) + ");").IsError);
    }

    [RuntimeFact]
    public void BilingualBuiltinAliasesAreTheSameFunction() {
        // The C# test redefined a C# builtin registered under both names; natively every bilingual builtin is one value.
        Assert.Equal(new TestValue("array", "[真, 真, 真, 5]"), WasmTestHost.Eval("[長度==len,型別==typeOf,印出==print,長度(\"繁體\")+len([1,2,3])];"));
    }

    [RuntimeFact]
    public void CallsPreserveArgumentEvaluationOrderAndSerializedPrograms() {
        var program = new LoweringCompiler().Compile("""
            變數 次=0;
            變數 更新=函式(){次++;回傳 次;};
            變數 合=函式(x,y,z){回傳 x*100+y*10+z;};
            [合(更新(),更新(),更新()),次];
            """);
        var wasm = new WasmCompiler().Compile(program);
        foreach (var candidate in new[] { wasm, WasmProgram.Load(wasm.Bytes.ToArray()) })
            Assert.Equal("[123, 3]\n", WasmTestHost.Run(candidate).Succeeded().Output);
    }

    [RuntimeTheory]
    [InlineData("len(1)", "Length requires")]                      // a builtin the guest runtime executes
    [InlineData("Thread.Wait(1)", "Expected a thread handle")]    // a host library function
    [InlineData("型別()", "argument")]                           // a host function's arity check
    public void NativeErrorsStopBeforeSubsequentSideEffects(string call, string message) {
        var run = WasmTestHost.Run($"變數 次=0; 印出(\"before\"); {call}; 次++; 印出(\"after\");");
        Assert.Equal(1, run.ExitCode);
        Assert.Equal("before", run.Lines[0]);
        Assert.Equal(2, run.Lines.Count);
        Assert.StartsWith("ERROR: ", run.Lines[1]);
        Assert.Contains(message, run.Lines[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostCallBenchmark() {
        // Opt-in: compares calls of a builtin the guest runtime executes (len) with calls of a host function (型別).
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_WASM_BENCHMARKS") != "1" || !WasmTestHost.Available) return;
        string Body(string call) => $"變數 總=0; 迴圈(變數 i=0;i<20000;i++) {{ 總+={call}; 總+={call}; 總+={call}; 總+={call}; }} 總;";
        foreach (var (name, call, expected) in new[] { ("guest builtin", "len([i,1,2,3])", "320000"), ("host function", "len(型別(i))", "560000") }) {
            var (result, times) = BenchmarkTest.Measure(Body(call));
            Assert.Equal(expected, result);
            output.WriteLine($"{name}: median {times[4]:F3} ms for 80000 calls");
        }
    }
}
