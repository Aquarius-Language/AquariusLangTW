using AquariusLang.ast;
using AquariusLang.Compiler;
using AquariusLang.lexer;
using AquariusLang.parser;
using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>Structs (結構 / struct): declarations, named and positional construction, fields, annotations, the type checker,
/// the runtime (printing, typeOf, identity, errors, the collector), modules, the native tier and both syntaxes.</summary>
public class StructTest
{
    private static AbstractSyntaxTree Parse(string source)
    {
        var parser = Parser.NewInstance(Lexer.NewInstance(source));
        var tree = parser.ParseAST();
        Assert.Empty(parser.Errors);
        return tree;
    }

    [Theory]
    [InlineData("結構 節點 { 左: 節點; 右: 節點; 值: 整數 }", "結構 節點 { 左: 節點; 右: 節點; 值: 整數 }")]
    [InlineData("struct Point { x: double, y: double, label }", "struct Point { x: double; y: double; label }")]
    [InlineData("struct Empty { }", "struct Empty {  }")]
    [InlineData("var p = Point(x: 1, y: 2);", "Point(x: 1, y: 2)")]
    [InlineData("var f = function(t: 節點): 節點 { t; };", "function(t: 節點): 節點")]
    public void DeclarationsAnnotationsAndNamedArgumentsParse(string source, string printed)
    {
        Assert.Contains(printed, Parse(source).String());
    }

    [Fact]
    public void StructIsAKeywordOnlyWhereADeclarationStarts()
    {
        // Programs using 結構 or struct as ordinary names keep working.
        Assert.Empty(TypeChecker.Check(Parse("var struct = 1; var 結構 = struct + 1; 結構;")).Errors);
    }

    [Theory]
    [InlineData("struct P { x: int; x: int }")]
    [InlineData("struct P { x: int }; var a: P[] = [];")]
    [InlineData("var f = function(a) { a; }; f(a: 1, 2);")]
    public void MalformedDeclarationsAreParseErrors(string source)
    {
        var parser = Parser.NewInstance(Lexer.NewInstance(source));
        parser.ParseAST();
        Assert.NotEmpty(parser.Errors);
    }

    [Theory]
    [InlineData("struct P { x: int }; var p = P(y: 1);")]
    [InlineData("struct P { x: int }; var p = P(x: \"text\");")]
    [InlineData("struct P { x: int; y }; var p = P(y: 1);")]
    [InlineData("struct P { x: int }; var p: P = P(x: 1); p.z;")]
    [InlineData("struct P { x: int }; var p: P = P(x: 1); p.x = 2.5d;")]
    [InlineData("struct P { x: int }; var p = P(1, 2);")]
    [InlineData("var f = function(t: Missing): int { 1; };")]
    [InlineData("struct P { x: int }; struct Q { x: int }; var f = function(t: P): int { 1; }; f(Q(x: 1));")]
    [InlineData("var f = function(a) { a; }; f(a: 1);")]
    [InlineData("struct P { s: string }; var p: P = P(s: \"a\"); p.s -= 1;")]
    public void DefiniteMistakesAreCompileErrors(string source)
    {
        Assert.NotEmpty(TypeChecker.Check(Parse(source)).Errors);
    }

    [RuntimeFact]
    public void StructsConstructPrintAndCompareLikeValues()
    {
        var results = WasmTestHost.EvalMany([
            "結構 節點 { 左: 節點; 右: 節點; 值: 整數 } 變數 樹 = 節點(左: 節點(值: 1), 值: 2); 樹;",
            "struct Point { x: double; y: double; label } var p = Point(1, 2); [p, typeOf(p), typeOf(p.x), p.label];",
            "struct P { x: int } var a = P(x: 1); var b = P(x: 1); var c = a; [a == b, a == c, a != b];",
            "struct P { x: int } var p = P(x: 1); p.x = 5; p.x += 2; p.x *= 3; p.x;",
            "struct P { x: int } var p = P(x: 7); p.x /= 2; p.x += 0.75d; p.x;",
            "struct P { x: float; s: string } var p = P(x: 1, s: \"a\"); p.s += \"b\"; [p.x / 3, p.s];",
            "struct P { v } var p = P(); p.v = [1, \"two\", P(v: 3)]; p;",
            "struct P { x: int } P;",
            "struct P { x: int } [typeOf(P), P == P];",
            "struct Tree { kids: array; size: int } var t = Tree(kids: [Tree(kids: [], size: 1), Tree(kids: [], size: 2)], size: 3); t.kids[1].size + t.size;",
            "struct Pair { a; b } var h = {\"k\": Pair(a: 1, b: \"x\")}; h[\"k\"].b;",
            "結構 甲 { 乙: 乙型 } 結構 乙型 { 甲: 甲 } 變數 a = 甲(乙: 乙型(甲: 甲())); 型別(a.乙.甲);",
        ]);
        Assert.Equal(new TestValue("節點", "節點{左: 節點{左: null, 右: null, 值: 1}, 右: null, 值: 2}"), results[0]);
        Assert.Equal("[Point{x: 1, y: 2, label: null}, Point, double, null]", results[1].Text);
        Assert.Equal("[假, 真, 真]", results[2].Text);
        Assert.Equal(21, results[3].Integer);
        Assert.Equal(3, results[4].Integer);
        Assert.Equal("[0.33333334, ab]", results[5].Text);
        Assert.Equal("P{v: [1, two, P{v: 3}]}", results[6].Text);
        Assert.Equal("結構 P", results[7].Text);
        Assert.Equal("[type, 真]", results[8].Text);
        Assert.Equal(5, results[9].Integer);
        Assert.Equal("x", results[10].Text);
        Assert.Equal("甲", results[11].Text);
    }

