using System.Text.RegularExpressions;
using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>Packed typed arrays: an array locked to int, float, double or bool stores only its elements' payloads
/// (runtime/guest/runtime.c, array_get). Every program here runs with its array annotations (packed) and without them
/// (whole Values), with both emitters: all four runs must give the same value, so packing never changes a result.
/// Programs whose annotations are the point (type errors) are checked separately.</summary>
public class PackedArrayTest
{
    private static readonly string[] Programs = [
        // 0-3: reads, writes, compound assignments and increments in typed functions (native) and at top level (generic).
        "var f = function(n: int): array { var a: int[] = Array.Make(n, 0); for (var i: int = 0; i < n; i++) { a[i] = i * 3 - 7; } " +
        "a[2] += 100; a[3] -= 5; a[4] *= 2; a[5] /= 2; a[6] %= 4; a[7]++; ++a[8]; var s: int = 0; for (var i: int = 0; i < n; i++) { s += a[i]; } return [s, a]; }; f(12);",
        "var a: double[] = Array.Make(5, 0.5d); a[1] = 2.25d; a[2] += 1; a[3] *= 3; a[4]++; var g = function(x: double[]): double { x[0] -= 0.125d; return x[0] + x[1] + x[2] + x[3] + x[4]; }; [g(a), a];",
        "var a: float[] = Array.Make(4, 1.5f); a[1] = 2; a[2] += 0.1f; a[3]++; var g = function(x: float[], k: int): float { x[k] *= 3; return x[k]; }; [g(a, 2), a];",
        "var b: bool[] = Array.Make(6, false); b[1] = true; b[4] = 1 < 2; var c = function(x: bool[]): int { var n: int = 0; for (var i: int = 0; i < len(x); i++) { if (x[i]) { n++; } } x[5] = !x[5]; return n; }; [c(b), b];",
        // 4-6: the Array library on every packed type.
        "var a: int[] = Array.Make(3, 7, 10); [len(a), Array.Cap(a) >= 10, Array.Append(a, 9), Array.Grow(a, 20), Array.Cap(a) >= 24, Array.Index(a, 9), Array.Contains(a, 8), Array.Reverse(a), Array.Fill(Array.Clone(a), 4), Array.Slice(a, 1, 3)];",
        "var d: double[] = [1.5d, 2.5d, 3.5d, 4.5d]; var e: double[] = [9.0d]; Array.AppendArray(d, e); Array.AppendArray(d, d); Array.Delete(d, 1, 3); [d, Array.Sum(d), Array.Min(d), Array.Max(d), Array.Sort(d), Array.Index(d, 9.0d)];",
        "var b: bool[] = [true, false, true]; var c = Array.Clone(b); Array.Append(c, 5); Array.Append(b, false); [b, c, Array.Contains(b, false), Array.Index(b, true), Array.Reverse(b), Array.Delete(b, 0, 1)];",
        // 7-9: copies between packed and whole-Value arrays, overlapping copies, widening.
        "var a: int[] = [1, 2, 3, 4, 5, 6]; var u = [10, 20, 30]; [Array.Copy(a, u, 1), a, Array.Copy(u, a, 0, 2), u, Array.Copy(a, a, 2, 0, 3), a, Array.Copy(a, a, 0, 3), a];",
        "var d: double[] = Array.Make(4, 0.0d); var i: int[] = [1, 2, 3]; var f: float[] = [0.5f, 1.25f]; Array.Copy(d, i); Array.Copy(d, f, 3); Array.AppendArray(d, i); Array.Append(d, 2); Array.Append(d, 0.75f); d;",
        "var f: float[] = [1.0f]; Array.AppendArray(f, [2, 3]); Array.Append(f, 7); Array.Fill(f, 2); f;",
        // 10-12: sorting and the kernels that call back into Aquarius.
        "var a: int[] = [5, -3, 9, 0, 2, 2, 100, -50]; var b: int[] = Array.Clone(a); Array.Sort(a); [a, Array.Sort(b, function(x, y) { return y - x; }), Array.Sum(a), Array.Min(a), Array.Max(a)];",
        "var f: float[] = [2.5f, -1.0f, 0.25f]; var d: double[] = [3.5d, 0.0d / 0.0d, -2.0d]; [Array.Sort(f), Array.Sum(f), Array.Min(d), Array.Max(f), Array.Map(f, function(x) { return x * 2; }), Array.Filter(f, function(x) { return x > 0; }), Array.Reduce(f, function(s, x) { return s + x; }, 0)];",
        "var big: int[] = Array.Make(0, 0); var s = 42; for (var i = 0; i < 3000; i++) { s = (s * 251 + 17) % 65536; Array.Append(big, s); } Array.Sort(big); var ok = true; for (var i = 1; i < len(big); i++) { if (big[i] < big[i - 1]) { ok = false; } } [ok, big[0], big[2999], Array.Sum(big)];",
        // 13-15: typed arrays in untyped code, clones, arrays of any values (陣列), struct fields and hashes.
        "var a: int[] = [1, 2, 3]; var touch = function(x, k) { x[k] = x[k] * 10; x[0] += 1; x[1]++; return x[k]; }; var sum = function(x) { var s = 0; for (var i = 0; i < len(x); i++) { s += x[i]; } return s; }; [touch(a, 2), sum(a), a];",
        "var b: bool[] = [true, false]; var f: float[] = [1.5f, 2.5f]; var any = function(x: array, k: int): array { return [x[k], typeOf(x[k])]; }; [any(b, 1), any(f, 0), any([1, \"s\"], 1)];",
        "struct Box { items: int[], label }; var box = Box([4, 5, 6], \"b\"); box.items[1] = 50; var h = { \"k\": box.items }; h[\"k\"][2] += 1; [box.items, h[\"k\"], Hash.Values(h)];",
        // 16-18: locking an untyped array (one object, so other names see the lock), printing, joining, strings of numbers.
        "var u = [1, 2, 3]; var t: int[] = u; t[0] = 9; Array.Append(u, 4); [u, t, len(u), String.Join(t, \"-\")];",
        "var rows: double[] = [0.1d, 1000000.5d, -0.0d]; var b: bool[] = [true]; print(rows, b); [String.Join(rows, \",\"), String.From(rows[0])];",
        "var s: string[] = [\"a\", \"b\"]; Array.Append(s, \"c\"); s[0] = s[0] + s[1]; [s, String.Join(s, \"\"), Array.Index(s, \"c\")];",
        // 19-21: the functional builtins, floodFill and nested typed arrays in arrays of any values.
        "var a: int[] = [1, 2, 3]; [last(a), rest(a), push(a, 4), a];",
        "var grid: int[] = [0, 0, 1, 0, 0, 1, 1, 1, 0]; [floodFill(grid, 3, 3, 0, 0, 7), grid];",
        "var m = [Array.Make(2, 0), Array.Make(2, 0)]; var set = function(r: int[], v: int): int { r[0] = v; r[1] = v * v; return r[1]; }; [set(m[0], 3), set(m[1], 4), m];",
        // 22-23: growth and copy-on-write while another array shares the storage.
        "var a: int[] = Array.Make(2, 1); var b = rest(a); var c = push(a, 3); a[0] = 99; Array.Append(a, 5); [a, b, c];",
        "var grow = function(n: int): int { var a: int[] = Array.Make(0, 0); for (var i: int = 0; i < n; i++) { Array.Append(a, i); var keep: int[] = a; keep[i] = keep[i] + 1; } var s: int = 0; for (var i: int = 0; i < n; i++) { s += a[i]; } return s; }; grow(5000);",
    ];

