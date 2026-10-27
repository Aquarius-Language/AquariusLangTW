using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>The desktop runtime JIT (runtime/platform/desktop/jit.c, WasmJit): hot loops of untyped code whose variables'
/// types are unknown when compiling get native variants installed while the program runs. Every program must print the
/// same with the JIT on and off (AQUARIUS_JIT=0), whatever the values do: change type mid-run, make the entry check of an
/// installed variant fail, raise errors, run on Thread workers or allocate enough to collect during native code.</summary>
public class JitTest
{
    // Values come from an untyped function, so the compiler cannot guess their types: only the runtime JIT can help.
    private const string Prelude = "var make = function(v) { v; };\n";

    private static readonly (string Name, string Source, bool Compiles)[] Programs = [
        ("numbers", "var n = make(5000); var scale = make(0.5d); var run = function() { var t = 0.0d; for (var i = 0; i < n; i++) { t += i * scale; } return t; };" +
            " var s = 0.0d; for (var r = 0; r < 5; r++) { s += run(); } print(s);", true),
        ("integers", "var n = make(4000); var run = function(m) { var c = 0; for (var i = 0; i < n; i++) { c += i % m; } return c; };" +
            " print(run(make(7)), run(make(13)));", true),
        // The type of a variable changes between runs of the loop: installed variants reject it, and the next one is tried.
        ("type changes", "var x = make(1); var run = function() { var t = 0.0d; for (var i = 0; i < 3000; i++) { t += x; } return t; };" +
            " var out = []; for (var k = 0; k < 6; k++) { out = push(out, run()); if (k == 1) { x = make(0.25d); } if (k == 3) { x = make(-4); } if (k == 4) { x = make(2); } } print(out);", false),
        // The type changes in the middle of a loop that already runs natively: the loop writes it with another type.
        ("change inside", "var y = make(3); var run = function() { var t = 0; for (var i = 0; i < 5000; i++) { t += y; if (i == 2500) { y = make(1.5d); } } return t; }; print(run(), y);", false),
        ("booleans", "var on = make(true); var run = function() { var c = 0; for (var i = 0; i < 4000; i++) { if (on) { c += 2; } else { c -= 1; } } return c; };" +
            " print(run()); on = make(false); print(run());", false),
        ("nested", "var rows = make(60); var cols = make(70); var run = function() { var s = 0; for (var i = 0; i < rows; i++) { for (var j = 0; j < cols; j++) { s += i * j; } } return s; };" +
            " print(run(), run());", true),
        ("wrapping", "var big = make(2147483000); var run = function() { var s = 0; for (var i = 0; i < 3000; i++) { s += big; } return s; }; print(run());", true),
        ("error", "var d = make(0); var run = function() { var s = 0; for (var i = 0; i < 3000; i++) { if (i == 2000) { s += 1 / d; } s++; } return s; }; print(run());", true),
        ("garbage", "var n = make(60000); var run = function() { var t = 0; for (var i = 0; i < n; i++) { var pair = [i, i + 1, i + 2]; t += len(pair) + i; } return t; }; print(run());", true),
        ("strings", "var word = make(\"ab\"); var run = function() { var s = \"\"; for (var i = 0; i < 2000; i++) { if (i % 500 == 0) { s = s + word; } } return s; }; print(run());", false),
    ];

    private static RunResult Run(WasmProgram program, bool jit, string? quantum = null)
    {
        var environment = new Dictionary<string, string> { ["AQUARIUS_JIT"] = jit ? "1" : "0", ["AQUARIUS_JIT_DIAGNOSTICS"] = "1" };
        if (quantum != null) environment["AQUARIUS_FUEL_QUANTUM"] = quantum;
        return WasmTestHost.Run(program, environment: environment);
    }

    [RuntimeFact]
    public void TheJitGivesTheSameOutputAsTheGenericCode()
    {
        Parallel.ForEach(Programs, item => {
            var program = new WasmCompiler().Compile(Prelude + item.Source);
            var off = Run(program, false); var on = Run(program, true);
            Assert.True(off.Output == on.Output && off.ExitCode == on.ExitCode, $"{item.Name}: JIT off\n{off.Output}{off.Error}\nJIT on\n{on.Output}{on.Error}");
            Assert.DoesNotContain("jit:", off.Error);
            if (item.Compiles) Assert.True(on.Error.Contains("installed"), $"{item.Name}: the JIT installed nothing\n{on.Error}");
            // Unoptimized compilation (the reference of every tier) agrees too.
            Assert.Equal(WasmTestHost.Run(new WasmCompiler(false).Compile(Prelude + item.Source)).Output, on.Output);
        });
    }

    [RuntimeFact]
    public void JitVariantsGiveTheSameOutputUnderAnyQuantum()
    {
        var program = new WasmCompiler().Compile(Prelude + Programs[2].Source);
        string expected = Run(program, false).Succeeded().Output;
        foreach (string quantum in new[] { "1", "17", "4096" })
            Assert.Equal(expected, Run(program, true, quantum).Succeeded().Output);
    }

    [RuntimeFact]
    public void ThreadWorkersInstallTheirOwnVariants()
    {
        var program = new WasmCompiler().Compile(Prelude + "var work = function(n, scale) { var t = 0.0d; for (var i = 0; i < n; i++) { t += i * scale; } return t; };" +
            " var a = Thread.Start(work, [20000, 0.25d]); var b = Thread.Start(work, [30000, 2]); print(Thread.Result(a), Thread.Result(b)); Thread.Close(a); Thread.Close(b);" +
            " print(work(make(1000), make(0.5d)));");
        var off = Run(program, false).Succeeded(); var on = Run(program, true).Succeeded();
        Assert.Equal(off.Output, on.Output);
        Assert.True(on.Error.Split('\n').Count(line => line.Contains("installed")) >= 2, on.Error);
    }

    [RuntimeFact]
    public void TheJitOnlyHelpsLoopsWithoutACompiledRegion()
    {
        // A loop whose variables the compiler can guess runs as a compiled region and needs no JIT.
        var program = new WasmCompiler().Compile("var n = 5000; var run = function() { var t = 0; for (var i = 0; i < n; i++) { t += i; } return t; }; print(run());");
        var on = Run(program, true).Succeeded();
        Assert.Equal("12497500", on.Output.Trim());
        Assert.DoesNotContain("installed", on.Error);
    }
}
