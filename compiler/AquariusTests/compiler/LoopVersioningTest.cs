using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>Versioned loops (WasmLoopVersioning): an innermost counted loop runs a fast copy, without its head's fuel
/// checkpoint and with indices proven in range unchecked, only when a test before the loop proves both. Every program
/// must give the generic emitter's result: out-of-range indices (at either end, with negative coefficients or through
/// 32-bit wrapping) fall back to the checked loop and raise the same error, and breaks, continues, inlined calls, outer
/// bounds, empty ranges and writes behave as before, under any quantum.</summary>
public class LoopVersioningTest
{
    private const string Sum = "var sum = function(a: int[], n: int): int { var s: int = 0; for (var k: int = 0; k < n; k++) { s += a[k] * 3 + a[n - 1 - k]; } return s; }; ";

    private static readonly string[] Programs = [
        Sum + "var a: int[] = Array.Make(100, 2); for (var i: int = 0; i < 100; i++) { a[i] = i * i % 17; } sum(a, 100);",
        Sum + "var a: int[] = Array.Make(10, 2); sum(a, 11);",
        Sum + "var a: int[] = Array.Make(10, 2); sum(a, 0);",
        "var wrap = function(a: int[]): int { var s: int = 0; for (var k: int = 0; k < 5; k++) { if (k % 4 == 0) { s += a[k * 1073741824 + 5]; } } return s; }; var a: int[] = Array.Make(8, 3); a[5] = 11; wrap(a);",
        "var stop = function(a: int[], n: int): int { var s: int = 0; for (var k: int = 2; k < n; k++) { if (a[k] < 0) { break; } if (k % 2 == 0) { continue; } s += a[k] * k; } return s; }; var a: int[] = Array.Make(50, 4); a[37] = -1; [stop(a, 50), stop(a, 30), stop(a, 1)];",
        "var mirror = function(a: int[], b: int[], n: int): int { for (var k: int = 0; k < n; k++) { b[k] = a[k] + b[n - 1 - k]; } return b[0] + b[n - 1]; }; var a: int[] = Array.Make(40, 5); var b: int[] = Array.Make(40, 1); mirror(a, b, 40);",
        "var mirror = function(a: int[], b: int[], n: int): int { for (var k: int = 0; k < n; k++) { b[k + 1] = a[k]; } return b[1]; }; var a: int[] = Array.Make(9, 5); var b: int[] = Array.Make(9, 1); mirror(a, b, 9);",
        "var sq = function(x: int): int { return x * x; }; var squares = function(a: int[], n: int): int { var s: int = 0; for (var k: int = 0; k < n; k++) { s += sq(a[k]) + sq(k); } return s; }; var a: int[] = Array.Make(300, 7); squares(a, 300);",
        "var m: int = 50; var outer = function(a: int[]): int { var s: int = 0; for (var k: int = 0; k < m; k++) { s += a[k]; m = m + 0; } return s; }; var a: int[] = Array.Make(60, 1); outer(a);",
        "const width = 12; var grid = function(a: double[]): double { var t: double = 0.0d; for (var r: int = 0; r < width; r++) { for (var c: int = 0; c < width; c++) { t += a[r * width + c] * c - a[c * width + r]; } } return t; }; " +
            "var a: double[] = Array.Make(144, 0.5d); for (var i: int = 0; i < 144; i++) { a[i] = i * 0.25d; } grid(a);",
        "var divide = function(a: int[], n: int): int { var s: int = 0; for (var k: int = 0; k < n; k++) { s += 100 / a[k]; } return s; }; var a: int[] = Array.Make(20, 3); a[13] = 0; divide(a, 20);",
        "var skip = function(a: int[], n: int): int { var s: int = 0; for (var k: int = 0; k < n; k++) { s += a[k]; k += 1; } return s; }; var a: int[] = Array.Make(31, 2); skip(a, 31);",
        "var big = function(n: int): int { var s: int = 0; for (var k: int = 2147483640; k < n; k++) { s += k % 5; } return s; }; big(2147483647);",
        "var tri = function(a: int[], n: int): int { var s: int = 0; for (var i: int = 0; i < n; i++) { for (var j: int = 0; j < i; j++) { s += a[i * n + j] - a[j * n + i]; } } return s; }; " +
            "var a: int[] = Array.Make(400, 0); for (var i: int = 0; i < 400; i++) { a[i] = i % 13; } tri(a, 20);",
    ];

    [RuntimeFact]
    public void VersionedLoopsMatchTheGenericEmitter()
    {
        var results = EmitterTiers.AssertSame(Programs);
        Assert.True(results[1].IsError);
        Assert.Equal(0, results[2].Integer);
        Assert.Equal(22, results[3].Integer);
        Assert.True(results[6].IsError);
        Assert.True(results[10].IsError);
    }

    [RuntimeFact]
    public void VersionedLoopsGiveThePortableResultsUnderAnyQuantum()
    {
        string program = Sum + "var sq = function(x: int): int { return x * x; }; " +
            "var run = function(a: int[], n: int): int { var t: int = 0; for (var r: int = 0; r < 40; r++) { for (var k: int = 0; k < n; k++) { t += sq(a[k]) - a[n - 1 - k]; } t += sum(a, n); } return t; }; " +
            "var a: int[] = Array.Make(500, 1); for (var i: int = 0; i < 500; i++) { a[i] = i % 23; } print(run(a, 500), run(a, 7));";
        string expected = WasmTestHost.Run(new WasmCompiler(false).Compile(program)).Succeeded().Output;
        foreach (string quantum in new[] { "1", "3", "17", "65536", "1048576" })
            Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum }).Succeeded().Output);
    }
}
