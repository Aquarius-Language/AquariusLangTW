using AquariusLang.Compiler;
using AquariusLang.Desktop;
using AquariusLang.ast;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using AquariusLang.runtime;
using AquariusLang.token;
using AquariusLang.Wasm;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusTests;

public class BilingualSyntaxTest {
    public static IEnumerable<object[]> Keywords() {
        yield return new object[] { "變數", "var", TokenType.LET };
        yield return new object[] { "函式", "function", TokenType.FUNCTION };
        yield return new object[] { "回傳", "return", TokenType.RETURN };
        yield return new object[] { "如果", "if", TokenType.IF };
        yield return new object[] { "否則如果", "elif", TokenType.ELSE_IF };
        yield return new object[] { "否則", "else", TokenType.ELSE };
        yield return new object[] { "迴圈", "for", TokenType.FOR };
        yield return new object[] { "中斷", "break", TokenType.BREAK };
        yield return new object[] { "真", "true", TokenType.TRUE };
        yield return new object[] { "假", "false", TokenType.FALSE };
    }

    [Theory, MemberData(nameof(Keywords))]
    public void BothSpellingsHaveTheSameTokenTypeAndKeepSourceRanges(string chinese, string english, string type) {
        string source = $"\uFEFF{chinese} {english}";
        var lexer = Lexer.NewInstance(source);
        foreach (string spelling in new[] { chinese, english }) {
            var token = lexer.NextToken();
            Assert.Equal(type, token.Type);
            Assert.Equal(spelling, token.Literal);
            Assert.Equal(source.IndexOf(spelling, StringComparison.Ordinal), token.Start);
            Assert.Equal(spelling.Length, token.Length);
            Assert.Equal(spelling, source.Substring(token.Start, token.Length));
        }
        Assert.Equal(TokenType.EOF, lexer.NextToken().Type);
        Assert.Empty(lexer.Errors);
    }

    [Theory, MemberData(nameof(Keywords))]
    public void KeywordsAreCaseSensitiveAndOnlyMatchWholeIdentifiers(string chinese, string english, string type) {
        foreach (string name in new[] { chinese + "值", "_" + chinese, english + "Value", english + "1", "_" + english,
                     english + "中文", "中文" + english, english + "\u0301", english + "𠀀", english.ToUpperInvariant() }) {
            var lexer = Lexer.NewInstance(name);
            var token = lexer.NextToken();
            Assert.Equal(TokenType.IDENT, token.Type);
            Assert.NotEqual(type, token.Type);
            Assert.Equal(name, token.Literal);
            Assert.Equal(TokenType.EOF, lexer.NextToken().Type);
        }
    }

    [Fact]
    public void CommentsAndStringsKeepKeywordTextUntouched() {
        const string text = "var 變數 function 函式 return 回傳 if 如果 else 否則 true 真";
        var lexer = Lexer.NewInstance($"# {text}\n## {text}\n##\n\"{text}\"");
        var token = lexer.NextToken();
        Assert.Equal(TokenType.STRING, token.Type);
        Assert.Equal(text, token.Literal);
        Assert.Equal(TokenType.EOF, lexer.NextToken().Type);
        Assert.Empty(lexer.Errors);
        AssertResult($"var text = \"{text}\"; text;", text);
    }

    public static IEnumerable<object[]> FunctionSpellings() {
        foreach (string variable in new[] { "變數", "var" })
        foreach (string function in new[] { "函式", "function" })
        foreach (string result in new[] { "回傳", "return" })
            yield return new object[] { variable, function, result };
    }

    [Theory, MemberData(nameof(FunctionSpellings))]
    public void DeclarationsFunctionsAndReturnsMixIndependently(string variable, string function, string result) {
        AssertResult($$"""
            {{variable}} 建立, make = {{function}}(起點) {
                {{variable}} 值 = 起點;
                {{result}} {{function}}(step) { 值 += step; {{result}} 值; };
            };
            {{variable}} count = make(10);
            {{variable}} 另一個 = 建立(20);
            [count(1), count(2), 另一個(3), 建立 == make];
            """, "[11, 13, 23, 真]");
    }

