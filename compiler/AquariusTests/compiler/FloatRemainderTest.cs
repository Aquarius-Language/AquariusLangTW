using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>% on floats and doubles is the exact IEEE remainder (C fmod, the guest's rt_remainder) in every tier: the
/// generic runtime, generic compiled code (known and dynamic tags, compound assignments, integer shadows) and native code.
/// It used to be x - trunc(x / y) * y, which loses the remainder of large quotients (1e17 % 3 gave 0, not 1), gave +0 for
/// -0 % y and NaN for x % Infinity.</summary>
public class FloatRemainderTest
{
    private const string Values = "[100000000000000000.0d, 1e300, -7.5, 7.5, -(0.0d), 0.0d, 5.0d, Math.Infinity, -Math.Infinity, Math.NaN, 0.3, 123456789.0d, -1e-300]";
    private const string Divisors = "[3, 7.0d, 2, -2, 0.1, Math.Infinity, 0.0d, -(0.0d), 1e-300, 0.75f, 7]";

    private static readonly string[] Programs = [
        // Dynamic tags in generic code (array elements of any type).
        $"var a = {Values}; var b = {Divisors}; var out = []; for (var i = 0; i < len(a); i++) {{ for (var j = 0; j < len(b); j++) {{ out = push(out, a[i] % b[j]); }} }} out;",
        // Known double tags in generic code, and compound assignments of a double and of an integer variable.
        "var r = function() { var x = 100000000000000000.0d; var y = 3.0d; var d = -0.0d - 0.0d; d %= 3; var n = 17; n %= 2.5d; var m = 1000000000; m %= 0.7d; return [x % y, x % 7, 1.0d / d, n, m]; }; r();",
        // Native code: typed doubles, floats, a mixed integer operand and compound assignments.
        "var f = function(x: double, y: double): double { return x % y; }; [f(100000000000000000.0d, 3), f(-7.5d, 2), f(-(0.0d), 3), f(5, Math.Infinity), f(1e300, 7), f(0.3, 0.1)];",
        "var g = function(x: float, y: int): float { var t: float = x; t %= y; return t + x % 0.4f; }; [g(1.5f, 1), g(-1e30f, 7), g(16777217.0f, 3)];",
        "var h = function(n: int): double { var t: double = 0.0d; for (var i: int = 1; i <= n; i++) { t += (i * 1234567.891d) % (i * 0.37d + 0.01d); } return t; }; h(5000);",
        "var k = function(n: int, d: double): int { var m: int = n; m %= d; return m; }; [k(1000000000, 0.7d), k(-7, 2.5d), k(5, Math.Infinity)];",
        // An integer remainder by a zero double is still an error.
        "var e = function(n: int, d: double): int { var m: int = n; m %= d; return m; }; e(5, 0.0d);",
    ];

    [RuntimeFact]
    public void EveryTierComputesTheExactRemainder()
    {
        var results = EmitterTiers.AssertSame(Programs);
        Assert.Equal("[1, 5, -Infinity, 2, 0]", results[1].Text);
        Assert.Equal("[1, -1.5, -0, 5, 1, 0.09999999999999998]", results[2].Text);
        Assert.Equal("[0.79999995, -1.3493038, 1.15]", results[3].Text);
        Assert.Equal("[0, -2, 5]", results[5].Text);
        Assert.True(results[6].IsError);
    }
}