    /// <summary>The same program with every array annotation removed: its arrays stay whole Values.</summary>
    private static string Unpacked(string source) => Regex.Replace(source, @"\s*:\s*(int|float|double|bool|string)\[\]", "");

    [RuntimeFact]
    public void PackedArraysGiveTheResultsOfWholeValueArrays()
    {
        var packed = EmitterTiers.AssertSame(Programs);
        var whole = EmitterTiers.AssertSame(Programs.Select(Unpacked).ToArray());
        var mismatches = Enumerable.Range(0, Programs.Length).Where(i => packed[i] != whole[i])
            .Select(i => $"program {i}: {Programs[i]}\n  packed: {packed[i]}\n  whole:  {whole[i]}").ToList();
        Assert.True(mismatches.Count == 0, "Packed arrays changed a result:\n" + string.Join('\n', mismatches));
        for (int i = 0; i < Programs.Length; i++) Assert.False(packed[i].IsError, $"program {i}: {packed[i]}");
        Assert.Equal("[4, [7, 7, 1, 7, 7, 1, 1, 1, 0]]", packed[20].Text);
    }

    private static readonly string[] Errors = [
        // A typed array keeps its type: wrong elements, reads and writes outside it are the generic errors.
        "var a: int[] = [1, 2]; a[0] = 1.5d; a;",
        "var a: int[] = [1, 2]; Array.Append(a, \"x\"); a;",
        "var a: bool[] = [true]; Array.Fill(a, 1); a;",
        "var a: int[] = [1, 2]; Array.AppendArray(a, [3, \"x\"]); a;",
        "var f = function(a: int[]): int { return a[5]; }; f([1, 2]);",
        "var f = function(a: double[]): double { a[2] = 1.0d; return a[0]; }; f([1.0d]);",
        "var a: double[] = Array.Make(2, 0); a;",
        "var a: int[] = [1, 2]; Array.Copy(a, [1.5d]); a;",
        "var g: int[] = [1, 2, 3, 4]; floodFill(g, 2, 2, 0, 0, 2.5d);",
        "var f = function(n: int): int { var a: int[] = Array.Make(n, 0); return len(a); }; f(-1);",
        "var f = function(n: int): int { var a: int[] = Array.Make(2, 0, n); return len(a); }; f(1);",
    ];

