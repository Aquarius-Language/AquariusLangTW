using AquariusLang.ast;
using AquariusLang.Compiler;
using AquariusLang.lexer;
using AquariusLang.parser;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>The syntax that declarative code (widget trees such as SaturnUI's) leans on: trailing commas, struct field
/// defaults, positional then named arguments, function defaults and named arguments, collection if / for / spread in
/// array literals, and short functions, in both syntaxes, through the parser, the type checker and the runtime.</summary>
public class DeclarativeSyntaxTest
{
    private static AbstractSyntaxTree Parse(string source)
    {
        var parser = Parser.NewInstance(Lexer.NewInstance(source));
        var tree = parser.ParseAST();
        Assert.Empty(parser.Errors);
        return tree;
    }

    private static void ParseError(string source)
    {
        var parser = Parser.NewInstance(Lexer.NewInstance(source));
        parser.ParseAST();
        Assert.NotEmpty(parser.Errors);
    }

    [Theory]
    [InlineData("var a = [1, 2,];", "[1, 2]")]
    [InlineData("var f = function(a, b,) { a; }; f(1, 2,);", "f(1, 2)")]
    [InlineData("struct S { a = 1; b: int = 2, }", "struct S { a = 1; b: int = 2 }")]
    [InlineData("var f = function(a, b = 3) { a; };", "function(a, b = 3)")]
    [InlineData("T(\"hi\", size: 2);", "T(hi, size: 2)")]
    [InlineData("var l = [0, ...r, if (c) 1 else 2, for (var i = 0; i < 3; i++) i];", "...r, if (c) 1 else 2, for (")]
    [InlineData("var f = x => x * 2;", "(x * 2)")]
    [InlineData("var f = (a, b = 1) => a + b;", "(a, b = 1) ")]
    [InlineData("var f = () => { 1; };", "() 1")]
    public void NewFormsParse(string source, string printed)
    {
        Assert.Contains(printed, Parse(source).String());
    }

    [Theory]
    [InlineData("f(a: 1, 2);")] // positional after named
    [InlineData("var f = function(a = 1, b) { a; };")] // a parameter without a default after one with a default
    [InlineData("var f = (a, b);")] // a parameter list without =>
    [InlineData("var f = (a + 1) => a;")] // a parameter that is no name
    [InlineData("[1 2];")]
    public void MalformedFormsAreParseErrors(string source) => ParseError(source);

    [Fact]
    public void OldFormsKeepTheirMeaning()
    {
        // A grouped assignment, an if or for with a block inside an array literal, and a hash after an arrow in parentheses.
        Assert.Contains("(x = 5)", Parse("var x = 1; (x = 5);").String());
        Assert.IsType<IfExpression>(((ArrayLiteral)((LetStatement)Parse("var a = [if (c) { 1; } else { 2; }];").Statements[0]).Value).Elements[0]);
        Assert.IsType<ForLoopLiteral>(((ArrayLiteral)((LetStatement)Parse("var a = [for (var i = 0; i < 2; i++) { i; }];").Statements[0]).Value).Elements[0]);
    }

    [Theory]
    [InlineData("struct P { x: int = \"a\" }")]
    [InlineData("var f = function(a: int = \"a\") { a; };")]
    [InlineData("var f = function(a, b = 1) { a; }; f(b: 2);")] // a required parameter left out
    [InlineData("var f = function(a) { a; }; f(1, a: 2);")] // a parameter given twice
    [InlineData("var f = function(a) { a; }; f(c: 2);")]
    [InlineData("struct P { x; y } var p = P(1, x: 2);")]
    [InlineData("struct P { x: int; y } var p = P(y: 1);")]
    public void DefiniteMistakesAreCompileErrors(string source)
    {
        Assert.NotEmpty(TypeChecker.Check(Parse(source)).Errors);
    }

    [Fact]
    public void DefaultsSatisfyRequiredFields()
    {
        Assert.Empty(TypeChecker.Check(Parse("struct P { x: int = 1; s: string = \"a\" } var p = P(); var q = P(s: \"b\");")).Errors);
    }

    [RuntimeFact]
    public void StructDefaultsAndMixedArguments()
    {
        var results = WasmTestHost.EvalMany([
            "struct Style { size: double = 14.0d; color = \"text\"; tags = [] } var a = Style(); var b = Style(color: \"red\",); Array.Append(a.tags, 1); [a.size, a.color, b.color, len(a.tags), len(b.tags)];",
            "struct Text { text; size = 12; weight = 400 } var t = Text(\"Hi\", weight: 700); [t.text, t.size, t.weight];",
            "var base = 3; struct P { x = base * 2; h = {\"k\": 1} } var p = P(); var q = P(); p.h[\"k\"] = 9; [p.x, q.h[\"k\"]];",
            "結構 樣式 { 大小: 整數 = 14; 顏色 = \"文字\" } 變數 s = 樣式(顏色: \"紅\"); [s.大小, s.顏色];",
            "struct P { x: int = 1 } var g = function(v) { v; }; var p = P(x: g(2)); p.x;",
            "struct P { x; y } var p = P(1, x: 2);",
            "struct P { x } var p = P(1, 2);",
        ]);
        Assert.Equal("[14, text, red, 1, 0]", results[0].Text);
        Assert.Equal("[Hi, 12, 700]", results[1].Text);
        Assert.Equal("[6, 1]", results[2].Text);
        Assert.Equal("[14, 紅]", results[3].Text);
        Assert.Equal(2, results[4].Integer);
        Assert.True(results[5].IsError);
        Assert.True(results[6].IsError);
    }

    [RuntimeFact]
    public void FunctionDefaultsAndNamedArguments()
    {
        var results = WasmTestHost.EvalMany([
            "var badge = function(label, tone = \"accent\", onPress = null) { label + \":\" + tone + \":\" + String.From(onPress == null); }; [badge(\"New\"), badge(\"New\", \"warn\"), badge(\"New\", onPress: 1), badge(label: \"X\", tone: \"t\")];",
            "var f = function(a, b = a * 2) { a + b; }; [f(1), f(1, 1)];",
            "var calls = 0; var next = function() { calls += 1; calls; }; var f = function(a = next()) { a; }; [f(), f(), f(10), calls];",
            "var f = function(n: int, scale: double = 2) { n * scale; }; [f(3), typeOf(f(3))];",
            "var f = function(a, b = 1) { a + b; }; f();",
            "var f = function(a, b = 1) { a + b; }; f(1, 2, 3);",
            "var f = function(a) { a; }; f(b: 1);",
            "var f = function(a) { a; }; var g = f; g(a: 5);",
            "變數 外 = 函式(甲, 乙 = 2) { 甲 * 乙; }; 外(甲: 4);",
            "var o = function(x, opts = {\"k\": 0}) { opts[\"k\"] += x; opts[\"k\"]; }; [o(1), o(1)];",
        ]);
        Assert.Equal("[New:accent:真, New:warn:真, New:accent:假, X:t:真]", results[0].Text);
        Assert.Equal("[3, 2]", results[1].Text);
        Assert.Equal("[1, 2, 10, 2]", results[2].Text);
        Assert.Equal("[6, double]", results[3].Text);
        Assert.True(results[4].IsError);
        Assert.True(results[5].IsError);
        Assert.True(results[6].IsError);
        Assert.Equal(5, results[7].Integer);
        Assert.Equal(8, results[8].Integer);
        Assert.Equal("[1, 1]", results[9].Text);
    }

    [RuntimeFact]
    public void CollectionElements()
    {
        var results = WasmTestHost.EvalMany([
            "var signedIn = false; var rest = [7, 8]; [0, if (signedIn) \"profile\" else \"login\", for (var i = 1; i < 4; i++) i * 10, ...rest, if (true) \"end\",];",
            "[for (var i = 0; i < 5; i++) if (i % 2 == 0) i];",
            "var rows = [[1, 2], [3]]; [for (var r = 0; r < len(rows); r++) ...rows[r]];",
            "var n = 2; [if (n == 1) \"one\" elif (n == 2) \"two\" else \"many\"];",
            "var x = 5; [if (x > 3) if (x > 4) \"big\" else \"mid\"];",
            "var fs = [for (var i = 0; i < 3; i++) function() { i; }]; [fs[0](), fs[2]()];",
            "var i = 9; var a = [for (var i = 0; i < 2; i++) i]; [a, i];",
            "[...[], ...[1], if (false) 2];",
            "[...5];",
            "變數 清單 = [迴圈 (變數 i = 0; i < 3; i++) i, 如果 (真) \"完\" 否則 \"未\"]; 清單;",
            "var f = function(k) { [k, for (var i = 0; i < k; i++) [i, ...[i]]]; }; [f(2), f(1)];",
        ]);
        Assert.Equal("[0, login, 10, 20, 30, 7, 8, end]", results[0].Text);
        Assert.Equal("[0, 2, 4]", results[1].Text);
        Assert.Equal("[1, 2, 3]", results[2].Text);
        Assert.Equal("[two]", results[3].Text);
        Assert.Equal("[big]", results[4].Text);
        Assert.Equal("[0, 2]", results[5].Text);
        Assert.Equal("[[0, 1], 9]", results[6].Text);
        Assert.Equal("[1]", results[7].Text);
        Assert.True(results[8].IsError);
        Assert.Equal("[0, 1, 2, 完]", results[9].Text);
        Assert.Equal("[[2, [0, 0], [1, 1]], [1, [0, 0]]]", results[10].Text);
    }

    [RuntimeFact]
    public void ShortFunctions()
    {
        var results = WasmTestHost.EvalMany([
            "var twice = x => x * 2; var sum = (x, y) => x + y; var none = () => \"none\"; [twice(4), sum(2, 3), none()];",
            "var block = (n) => { var m = n + 1; m * 3; }; var hashy = () => ({\"k\": 1}); [block(1), hashy()[\"k\"]];",
            "var f = (x, y = 10) => x + y; [f(1), f(1, 2), f(x: 5)];",
            "var f = (x: int, y: int = 2) => x * y; f(3);",
            "var count = 0; var inc = () => count += 1; inc(); inc(); count;",
            "var apply = function(f, v) { f(v); }; apply(x => x + 1, 1);",
            "Array.Map([1, 2, 3], x => x * x);",
            "var adder = a => b => a + b; adder(2)(3);",
        ]);
        Assert.Equal("[8, 5, none]", results[0].Text);
        Assert.Equal("[6, 1]", results[1].Text);
        Assert.Equal("[11, 3, 15]", results[2].Text);
        Assert.Equal(6, results[3].Integer);
        Assert.Equal(2, results[4].Integer);
        Assert.Equal(2, results[5].Integer);
        Assert.Equal("[1, 4, 9]", results[6].Text);
        Assert.Equal(5, results[7].Integer);
    }
}
