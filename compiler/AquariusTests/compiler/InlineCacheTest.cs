using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>Inline caches of struct fields in generic code (WasmBodyEmitter.StructField): a member name constant caches the
/// struct type id and field index of its last struct, so a struct of that type reads (and, for an unannotated field,
/// writes) the field in place. Every other receiver, a typed field's write and a miss take the helper, so results equal
/// the unoptimized compiler's.</summary>
public class InlineCacheTest
{
    private static readonly string[] Programs = [
        // One type: reads and untyped writes in a loop.
        "struct P { x; y } var f = function() { var p = P(0, 0); for (var i = 0; i < 1000; i++) { p.x = p.x + 1; p.y = p.y + p.x % 3; } return [p.x, p.y]; }; f();",
        // Two types at the same sites, with the field at different positions, alternating.
        "struct A { x; y } struct B { y; z; x } var f = function() { var items = [A(1, 2), B(3, 4, 5)]; var s = 0; for (var i = 0; i < 10; i++) { var o = items[i % 2]; " +
        "o.x = o.x + i; s += o.x * 10 + o.y; } return [s, items[0].x, items[1].x]; }; f();",
        // Annotated fields: writes are checked and widened by the helper, reads are cached.
        "struct T { n: int; d: double; any } var f = function() { var t = T(n: 1, d: 0.5d); for (var i = 0; i < 5; i++) { t.d = i; t.n = t.n + i; t.any = t.d; } return [t.n, t.d, typeOf(t.d), t.any]; }; f();",
        "struct T { n: int } var f = function() { var t = T(1); t.n = \"x\"; return t; }; f();",
        // A null receiver, an unknown field and a module member keep their behavior.
        "struct P { x } var f = function() { var p = P(1); var e = []; var q = e[0]; var r = []; Array.Append(r, p.x); var h = function() { return q.x; }; return h(); }; f();",
        "struct P { x } var f = function() { var p = P(1); return p.nope; }; f();",
        "var f = function() { var s = 0; for (var i = 0; i < 3; i++) { s += len(Array.Make(i, 0)); } return [s, typeOf(Array.Len)]; }; f();",
        // A struct type declared in a function: every call makes a new type value of the same declaration.
        "var make = function(v) { struct Box { value } return Box(v); }; var f = function() { var s = 0; for (var i = 0; i < 6; i++) { var b = make(i); b.value = b.value * 2; s += b.value; } return s; }; f();",
        // Top-level (dynamic) receivers.
        "struct P { x; y } var p = P(1, 2); for (var i = 0; i < 4; i++) { p.y = p.x + p.y; } var r = [p.x, p.y]; r;",
    ];

    [RuntimeFact]
    public void CachedFieldsMatchTheUnoptimizedCompiler()
    {
        var results = EmitterTiers.AssertSame(Programs);
        Assert.Equal("[1000, 1000]", results[0].Text);
        Assert.Equal("[11, 4, double, 4]", results[2].Text);
        Assert.True(results[3].IsError && results[4].IsError && results[5].IsError);
        Assert.StartsWith("[3, ", results[6].Text);
        Assert.Equal(30, results[7].Integer);
        Assert.Equal("[1, 6]", results[8].Text);
    }

    [RuntimeFact]
    public void CachedFieldsSurviveCollectionsAndTinyQuanta()
    {
        // Many short-lived structs of two types force collections while the caches hold their type ids.
        string program = "struct A { x; pad } struct B { pad; x } var f = function(n) { var s = 0; for (var i = 0; i < n; i++) { var o = A(i, [i]); " +
            "if (i % 3 == 0) { o = B([i], i); } o.x = o.x + 1; s = (s + o.x) % 1000003; } return s; }; print(f(200000));";
        string expected = WasmTestHost.Run(new WasmCompiler(false).Compile(program)).Succeeded().Output;
        foreach (string quantum in new[] { "7", "65536" })
            Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum }).Succeeded().Output);
    }
}
