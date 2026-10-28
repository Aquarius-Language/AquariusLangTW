using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>陣列.追加 / Array.Append in the native tier: native functions and regions append to typed (and loose) arrays
/// through the runtime instead of leaving for the generic code, and every array local is described again afterwards (the
/// count and possibly the storage change, also for other names of the same array and after a call that appends). Results
/// equal the unoptimized compiler's.</summary>
public class NativeArrayAppendTest
{
    private static readonly string[] Programs = [
        // 0: a typed function filling a typed array, then reading it.
        "var fill = function(n: int): int { var a: int[] = Array.Make(0, 0, n); for (var i: int = 0; i < n; i++) { Array.Append(a, i * i); } " +
        "var t: int = 0; for (var i: int = 0; i < len(a); i++) { t += a[i]; } return t + len(a); }; fill(1000);",
        // 1: Chinese names, the loop of an untyped body (a region), growing past the initial capacity.
        "變數 跑 = 函式() { 變數 值: 整數[] = 陣列.建立(0, 0); 變數 種子: 整數 = 42; 迴圈 (變數 i: 整數 = 0; i < 5000; i++) { 種子 = (種子 * 251 + 17) % 65536; 陣列.追加(值, 種子); } " +
        "回傳 [長度(值), 值[0], 值[4999]]; }; 跑();",
        // 2: another name for the same array sees the new count and elements.
        "var alias = function(n: int): int { var a: int[] = Array.Make(1, 7); var b: int[] = a; for (var i: int = 0; i < n; i++) { Array.Append(a, i); } return len(b) * 1000 + b[n]; }; alias(20);",
        // 3: a callee appends; the caller's companions are read again after the call.
        "var push = function(a: int[], v: int): int { Array.Append(a, v); return len(a); }; " +
        "var outer = function(a: int[], n: int): int { var s: int = 0; for (var i: int = 0; i < n; i++) { s += push(a, i); s += a[len(a) - 1]; } return s; }; var start: int[] = Array.Make(0, 0); outer(start, 100);",
        // 4: widening like the generic element check.
        "var widen = function(): array { var f: float[] = Array.Make(0, 0.5f); var d: double[] = Array.Make(0, 0.5d); Array.Append(f, 3); Array.Append(f, 0.1f); " +
        "Array.Append(d, 7); Array.Append(d, 0.1f); Array.Append(d, 0.1d); return [f, d]; }; widen();",
        // 5: Boolean arrays.
        "var flags = function(n: int): int { var b: bool[] = Array.Make(0, true); for (var i: int = 0; i < n; i++) { Array.Append(b, i % 3 == 0); } " +
        "var c: int = 0; for (var i: int = 0; i < len(b); i++) { if (b[i]) { c++; } } return c; }; flags(100);",
        // 6: an element of another type stays generic and reports the generic error.
        "var bad = function(a: int[]): int { var x = \"s\"; Array.Append(a, x); return len(a); }; bad(Array.Make(0, 0));",
        // 7: a user's Array shadows the module: the generic code reports using it.
        "var Array = 1; var f = function(n: int): int { var a: int[] = [1]; Array.Append(a, n); return len(a); }; f(3);",
        // 8: an outer typed array binding appended inside a function, read by its caller.
        "var board: int[] = Array.Make(0, 0); var grow = function(n: int): int { for (var i: int = 0; i < n; i++) { Array.Append(board, i + 1); } return len(board); }; " +
        "var total = function(): int { var t: int = grow(10) + grow(5); return t * 100 + board[14]; }; total();",
        // 9: the result of Append is the array itself.
        "var same = function(): int { var a: int[] = Array.Make(0, 0); var r: int[] = Array.Append(a, 5); Array.Append(r, 6); return len(a) * 10 + a[1]; }; same();",
        // 10: a clone's loose (untyped) array.
        "var addAll = function(a, n) { for (var i = 0; i < n; i++) { Array.Append(a, i); } return len(a); }; var xs = [1, 2, 3]; [addAll(xs, 4), xs];",
        // 11: a copy keeps its own elements (shared storage copies on append).
        "var copies = function(): array { var a: int[] = Array.Make(2, 1); var b: int[] = Array.Clone(a); Array.Append(b, 9); Array.Append(a, 8); return [a, b]; }; copies();",
        // 12: appends inside a loop whose bound reads the growing length.
        "var growing = function(): int { var a: int[] = Array.Make(1, 1); for (var i: int = 0; i < len(a) && len(a) < 64; i++) { Array.Append(a, a[i] * 2); } return a[63]; }; growing();",
        // 13: an untyped value from untyped code is checked like the generic element check.
        "var mk = function(v) { return v; }; var put = function(a: int[]): int { Array.Append(a, mk(3)); return len(a); }; put(Array.Make(0, 0));",
    ];

    [RuntimeFact]
    public void ArrayAppendsMatchTheUnoptimizedCompiler()
    {
        var results = EmitterTiers.AssertSame(Programs);
        // Every program runs (only 6 and 7 are meant to fail), so no case passes by failing the same way twice.
        for (int i = 0; i < Programs.Length; i++) Assert.True(results[i].IsError == (i is 6 or 7), $"program {i}: {results[i]}");
        Assert.Equal(332833500 + 1000, results[0].Integer);
        Assert.Equal(21019, results[2].Integer);
        Assert.Equal(2505, results[8].Integer);
        Assert.Equal(26, results[9].Integer);
        Assert.Equal("[7, [1, 2, 3, 0, 1, 2, 3]]", results[10].Text);
        Assert.Equal("[[1, 1, 8], [1, 1, 9]]", results[11].Text);
        Assert.True(results[6].IsError);
        Assert.True(results[7].IsError);
    }

    [RuntimeFact]
    public void TypedAppendLoopsTakeTheNativeTier()
    {
        static List<string> Reasons(params int[] programs) {
            var reasons = new List<string>();
            var previous = WasmCompiler.TierDiagnostics;
            WasmCompiler.TierDiagnostics = reasons.Add;
            try { foreach (int i in programs) new WasmCompiler().Compile(Programs[i]); }
            finally { WasmCompiler.TierDiagnostics = previous; }
            return reasons;
        }
        // Typed functions that append run natively, also with 陣列.建立 / Array.Make straight into a typed annotation, and so
        // does the untyped function of program 1 (as a clone).
        Assert.DoesNotContain(Reasons(0, 2, 3, 5, 8, 9), r => r.Contains("native: rejected") && !r.Contains("function 0:"));
        Assert.Contains(Reasons(1), r => r.Contains("native: accepted"));
    }

    [RuntimeFact]
    public void AppendLoopsCollectAndGiveTheSameOutputUnderAnyQuantum()
    {
        // Enough appends to arm collections while the loop runs (it polls at its head) and to copy storage many times.
        string program = "var churn = function(n: int): int { var t: int = 0; for (var r: int = 0; r < 40; r++) { var a: int[] = Array.Make(0, 0); " +
            "for (var i: int = 0; i < n; i++) { Array.Append(a, i); } var keep: int[] = a; t += len(keep) + keep[n - 1]; } return t; }; " +
            "var top = []; for (var i = 0; i < 30000; i++) { Array.Append(top, i); } print(churn(20000), len(top));";
        string expected = WasmTestHost.Run(new WasmCompiler(false).Compile(program)).Succeeded().Output;
        foreach (string quantum in new[] { "1", "17", "65536" })
            Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum }).Succeeded().Output);
        Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_JIT"] = "0" }).Succeeded().Output);
    }
}
