using AquariusLang.ast;
using AquariusLang.Compiler;
using AquariusLang.lexer;
using AquariusLang.parser;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>Optional `name: type` annotations in both syntaxes, their compile-time and runtime checks, the numeric
/// conversions and the integer rules shared by typed and untyped code.</summary>
public class TypeAnnotationTest
{
    private static AbstractSyntaxTree Parse(string source)
    {
        var parser = Parser.NewInstance(Lexer.NewInstance(source));
        var tree = parser.ParseAST();
        Assert.Empty(parser.Errors);
        return tree;
    }

    [Theory]
    [InlineData("變數 x: 整數 = 1;", "x: 整數")]
    [InlineData("var x: int = 1;", "x: int")]
    [InlineData("var a: double[] = [1.0d];", "a: double[]")]
    [InlineData("var f = function(a: int, b: 雙精度): 布林 { 真; };", "function(a: int, b: 雙精度): 布林")]
    [InlineData("迴圈 (變數 i: 整數 = 0; i < 3; i++) { }", "i: 整數")]
    [InlineData("變數 樹: 陣列 = [0, 0];", "樹: 陣列")]
    [InlineData("var f = function(tree: array, d: int): array { tree; };", "function(tree: array, d: int): array")]
    public void AnnotationsParseInBothSyntaxes(string source, string printed)
    {
        Assert.Contains(printed, Parse(source).String());
    }

    [Fact]
    public void TypeNamesAreNotReservedWords()
    {
        Assert.Empty(TypeChecker.Check(Parse("var int = 3; var 整數 = 4; var double = int + 整數;")).Errors);
        // 陣列 / array is also the Array module's name; only after a colon is it a type.
        Assert.Empty(TypeChecker.Check(Parse("var a: 陣列 = 陣列.建立(2, 0); var array = 1;")).Errors);
    }

    [Fact]
    public void AnArrayTypeTakesNoBrackets()
    {
        var parser = Parser.NewInstance(Lexer.NewInstance("var a: array[] = [];"));
        parser.ParseAST();
        Assert.NotEmpty(parser.Errors);
    }

    [Theory]
    [InlineData("var x: int = 2.5d;")]
    [InlineData("var x: int = \"text\";")]
    [InlineData("var x: 字串 = 1;")]
    [InlineData("var x: int = 1; x = 真;")]
    [InlineData("var x: int = 1; var f = function() { x = 1.5d; };")]
    [InlineData("var x: int = 1; var x: double = 1.0d;")]
    [InlineData("var b: bool = 真; b += 1;")]
    [InlineData("var f = function(n: int): int { var y = n; };")]
    [InlineData("var f = function(n: int): int { return 1.5d; };")]
    [InlineData("var f = function(n: int): int { n; }; f(2.5d);")]
    [InlineData("var a: int[] = 1;")]
    [InlineData("var x: 整數 = 整數(1) + 0.5d;")]
    [InlineData("var a: array = 1;")]
    [InlineData("var a: 陣列 = 1.5d;")]
    [InlineData("var f = function(t: array): int { return 0; }; f(真);")]
    public void DefiniteMismatchesAreCompileErrors(string source)
    {
        var errors = TypeChecker.Check(Parse(source)).Errors;
        Assert.NotEmpty(errors);
        Assert.Throws<CompilationException>(() => new LoweringCompiler().Compile(source));
    }

    [Theory]
    [InlineData("var x: double = 1;")]
    [InlineData("var x: float = 1; var y: double = x;")]
    [InlineData("var x: int = 1; x += 0.5d;")]
    [InlineData("var s: string = \"a\"; s += \"b\";")]
    [InlineData("var f = function(a: double): double { a; }; f(1);")]
    [InlineData("var g = function(v) { v; }; var x: int = g(1);")]
    [InlineData("var a: int[] = [1, 2]; var b: array = a;")]
    [InlineData("var a: array = [1, 2]; var b: int[] = a;")]
    [InlineData("var f = function(t: array): int { return len(t); }; var a: double[] = [1.0d]; f(a);")]
    public void WideningAndUntypedValuesCompile(string source)
    {
        Assert.Empty(TypeChecker.Check(Parse(source)).Errors);
    }

