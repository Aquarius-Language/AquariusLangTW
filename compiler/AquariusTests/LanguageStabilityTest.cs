using AquariusLang.lexer;
using AquariusLang.parser;

namespace AquariusTests;

/// <summary>Regression cases for gaps found by writing examples and by monkey testing: the null literal, string arrays,
/// compound assignment and ++ on elements and fields, assignment through members, typed array widening, loop
/// closures, loops without some of their parts, string escapes, statements after blocks and parser limits.</summary>
public class LanguageStabilityTest
{
    [RuntimeFact]
    public void NullIsALiteralInBothLanguages() => LanguageCases.Expect(
        ("[null, 空值, 型別(null), null == 空值, [1][5] == null, {\"a\": 1}[\"b\"] == null, null != 0];", "array", "[null, null, null, 真, 真, 真, 真]"),
        ("struct N { v: int; next: N } var n = N(1, N(2)); var t = 0; for (var at = n; at != null; at = at.next) { t += at.v; } t;", "integer", "3"),
        ("struct N { v: int; next: N } var f = function(n: N): N { if (n.v > 0) { return null; } return n; }; [f(N(1)), f(N(0)).v];", "array", "[null, 0]"));

    [RuntimeFact]
    public void BareReturnReturnsNull() => LanguageCases.Expect(
        ("var f = function(x) { if (x > 0) { return; } return x; }; [f(1), f(-2)];", "array", "[null, -2]"),
        ("var log = []; var g = function() { Array.Append(log, 1); return }; g(); [g(), log];", "array", "[null, [1, 1]]"),
        ("變數 h = 函式() { 回傳; }; h();", "null", "null"));

    [Fact]
    public void NullOnlyFitsStructAnnotations()
    {
        Assert.Contains("空值 null", CompileError("var x: int = null;"));
        Assert.Contains("空值 null", CompileError("var f = function(s: string): int { 1; }; f(null);"));
        Assert.Contains("空值 null", CompileError("struct P { xs: int[] } P(null);"));
        Assert.Null(CompileError("struct P { q: P } var p: P = null; var f = function(): P { return null; }; P(q: null);"));
    }

    [RuntimeFact]
    public void StringArraysCanBeTyped()
    {
        LanguageCases.Expect(
            ("var a: string[] = [\"x\", \"yy\"]; a[0] = \"z\"; a[1] += \"!\"; Array.Append(a, \"w\"); a;", "array", "[z, yy!, w]"),
            ("var f = function(a: 字串[]): int { var t: int = 0; for (var i: int = 0; i < len(a); i++) { t += len(a[i]); } return t; }; f([\"ab\", \"cde\"]);", "integer", "5"));
        LanguageCases.ExpectErrors(
            ("var a: string[] = [\"x\", 1];", "expects string[]"),
            ("var a: string[] = [\"x\"]; var v = 1; a[0] = v;", "the array holds string"),
            ("var a: string[] = []; Array.Append(a, 5);", "the array holds string"));
        Assert.Contains("字串[] string[]", CompileError("var a: string[] = [\"x\"]; a[0] = 1;"));
    }

    [RuntimeFact]
    public void CompoundAssignmentWorksOnElementsEntriesAndNestedMembers() => LanguageCases.Expect(
        ("var a: int[] = [1, 2, 3, 4]; a[0] += 10; a[1] *= 5; a[2] -= 1; a[3] += 2.9d; a;", "array", "[11, 10, 2, 6]"),
        ("var a = [1, 2.5d]; a[0] /= 2; a[1] %= 2; [a, 型別(a[0])];", "array", "[[0, 0.5], integer]"),
        ("var h = {\"x\": 1}; h[\"x\"] += 4; h[\"y\"] = 2; h[\"y\"] *= 3; h;", "hash", "{x: 5, y: 6}"),
        ("var g = [[1, 2], [3, 4]]; g[1][0] += 100; g;", "array", "[[1, 2], [103, 4]]"),
        ("struct S { xs: double[]; inner: S } var s = S(xs: [1.0d, 2.0d], inner: S(xs: [9.0d])); s.xs[0] = 5.5d; s.xs[1] += 1; s.inner.xs[0] -= 0.5d; [s.xs, s.inner.xs];", "array", "[[5.5, 3], [8.5]]"),
        ("var t = 0; var f = function() { t++; return 0; }; var a = [5]; a[f()] += 1; [a, t];", "array", "[[6], 1]"));

