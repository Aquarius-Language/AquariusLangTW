using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>Speculation on untyped arrays: an array the TypeChecker guesses holds one scalar type (made by 陣列.建立 /
/// Array.Make with a scalar fill, a literal of one scalar type, or copied from such a variable or parameter) lets a loop
/// that reads or writes it become a native region. The entry checks the array's element kind (Array.kind, kept by every
/// write, found by a scan when unknown); a read outside the array leaves the region at that read, where the generic code
/// gives null. Results never differ from the unspeculated compiler's.</summary>
public class LooseArrayTest
{
    private static readonly string[] Programs = [
        // The guess holds: a matrix product over arrays made with an integer fill.
        "var f = function(n) { var a = Array.Make(n * n, 0); var b = Array.Make(n * n, 0); for (var i = 0; i < n * n; i++) { a[i] = i % 7; b[i] = i % 5; } " +
        "var s = 0; for (var i = 0; i < n; i++) { for (var j = 0; j < n; j++) { for (var k = 0; k < n; k++) { s += a[i * n + k] * b[k * n + j]; } } } return s; }; f(12);",
        // A read past the end inside the loop: the generic code reads null and reports the + of null.
        "var f = function() { var a = Array.Make(4, 1); var s = 0; for (var i = 0; i < 6; i++) { s += a[i]; } return s; }; f();",
        // A read past the end stored in a variable: the loop continues generically with the null.
        "var f = function() { var a = [1, 2, 3]; var last = 0; var seen = 0; for (var i = 0; i < 5; i++) { last = a[i]; seen++; } return [last, seen]; }; f();",
        // The array changes type between entries: a double written by generic code, then integers again.
        "var f = function() { var a = Array.Make(5, 0); var t = 0; for (var k = 0; k < 4; k++) { for (var i = 0; i < 5; i++) { t += a[i] + i; } " +
        "if (k == 0) { a[2] = 0.5d; } if (k == 2) { a[2] = 7; } } return [t, typeOf(t)]; }; f();",
        // Mixed elements from the start: no speculation holds.
        "var f = function() { var a = [1, 2, 3]; Array.Append(a, \"x\"); var c = 0; for (var i = 0; i < 3; i++) { c += a[i]; } return [c, a]; }; f();",
        // Booleans and doubles, a guessed parameter, and writes in the region.
        "var sieve = function(n) { var flags = Array.Make(n + 1, true); flags[0] = false; flags[1] = false; for (var p = 2; p * p <= n; p++) { if (flags[p]) { " +
        "for (var j = p * p; j <= n; j += p) { flags[j] = false; } } } var c = 0; for (var i = 2; i <= n; i++) { if (flags[i]) { c++; } } return c; }; sieve(1000);",
        "var scale = function(v, k) { for (var i = 0; i < len(v); i++) { v[i] = v[i] * k; } return v; }; scale([1.5d, 2.0d, 0.25d], 2.0d);",
        // A typed array passed to an untyped parameter guessed as an integer array.
        "var sum = function(v) { var s = 0; for (var i = 0; i < len(v); i++) { s += v[i]; } return s; }; var t: int[] = [4, 5, 6]; [sum(t), sum([1, 2])];",
        // Library changes between loops keep or reset the kind: Fill, Sort, AppendArray with doubles, Copy.
        "var f = function() { var a = Array.Make(6, 3); var r = []; var g = function() { var s = 0; for (var i = 0; i < len(a); i++) { s += a[i]; } return s; }; " +
        "Array.Append(r, g()); Array.Fill(a, 2); Array.Append(r, g()); Array.AppendArray(a, [0.5d]); Array.Append(r, g()); return r; }; f();",
        "var f = function() { var a = Array.Make(6, 0); for (var i = 0; i < 6; i++) { a[i] = 6 - i; } Array.Sort(a); var s = 0; for (var i = 0; i < 6; i++) { s = s * 10 + a[i]; } return s; }; f();",
        // An untyped quicksort: the partition loops over a guessed parameter array.
        "var sort = function(a, lo, hi) { var i = lo; var j = hi; var pivot = a[(lo + hi) / 2]; for (var g = 0; i <= j; 0) { for (var l = 0; a[i] < pivot; i++) { } " +
        "for (var r = 0; a[j] > pivot; j -= 1) { } if (i <= j) { var t = a[i]; a[i] = a[j]; a[j] = t; i++; j -= 1; } } if (lo < j) { sort(a, lo, j); } if (i < hi) { sort(a, i, hi); } }; " +
        "var v = Array.Make(0, 0, 300); var seed = 42; for (var i = 0; i < 300; i++) { seed = (seed * 251 + 17) % 65536; Array.Append(v, seed); } sort(v, 0, 299); [v[0], v[150], v[299]];",
    ];

    [RuntimeFact]
    public void LooseArraysMatchTheUnspeculatedCompiler()
    {
        var results = EmitterTiers.AssertSame(Programs);
        Assert.True(results[1].IsError);
        Assert.Equal("[null, 5]", results[2].Text);
        Assert.Equal("[6, [1, 2, 3, x]]", results[4].Text);
        Assert.Equal(168, results[5].Integer);
        Assert.Equal("[3, 4, 0.5]", results[6].Text);
        Assert.Equal("[15, 3]", results[7].Text);
        Assert.Equal("[18, 12, 12]", results[8].Text);
        Assert.Equal(123456, results[9].Integer);
    }

    [RuntimeFact]
    public void LoopsOverGuessedArraysBecomeNativeRegions()
    {
        var reasons = new List<string>();
        var previous = WasmCompiler.TierDiagnostics;
        WasmCompiler.TierDiagnostics = reasons.Add;
        try { new WasmCompiler().Compile(Programs[0]); new WasmCompiler().Compile(Programs[10]); }
        finally { WasmCompiler.TierDiagnostics = previous; }
        Assert.True(reasons.Count(r => r.Contains("native region: accepted")) >= 3, string.Join("\n", reasons));
    }

    [RuntimeFact]
    public void LooseArraysGiveTheSameOutputUnderAnyQuantum()
    {
        string program = "var f = function(n) { var a = Array.Make(n, 0); var t = 0.0d; for (var k = 0; k < 30; k++) { for (var i = 0; i < n + 2; i++) { var x = a[i]; " +
            "if (i < n) { a[i] = x + k; t += x; } } if (k % 7 == 3) { a[1] = 0.5d; } if (k % 7 == 5) { a[1] = 1; } } return t; }; print(f(400));";
        string expected = WasmTestHost.Run(new WasmCompiler(false).Compile(program)).Succeeded().Output;
        foreach (string quantum in new[] { "1", "17", "65536" })
            Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum }).Succeeded().Output);
        Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_JIT"] = "0" }).Succeeded().Output);
    }
}
