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
        "var mandel = function(n: int): int { var sum: int = 0; for (var y: int = 0; y < n; y++) { var ci = 2.0d * y / n - 1.0d; for (var x: int = 0; x < n; x++) { var cr = 3.0d * x / n - 2.0d; var zr = 0.0d; var zi = 0.0d; var c: int = 0; for (var g: int = 0; c < 60 && zr * zr + zi * zi <= 4.0d; c++) { var nr = zr * zr - zi * zi + cr; zi = 2.0d * zr * zi + ci; zr = nr; } sum += c; } } return sum; }; mandel(40);",
    ];

    [RuntimeFact]
    public void NativeFunctionsMatchTheGenericEmitter()
    {
        EmitterTiers.AssertSame(Programs);
    }

    [RuntimeFact]
    public void TypedFunctionsTakeTheNativeTier()
    {
        var reasons = new List<string>();
        var previous = WasmCompiler.TierDiagnostics;
        WasmCompiler.TierDiagnostics = reasons.Add;
        try { foreach (string program in Programs) new WasmCompiler().Compile(program); }
        finally { WasmCompiler.TierDiagnostics = previous; }
        Assert.DoesNotContain(reasons, r => r.Contains("native: the typed body"));
        int typedFunctions = Programs.Sum(p => System.Text.RegularExpressions.Regex.Matches(p, @"\):\s*(int|double|float|bool)\s*\{").Count);
        Assert.Equal(typedFunctions, reasons.Count(r => r.EndsWith("native: accepted")));
    }

    [RuntimeFact]
    public void TypedResultsEqualUntypedResults()
    {
        string[] untyped = Programs.Select(p => System.Text.RegularExpressions.Regex.Replace(p, @":\s*(int|double|float|bool)\b", "")).ToArray();
        // Untyped integer division of a double by an int literal stays a double, so the programs mean the same.
        var typed = WasmTestHost.EvalMany(Programs);
        var plain = WasmTestHost.EvalMany(untyped);
        for (int i = 0; i < Programs.Length; i++) Assert.True(typed[i] == plain[i], $"{Programs[i]}\n  typed:   {typed[i]}\n  untyped: {plain[i]}");
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
        ]);
        Assert.Contains("Integer division by zero", results[0].Message);
        Assert.Contains("Integer remainder by zero", results[1].Message);
        Assert.Contains("Integer division by zero", results[2].Message);
        Assert.Contains("n expects int, got DOUBLE", results[3].Message);
        Assert.Equal(9, results[4].Integer);
    }
}
