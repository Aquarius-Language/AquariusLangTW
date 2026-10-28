using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>Speculation on untyped bindings: a loop of untyped code that reads or writes untyped outer variables runs as a
/// native region typed by the variables' guessed types (TypeChecker), behind a tag check at every entry. A wrong guess
/// leaves the loop to the generic code, so results never differ from the unspeculated compiler's.</summary>
public class SpeculationTest
{
    private static readonly string[] Programs = [
        // The guess holds: the loop reads an untyped global integer.
        "var n = 1000; var f = function() { var s = 0; for (var i = 0; i < n; i++) { s += i * 2; } return s; }; f();",
        // The guess fails on the second call (n became a double through untyped code): the generic loop runs instead.
        "var n = 10; var mk = function(v) { v; }; var f = function() { var s = 0.0d; for (var i = 0; i < n; i++) { s += 1.5d; } return s; }; var a = f(); n = mk(10.5d); [a, f()];",
        // The type changes between entries of an inner loop: some entries run natively, the others generically.
        "var x = 1; var mk = function(v) { v; }; var f = function() { var t = 0.0d; for (var k = 0; k < 6; k++) { for (var j = 0; j < 3; j++) { t += x; } " +
        "if (k == 2) { x = mk(0.5d); } if (k == 4) { x = mk(2); } } return t; }; f();",
        // The loop writes a speculated global, then generic code reads it.
        "var total = 0; var f = function(n) { for (var i = 0; i < n; i++) { total += i; } }; f(100); f(50); [total, typeOf(total)];",
        // Conflicting guesses: no speculation, same result.
        "var y = 0; y = 2.5d; var f = function() { var s = 0.0d; for (var i = 0; i < 4; i++) { s += y; } return s; }; f();",
        // Booleans and floats.
        "var on = true; var f = function() { var c = 0; for (var i = 0; i < 10; i++) { if (on) { c++; } } return c; }; f();",
        "var scale = 0.5f; var f = function() { var s = 0.0f; for (var i = 0; i < 7; i++) { s += scale * i; } return s; }; [f(), typeOf(f())];",
        // Errors inside a speculated loop give the generic runtime's error.
        "var d = 0; var f = function() { var s = 0; for (var i = 0; i < 3; i++) { s += 10 / d; } return s; }; f();",
        // The guess is a number but the variable holds a string at run time.
        "var label = 1; var mk = function(v) { v; }; label = mk(\"x\"); var f = function() { var s = \"\"; for (var i = 0; i < 3; i++) { s = s + label; } return s; }; f();",
        // Wrapping integer arithmetic on a speculated global matches the generic runtime.
        "var big = 2147483600; var f = function() { var s = big; for (var i = 0; i < 100; i++) { s += big; } return s; }; f();",
    ];

    [RuntimeFact]
    public void SpeculatedLoopsMatchTheUnspeculatedCompiler()
    {
        var results = EmitterTiers.AssertSame(Programs);
        Assert.Equal(999000, results[0].Integer);
        Assert.Equal("[15, 16.5]", results[1].Text);
        Assert.Equal(18, results[2].Number);
        Assert.Equal("[6175, integer]", results[3].Text);
        Assert.Equal(10, results[4].Number);
        Assert.Equal(10, results[5].Integer);
        Assert.Equal("[10.5, float]", results[6].Text);
        Assert.True(results[7].IsError);
        Assert.Equal("xxx", results[8].Text);
    }

    [RuntimeFact]
    public void LoopsReadingUntypedGlobalsBecomeNativeRegions()
    {
        var reasons = new List<string>();
        var previous = WasmCompiler.TierDiagnostics;
        WasmCompiler.TierDiagnostics = reasons.Add;
        try { new WasmCompiler().Compile(Programs[0]); new WasmCompiler().Compile(Programs[3]); }
        finally { WasmCompiler.TierDiagnostics = previous; }
        // Their functions are clones of untyped functions (guessed parameters) or their loops are regions.
        Assert.Equal(2, reasons.Count(r => r.Contains("native region: accepted") || r.Contains("native: accepted: clone")));
    }

    [RuntimeFact]
    public void SpeculatedLoopsGiveTheSameResultsUnderAnyQuantum()
    {
        // Small quanta make regions return to the generic loop head and re-enter (re-checking the guess) many times.
        string program = "var n = 3000; var x = 1; var mk = function(v) { v; }; var f = function() { var t = 0.0d; " +
            "for (var k = 0; k < 40; k++) { for (var j = 0; j < n; j++) { t += x; } if (k % 3 == 0) { x = mk(0.25d); } else { x = mk(k); } } return t; }; print(f());";
        string expected = WasmTestHost.Run(new WasmCompiler(false).Compile(program)).Succeeded().Output;
        foreach (string quantum in new[] { "1", "17", "4096", "65536" })
            Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum }).Succeeded().Output);
    }
}