    [Theory]
    [InlineData("如果", "否則如果", "否則")]
    [InlineData("if", "elif", "else")]
    [InlineData("if", "else if", "else")]
    [InlineData("如果", "else 如果", "否則")]
    [InlineData("if", "否則 if", "else")]
    [InlineData("如果", "否則 如果", "else")]
    [InlineData("if", "else\n# branch comment\n如果", "否則")]
    [InlineData("如果", "否則 ## branch comment ## if", "else")]
    public void ConditionalBranchesSelectExactlyOneResult(string first, string next, string last) {
        AssertResult($$"""
            var effects = 0;
            變數 classify = function(n) {
                {{first}} (n < 0) { effects++; return -1; }
                {{next}} (n == 0) { effects++; 回傳 0; }
                {{next}} (n == 1) { effects++; return 1; }
                {{last}} { effects++; 回傳 2; }
                effects += 100;
            };
            [classify(-5), classify(0), classify(1), classify(9), effects];
            """, "[-1, 0, 1, 2, 4]");
    }

    [Theory]
    [InlineData("if (false) { 1; } else if (false) { 2; };", "null")]
    [InlineData("如果 (假) { 1; } 否則如果 (假) { 2; };", "null")]
    [InlineData("if (true) { if (false) { 1; } else if (true) { 2; } else { 3; } } else { 4; };", "2")]
    [InlineData("如果 (真) { if (假) { 1; } 否則 如果 (真) { 2; } else { 3; } } 否則 { 4; };", "2")]
    [InlineData("[true == 真, false == 假, !false, true && 真, false || 真];", "[真, 真, 真, 真, 真]")]
    [InlineData("var factorial = function(n) { if (n < 2) { return 1; } return n * factorial(n - 1); }; factorial(6);", "720")]
    [InlineData("變數 階乘, factorial = function(n) { if (n < 2) { 回傳 1; } return n * 階乘(n - 1); }; factorial(6);", "720")]
    [InlineData("var a = [1, 2]; 變數 h = {\"var\": a}; h[\"var\"][0] = 5; a[1] = a[1] + 3; [a, h[\"var\"], len(a), 長度(a)];", "[[5, 5], [5, 5], 2, 2]")]
    [InlineData("var n = 5; 變數 f = 1.5f; var d = 2.5d; [n++, ++n, f++, ++f, d++, ++d, 10 % 3];", "[5, 7, 1.5, 3.5, 2.5, 4.5, 1]")]
    [InlineData("var returnValue = 4; 變數 if成立 = 5; var TRUE = 6; returnValue + if成立 + TRUE;", "15")]
    public void ExpressionsAndDataKeepTheSameSemantics(string source, string expected) => AssertResult(source, expected);

    [Theory]
    [InlineData("變數", "迴圈", "如果", "中斷", "函式", "回傳", "真", "假")]
    [InlineData("var", "for", "if", "break", "function", "return", "true", "false")]
    [InlineData("var", "迴圈", "if", "中斷", "函式", "return", "真", "false")]
    [InlineData("變數", "for", "如果", "break", "function", "回傳", "true", "假")]
    public void LoopsBreakReturnsAndIterationClosuresWorkInBothLanguages(
        string variable, string loop, string condition, string stop, string function, string result, string yes, string no) {
        AssertResult($$"""
            {{variable}} count = 0;
            {{variable}} saved = [];
            {{loop}} ({{variable}} i = 0; i < 5; i++) {
                {{variable}} local = i;
                {{condition}} (i == 3 && {{yes}} && !{{no}}) { {{stop}}; count += 100; }
                saved = push(saved, {{function}}() { {{result}} local; });
                count++;
            }
            {{variable}} early = {{function}}() {
                {{loop}} ({{variable}} i = 0; i < 2; ++i) { {{result}} 7; }
                {{result}} 99;
            };
            [count, saved[0](), saved[1](), saved[2](), early()];
            """, "[3, 0, 1, 2, 7]");
    }