    [RuntimeFact]
    public void AnnotationsCheckAndWidenAtRunTime()
    {
        var results = WasmTestHost.EvalMany([
            "var d: double = 3; typeOf(d);",
            "var f: float = 2; typeOf(f);",
            "var f = function(a: double): double { a; }; typeOf(f(1));",
            "var g = function(v) { v; }; var x: int = g(5); x;",
            "var a: int[] = Array.Make(3, 7); len(a);",
            "var g = function(v) { v; }; var x: int = g(\"text\"); x;",
            "var f = function(n: int): int { n; }; var g = function(v) { v; }; f(g(1.5d));",
            "var f = function(n: int): int { if (n > 0) { return n; } }; f(-1);",
            "var g = function(v) { v; }; var a: int[] = g([1, 2.5d]); a;",
            "var g = function(v) { v; }; var a: array = g([1, \"x\", [2]]); a;",
            "var g = function(v) { v; }; var a: 陣列 = g(5); a;",
            "var a: int[] = [1, 2]; var b: array = a; b[1] = 2.5d;",
            "var a: array = [1, 2]; var b: int[] = a; b;",
        ]);
        Assert.Equal(new TestValue("string", "double"), results[0]);
        Assert.Equal(new TestValue("string", "float"), results[1]);
        Assert.Equal(new TestValue("string", "double"), results[2]);
        Assert.Equal(5, results[3].Integer);
        Assert.Equal(3, results[4].Integer);
        Assert.Contains("x expects int, got STRING", results[5].Message);
        Assert.Contains("n expects int, got DOUBLE", results[6].Message);
        Assert.Contains("expects int, got NULL", results[7].Message);
        Assert.Contains("a expects int[], got ARRAY", results[8].Message);
        Assert.Equal(new TestValue("array", "[1, x, [2]]"), results[9]);
        Assert.Contains("a expects array, got INTEGER", results[10].Message);
        Assert.Contains("the array holds int, got DOUBLE", results[11].Message);
        Assert.Equal(new TestValue("array", "[1, 2]"), results[12]);
    }

    [RuntimeFact]
    public void ConversionsTruncateAndSaturate()
    {
        var results = WasmTestHost.EvalMany([
            "[int(3.9d), 整數(-3.9d), int(100000000000.0d), int(0.0d - 100000000000.0d), int(0.0d / 0.0d), int(7)];",
            "[typeOf(double(7)), typeOf(雙精度(1.5f)), typeOf(float(2)), typeOf(浮點數(2.5d))];",
            "int(\"7\");",
        ]);
        Assert.Equal(new TestValue("array", "[3, -3, 2147483647, -2147483648, 0, 7]"), results[0]);
        Assert.Equal(new TestValue("array", "[double, double, float, float]"), results[1]);
        Assert.True(results[2].IsError);
    }

    [RuntimeFact]
    public void IntegersWrapAndDivisionByZeroIsAnError()
    {
        var results = WasmTestHost.EvalMany([
            "var n = 2147483647; [n + 1, n * 2, 0 - n - 2, -(n + 1)];",
            "var n: int = 2147483647; n++; var m: int = 0 - 2147483647 - 1; m -= 1; [n, m];",
            "[7 / 2, -7 / 2, 7 % -3, -7 % 3, (0 - 2147483647 - 1) / -1, (0 - 2147483647 - 1) % -1];",
            "var x = 1; x / 0;",
            "var x: int = 1; x /= 0;",
            "var x = 1; x % 0;",
            "var i: int = 10; i += 0.75d; i *= 1.5d; i;",
            "var big: int = 1; big += 100000000000.0d; big;",
        ]);
        Assert.Equal(new TestValue("array", "[-2147483648, -2, 2147483647, -2147483648]"), results[0]);
        Assert.Equal(new TestValue("array", "[-2147483648, 2147483647]"), results[1]);
        Assert.Equal(new TestValue("array", "[3, -3, 1, -1, -2147483648, 0]"), results[2]);
        Assert.Contains("Integer division by zero", results[3].Message);
        Assert.Contains("Integer division by zero", results[4].Message);
        Assert.Contains("Integer remainder by zero", results[5].Message);
        Assert.Equal(15, results[6].Integer);
        Assert.Equal(2147483647, results[7].Integer);
    }

    [RuntimeFact]
    public void TypedProgramsMatchTheGenericEmitter()
    {
        EmitterTiers.AssertSame([
            "var fib = function(n: int): int { if (n < 2) { return n; } fib(n - 1) + fib(n - 2); }; fib(15);",
            "變數 總和: 整數 = 0; 迴圈 (變數 i: 整數 = 0; i < 100; i++) { 總和 += i * i; } 總和;",
            "var x: int = 2147483000; for (var i: int = 0; i < 10; i++) { x += 1000; x *= 3; } x;",
            "var d: double = 0; for (var i: int = 1; i <= 10; i++) { d += 1.0d / i; } d;",
            "var c: int = 1000; for (var i: int = 0; i < 3; i++) { c /= 3; c *= 2; c -= 1; c %= 97; } c;",
            "var f = function(a: int, b: double): double { a * b; }; f(3, 2.5d) + f(1, 1);",
            "var e = function(i: int, j: int): double { var s = i + j; 1.0d / (s * (s + 1) / 2 + i + 1); }; e(3, 4);",
        ]);
    }
}
