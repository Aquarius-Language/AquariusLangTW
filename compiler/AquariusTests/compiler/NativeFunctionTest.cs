using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>The native tier of fully typed functions: the optimizing compiler (native code, direct calls, loops, outer
/// typed bindings by address, deoptimization at the fuel and depth bounds, raised errors) must give the generic emitter's
/// results, and typed programs must give the same results as their untyped forms.</summary>
public class NativeFunctionTest
{
    private static readonly string[] Programs = [
        // Recursion, deeper than the native depth bound (the rest continues in generic frames).
        "var fib = function(n: int): int { if (n < 2) { return n; } return fib(n - 1) + fib(n - 2); }; fib(22);",
        "var depth = function(k: int): int { if (k == 0) { return 0; } return 1 + depth(k - 1); }; depth(30000);",
        "var ack = function(m: int, n: int): int { if (m == 0) { return n + 1; } if (n == 0) { return ack(m - 1, 1); } return ack(m - 1, ack(m, n - 1)); }; ack(2, 300);",
        // Loops, break, nested scopes reusing a name, and a loop long enough to exhaust the fuel quantum.
        "var squares = function(n: int): int { var total: int = 0; for (var i: int = 0; i < n; i++) { for (var j: int = 0; j < i; j++) { total += j; } } for (var i: int = 0; i < 3; i++) { total -= i; } return total; }; squares(300);",
        "var first = function(limit: int): int { var found: int = -1; for (var i: int = 2; i < limit; i++) { if (i * i > 500) { found = i; break; } } return found; }; first(100);",
        "var spin = function(n: int): int { var c: int = 0; for (var i: int = 0; i < n; i++) { c += i % 7; c = c * 3 % 1000003; } return c; }; spin(3000000);",
        "var collatz = function(n: int): int { var steps: int = 0; for (var start: int = 1; start <= n; start++) { for (var v: int = start; v != 1; steps++) { var half = v / 2; if (half * 2 == v) { v = half; } else { v = v * 3 + 1; } } } return steps; }; collatz(3000);",
        // Outer typed bindings read and written by address, and direct calls between siblings.
        "var n: int = 40; var total: int = 0; var add = function(k: int): int { for (var i: int = 0; i < n; i++) { total += i * k; } return total; }; add(3) + add(1);",
        "var e = function(i: int, j: int): double { var s = i + j; return 1.0d / (s * (s + 1) / 2 + i + 1); }; var sum = function(m: int): double { var t: double = 0.0d; for (var i: int = 0; i < m; i++) { for (var j: int = 0; j < m; j++) { t += e(i, j); } } return t; }; sum(60);",
        "var count: int = 0; var bump = function(): int { count++; return count; }; var many = function(k: int): int { for (var i: int = 0; i < k; i++) { bump(); } return count; }; many(10) + many(5);",
        // Floats, doubles, conversions, mixed arithmetic and wrap-around.
        "var f = function(a: float, b: float): float { return a * b + 0.1f; }; [f(1.5f, 2.25f), f(3, 4)];",
        "var mix = function(i: int, d: double): double { return double(i) / 3 + d * i - int(d) % 4; }; mix(7, 2.75d);",
        "var conv = function(d: double): int { return int(d) + int(0.0d - d) + int(d * 1000000000000.0d); }; conv(3.75d);",
        "var wrap = function(a: int): int { var x: int = a; for (var i: int = 0; i < 40; i++) { x = x * 31 + 7; x -= i; } return x; }; wrap(123456789);",
        "var rem = function(a: double, b: double): double { return a % b; }; [rem(7.5d, 2.0d), rem(0.0d - 7.5d, 2.0d)];",
        "var cmp = function(a: int, b: double): bool { return a < b && !(a == b) || a >= 100; }; [cmp(1, 1.5d), cmp(2, 1.5d), cmp(100, 0.0d)];",
        // Typed arrays: reads, writes through shared (copy-on-write) storage, len, outer typed arrays and recursion.
        "var sort = function(a: int[], lo: int, hi: int): int { var i = lo; var j = hi; var pivot = a[(lo + hi) / 2]; for (var g = 0; i <= j; 0) { for (var l = 0; a[i] < pivot; i++) { } for (var r = 0; a[j] > pivot; j -= 1) { } if (i <= j) { var tmp = a[i]; a[i] = a[j]; a[j] = tmp; i++; j -= 1; } } if (lo < j) { sort(a, lo, j); } if (i < hi) { sort(a, i, hi); } return 0; }; var v: int[] = Array.Make(0, 0, 3000); var seed = 42; for (var i = 0; i < 3000; i++) { seed = (seed * 251 + 17) % 65536; Array.Append(v, seed); } var copy = v; sort(v, 0, 2999); [v[0], v[1500], v[2999], copy[0], len(v)];",
        "var sieve = function(flags: bool[], n: int): int { flags[0] = false; flags[1] = false; for (var p: int = 2; p * p <= n; p++) { if (flags[p]) { for (var j: int = p * p; j <= n; j += p) { flags[j] = false; } } } var count: int = 0; for (var i: int = 2; i <= n; i++) { if (flags[i]) { count += 1; } } return count; }; var f: bool[] = Array.Make(20001, true); sieve(f, 20000);",
        "var n: int = 8; var board: int[] = Array.Make(8, 0); var solve = function(row: int): int { if (row == n) { return 1; } var count: int = 0; for (var col: int = 0; col < n; col++) { var safe = true; for (var prev: int = 0; prev < row; prev++) { var delta = row - prev; if (board[prev] == col || board[prev] == col - delta || board[prev] == col + delta) { safe = false; break; } } if (safe) { board[row] = col; count += solve(row + 1); } } return count; }; solve(0);",
        "var m: int = 40; var element = function(i: int, j: int): double { var s = i + j; return 1.0d / (s * (s + 1) / 2 + i + 1); }; var multiply = function(src: double[], dst: double[], transpose: bool): int { for (var i: int = 0; i < m; i++) { var total: double = 0.0d; for (var j: int = 0; j < m; j++) { var c: double = 0.0d; if (transpose) { c = element(j, i); } else { c = element(i, j); } total += c * src[j]; } dst[i] = total; } return 0; }; var u: double[] = Array.Make(m, 1.0d); var t: double[] = Array.Make(m, 0.0d); var w: double[] = Array.Make(m, 0.0d); for (var k = 0; k < 10; k++) { multiply(u, t, false); multiply(t, w, true); multiply(w, t, false); multiply(t, u, true); } u[0] + u[39];",
        "var mat = function(a: int[], b: int[], n: int): int { var sum: int = 0; for (var i: int = 0; i < n; i++) { for (var j: int = 0; j < n; j++) { var total: int = 0; for (var k: int = 0; k < n; k++) { total += a[i * n + k] * b[k * n + j]; } sum += total; } } return sum; }; var a: int[] = Array.Make(3600, 3); var b: int[] = Array.Make(3600, 5); a[7] = 11; mat(a, b, 60);",
        "var mandel = function(n: int): int { var sum: int = 0; for (var y: int = 0; y < n; y++) { var ci = 2.0d * y / n - 1.0d; for (var x: int = 0; x < n; x++) { var cr = 3.0d * x / n - 2.0d; var zr = 0.0d; var zi = 0.0d; var c: int = 0; for (var g: int = 0; c < 60 && zr * zr + zi * zi <= 4.0d; c++) { var nr = zr * zr - zi * zi + cr; zi = 2.0d * zr * zi + ci; zr = nr; } sum += c; } } return sum; }; mandel(40);",
        // Inlined leaves: widened arguments, a raised error, the callee's entry checkpoint failing on fuel (a long loop)
        // and on the depth bound (recursion), and an inlined Boolean and array-reading callee.
        "var half = function(x: double, k: int): double { return x / 2.0d + k; }; var run = function(n: int): double { var t: double = 0.0d; for (var i: int = 0; i < n; i++) { t += half(i, 1); } return t; }; run(2000000);",
        "var div = function(a: int, b: int): int { return a / b; }; var run = function(n: int): int { var t: int = 0; for (var i: int = 5; i > n; i -= 1) { t += div(100, i); } return t; }; run(-3);",
        "var inc = function(k: int): int { return k + 1; }; var down = function(d: int): int { if (d == 0) { return 0; } return inc(down(d - 1)); }; down(20000);",
        "var even = function(k: int): bool { return k % 2 == 0; }; var at = function(a: int[], i: int): int { return a[i]; }; var count = function(a: int[]): int { var c: int = 0; for (var i: int = 0; i < len(a); i++) { if (even(at(a, i))) { c += at(a, i); } } return c; }; count([3, 4, 5, 6, 8]);",
        // Arrays of any values (陣列 / array): native literals of mixed elements, reads of any element (null outside the
        // array), elements passed on to array parameters, typed arrays passed as arrays, and deep recursion.
        "var build = function(d: int): array { if (d == 0) { return [0, 0]; } return [build(d - 1), build(d - 1)]; }; var check = function(t: array, d: int): int { if (d == 0) { return 1; } return 1 + check(t[0], d - 1) + check(t[1], d - 1); }; check(build(12), 12);",
        "var mixed = function(k: int): array { return [k, k * 1.5d, k > 2, [k]]; }; var count = function(a: array): int { return len(a); }; [mixed(3), count(mixed(1)), count([1, 2, 3])];",
        "var first = function(t: array): int { return len(t); }; var nested = function(t: array): int { return first(t[0]) + first(t[1]); }; nested([[1, 2], [3]]);",
        "var down = function(d: int): array { if (d == 0) { return []; } return [down(d - 1)]; }; var depth = function(t: array, d: int): int { if (len(t) == 0) { return d; } return depth(t[0], d + 1); }; depth(down(20000), 0);",
        "var pick = function(t: array, i: int): array { return [t[i]]; }; [pick([1, [2]], 1), pick([1], 5)];",
        // Array companions: copy-on-write of shared storage inside a loop and inside a callee, seen through aliases.
        "var touch = function(a: int[], b: int[], n: int): int { var s: int = 0; for (var i: int = 0; i < n; i++) { a[0] = a[0] + i; s += a[0] + b[0] + len(b); } return s; }; var a: int[] = [1, 2, 3]; var b: int[] = push(a, 4); [touch(a, b, 5), a, b];",
        "var poke = function(a: int[]): int { a[1] = a[1] + 10; return 0; }; var loop = function(a: int[]): int { var s: int = 0; for (var i: int = 0; i < 4; i++) { poke(a); s += a[1]; } return s; }; var a: int[] = [1, 2, 3]; var b: int[] = push(a, 9); [loop(a), a, b];",
    ];

    [RuntimeFact]
    public void NativeFunctionsMatchTheGenericEmitter()
    {
        EmitterTiers.AssertSame(Programs);
    }

    /// <summary>Calls whose result a native function returns at once are Wasm tail calls (return_call): the callee takes
    /// over the activation, so tail recursion runs natively at any depth, and a callee that deoptimizes or fails returns
    /// straight to the tail caller's caller.</summary>
    private static readonly string[] TailPrograms = [
        "var loop = function(n: int, acc: int): int { if (n == 0) { return acc; } return loop(n - 1, acc + n % 7); }; loop(1000000, 0);",
        "var odd = function(n: int): bool { if (n == 0) { return false; } return even(n - 1); }; var even = function(n: int): bool { if (n == 0) { return true; } return odd(n - 1); }; [even(100001), odd(77777)];",
        "var step = function(n: int, d: double): double { if (n == 0) { return d; } return step(n - 1, d * 0.5d + 1.0d); }; var outer = function(k: int): double { return step(k, 0.0d) + 1.0d; }; outer(300000);",
        "var div = function(a: int, b: int): int { return a / b; }; var down = function(n: int): int { if (n == 0) { return div(1, n); } return down(n - 1); }; down(50000);",
        "var ack = function(m: int, n: int): int { if (m == 0) { return n + 1; } if (n == 0) { return ack(m - 1, 1); } return ack(m - 1, ack(m, n - 1)); }; ack(3, 6);",
    ];

    [RuntimeFact]
    public void TailCallsMatchTheGenericEmitterUnderAnyQuantum()
    {
        var results = EmitterTiers.AssertSame(TailPrograms);
        Assert.Equal("[假, 真]", results[1].Text);
        Assert.True(results[3].IsError);
        Assert.Equal(509, results[4].Integer);
        string program = "var loop = function(n: int, acc: int): int { if (n == 0) { return acc; } return loop(n - 1, acc + n % 7); }; " +
            "var odd = function(n: int): bool { if (n == 0) { return false; } return even(n - 1); }; var even = function(n: int): bool { if (n == 0) { return true; } return odd(n - 1); }; " +
            "print(loop(200000, 0), even(30001));";
        string expected = WasmTestHost.Run(new WasmCompiler(false).Compile(program)).Succeeded().Output;
        foreach (string quantum in new[] { "1", "7", "4096" })
            Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum }).Succeeded().Output);
    }

    /// <summary>The desktop host runs longer run slices than the portable quantum (runtime/platform/desktop/main.c);
    /// results must not depend on it, including where native code deoptimizes at a slice's end.</summary>
    [RuntimeFact]
    public void TheDesktopQuantumGivesThePortableResults()
    {
        string[] programs = [
            "var spin = function(n: int): int { var c: int = 0; for (var i: int = 0; i < n; i++) { c += i % 7; c = c * 3 % 1000003; } return c; }; print(spin(3000000));",
            "var depth = function(k: int): int { if (k == 0) { return 0; } return 1 + depth(k - 1); }; print(depth(30000));",
            "var half = function(x: double, k: int): double { return x / 2.0d + k; }; var run = function(n: int): double { var t: double = 0.0d; for (var i: int = 0; i < n; i++) { t += half(i, 1); } return t; }; print(run(3000000));",
            "var t = 0; for (var i = 0; i < 2000000; i++) { t += i % 3; } print(t);",
        ];
        foreach (string program in programs) {
            string desktop = WasmTestHost.Run(program).Succeeded().Output;
            string portable = WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = "65536" }).Succeeded().Output;
            string tiny = WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = "17" }).Succeeded().Output;
            Assert.Equal(portable, desktop);
            Assert.Equal(portable, tiny);
        }
    }

    /// <summary>Native code deoptimizes at every quantum's end and resumes natively at the marked site; when it declines
    /// (no budget left) the generic body continues the frame, and a later resume must not repeat work from the old site.
    /// Two recursive calls per activation make every kind of site resume under many small quanta.</summary>
    [RuntimeFact]
    public void ResumesNeverRepeatWorkUnderAnyQuantum()
    {
        string[] programs = [
            "var build = function(d: int): array { if (d == 0) { return [0, 0]; } return [build(d - 1), build(d - 1)]; }; " +
            "var check = function(t: array, d: int): int { if (d == 0) { return 1; } return 1 + check(t[0], d - 1) + check(t[1], d - 1); }; " +
            "var total = 0; for (var i = 0; i < 4; i++) { total += check(build(6), 6); } print(total);",
            "var fib = function(n: int): int { if (n < 2) { return n; } return fib(n - 1) + fib(n - 2); }; print(fib(16));",
            "var sum = function(n: int): int { var t: int = 0; for (var i: int = 0; i < n; i++) { t += i % 5; } return t; }; " +
            "var twice = function(n: int): int { return sum(n) + sum(n + 1); }; print(twice(300));",
        ];
        foreach (string program in programs) {
            string expected = WasmTestHost.Run(program).Succeeded().Output;
            for (int quantum = 1; quantum <= 40; quantum++)
                Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum.ToString() }).Succeeded().Output);
        }
    }

    [RuntimeFact]
    public void TypedFunctionsTakeTheNativeTier()
    {
        var reasons = new List<string>();
        var previous = WasmCompiler.TierDiagnostics;
        WasmCompiler.TierDiagnostics = reasons.Add;
        try { foreach (string program in Programs) new WasmCompiler().Compile(program); }
        finally { WasmCompiler.TierDiagnostics = previous; }
        Assert.DoesNotContain(reasons, r => r.Contains("native: rejected"));
        int typedFunctions = Programs.Sum(p => System.Text.RegularExpressions.Regex.Matches(p, @"\):\s*(int|double|float|bool|array)\s*\{").Count);
        Assert.Equal(typedFunctions, reasons.Count(r => r.EndsWith("native: accepted")));
    }

    [RuntimeFact]
    public void TypedResultsEqualUntypedResults()
    {
        string[] untyped = Programs.Select(p => System.Text.RegularExpressions.Regex.Replace(p, @":\s*(int|double|float|bool|array)\b(\[\])?", "")).ToArray();
        // Untyped integer division of a double by an int literal stays a double, so the programs mean the same.
        var typed = WasmTestHost.EvalMany(Programs);
        var plain = WasmTestHost.EvalMany(untyped);
        for (int i = 0; i < Programs.Length; i++) Assert.True(typed[i] == plain[i], $"{Programs[i]}\n  typed:   {typed[i]}\n  untyped: {plain[i]}");
    }

    [RuntimeFact]
    public void TypedArraysKeepTheirElementType()
    {
        var results = EmitterTiers.AssertSame([
            "var a: int[] = [1, 2, 3]; var alias = a; alias[0] = 2.5d; a;",
            "var a: int[] = [1, 2, 3]; Array.Append(a, \"x\"); a;",
            "var a: int[] = [1, 2, 3]; Array.Fill(a, 9); a;",
            "var a: int[] = [1, 2, 3]; a[7];",
            "var read = function(a: int[], i: int): int { return a[i]; }; var a: int[] = [1, 2, 3]; read(a, 3);",
            "var write = function(a: int[], i: int): int { a[i] = 1; return 0; }; var a: int[] = [1, 2, 3]; write(a, -1);",
            "var a: int[] = []; var b: double[] = a; b;",
            "var plain = [1, 2, 3]; plain[7];",
        ]);
        Assert.Contains("the array holds int, got DOUBLE", results[0].Message);
        Assert.Contains("the array holds int, got STRING", results[1].Message);
        Assert.Equal(new TestValue("array", "[9, 9, 9]"), results[2]);
        Assert.Contains("Array index is out of bounds", results[3].Message);
        Assert.Contains("Array index is out of bounds", results[4].Message);
        Assert.Contains("Array assignment index is out of bounds", results[5].Message);
        Assert.Contains("b expects", results[6].Message);
        Assert.Equal(new TestValue("null", "null"), results[7]);
    }

    [RuntimeFact]
    public void NativeErrorsAndGuardsBehaveLikeGenericCode()
    {
        var results = EmitterTiers.AssertSame([
            "var g = function(a: int, b: int): int { return a / b; }; g(1, 0);",
            "var g = function(a: int, b: int): int { return a % b; }; g(1, 0);",
            "var g = function(a: int, b: int): int { var t: int = 0; for (var i: int = 0; i < 10; i++) { t += a / (b - i); } return t; }; g(100, 5);",
            "var f = function(n: int): int { return n * 2; }; var h = function(v) { v; }; f(h(2.5d));",
            "var limit: int = 3; var f = function(): int { return limit; }; limit = 9; f();",
            // An element passed to an array parameter must be an array: the callee's annotation check reports it.
            "var first = function(t: array): int { return len(t); }; var at = function(t: array, i: int): int { return first(t[i]); }; at([1, 2], 0);",
            "var first = function(t: array): int { return len(t); }; var at = function(t: array, i: int): int { return first(t[i]); }; at([[1]], 3);",
            // An inlined callee raises its error from the caller's activation.
            "var at = function(a: int[], i: int): int { return a[i]; }; var read = function(a: int[]): int { return at(a, 9); }; read([1, 2]);",
        ]);
        Assert.Contains("Integer division by zero", results[0].Message);
        Assert.Contains("Integer remainder by zero", results[1].Message);
        Assert.Contains("Integer division by zero", results[2].Message);
        Assert.Contains("n expects int, got DOUBLE", results[3].Message);
        Assert.Equal(9, results[4].Integer);
        Assert.Contains("t expects array, got INTEGER", results[5].Message);
        Assert.Contains("t expects array, got NULL", results[6].Message);
        Assert.Contains("Array index is out of bounds", results[7].Message);
    }
}