    [RuntimeFact]
    public void FieldTypesAnnotationsAndNullAreCheckedAtRunTime()
    {
        var results = WasmTestHost.EvalMany([
            "struct P { x: int } var g = function(v) { v; }; var p = P(x: g(\"s\"));",
            "struct P { x: int } var g = function(v) { v; }; var p = P(x: 1); p.x = g(2.5d);",
            "struct P { x: int } var make = function(t) { t(); }; make(P);",
            "struct P { x: int } struct Q { x: int } var f = function(t: P): int { 1; }; var g = function(v) { v; }; f(g(Q(x: 1)));",
            "struct P { next: P } var p = P(); p.next.next;",
            "struct P { next: P } var p = P(); p.next.next = 1;",
            "struct P { x } var p = P(); p.y;",
            "struct P { x } var p = P(); p.y = 1;",
            "struct P { x } var p = P(); var h = {}; h[p] = 1;",
            "var g = function(v) { v; }; var x = g(5); x.field;",
            "var f = function(a) { a; }; var g = function(v) { v; }; g(f)(a: 1);",
            "struct P { x: int } var f = function(t: P): int { if (typeOf(t) == \"null\") { return -1; } t.x; }; var p = P(x: 4); [f(p), f(p.x == 4 && false ? p : P(x: 9))];",
        ]);
        Assert.Contains("P.x expects int, got STRING", results[0].Message);
        Assert.Contains("P.x expects int, got DOUBLE", results[1].Message);
        Assert.Contains("P.x expects int, got NULL", results[2].Message);
        Assert.Contains("t expects P, got Q", results[3].Message);
        Assert.Contains("Cannot access member of NULL", results[4].Message);
        Assert.Contains("Cannot assign a member of NULL", results[5].Message);
        Assert.Contains("Unknown field of P: y", results[6].Message);
        Assert.Contains("Unknown field of P: y", results[7].Message);
        Assert.Contains("Unusable as hash key", results[8].Message);
        Assert.Contains("Cannot access member of INTEGER", results[9].Message);
        Assert.Contains("Named arguments need a struct type", results[10].Message);
    }

    [RuntimeFact]
    public void StructTypesCrossModulesAndClosures()
    {
        var modules = new Dictionary<string, string> {
            ["shapes.aqua"] = "struct Point { x: double; y: double } var origin = Point(x: 0, y: 0); var length2 = function(p: Point): double { p.x * p.x + p.y * p.y; };",
            ["main.aqua"] = "var shapes = import(\"shapes.aqua\"); var p = shapes.Point(x: 3, y: 4); var make = shapes.Point; " +
                "var counter = function() { var hits = make(x: 1, y: 1); function() { hits.x += 1; hits.x; }; }; var c = counter(); c(); " +
                "print(p, shapes.length2(p), shapes.origin, c(), typeOf(make(5, 6)));",
        };
        var run = WasmTestHost.RunModules(modules, "main.aqua");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("Point{x: 3, y: 4} 25 Point{x: 0, y: 0} 3 Point", run.Lines[0]);
    }

