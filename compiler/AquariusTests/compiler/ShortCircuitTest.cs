using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary><c>&amp;&amp;</c> and <c>||</c> evaluate their right operand only when the left one does not decide the
/// result, on every tier (generic, typed scalar, native), and still report a non-Boolean operand they evaluate.</summary>
public class ShortCircuitTest
{
    [RuntimeFact]
    public void TheRightOperandRunsOnlyWhenItDecides()
    {
        LanguageCases.Expect(
            ("var f = null; f != null && f(1);", "boolean", "假"),
            ("var f = null; f == null || f(1);", "boolean", "真"),
            ("var f = function(x) { x > 0; }; f != null && f(1);", "boolean", "真"),
            ("var calls = 0; var g = function() { calls++; true; }; [false && g(), true || g(), calls];", "array", "[假, 真, 0]"),
            ("var calls = 0; var g = function() { calls++; true; }; [true && g(), false || g(), calls];", "array", "[真, 真, 2]"),
            ("[true && true, true && false, false && true, false && false];", "array", "[真, 假, 假, 假]"),
            ("[true || true, true || false, false || true, false || false];", "array", "[真, 真, 真, 假]"),
            ("var a = [1, 2]; var i = 5; i < 長度(a) && a[i] > 0;", "boolean", "假"),
            ("var 次數 = 0; 變數 記 = 函式(值) { 次數++; 值; }; [(記(假) && 記(真)) || 記(真), 次數];", "array", "[真, 2]"),
            ("var n = 0; for (var i = 0; i < 10 && n < 3; i++) { n++; } n;", "integer", "3"),
            ("var x = if (true && !false) { 1; } else { 2; }; x;", "integer", "1"));
    }

    [RuntimeFact]
    public void AnEvaluatedNonBooleanOperandIsStillAnError()
    {
        LanguageCases.ExpectErrors("1 && true;", "true && 1;", "null || true;", "false || 1;", "true && null;");
        LanguageCases.Expect(("false && 1;", "boolean", "假"), ("true || 1;", "boolean", "真"));
    }

    private static readonly string[] TierPrograms = [
        "var inside = function(a: array, i: int): bool { return i >= 0 && i < 長度(a) && a[i] > 0; }; var a = [3, 0, 5]; [inside(a, -1), inside(a, 0), inside(a, 1), inside(a, 7)];",
        "var count = function(n: int): int { var c: int = 0; for (var i: int = 0; i < n; i++) { if (i % 3 == 0 || i % 5 == 0) { c++; } } return c; }; count(100000);",
        "var walk = function(n: int): int { var last: int = 0; for (var k: int = 0; k < n && k * k < 5000; k++) { last = k; } return last; }; walk(1000);",
        "var pick = function(a: bool, b: bool, c: bool): bool { return (a && b) || (!a && c); }; [pick(true, true, false), pick(true, false, true), pick(false, false, true), pick(false, true, false)];",
        "var calls = 0; var g = function(x) { calls++; x; }; var f = function(a, b) { (a && g(b)) || g(!b); }; [f(true, true), f(false, true), f(true, false), calls];",
        "var h = {\"n\": 7}; var f = function(n) { n > 3 && n < 10; }; [f(h[\"n\"]), f(2), f(2.5), f(20)];",
        "var f = function(a, b) { a && b; }; f(true, 1);",
        "var f = function(a, b) { a || b; }; f(false, null);",
    ];

    [RuntimeFact]
    public void EveryTierShortCircuits()
    {
        var results = EmitterTiers.AssertSame(TierPrograms);
        Assert.Equal("[假, 真, 假, 假]", results[0].Text);
        Assert.Equal("46667", results[1].Text);
        Assert.Equal("70", results[2].Text);
        Assert.Equal("[真, 假, 真, 假]", results[3].Text);
        Assert.Equal("[真, 假, 真, 4]", results[4].Text);
        Assert.True(results[6].IsError); Assert.True(results[7].IsError);
    }

    [RuntimeFact]
    public void TypedFunctionsWithLogicStayNative()
    {
        const string source = "var count = function(n: int): int { var c: int = 0; for (var i: int = 0; i < n; i++) { if (i % 3 == 0 || (i % 5 == 0 && i > 2)) { c++; } } return c; }; print(count(100000));";
        var reasons = new List<string>();
        var previous = WasmCompiler.TierDiagnostics;
        WasmCompiler.TierDiagnostics = reasons.Add;
        try { new WasmCompiler().Compile(source); }
        finally { WasmCompiler.TierDiagnostics = previous; }
        Assert.Contains(reasons, r => r.Contains("native: accepted"));
        Assert.DoesNotContain(reasons, r => r.Contains("native: rejected") && !r.Contains("function 0:"));
        Assert.Equal("46667\n", WasmTestHost.Output(source));
    }
}
