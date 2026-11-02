using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>The integer bitwise operators &amp; | ^ ~ &lt;&lt; &gt;&gt; (arithmetic) &gt;&gt;&gt; (logical) and their compound
/// assignments: integers only, shift counts masked to 5 bits like i32, the same results in the generic runtime, generic
/// compiled code (inline for proven integers) and native code.</summary>
public class BitwiseOperatorTest
{
    [RuntimeFact]
    public void Semantics() => LanguageCases.Expect(
        ("[6 & 3, 6 | 3, 6 ^ 3, ~5, ~-1, 1 << 3, -16 >> 2, -16 >>> 28, -1 >>> 0, -1 >>> 1, 1 << 31, 1 << 32, 1 << 33, 1 << -1, 8 >> 35, 0xFF00 & 0x0FF0];", "array",
            "[2, 7, 5, -6, 0, 8, -4, 15, -1, 2147483647, -2147483648, 1, 2, -2147483648, 1, 3840]"),
        ("[1 + 2 << 1, 1 << 2 + 1, (5 & 3) == 1, 1 | 2 ^ 3 & 4, 2 < 1 << 3, 型別(6 & 3)];", "array", "[6, 8, 真, 3, 真, integer]"),
        ("變數 x = 12; x &= 10; 變數 y = 1; y <<= 4; 變數 z = -64; z >>>= 28; 變數 w = 5; w ^= 3; w |= 8; 變數 v = -9; v >>= 1; [x, y, z, w, v];", "array", "[8, 16, 15, 14, -5]"),
        ("變數 a = [1, 2]; a[0] <<= 3; 變數 h = {\"k\": 6}; h[\"k\"] &= 3; 結構 點 { x: 整數 }; 變數 p = 點(5); p.x ^= 1; [a, h, p.x];", "array", "[[8, 2], {k: 2}, 4]"),
        // FNV-1a and xorshift32 in untyped code.
        ("變數 h = 0x811C9DC5; 迴圈 (變數 i = 0; i < 4; i++) { h ^= i + 97; h *= 16777619; } 變數 x = 0x92D68CA2; 迴圈 (變數 i = 0; i < 10; i++) { x ^= x << 13; x ^= x >>> 17; x ^= x << 5; } [h, x];",
            "array", "[-835421763, -1641071047]"));

    [RuntimeFact]
    public void NonIntegersAreErrors() => LanguageCases.ExpectErrors(
        ("變數 f = 函式(x) { x & 1; }; f(2.5d);", "Bitwise operators need integers / 位元運算需要整數: DOUBLE & INTEGER"),
        ("變數 f = 函式(x) { ~x; }; f(\"a\");", "位元運算需要整數: ~STRING"),
        ("變數 f = 函式(x) { 1 << x; }; f(真);", "INTEGER << BOOLEAN"),
        ("變數 d = 1.5d; 變數 f = 函式() { d |= 1; }; f();", "DOUBLE | INTEGER"),
        // Known types are compile errors.
        ("1.5 & 1;", "The bitwise operator & needs integers"),
        ("~1.5f;", "The bitwise operator ~ needs an integer"),
        ("變數 x: 雙精度 = 1; x &= 2;", "cannot use &="));

    private static readonly string[] Programs = [
        // Native code: typed functions with every operator, compound assignments, shifts by large and negative counts.
        "var xs = function(s: int, n: int): int { var x: int = s; for (var i: int = 0; i < n; i++) { x ^= x << 13; x ^= x >>> 17; x ^= x << 5; } return x; }; [xs(0x92D68CA2, 10), xs(1, 100000)];",
        "var fnv = function(a: int[]): int { var h: int = 0x811C9DC5; for (var i: int = 0; i < len(a); i++) { h = (h ^ a[i]) * 16777619; } return h; }; fnv([1, 2, 3, 400000, -7]);",
        "var ops = function(a: int, b: int): array { return [a & b, a | b, a ^ b, ~a, a << b, a >> b, a >>> b, a << (b - 40), a >>> (b + 32)]; }; [ops(-16, 2), ops(0x7FFFFFFF, 31), ops(12345, 0), ops(-1, 33)];",
        "var pop = function(v: int): int { var c: int = 0; var x: int = v; for (var i: int = 0; i < 32; i++) { c += x & 1; x >>>= 1; } return c; }; [pop(-1), pop(0), pop(0x55555555), pop(-2147483648)];",
        "var flags = function(a: int[]): int[] { for (var i: int = 0; i < len(a); i++) { a[i] |= 1 << i; a[i] &= ~4; a[i] ^= 0xF0; a[i] <<= 1; a[i] >>= 1; } return a; }; flags([0, 0, 0, 7, -1]);",
        // Untyped functions (clones) and generic code with proven integer tags.
        "var mix = function(a, b) { var t = a; for (var i = 0; i < 1000; i++) { t = (t << 5) ^ (t >>> 27) ^ b; b = b + i; } return t; }; mix(17, 3);",
        "var t = 0; for (var i = 0; i < 2000; i++) { t ^= i << (i & 7); t = t >>> 1 | t << 31; } t;",
        // Dynamic operands and shadowed module globals (generic code calls the runtime).
        "var g = 5; var bump = function(k) { g |= k; g <<= 1; g ^= 3; return g; }; [bump(1), bump(8), g];",
        "var any = [1, 2.5d, 3]; var r = []; for (var i = 0; i < 3; i = i + 2) { r = push(r, any[i] << 2); } r;",
        // A non-integer reaches a typed function's generic fallback, and an untyped one.
        "var bad = function(x) { return x >> 1; }; bad(2.0f);",
    ];

    [RuntimeFact]
    public void EveryTierGivesTheSameResults()
    {
        var results = EmitterTiers.AssertSame(Programs);
        Assert.Equal("[-1641071047, " + Xorshift(1, 100000) + "]", results[0].Text);
        Assert.Equal("[32, 0, 16, 1]", results[3].Text);
        Assert.True(results[9].IsError);
    }

    private static int Xorshift(int seed, int n)
    {
        uint x = (uint)seed;
        for (int i = 0; i < n; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; }
        return unchecked((int)x);
    }

    [RuntimeFact]
    public void TypedBitwiseFunctionsTakeTheNativeTier()
    {
        var reasons = new List<string>();
        var previous = WasmCompiler.TierDiagnostics;
        WasmCompiler.TierDiagnostics = reasons.Add;
        try { foreach (string program in Programs.Take(5)) new WasmCompiler().Compile(program); }
        finally { WasmCompiler.TierDiagnostics = previous; }
        Assert.DoesNotContain(reasons, r => r.Contains("native: rejected"));
        Assert.Equal(5, reasons.Count(r => r.EndsWith("native: accepted")));
    }
}
