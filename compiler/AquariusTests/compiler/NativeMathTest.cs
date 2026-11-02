using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>數學 / Math calls in the native tier (WasmNativeFunction.MathCall, WasmMath): native code must give the generic
/// call's exact results (NaN, ±0, ±Infinity, float and double, integer wrap-around), and typed functions calling Math take
/// the native tier.</summary>
public class NativeMathTest
{
    private static readonly string[] Programs = [
        "var f = function(x: double): array { return [Math.Sqrt(x), Math.Floor(x), Math.Ceil(x), Math.Trunc(x), Math.Abs(x), Math.Round(x), Math.Sign(x)]; }; [f(2.5d), f(-2.5d), f(-(0.0d)), f(Math.Infinity), f(-Math.Infinity), f(Math.NaN), f(0.49999999999999994d)];",
        "var z = function(x: double): array { return [1.0d / Math.Round(x), 1.0d / Math.Min(0.0d, x), 1.0d / Math.Max(x, 0.0d), 1.0d / Math.Sign(x), 1.0d / Math.Trunc(x), 1.0d / Math.Ceil(x), 1.0d / Math.Abs(x)]; }; [z(-0.4d), z(-(0.0d)), z(0.0d)];",
        "var g = function(a: int, b: float): float { return Math.Min(a, b) + Math.Max(a, b) * 2 + Math.Abs(b) + Math.Clamp(b, -1, a); }; [g(3, 2.5f), g(-1, -1.5f), g(16777217, 0.1f)];",
        "var h = function(a: int, b: int): int { return Math.Abs(a) + Math.Sign(b) * 10 + Math.Min(a, b) + Math.Max(a, b) + Math.Clamp(a, -5, 5) + Math.Floor(a) + Math.RoundInt(a) + Math.TruncInt(b); }; [h(7, -3), h(-2147483647 - 1, 0), h(0, 9), h(2147483647, 2147483647)];",
        "var r = function(x: double): int { return Math.RoundInt(x) * 1000 + Math.FloorInt(x) * 100 + Math.CeilInt(x) * 10 + Math.TruncInt(x); }; [r(2.5d), r(-2.5d), r(0.49999999999999994d), r(10000000000.0d), r(Math.NaN), r(-0.5d), r(-10000000000.0d)];",
        "var t = function(x: double): double { return Math.Sin(x) + Math.Cos(x) * 2 + Math.Tan(x) * 3 + Math.Exp(x / 10) + Math.Log(Math.Abs(x) + 1) + Math.Log2(x * x + 1) + Math.Log10(x * x + 2) + Math.Atan2(x, 2) + Math.Pow(1.5d, x / 4) + Math.Hypot(x, 3) + Math.Cbrt(x) + Math.Sinh(x / 8) + Math.Cosh(x / 8) + Math.Tanh(x) + Math.Atan(x) + Math.Fmod(x, 0.7d) + Math.Asin(x / 100) + Math.Acos(x / 100); }; var s = function(n: int): double { var total: double = 0.0d; for (var i: int = 0; i < n; i++) { total += t(i * 0.37d - 20); } return total; }; s(3000);",
        "var ff = function(x: float): array { return [Math.Sqrt(x), Math.Sin(x), Math.Round(x), Math.Pow(x, 2), Math.Floor(x), Math.Hypot(x, 1), Math.Fmod(x, 0.4f), Math.Exp(x)]; }; [ff(2.5f), ff(-1.5f), ff(0.1f)];",
        "var special = function(x: double): array { return [Math.Log(x), Math.Sqrt(x), Math.Pow(x, -1), Math.Exp(x * 1000), Math.Asin(x * 3), Math.IsNaN(Math.Log(x)), Math.IsFinite(Math.Log(x)), Math.IsNaN(x), Math.IsFinite(x), Math.Atan2(x, -1), Math.Fmod(5, x), Math.Fmod(x, 0)]; }; [special(0.0d), special(-1.0d), special(1.0d), special(Math.Infinity), special(Math.NaN)];",
        "var ints = function(k: int): array { return [Math.IsNaN(k), Math.IsFinite(k), Math.Sqrt(k), Math.Pow(k, k), Math.Log(k), Math.Fmod(k, 3), Math.Hypot(k, k)]; }; [ints(4), ints(0), ints(-3)];",
        "var c = function(r: double): double { return Math.PI * r * r + Math.E + Math.TAU + 數學.圓周率 + 數學.自然常數; }; var inf = function(): bool { return Math.Infinity > 1.0d && Math.NaN != Math.NaN && 數學.無限大 == Math.Infinity && Math.IsNaN(數學.非數); }; [c(2.0d), inf()];",
        "var 圓 = function(半徑: double): double { return 數學.圓周率 * 數學.次方(半徑, 2) + 數學.平方根(半徑) + 數學.四捨五入(半徑) + 數學.正弦(半徑); }; [圓(3.0d), 圓(0.5d)];",
        // An untyped function (a clone) and its loop speculate on numbers.
        "var run = function(n) { var t = 0.0d; for (var i = 0; i < n; i++) { t += Math.Sqrt(i) * Math.Sin(i) + Math.Abs(i - 50) + Math.Floor(i / 3.0d); } return t; }; run(50000);",
        // Calls native code leaves to the runtime: a wrong argument count (an error), Random and Seed.
        "var bad = function(x: double): double { return Math.Pow(x); }; bad(2.0d);",
        "var rnd = function(n: int): double { Math.Seed(n); return Math.Random() + Math.RandomInt(0, 10); }; [rnd(1), rnd(1), rnd(2)];",
    ];