    [RuntimeFact]
    public void IncrementWorksOnElementsAndFields() => LanguageCases.Expect(
        ("var a: int[] = [1, 2]; a[0]++; ++a[1]; [a[0]++, ++a[0], a];", "array", "[2, 4, [4, 3]]"),
        ("struct P { n: int; d: double } var p = P(1, 1.5d); p.n++; ++p.n; [p.n++, ++p.d, p];", "array", "[3, 2.5, P{n: 4, d: 2.5}]"),
        ("var h = {\"k\": 1}; h[\"k\"]++; [++h[\"k\"], h];", "array", "[3, {k: 3}]"),
        ("struct B { items: int[] } var b = B([1, 2]); b.items[1]++; b.items;", "array", "[1, 3]"),
        ("var a: int[] = [2147483647]; a[0]++; a;", "array", "[-2147483648]"));

    [RuntimeFact]
    public void InvalidElementAndFieldWritesAreLanguageErrors() => LanguageCases.ExpectErrors(
        ("var a = [\"s\"]; a[0]++;", "Increment requires a number"),
        ("var h = {}; h[\"n\"]++;", "Increment requires a number"),
        ("var a = [1]; a[3]++;", "bounds"),
        ("var a = [1, 2]; a[5] += 1;", "bounds"),
        ("var a = [1, 2]; a[\"x\"] += 1;", "integer"),
        ("var a = [1, 2, 3]; var f = function() { Array.Delete(a, 0, 3); return 1; }; a[2] += f();", "bounds"),
        ("struct N { v: int; next: N } N(1).next.v++;", "Cannot assign a member of NULL"),
        ("struct N { v: int; next: N } var f = function(n: N): int { n.next.v += 2; return 0; }; f(N(1));", "Cannot assign a member of NULL"));

    [RuntimeFact]
    public void TypedFloatingArraysWidenNumbersLikeVariables() => LanguageCases.Expect(
        (LanguageCases.Typed("var a: double[] = [1.0d, 2.0d]; a[0] = 3; var i = 4; a[1] = i; return a;"), "array", "[[3, 4], [double, double]]"),
        (LanguageCases.Typed("var a: float[] = [1.0f]; a[0] = 2; Array.Append(a, 3); Array.AppendArray(a, [4, 5]); return a;"), "array", "[[2, 3, 4, 5], [float, float, float, float]]"),
        ("var src = [1, 2]; var a: double[] = [0.5d]; Array.AppendArray(a, src); [a, 型別(a[1]), 型別(src[0])];", "array", "[[0.5, 1, 2], double, integer]"));

    [RuntimeFact]
    public void ClosuresKeepTheirIterationsLoopVariable() => LanguageCases.Expect(
        ("var cs = []; for (var i = 0; i < 3; i++) { cs = push(cs, function() { return i; }); } [cs[0](), cs[1](), cs[2]()];", "array", "[0, 1, 2]"),
        ("var f = function() { var cs = []; for (var i: int = 0; i < 3; i++) { var j = i * 10; cs = push(cs, function() { return i + j; }); } return cs; }; var c = f(); [c[0](), c[1](), c[2]()];", "array", "[0, 11, 22]"));

    [RuntimeFact]
    public void LoopPartsMayBeLeftOut() => LanguageCases.Expect(
        ("var i = 0; for (; i < 3;) { i++; } i;", "integer", "3"),
        ("var n = 0; 迴圈 (;;) { n++; 如果 (n > 4) { 中斷; } } n;", "integer", "5"),
        ("var out = []; for (var i = 0; i < 2;) { out = push(out, i); i++; } out;", "array", "[0, 1]"),
        ("var f = function() { for (;;) { return 7; } }; f();", "integer", "7"));