    [RuntimeFact]
    public void ManyStructsSurviveCollectionsUnderAnyQuantum()
    {
        // A long chain and many short-lived trees: every collection must keep reachable structs and their types.
        string program = "struct Link { next: Link; value: int } struct Leaf { a; b } var head = Link(value: 0); " +
            "for (var i = 1; i < 120000; i++) { head = Link(next: head, value: i); var garbage = Leaf(a: [i], b: Leaf(a: i)); } " +
            "var total = 0; var n = head; for (var k = 0; typeOf(n) != \"null\"; k++) { total += n.value % 7; n = n.next; } print(total);";
        string expected = WasmTestHost.Run(program).Succeeded().Output;
        Assert.Equal("359997", expected.Trim());
        foreach (string quantum in new[] { "17", "4096" })
            Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum }).Succeeded().Output);
    }

    private static readonly string[] Typed = [
        // Typed functions over structs run natively: construction, typed and untyped fields, null fields, identity.
        "struct Node { left: Node; right: Node } var build = function(d: int): Node { if (d == 0) { return Node(); } return Node(left: build(d - 1), right: build(d - 1)); }; " +
        "var count = function(t: Node, d: int): int { if (d == 0) { return 1; } return 1 + count(t.left, d - 1) + count(t.right, d - 1); }; count(build(10), 10);",
        "struct V { x: double; y: double } var add = function(a: V, b: V): V { return V(x: a.x + b.x, y: a.y + b.y); }; " +
        "var sum = function(n: int): double { var acc: V = V(x: 0, y: 0); for (var i: int = 0; i < n; i++) { acc = add(acc, V(x: i, y: 1)); } return acc.x + acc.y; }; sum(1000);",
        "struct C { n: int; tag } var bump = function(c: C, k: int): int { c.n += k; c.n *= 2; c.n -= 1; c.tag = k; return c.n; }; var c = C(n: 1); [bump(c, 3), c, bump(c, 2)];",
        "struct P { x: int } var same = function(a: P, b: P): bool { return a == b; }; var p = P(x: 1); [same(p, p), same(p, P(x: 1))];",
        "struct L { next: L; v: int } var walk = function(l: L): int { var t: int = 0; var cur: L = l; for (var g: int = 0; t < 1000000 && cur.v >= 0; g++) { t += cur.v; if (cur.v == 0) { return t; } cur = cur.next; } return t; }; " +
        "var make = function(n: int): L { var l: L = L(v: 0); for (var i: int = 1; i <= n; i++) { l = L(next: l, v: i); } return l; }; walk(make(500));",
        "struct B { flag: bool; d: double; f: float } var mk = function(i: int): B { return B(flag: i > 2, d: i, f: i / 3); }; var show = function(n: int): double { var b: B = mk(n); if (b.flag) { return b.d + b.f; } return b.f; }; [show(1), show(9)];",
        "struct Box { items: int[]; any: array } var total = function(b: Box): int { var t: int = 0; for (var i: int = 0; i < len(b.items); i++) { t += b.items[i]; } return t + len(b.any); }; total(Box(items: [1, 2, 3], any: [1, \"x\"]));",
        "struct P { x: int } var read = function(p: P): int { return p.x; }; var g = function(v) { v; }; read(g(P()));",
        "struct P { next: P } var deep = function(p: P): P { return p.next.next; }; deep(P());",
    ];

    [RuntimeFact]
    public void TypedStructCodeRunsNativelyAndMatchesTheGenericEmitter()
    {
        var results = EmitterTiers.AssertSame(Typed);
        Assert.Equal(2047, results[0].Integer);
        Assert.Equal(500500, results[1].Number);
        Assert.Equal("[7, C{n: 17, tag: 2}, 17]", results[2].Text); // the array holds the struct itself
        Assert.Equal("[真, 假]", results[3].Text);
        Assert.Equal(125250, results[4].Integer);
        Assert.Contains("P.x (整數 int) needs a value", results[7].Message);
        Assert.Contains("Cannot access member of NULL", results[8].Message);
        var reasons = new List<string>();
        var previous = WasmCompiler.TierDiagnostics;
        WasmCompiler.TierDiagnostics = reasons.Add;
        try { foreach (string program in Typed.Take(7)) new WasmCompiler().Compile(program); }
        finally { WasmCompiler.TierDiagnostics = previous; }
        Assert.DoesNotContain(reasons, r => r.Contains("native: rejected"));
    }

    [RuntimeFact]
    public void TypedStructCodeGivesTheSameResultsUnderAnyQuantum()
    {
        foreach (string source in Typed.Take(5)) {
            string program = $"print({source.TrimEnd(';')});".Replace("; print(", "; print(");
            string body = source[..source.LastIndexOf(';', source.Length - 2)] + ";";
            string last = source[(source.LastIndexOf(';', source.Length - 2) + 1)..].Trim().TrimEnd(';');
            string printed = body + " print(" + last + ");";
            string expected = WasmTestHost.Run(printed).Succeeded().Output;
            foreach (string quantum in new[] { "1", "5", "33", "65536" })
                Assert.Equal(expected, WasmTestHost.Run(printed, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum }).Succeeded().Output);
        }
    }
}
