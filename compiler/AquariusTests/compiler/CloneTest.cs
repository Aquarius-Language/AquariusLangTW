using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>Typed clones of untyped functions: an untyped function whose parameters all have guesses (TypeChecker) gets a
/// native-tier body for exactly those types, with the result type the native tier proves. Its entry takes only arguments
/// of exactly those types (any other call runs the generic body), and a read outside a guessed array deoptimizes to the
/// generic body at that read, so results never differ from the unoptimized compiler's.</summary>
public class CloneTest
{
    private static readonly string[] Programs = [
        // The guesses hold: recursion, sibling calls and tail calls.
        "var fib = function(n) { if (n < 2) { return n; } return fib(n - 1) + fib(n - 2); }; fib(22);",
        "var ack = function(m, n) { if (m == 0) { return n + 1; } if (n == 0) { return ack(m - 1, 1); } return ack(m - 1, ack(m, n - 1)); }; ack(2, 60);",
        "var even = function(n) { if (n == 0) { return true; } return odd(n - 1); }; var odd = function(n) { if (n == 0) { return false; } return even(n - 1); }; [even(5001), odd(301)];",
        // The guess fails for some calls: an argument of another type from untyped code runs the generic body.
        "var mk = function(v) { return v; }; var twice = function(n) { return n * 2; }; [twice(3), twice(mk(1.5d)), twice(mk(4)), typeOf(twice(mk(1.5d)))];",
        "var mk = function(v) { return v; }; var join = function(a, b) { return a + b; }; [join(1, 2), join(mk(\"x\"), mk(\"y\"))];",
        // Every result type: double, Boolean, float, an array of any values, none.
        "var half = function(x) { return x / 2.0d; }; var big = function(x) { return x > 10; }; var f = function(x) { return x * 0.5f; }; [half(3.0d), big(11), f(3.0f), typeOf(f(2.0f))];",
        "var pair = function(n) { return [n, n * 2]; }; var deep = function(d) { if (d == 0) { return [0]; } return [deep(d - 1)]; }; [pair(4), deep(3)];",
        "var total = 0; var add = function(k) { total += k; }; for (var i = 0; i < 5; i++) { add(i); } var r = total; r;",
        // Outer guessed bindings: read, written and changed to another type by generic code between calls.
        "var scale = 3; var mk = function(v) { return v; }; var mul = function(x) { return x * scale; }; var a = mul(2); scale = mk(0.5d); [a, mul(4)];",
        "var count = 0; var bump = function(k) { count = count + k; return count; }; [bump(2), bump(5), count];",
        // Guessed arrays: a read past the end deoptimizes there (null), and an array whose elements change type runs generically.
        "var get = function(a, i) { return a[i]; }; [get([1, 2, 3], 1), get([1, 2, 3], 7)];",
        "var sum = function(a) { var s = 0; for (var i = 0; i < len(a); i++) { s += a[i]; } return s; }; var v = [1, 2, 3]; var r = [sum(v)]; v[0] = 0.5d; Array.Append(r, sum(v)); v[0] = 9; Array.Append(r, sum(v)); r;",
        "var put = function(a, i, x) { a[i] = x; return len(a); }; var v = Array.Make(3, 0); [put(v, 1, 5), v, put(v, 2, 7), v];",
        // Errors, arity and rebinding keep the generic behavior.
        "var div = function(a, b) { return a / b; }; div(1, 0);",
        "var f = function(a, b) { return a + b; }; f(1, 2, 3);",
        "var f = function(n) { return n + 1; }; var a = f(1); f = function(n) { return n * 100; }; [a, f(2)];",
        // A clone whose callee is not native (it builds a string) stays generic.
        "var label = function(n) { return String.From(n); }; var f = function(n) { return label(n * 2); }; f(21);",
    ];

    [RuntimeFact]
    public void ClonesMatchTheUnoptimizedCompiler()
    {
        var results = EmitterTiers.AssertSame(Programs);
        Assert.Equal(17711, results[0].Integer);
        Assert.Equal(123, results[1].Integer);
        Assert.Equal("[假, 真]", results[2].Text);
        Assert.Equal("[6, 3, 8, double]", results[3].Text);
        Assert.Equal("[3, xy]", results[4].Text);
        Assert.Equal(10, results[7].Integer);
        Assert.Equal("[6, 2]", results[8].Text);
        Assert.Equal("[2, 7, 7]", results[9].Text);
        Assert.Equal("[2, null]", results[10].Text);
        Assert.Equal("[6, 5, 14]", results[11].Text);
        Assert.True(results[13].IsError && results[14].IsError);
        Assert.Equal("[2, 200]", results[15].Text);
        Assert.Equal("42", results[16].Text);
    }

    [RuntimeFact]
    public void UntypedFunctionsWithGuessedParametersBecomeClones()
    {
        var reasons = new List<string>();
        var previous = WasmCompiler.TierDiagnostics;
        WasmCompiler.TierDiagnostics = reasons.Add;
        try { foreach (int i in new[] { 0, 1, 2, 5, 6 }) new WasmCompiler().Compile(Programs[i]); }
        finally { WasmCompiler.TierDiagnostics = previous; }
        Assert.True(reasons.Count(r => r.Contains("native: accepted: clone")) >= 8, string.Join("\n", reasons));
    }

    [RuntimeFact]
    public void ClonesGiveTheSameOutputUnderAnyQuantum()
    {
        string program = "var ack = function(m, n) { if (m == 0) { return n + 1; } if (n == 0) { return ack(m - 1, 1); } return ack(m - 1, ack(m, n - 1)); }; " +
            "var fib = function(n) { if (n < 2) { return n; } return fib(n - 1) + fib(n - 2); }; " +
            "var mk = function(v) { return v; }; print(ack(2, 200), fib(17), fib(mk(10)), ack(mk(1), mk(5)));";
        string expected = WasmTestHost.Run(new WasmCompiler(false).Compile(program)).Succeeded().Output;
        foreach (string quantum in new[] { "1", "17", "4096" })
            Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum }).Succeeded().Output);
        Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_JIT"] = "0" }).Succeeded().Output);
    }
}