    [RuntimeFact]
    public void NativeMathMatchesTheGenericEmitter()
    {
        var results = EmitterTiers.AssertSame(Programs);
        Assert.Equal("[[1.5811388300841898, 2, 3, 2, 2.5, 3, 1], [NaN, -3, -2, -2, 2.5, -3, -1], [-0, -0, -0, -0, 0, -0, -0], " +
            "[Infinity, Infinity, Infinity, Infinity, Infinity, Infinity, 1], [NaN, -Infinity, -Infinity, -Infinity, Infinity, -Infinity, -1], " +
            "[NaN, NaN, NaN, NaN, NaN, NaN, NaN], [0.7071067811865475, 0, 1, 0, 0.49999999999999994, 0, 1]]", results[0].Text);
        Assert.Equal("[[-Infinity, -2.5, Infinity, -1, -Infinity, -Infinity, 2.5], [-Infinity, -Infinity, Infinity, -Infinity, -Infinity, -Infinity, Infinity], " +
            "[Infinity, Infinity, Infinity, Infinity, Infinity, Infinity, Infinity]]", results[1].Text);
        Assert.True(results[12].IsError);
    }

    [RuntimeFact]
    public void TypedMathFunctionsTakeTheNativeTier()
    {
        var reasons = new List<string>();
        var previous = WasmCompiler.TierDiagnostics;
        WasmCompiler.TierDiagnostics = reasons.Add;
        try { foreach (string program in Programs.Take(11)) new WasmCompiler().Compile(program); }
        finally { WasmCompiler.TierDiagnostics = previous; }
        Assert.DoesNotContain(reasons, r => r.Contains("native: rejected"));
        int typedFunctions = Programs.Take(11).Sum(p => System.Text.RegularExpressions.Regex.Matches(p, @"\):\s*(int|double|float|bool|array)\s*\{").Count);
        Assert.Equal(typedFunctions, reasons.Count(r => r.EndsWith("native: accepted")));
    }

    [RuntimeFact]
    public void MathCallsGiveTheSameOutputUnderAnyQuantum()
    {
        string program = Programs[5].Replace("s(3000);", "print(s(3000));");
        string expected = WasmTestHost.Run(new WasmCompiler(false).Compile(program)).Succeeded().Output;
        foreach (string quantum in new[] { "1", "13", "65536" })
            Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum }).Succeeded().Output);
        Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_JIT"] = "0" }).Succeeded().Output);
    }

    [Fact]
    public void TheNativeTablesEqualTheGuestRuntime()
    {
        // WasmMath lists the functions, arities and constants of runtime/guest/maths.h in the same order.
        string maths = File.ReadAllText(Path.Combine(AquariusLang.Compiler.AquariusInstallation.Require().Root, "runtime", "guest", "maths.h"), System.Text.Encoding.UTF8);
        string Table(string name) => System.Text.RegularExpressions.Regex.Match(maths, name + @"\[\w+\](?:\[2\])?\s*=\s*\{(.*?)\};", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        string[] Pairs(string table) => System.Text.RegularExpressions.Regex.Matches(table, @"\{\s*u""([^""]*)""\s*,\s*u""([^""]*)""\s*\}").Select(m => m.Groups[1].Value + "|" + m.Groups[2].Value).ToArray();
        Assert.Equal(Pairs(Table("math_names")), WasmMath.Functions.Select(f => f.English + "|" + f.Chinese));
        Assert.Equal(Table("math_minimum").Split(',').Select(int.Parse), WasmMath.Functions.Select(f => f.Arity));
        Assert.Equal(Table("math_maximum").Split(',').Select(int.Parse), WasmMath.Functions.Select(f => f.Arity));
        Assert.Equal(Pairs(Table("math_constants")), WasmMath.Constants.Select(c => c.English + "|" + c.Chinese));
        Assert.Contains("#define MATH_FIRST_ID " + WasmMath.FirstId + "u", maths);
        Assert.Equal(Enum.GetNames<WasmMath.Function>(), WasmMath.Functions.Select(f => f.English));
    }
}