    [Fact]
    public void ParserKeepsTheOriginalDeclarationAndFunctionTokens() {
        var parser = Parser.NewInstance(Lexer.NewInstance("var 加法, add = function(甲, b) { 回傳 甲 + b; };"));
        var tree = parser.ParseAST();
        Assert.Empty(parser.Errors);
        var declaration = Assert.IsType<LetStatement>(Assert.Single(tree.Statements));
        Assert.Equal("var", declaration.Token.Literal);
        Assert.Equal("加法", declaration.Name.Value);
        Assert.Equal("add", declaration.Alias!.Value);
        var function = Assert.IsType<FunctionLiteral>(declaration.Value);
        Assert.Equal("function", function.Token.Literal);
        Assert.Equal(new[] { "甲", "b" }, function.Parameters.Select(p => p.Value));
    }

    [Theory]
    [InlineData("var function = 1;")]
    [InlineData("變數 return = 1;")]
    [InlineData("var f = function(if) { 1; };")]
    [InlineData("var f, f = function() {};")]
    [InlineData("var 中文, english = 42;")]
    [InlineData("if (true) { 1; } else (false) { 2; }")]
    [InlineData("if (true) { 1; } else if { 2; }")]
    [InlineData("if (true) { 1; } else { 2; } elif (true) { 3; }")]
    [InlineData("for (var i = 0; i < 2; i++) { var n = ; }")]
    public void InvalidEnglishAndMixedSyntaxProducesCompilationErrors(string source) =>
        Assert.Throws<CompilationException>(() => new LoweringCompiler().Compile(source));

    [Theory, MemberData(nameof(Keywords))]
    public void HostFunctionNamesRejectReservedEnglishAndChineseWords(string chinese, string english, string type) {
        foreach (string name in new[] { chinese, english }) {
            Assert.Equal(type, TokenLookup.LookupIdentifier(name));
            var env = AquaEnvironment.NewEnvironment();
            Assert.Throws<ArgumentException>(() => FunctionRegistration.Define(env, new BuiltinObj(_ => new IntegerObj(1)), "有效名稱", name));
            Assert.Empty(env.OwnedBindings);
        }
    }

    [Theory]
    [InlineData("var n = 0; var fail = function() { unknown; }; fail(); n = 1;")]
    [InlineData("變數 n = 0; var fail = 函式() { unknown; }; fail(); n = 1;")]
    public void RuntimeErrorsStopLaterSideEffects(string source) {
        var env = AquaEnvironment.NewEnvironment();
        using var runtime = new WasmRuntime(new Builtins());
        Assert.IsType<ErrorObj>(runtime.Execute(new WasmCompiler().Compile(source), env));
        Assert.Equal(0, Assert.IsType<IntegerObj>(env.GetOwned("n")).Value);
    }

    [Fact]
    public void InteractiveSessionsCanChangeSyntaxBetweenEvaluations() {
        var evaluator = CompiledEvaluator.NewInstance(new Builtins());
        var env = AquaEnvironment.NewEnvironment();
        evaluator.Eval(Parse("var n = 40; var next = function() { 回傳 ++n; };"), env);
        Assert.Equal("41", evaluator.Eval(Parse("變數 答案 = next(); 答案;"), env).Inspect());
        Assert.Equal("[41, 42]", evaluator.Eval(Parse("[答案, next()];"), env).Inspect());
    }

    private static AbstractSyntaxTree Parse(string source) {
        var parser = Parser.NewInstance(Lexer.NewInstance(source));
        var tree = parser.ParseAST();
        Assert.Empty(parser.Errors);
        return tree;
    }

    private static void AssertResult(string source, string expected) {
        var code = new LoweringCompiler().Compile(Parse(source));
        using var builtins = new DesktopBuiltins();
        using var compiled = new CompiledRuntime(builtins);
        Assert.Equal(expected, compiled.Execute(code).Inspect());
        var program = new WasmCompiler().Compile(code);
        using var reloaded = new WasmRuntime(builtins);
        Assert.Equal(expected, reloaded.Execute(WasmProgram.Load(program.Bytes.ToArray())).Inspect());
    }
}
