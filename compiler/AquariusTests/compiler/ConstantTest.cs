using AquariusLang.ast;
using AquariusLang.Compiler;
using AquariusLang.lexer;
using AquariusLang.parser;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>Constants (常數 / const): parsing, the type checker's write and redeclaration errors, the type of an unannotated
/// literal, and the native tier's folding, which must give the generic emitter's results (including a constant read
/// before its declaration has run, which stays the generic error).</summary>
public class ConstantTest
{
    private static AbstractSyntaxTree Parse(string source)
    {
        var parser = Parser.NewInstance(Lexer.NewInstance(source));
        var tree = parser.ParseAST();
        Assert.Empty(parser.Errors);
        return tree;
    }

    [Theory]
    [InlineData("常數 上限: 整數 = 10;", "(常數 上限: 整數 = 10)")]
    [InlineData("const limit = 2.5d;", "(const limit = 2.5d)")]
    public void ConstantsParseAsDeclarations(string source, string printed)
    {
        var tree = Parse(source);
        Assert.Contains(printed, tree.String());
        Assert.True(Assert.IsType<LetStatement>(tree.Statements[0]).Constant);
    }

    [Fact]
    public void ConstIsAKeywordOnlyWhereADeclarationStarts()
    {
        Assert.Empty(TypeChecker.Check(Parse("var const = 1; var 常數 = const + 1; 常數;")).Errors);
    }

    [Theory]
    [InlineData("const n = 1; n = 2;")]
    [InlineData("常數 n = 1; n += 2;")]
    [InlineData("const n = 1; n++;")]
    [InlineData("const n = 1; var f = function() { n = 3; };")]
    [InlineData("const n = 1; var n = 2;")]
    [InlineData("var n = 1; const n = 2;")]
    [InlineData("const n: int = 2.5d;")]
    public void WritesAndRedeclarationsAreCompileErrors(string source)
    {
        Assert.NotEmpty(TypeChecker.Check(Parse(source)).Errors);
    }

    [Fact]
    public void ShadowingInAnInnerScopeAndElementWritesAreAllowed()
    {
        Assert.Empty(TypeChecker.Check(Parse("const n = 1; var f = function() { var n = 2; n = 3; }; const a = [1, 2]; a[0] = 5;")).Errors);
    }

    [Fact]
    public void OnlyLiteralConstantsDeclaredAheadOfTheirUsersAreFolded()
    {
        var checker = TypeChecker.Check(Parse(
            "const a = 3; const b: double = 2; const c = -1.5f; var x = 4; const d = x; " +
            "var f = function(i: int): double { return i * a + b + c + d + e; }; const e = 1; " +
            "if (true) { const g = 5; }"));
        Assert.Empty(checker.Errors);
        var outer = checker.Natives.Values.Single().Outer;
        Assert.Equal(3, outer["a"].Constant);
        Assert.Equal(2, outer["b"].Constant);
        Assert.Equal(-1.5, outer["c"].Constant);
        Assert.Null(outer["d"].Constant); // not a literal
        Assert.Null(outer["e"].Constant); // declared after the function
        Assert.Equal(TypeChecker.Int, outer["a"].Type);
    }

    private static readonly string[] Programs = [
        "const n = 12; var f = function(k: int): int { var t: int = 0; for (var i: int = 0; i < n; i++) { t += i * k + n; } return t; }; f(3);",
        "常數 尺寸: 整數 = 5; 常數 比例: 雙精度 = 0.25d; 變數 g = 函式(x: 整數): 雙精度 { 回傳 x * 比例 + 尺寸; }; g(9);",
        "const half: float = 0.5f; const flag = true; var h = function(x: float): float { if (flag) { return x * half; } return x; }; h(3.0f);",
        "const widened: double = 7; var w = function(): double { return widened / 2; }; w();",
        "var early = function(): int { return late * 2; }; var r = early(); const late = 4; r;",
        "const big = 2147483647; var o = function(): int { return big + 1; }; o();",
        "const n = 30; var loop = function(): int { var a: int[] = Array.Make(n, 1); var s: int = 0; for (var i: int = 0; i < n; i++) { s += a[i] * n; } return s; }; loop();",
    ];

    [RuntimeFact]
    public void FoldedConstantsMatchTheGenericEmitterUnderAnyQuantum()
    {
        var results = EmitterTiers.AssertSame(Programs);
        Assert.Equal(342, results[0].Integer);
        Assert.True(results[4].IsError);
        Assert.Equal(-2147483648, results[5].Integer);
        string program = "const n = 40; const step: double = 0.5d; var f = function(k: int): double { var t: double = 0.0d; " +
            "for (var i: int = 0; i < n; i++) { t += i * step + k; } return t; }; print(f(1), f(2));";
        string expected = WasmTestHost.Run(new AquariusLang.Wasm.WasmCompiler(false).Compile(program)).Succeeded().Output;
        foreach (string quantum in new[] { "1", "3", "17" })
            Assert.Equal(expected, WasmTestHost.Run(program, new Dictionary<string, string> { ["AQUARIUS_FUEL_QUANTUM"] = quantum }).Succeeded().Output);
    }
}
