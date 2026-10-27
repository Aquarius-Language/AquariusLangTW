using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>Native regions: loops of untyped code (top-level scripts and untyped functions) whose bodies only use typed
/// bindings run natively from their loop heads, leave to the generic body at their exit or where the budget runs out, and
/// must give the generic emitter's results and the untyped program's results.</summary>
public class NativeRegionTest
{
    private static readonly string[] Programs = [
        "var n: int = 300; var s: int = 0; for (var i: int = 0; i < n; i++) { for (var j: int = 0; j < i; j++) { s += j % 7; } } s;",
        "var limit: int = 100000; var steps: int = 0; for (var start: int = 1; start <= limit; start++) { for (var v: int = start; v != 1; steps++) { var half = v / 2; if (half * 2 == v) { v = half; } else { v = v * 3 + 1; } } } steps;",
        "var n: int = 60; var a: int[] = Array.Make(n * n, 0); var b: int[] = Array.Make(n * n, 0); for (var i: int = 0; i < n; i++) { for (var j: int = 0; j < n; j++) { var x = i + j; a[i * n + j] = x % 7; b[i * n + j] = (i * 2 + j) % 5; } } var sum: int = 0; for (var i: int = 0; i < n; i++) { for (var j: int = 0; j < n; j++) { var t: int = 0; for (var k: int = 0; k < n; k++) { t += a[i * n + k] * b[k * n + j]; } sum += t; } } sum;",
        "var n: int = 20000; var flags: bool[] = Array.Make(n + 1, true); flags[0] = false; flags[1] = false; for (var p: int = 2; p * p <= n; p++) { if (flags[p]) { for (var j: int = p * p; j <= n; j += p) { flags[j] = false; } } } var count: int = 0; for (var i: int = 2; i <= n; i++) { if (flags[i]) { count += 1; } } count;",
        "var n: int = 120; var sum: int = 0; for (var y: int = 0; y < n; y++) { var ci = 2.0d * y / n - 1.0d; for (var x: int = 0; x < n; x++) { var cr = 3.0d * x / n - 2.0d; var zr = 0.0d; var zi = 0.0d; var c: int = 0; for (var g: int = 0; c < 60 && zr * zr + zi * zi <= 4.0d; c++) { var nr = zr * zr - zi * zi + cr; zi = 2.0d * zr * zi + ci; zr = nr; } sum += c; } } sum;",
        "var found: int = -1; for (var i: int = 2; i < 1000; i++) { if (i * i > 777) { found = i; break; } } found;",
        "var total: double = 0.0d; for (var i: int = 1; i <= 2000; i++) { total += 1.0d / i; } total;",
        "var x: int = 7; for (var i: int = 0; i < 50; i++) { x = x * 1103515245 + 12345; } x;",
        // The loop variable carries out of an untyped function's own scope too.
        "var f = function(n) { var acc: int = 0; for (var i: int = 0; i < n; i++) { acc += i * i; } return acc; }; f(1000);",
    ];

    [RuntimeFact]
    public void RegionsMatchTheGenericEmitter()
    {
        EmitterTiers.AssertSame(Programs);
    }

    [RuntimeFact]
    public void TypedLoopsBecomeRegions()
    {
        var reasons = new List<string>();
        var previous = WasmCompiler.TierDiagnostics;
        WasmCompiler.TierDiagnostics = reasons.Add;
        try { foreach (string program in Programs) new WasmCompiler().Compile(program); }
        finally { WasmCompiler.TierDiagnostics = previous; }
        Assert.True(reasons.Count(r => r.Contains("native region: accepted")) >= Programs.Length, string.Join("\n", reasons));
    }

    [RuntimeFact]
    public void RegionResultsEqualUntypedResults()
    {
        string[] untyped = Programs.Select(p => System.Text.RegularExpressions.Regex.Replace(p, @":\s*(int|double|float|bool)\b(\[\])?", "")).ToArray();
        var typed = WasmTestHost.EvalMany(Programs);
        var plain = WasmTestHost.EvalMany(untyped);
        for (int i = 0; i < Programs.Length; i++) Assert.True(typed[i] == plain[i], $"{Programs[i]}\n  typed:   {typed[i]}\n  untyped: {plain[i]}");
    }

    [RuntimeFact]
    public void RegionsKeepGenericErrorsAndBindings()
    {
        var results = EmitterTiers.AssertSame([
            "var d: int = 5; var s: int = 0; for (var i: int = 0; i < 10; i++) { s += 100 / (d - i); } s;",
            "var a: int[] = [1, 2, 3]; var s: int = 0; for (var i: int = 0; i < 5; i++) { s += a[i]; } s;",
            // A region rereads its bindings at every entry: code between loops may change them.
            "var n: int = 3; var s: int = 0; for (var r = 0; r < 2; r++) { for (var i: int = 0; i < n; i++) { s += i; } n = 10; } s;",
        ]);
        Assert.Contains("Integer division by zero", results[0].Message);
        Assert.Contains("Array index is out of bounds", results[1].Message);
        Assert.Equal(48, results[2].Integer);
    }
}