    [RuntimeFact]
    public void PackedArraysKeepTheirTypeErrors()
    {
        var results = EmitterTiers.AssertSame(Errors);
        for (int i = 0; i < Errors.Length; i++) Assert.True(results[i].IsError, $"error case {i}: {results[i]}");
    }

    [RuntimeFact]
    public void TypedArrayLoopsTakeTheNativeTier()
    {
        var reasons = new List<string>();
        var previous = WasmCompiler.TierDiagnostics;
        WasmCompiler.TierDiagnostics = reasons.Add;
        try {
            new WasmCompiler().Compile("var f = function(n: int): int { var a: bool[] = Array.Make(n + 1, true); var c: int = 0; for (var i: int = 2; i <= n; i++) { if (a[i]) { c++; for (var j: int = i * i; j <= n; j += i) { a[j] = false; } } } return c; }; f(100);");
            new WasmCompiler().Compile("var f = function(n: int): double { var a: double[] = Array.Make(n, 1.0d, n * 2); var s: double = 0.0d; for (var i: int = 0; i < n; i++) { a[i] *= 1.5d; s += a[i]; } return s; }; f(10);");
        } finally { WasmCompiler.TierDiagnostics = previous; }
        Assert.DoesNotContain(reasons, r => r.Contains("native: rejected"));
    }

    [RuntimeFact]
    public void PackedArraysSurviveCollectionsThreadsAndAnyQuantum()
    {
        // Typed arrays allocated, locked, grown and dropped while collections run; one goes to a thread and comes back.
        string program = "var churn = function(n: int): int { var t: int = 0; for (var r: int = 0; r < 30; r++) { var a: int[] = Array.Make(n, r); " +
            "var d: double[] = Array.Make(0, 0.0d); for (var i: int = 0; i < n; i++) { a[i] += i; Array.Append(d, i * 0.5d); } var u = Array.Clone(a); " +
            "var locked: int[] = u; t += locked[n - 1] + len(d); } return t; }; " +
            "var work = function(xs) { var s = 0; for (var i = 0; i < len(xs); i++) { s += xs[i]; } return [s, xs]; }; " +
            "var args: int[] = [4, 5, 6]; var h = Thread.Start(function(a, b, c) { var r: double[] = [a * 1.5d, b * 1.0d]; return [a + b + c, r]; }, args); " +
            "print(churn(20000), Thread.Result(h), work(args)); Thread.Close(h);";
        string expected = WasmTestHost.Run(new WasmCompiler(false).Compile(program)).Succeeded().Output;
        Assert.Equal(expected, WasmTestHost.Run(new WasmCompiler(false).Compile(Unpacked(program))).Succeeded().Output);
        foreach (string quantum in new[] { "1", "17", "65536" })
            Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum }).Succeeded().Output);
        Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_JIT"] = "0" }).Succeeded().Output);
    }
}
