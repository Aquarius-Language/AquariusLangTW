using AquariusLang.Cli;
using AquariusLang.Compiler;
using AquariusLang.ast;
using AquariusLang.lexer;
using AquariusLang.parser;
using AquariusLang.token;

namespace AquariusTests;

/// <summary>Chinese, English and mixed keyword spellings: tokens, parsing and identical program semantics.</summary>
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
        string source = $"﻿{chinese} {english}";
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
                     english + "中文", "中文" + english, english + "́", english + "𠀀", english.ToUpperInvariant() }) {
            var lexer = Lexer.NewInstance(name);
            var token = lexer.NextToken();
            Assert.Equal(TokenType.IDENT, token.Type);
            Assert.NotEqual(type, token.Type);
            Assert.Equal(name, token.Literal);
            Assert.Equal(TokenType.EOF, lexer.NextToken().Type);
        }
    }

    [Theory, MemberData(nameof(Keywords))]
    public void TokenLookupReservesBothSpellings(string chinese, string english, string type) {
        Assert.Equal(type, TokenLookup.LookupIdentifier(chinese));
        Assert.Equal(type, TokenLookup.LookupIdentifier(english));
    }

    private const string KeywordText = "var 變數 function 函式 return 回傳 if 如果 else 否則 true 真";

    [Fact]
    public void CommentsAndStringsKeepKeywordTextUntouched() {
        var lexer = Lexer.NewInstance($"# {KeywordText}\n## {KeywordText}\n##\n\"{KeywordText}\"");
        var token = lexer.NextToken();
        Assert.Equal(TokenType.STRING, token.Type);
        Assert.Equal(KeywordText, token.Literal);
        Assert.Equal(TokenType.EOF, lexer.NextToken().Type);
        Assert.Empty(lexer.Errors);
    }

    [RuntimeFact]
    public void StringsKeepKeywordTextAtRuntime() =>
        LanguageCases.Expect(($"var text = \"{KeywordText}\"; # {KeywordText}\ntext;", "string", KeywordText));

    public static IEnumerable<(string Variable, string Function, string Result)> FunctionSpellings() {
        foreach (string variable in new[] { "變數", "var" })
        foreach (string function in new[] { "函式", "function" })
        foreach (string result in new[] { "回傳", "return" })
            yield return (variable, function, result);
    }

    [RuntimeFact]
    public void DeclarationsFunctionsAndReturnsMixIndependently() => LanguageCases.Expect(FunctionSpellings().Select(s => ($$"""
        {{s.Variable}} 建立, make = {{s.Function}}(起點) {
            {{s.Variable}} 值 = 起點;
            {{s.Result}} {{s.Function}}(step) { 值 += step; {{s.Result}} 值; };
        };
        {{s.Variable}} count = make(10);
        {{s.Variable}} 另一個 = 建立(20);
        [count(1), count(2), 另一個(3), 建立 == make];
        """, "array", "[11, 13, 23, 真]")).ToArray());

    [RuntimeFact]
    public void ConditionalBranchesSelectExactlyOneResult() {
        var spellings = new (string First, string Next, string Last)[] {
            ("如果", "否則如果", "否則"),
            ("if", "elif", "else"),
            ("if", "else if", "else"),
            ("如果", "else 如果", "否則"),
            ("if", "否則 if", "else"),
            ("如果", "否則 如果", "else"),
            ("if", "else\n# branch comment\n如果", "否則"),
            ("如果", "否則 ## branch comment ## if", "else"),
        };
        LanguageCases.Expect(spellings.Select(s => ($$"""
            var effects = 0;
            變數 classify = function(n) {
                {{s.First}} (n < 0) { effects++; return -1; }
                {{s.Next}} (n == 0) { effects++; 回傳 0; }
                {{s.Next}} (n == 1) { effects++; return 1; }
                {{s.Last}} { effects++; 回傳 2; }
                effects += 100;
            };
            [classify(-5), classify(0), classify(1), classify(9), effects];
            """, "array", "[-1, 0, 1, 2, 4]")).ToArray());
    }

    [RuntimeFact]
    public void ExpressionsAndDataKeepTheSameSemantics() => LanguageCases.Expect(
        ("if (false) { 1; } else if (false) { 2; };", "null", "null"),
        ("如果 (假) { 1; } 否則如果 (假) { 2; };", "null", "null"),
        ("if (true) { if (false) { 1; } else if (true) { 2; } else { 3; } } else { 4; };", "integer", "2"),
        ("如果 (真) { if (假) { 1; } 否則 如果 (真) { 2; } else { 3; } } 否則 { 4; };", "integer", "2"),
        ("[true == 真, false == 假, !false, true && 真, false || 真];", "array", "[真, 真, 真, 真, 真]"),
        ("var factorial = function(n) { if (n < 2) { return 1; } return n * factorial(n - 1); }; factorial(6);", "integer", "720"),
        ("變數 階乘, factorial = function(n) { if (n < 2) { 回傳 1; } return n * 階乘(n - 1); }; factorial(6);", "integer", "720"),
        ("var a = [1, 2]; 變數 h = {\"var\": a}; h[\"var\"][0] = 5; a[1] = a[1] + 3; [a, h[\"var\"], len(a), 長度(a)];", "array", "[[5, 5], [5, 5], 2, 2]"),
        (LanguageCases.Typed("var n = 5; 變數 f = 1.5f; var d = 2.5d; [n++, ++n, f++, ++f, d++, ++d, 10 % 3];"), "array",
            "[[5, 7, 1.5, 3.5, 2.5, 4.5, 1], [integer, integer, float, float, double, double, integer]]"),
        ("var returnValue = 4; 變數 if成立 = 5; var TRUE = 6; returnValue + if成立 + TRUE;", "integer", "15"));

    [RuntimeFact]
    public void LoopsBreakReturnsAndIterationClosuresWorkInBothLanguages() {
        var spellings = new[] {
            new[] { "變數", "迴圈", "如果", "中斷", "函式", "回傳", "真", "假" },
            new[] { "var", "for", "if", "break", "function", "return", "true", "false" },
            new[] { "var", "迴圈", "if", "中斷", "函式", "return", "真", "false" },
            new[] { "變數", "for", "如果", "break", "function", "回傳", "true", "假" },
        };
        LanguageCases.Expect(spellings.Select(k => ($$"""
            {{k[0]}} count = 0;
            {{k[0]}} saved = [];
            {{k[1]}} ({{k[0]}} i = 0; i < 5; i++) {
                {{k[0]}} local = i;
                {{k[2]}} (i == 3 && {{k[6]}} && !{{k[7]}}) { {{k[3]}}; count += 100; }
                saved = push(saved, {{k[4]}}() { {{k[5]}} local; });
                count++;
            }
            {{k[0]}} early = {{k[4]}}() {
                {{k[1]}} ({{k[0]}} i = 0; i < 2; ++i) { {{k[5]}} 7; }
                {{k[5]}} 99;
            };
            [count, saved[0](), saved[1](), saved[2](), early()];
            """, "array", "[3, 0, 1, 2, 7]")).ToArray());
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

    [RuntimeTheory]
    [InlineData("var n = 0; var fail = function() { unknown; }; print(\"n\", n); fail(); n = 1; print(\"n\", n);")]
    [InlineData("變數 n = 0; var fail = 函式() { unknown; }; 印出(\"n\", n); fail(); n = 1; 印出(\"n\", n);")]
    public void RuntimeErrorsStopLaterSideEffects(string source) {
        var run = WasmTestHost.Run(source);
        Assert.Equal(1, run.ExitCode);
        Assert.Equal("n 0\nERROR: Identifier not found: unknown\n", run.Output);
    }

    [RuntimeFact]
    public void InteractiveSessionsCanChangeSyntaxBetweenEvaluations() {
        // aqua repl replays the session for each input and shows only the new input's output and value.
        var output = new StringWriter();
        Repl.Run(new StringReader("var n = 40; var next = function() { 回傳 ++n; };\n變數 答案 = next(); 答案;\n[答案, next()];\n"), output);
        string text = output.ToString().Replace("\r\n", "\n");
        Assert.Contains(Repl.Prompt + "41\n", text);
        Assert.Contains(Repl.Prompt + "[41, 42]\n", text);
        Assert.DoesNotContain("ERROR", text);
    }
}
