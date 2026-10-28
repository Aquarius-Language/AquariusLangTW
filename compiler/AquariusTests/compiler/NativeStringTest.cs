using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>Typed strings (字串 / string) in the native tier: lengths and code units are read in place, equality,
/// concatenation and one-unit results call the runtime, `s = s + t` on a string slot appends in place while the slot owns
/// its text, and a read past the end deoptimizes at that read. Results equal the unoptimized compiler's.</summary>
public class NativeStringTest
{
    private static readonly string[] Programs = [
        // Code units, lengths and equality.
        "var pal = function(s: string): bool { var n: int = len(s); for (var i: int = 0; i < n / 2; i++) { if (s[i] != s[n - 1 - i]) { return false; } } return true; }; " +
        "[pal(\"level\"), pal(\"星泉泉星\"), pal(\"abc\"), pal(\"\"), pal(\"上海自來水來自海上\")];",
        "var count = function(s: string, c: string): int { var k: int = 0; for (var i: int = 0; i < len(s); i++) { if (s[i] == c) { k++; } } return k; }; [count(\"banana\", \"a\"), count(\"星星點點\", \"星\")];",
        // Surrogate pairs are two code units, as in the generic runtime.
        "var units = function(s: string): int { return len(s); }; var first = function(s: string): string { return s[0]; }; [units(\"😀a\"), len(first(\"😀\"))];",
        // Concatenation, appends and the copies they must not change.
        "var build = function(n: int): string { var s: string = \"\"; for (var i: int = 0; i < n; i++) { s = s + \"ab\"; } return s; }; [len(build(1000)), build(3)];",
        "var keep = function(n: int): array { var s: string = \"x\"; var t: string = s; var kept = [t]; for (var i: int = 0; i < n; i++) { s = s + \"y\"; if (i == 1) { t = s; } } " +
        "return [s, t]; }; keep(4);",
        "var join = function(a: string, b: string): string { return a + \"-\" + b; }; var twice = function(s: string): string { return join(s, s); }; twice(\"甲\");",
        // A read past the end deoptimizes there: the generic body gives null and the annotation check reports it.
        "var at = function(s: string, i: int): string { return s[i]; }; at(\"abc\", 7);",
        "var at = function(s: string, i: int): string { return s[i]; }; [at(\"abc\", 1), at(\"abc\", 2)];",
        // Arguments of another type from untyped code run the generic body.
        "var mk = function(v) { return v; }; var size = function(s: string): int { return len(s); }; [size(\"abcd\"), size(mk(\"xy\"))];",
        "var mk = function(v) { return v; }; var size = function(s: string): int { return len(s); }; size(mk(5));",
        // Strings from an array of any values, and string results through calls.
        "var pal = function(s: string): bool { var n: int = len(s); for (var i: int = 0; i < n / 2; i++) { if (s[i] != s[n - 1 - i]) { return false; } } return true; }; " +
        "var many = function(words: array): int { var c: int = 0; for (var i: int = 0; i < len(words); i++) { if (pal(words[i])) { c++; } } return c; }; many([\"aa\", \"ab\", \"aba\", \"\"]);",
        // A user's 長度 / len shadows the builtin: the native entry declines and the generic body calls the user's.
        "var len = function(x) { return 42; }; var size = function(s: string): int { return len(s); }; size(\"abc\");",
        "變數 長度 = 函式(x) { 回傳 7; }; 變數 量 = 函式(a: 整數[]): 整數 { 回傳 長度(a); }; 量([1, 2]);",
    ];

    [RuntimeFact]
    public void NativeStringsMatchTheUnoptimizedCompiler()
    {
        var results = EmitterTiers.AssertSame(Programs);
        Assert.Equal("[真, 真, 假, 真, 真]", results[0].Text);
        Assert.Equal("[3, 2]", results[1].Text);
        Assert.Equal("[3, 1]", results[2].Text);
        Assert.Equal("[2000, ababab]", results[3].Text);
        Assert.Equal("[xyyyy, xyy]", results[4].Text);
        Assert.Equal("甲-甲", results[5].Text);
        Assert.True(results[6].IsError);
        Assert.Equal("[b, c]", results[7].Text);
        Assert.Equal("[4, 2]", results[8].Text);
        Assert.True(results[9].IsError);
        Assert.Equal(3, results[10].Integer);
        Assert.Equal(42, results[11].Integer);
        Assert.Equal(7, results[12].Integer);
    }

    [RuntimeFact]
    public void TypedStringFunctionsTakeTheNativeTier()
    {
        var reasons = new List<string>();
        var previous = WasmCompiler.TierDiagnostics;
        WasmCompiler.TierDiagnostics = reasons.Add;
        try { foreach (int i in new[] { 0, 1, 3, 5, 10 }) new WasmCompiler().Compile(Programs[i]); }
        finally { WasmCompiler.TierDiagnostics = previous; }
        Assert.DoesNotContain(reasons, r => r.Contains("native: rejected"));
    }

    [RuntimeFact]
    public void StringLoopsCollectAndGiveTheSameOutputUnderAnyQuantum()
    {
        // Concatenations that are not appends allocate a new text each time: the loop lets pending collections run.
        string program = "var churn = function(n: int): int { var keep: string = \"\"; var total: int = 0; for (var i: int = 0; i < n; i++) { " +
            "var t: string = keep + \"0123456789\"; total += len(t); if (i % 1000 == 0) { keep = keep + \"z\"; } } return total; }; " +
            "var build = function(n: int): string { var s: string = \"\"; for (var i: int = 0; i < n; i++) { s = s + \"ab\"; } return s; }; " +
            "print(churn(200000), len(build(50000)));";
        string expected = WasmTestHost.Run(new WasmCompiler(false).Compile(program)).Succeeded().Output;
        foreach (string quantum in new[] { "1", "17", "65536" })
            Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum }).Succeeded().Output);
        Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_JIT"] = "0" }).Succeeded().Output);
    }
}
