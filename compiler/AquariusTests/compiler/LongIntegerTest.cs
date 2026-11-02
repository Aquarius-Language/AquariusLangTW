using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>長整數 / long, 64-bit integers: the shared vectors in tests/long-integer.json (arithmetic, wrap-around, exact
/// comparisons with doubles, conversions, printing, hashing, Math, arrays), errors, literals, threads, and the generic
/// emitter's inline i64 code against the runtime.</summary>
public class LongIntegerTest
{
    [RuntimeFact]
    public void Vectors()
    {
        using var json = ArrayLibraryTest.Vectors("long-integer");
        var cases = json.RootElement.GetProperty("valid").EnumerateArray()
            .Select(item => (item[1].GetString()!, ArrayLibraryTest.TypeOf(item[2].GetString()!), item[2].GetString()!)).ToArray();
        Assert.Equal(10, cases.Length);
        LanguageCases.Expect(cases);
    }

    [RuntimeFact]
    public void InvalidOperationsReportErrors()
    {
        using var json = ArrayLibraryTest.Vectors("long-integer");
        var sources = json.RootElement.GetProperty("invalid").EnumerateArray().Select(i => i.GetString()!).ToArray();
        Assert.Equal(7, sources.Length);
        LanguageCases.ExpectErrors(sources);
        LanguageCases.ExpectErrors(("1L / 0;", "Integer division by zero"), ("變數 f = 函式(x) { x & 1; }; f(1.5d);", "DOUBLE & INTEGER"));
    }

    [RuntimeFact]
    public void CompileTimeTypesAndLiterals() => LanguageCases.ExpectErrors(
        ("2147483648;", "for a larger integer use a long: 2147483648L"),
        ("9223372036854775808L;", "Could not parse 9223372036854775808L as a 64-bit long"),
        ("變數 x: 長整數 = 1.5;", "x expects 長整數 long, got 雙精度 double"),
        ("變數 x: 整數 = 5L;", "x expects 整數 int, got 長整數 long"),
        ("1.5L;", "Invalid number literal 1.5L"),
        ("1L & 1.5;", "The bitwise operator & needs integers"));

    [RuntimeFact]
    public void WorkersCarryLongs() => LanguageCases.Expect(
        ("變數 t = Thread.Start(函式(x) { x * 3 + 1L; }, [3000000000L]); [Thread.Result(t), 型別(Thread.Result(t))];", "array", "[9000000001, long]"));

    private static readonly string[] Programs = [
        // Proven longs in generic code: the emitter's inline i64 arithmetic, comparisons and bitwise operators.
        "var f = function(n) { var t = 0L; for (var i = 0; i < n; i++) { t = t * 31 + i; } return t; }; f(100000);",
        "var g = function(n) { var h = 0xCBF29CE484222325L; for (var i = 0; i < n; i++) { h = (h ^ i) * 0x100000001B3L; } return h; }; g(5000);",
        "var s = function(n) { var x = 88172645463325252L; for (var i = 0; i < n; i++) { x = x ^ x << 13; x = x ^ x >>> 7; x = x ^ x << 17; } return [x, x < 0, x == 0L, ~x, -x]; }; s(1000);",
        "var c = function() { var a = 5000000000L; var b = 3; return [a + b, a - b, a * b, a < b, a > b, a <= 5000000000L, a == 5000000000L, a != b, a & b, a | b, a ^ b, a << b, a >> b, a >>> b, b << a]; }; c();",
        // Division, remainder, mixing with doubles and floats, and unknown tags take the runtime.
        "var d = function(a, b) { return [a / b, a % b, a + 0.5, a * 1.5f, a < 0.5]; }; [d(7L, 2), d(-9223372036854775807L - 1, -1), d(5, 2L)];",
        "var m = [1, 2L, 3.5, 4L]; var t = 0; for (var i = 0; i < len(m); i++) { t = t + m[i]; } [t, typeOf(t)];",
        "var top = 9223372036854775807L; var k = 0; for (var i = 0; i < 3; i++) { top++; k += 1; } [top, k];",
    ];

    [RuntimeFact]
    public void GenericInlineCodeMatchesTheRuntime()
    {
        var results = EmitterTiers.AssertSame(Programs);
        Assert.Equal(Hash31(100000).ToString(), results[0].Text);
        Assert.Equal("[5000000003, 4999999997, 15000000000, 假, 真, 真, 真, 真, 0, 5000000003, 5000000003, 40000000000, 625000000, 625000000, 3]", results[3].Text);
    }

    private static long Hash31(int n)
    {
        long t = 0;
        for (int i = 0; i < n; i++) t = unchecked(t * 31 + i);
        return t;
    }
}