    [RuntimeFact]
    public void ContinueStartsTheNextIteration()
    {
        LanguageCases.Expect(
            ("var out = []; for (var i = 0; i < 6; i++) { if (i % 2 == 0) { continue; } out = push(out, i); } out;", "array", "[1, 3, 5]"),
            ("var t = 0; 迴圈 (變數 i = 0; i < 3; i++) { 迴圈 (變數 j = 0; j < 3; j++) { 如果 (j == i) { 繼續; } t += 10 * i + j; } } t;", "integer", "66"),
            ("var f = function(n: int): int { var s: int = 0; for (var i: int = 0; i < n; i++) { if (i % 3 == 0) { continue; } s += i; } return s; }; f(10);", "integer", "27"),
            ("var cs = []; for (var i = 0; i < 4; i++) { if (i == 1) { continue; } cs = push(cs, function() { i; }); } [cs[0](), cs[1](), cs[2]()];", "array", "[0, 2, 3]"),
            ("var n = 0; var i = 0; for (;;) { i++; if (i > 5) { break; } if (i == 2) { continue; } n += i; } n;", "integer", "13"));
        Assert.Contains("continue can only be used inside a loop", CompileError("var f = function() { continue; };"));
        Assert.Contains("continue can only be used inside a loop", CompileError("for (var i = 0; i < 2; i++) { var f = function() { continue; }; }"));
    }

    [RuntimeFact]
    public void StatementsAfterBlocksStandAlone() => LanguageCases.Expect(
        ("var a = [1, 2]; if (true) { a = [3]; } [4, 5]; a;", "array", "[3]"),
        ("var a = 1; if (true) { a = 2; } -a; a;", "integer", "2"),
        ("var a = [0]; for (var i = 0; i < 2; i++) {} ++a[0]; a;", "array", "[1]"),
        ("var g = function() { 5; }; for (var i = 0; i < 1; i++) {} (g)();", "integer", "5"),
        ("(if (true) { 1; } else { 2; }) + 10;", "integer", "11"));

    [RuntimeFact]
    public void StringsEscapeQuotesAndBackslashesOnly() => LanguageCases.Expect(
        ("\"say \\\"hi\\\" \\\\ C:\\dir\\file \\t\";", "string", "say \"hi\" \\ C:\\dir\\file \\t"),
        ("長度(\"\\\"\");", "integer", "1"));

    [RuntimeFact]
    public void TheSmallestIntegerHasALiteral() => LanguageCases.Expect(
        ("var x = -2147483648; [x, x - 1, -x, x / -1, 型別(x)];", "array", "[-2147483648, 2147483647, -2147483648, -2147483648, integer]"));

    [Theory]
    [InlineData("print(\"unterminated);", "Unterminated string")]
    [InlineData("print(2147483648);", "Could not parse 2147483648 as a 32-bit integer")]
    [InlineData("var f = function(x: int, x: int) { x; };", "Duplicate parameter x")]
    [InlineData("1 = 2;", "The left side of = must be a variable, a field or an index")]
    [InlineData("var f = function() { 1; }; f() += 3;", "The left side of += must be a variable, a field or an index")]
    public void InvalidProgramsReportOneClearError(string source, string message)
    {
        string? error = CompileError(source);
        Assert.NotNull(error);
        Assert.Contains(message, error);
    }

    [Fact]
    public void DeepNestingIsOneParserErrorNotACrash()
    {
        string source = "print(" + new string('(', 300) + "1" + new string(')', 300) + ");";
        var lexer = Lexer.NewInstance(source);
        var parser = Parser.NewInstance(lexer);
        parser.ParseAST();
        Assert.Equal(["運算式巢狀超過 256 層 / Expression nesting exceeds the limit of 256."], parser.Errors);
    }

    [Fact]
    public void UnknownTypesAreReportedOnce() =>
        Assert.Equal("未知的型別 Q（可用：整數、浮點數、雙精度、布林、字串、陣列或結構名稱）/ Unknown type Q (use int, float, double, bool, string, array or a struct name).",
            CompileError("var p: Q = 1;"));

    /// <summary>The compiler's error message for a program, or null when it compiles.</summary>
    private static string? CompileError(string source)
    {
        try { new AquariusLang.Compiler.LoweringCompiler().Compile(source); return null; }
        catch (AquariusLang.Compiler.CompilationException e) { return e.Message; }
    }
}
