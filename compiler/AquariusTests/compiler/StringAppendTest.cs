using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>Appends to a promoted local (`s = s + x`, `s += x`) extend the slot's own text in place while no other value
/// refers to it (WasmBodyEmitter.appendAt, rt_append). Every other read of the slot ends that ownership, so aliases,
/// stored copies and returned strings never change, and results equal the unoptimized compiler's.</summary>
public class StringAppendTest
{
    private static readonly string[] Programs = [
        // Copies taken between appends keep their own text.
        "var f = function(n) { var s = \"\"; var kept = []; for (var i = 0; i < n; i++) { s = s + \"ab\"; if (i % 3 == 0) { Array.Append(kept, s); } " +
        "s += String.From(i % 10); if (i == 2) { var u = s; s = s + \"!\"; Array.Append(kept, u); } } return [len(s), kept]; }; f(7);",
        // Appending a string to itself, appends to numbers and appends inside expressions.
        "var f = function() { var s = \"x\"; for (var i = 0; i < 4; i++) { s = s + s; } var n = 1; n = n + 2; n += 0.5d; var t = \"a\"; t += \"b\"; " +
        "t = t + \"c\"; var w = t + \"d\"; return [s, n, typeOf(n), t, w]; }; f();",
        // A hash key, an array element and a closure-free helper argument taken from the slot do not change later.
        "var g = function(x) { return x; }; var f = function() { var s = \"k\"; var h = {}; h[s] = 1; s = s + \"1\"; var a = [s]; s += \"2\"; var r = g(s); " +
        "s = s + \"3\"; return [h[\"k\"], a, r, s, Hash.Keys(h)]; }; f();",
        // Recursion: every activation's slot owns its own text.
        "var f = function(d) { var s = \"<\"; if (d > 0) { s = s + f(d - 1); } s = s + \">\"; return s; }; f(3);",
        // A loop that builds, then a loop that only reads, then builds again.
        "var f = function() { var s = \"\"; var out = []; for (var k = 0; k < 3; k++) { for (var i = 0; i < 4; i++) { s += String.From(k); } " +
        "Array.Append(out, s); } return out; }; f();",
        // Annotated slots, Chinese keywords and non-string operands keep their errors.
        "變數 f = 函式() { 變數 s: 字串 = \"甲\"; 迴圈 (變數 i = 0; i < 3; i++) { s = s + \"乙\"; } 回傳 s; }; f();",
        "var f = function() { var s = \"a\"; s = s + 1; return s; }; f();",
        "var f = function() { var s = \"a\"; s += [1]; return s; }; f();",
        // Top-level (dynamic) bindings are not slots and are unaffected.
        "var s = \"\"; var t = s; for (var i = 0; i < 5; i++) { s = s + \"z\"; } var r = [s, t]; r;",
    ];

    [RuntimeFact]
    public void AppendsMatchTheUnoptimizedCompiler()
    {
        var results = EmitterTiers.AssertSame(Programs);
        Assert.Equal("[22, [ab, ab0ab1ab2, ab0ab1ab2!ab, ab0ab1ab2!ab3ab4ab5ab]]", results[0].Text);
        Assert.Equal("[xxxxxxxxxxxxxxxx, 3, integer, abc, abcd]", results[1].Text);
        Assert.Equal("[1, [k1], k12, k123, [k]]", results[2].Text);
        Assert.Equal("<<<<>>>>", results[3].Text);
        Assert.Equal("[0000, 00001111, 000011112222]", results[4].Text);
        Assert.Equal("甲乙乙乙", results[5].Text);
        Assert.True(results[6].IsError && results[7].IsError);
        Assert.Equal("[zzzzz, ]", results[8].Text);
    }

    [RuntimeFact]
    public void LongAppendLoopsGiveTheSameOutputUnderAnyQuantum()
    {
        string program = "var f = function(n) { var s = \"\"; var marks = []; for (var i = 0; i < n; i++) { s = s + String.From(i % 7); " +
            "if (i % 5000 == 0) { Array.Append(marks, s); } } return [len(s), len(marks), len(marks[3]), String.Slice(s, 100, 110)]; }; print(f(40000));";
        string expected = WasmTestHost.Run(new WasmCompiler(false).Compile(program)).Succeeded().Output;
        Assert.Equal("[40000, 8, 15001, 2345601234]", expected.Trim());
        foreach (string quantum in new[] { "1", "17", "65536" })
            Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum }).Succeeded().Output);
    }
}
