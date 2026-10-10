using AquariusLang.Compiler;
using AquariusLang.ast;
using AquariusLang.runtime;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using AquariusLang.token;
using Environment = AquariusLang.Object.Environment;

namespace AquariusTests;

public class ChineseSyntaxTest {
    [Fact]
    public void KeywordsAndUnicodeIdentifiersAreTokenized() {
        Lexer lexer = Lexer.NewInstance("函式 變數 真 假 如果 否則 否則如果 回傳 迴圈 中斷 計數1 中文_變數 𠀀值 e\u0301 ++ += +");
        string[] types = {
            TokenType.FUNCTION, TokenType.LET, TokenType.TRUE, TokenType.FALSE,
            TokenType.IF, TokenType.ELSE, TokenType.ELSE_IF, TokenType.RETURN,
            TokenType.FOR, TokenType.BREAK, TokenType.IDENT, TokenType.IDENT,
            TokenType.IDENT, TokenType.IDENT, TokenType.INCREMENT, TokenType.PLUS_EQ,
            TokenType.PLUS, TokenType.EOF
        };
        string[] literals = {
            "函式", "變數", "真", "假", "如果", "否則", "否則如果", "回傳",
            "迴圈", "中斷", "計數1", "中文_變數", "𠀀值", "e\u0301", "++", "+=", "+", ""
        };
        for (int i = 0; i < types.Length; i++) {
            Token token = lexer.NextToken();
            Assert.Equal(types[i], token.Type);
            Assert.Equal(literals[i], token.Literal);
        }
    }

    [Theory]
    [InlineData("變數 計數 = 5; 計數++;", 5)]
    [InlineData("變數 計數 = 5; ++計數;", 6)]
    [InlineData("變數 計數 = 5; 計數++; 計數;", 6)]
    [InlineData("變數 計數 = 5; ++計數; 計數;", 6)]
    [InlineData("變數 計數 = 5; 計數++ + ++計數;", 12)]
    [InlineData("變數 計數 = 5; 2 * 計數++ + ++計數 * 3;", 31)]
    [InlineData("變數 計數 = 5; -計數++;", -5)]
    [InlineData("變數 計數 = 5; -(++計數);", -6)]
    [InlineData("變數 計數 = 0; 變數 加 = 函式(甲, 乙) { 回傳 甲 + 乙; }; 加(計數++, ++計數);", 2)]
    [InlineData("變數 中文_數字1 = 3; 中文_數字1 += 2; 中文_數字1++; 中文_數字1;", 6)]
    [InlineData("變數 𠀀值 = 3; ++𠀀值;", 4)]
    [InlineData("變數 真值 = 5; 變數 如果成立 = 6; 真值 + 如果成立;", 11)]
    public void IncrementReturnsTheCorrectValue(string input, int expected) {
        Assert.Equal(expected, Assert.IsType<IntegerObj>(Evaluate(input)).Value);
    }

    [Fact]
    public void NumericTypesAndOldValuesArePreserved() {
        ArrayObj result = Assert.IsType<ArrayObj>(Evaluate(
            "變數 小數 = 1.5f; 變數 倍精度 = 2.5d; [小數++, ++小數, 小數, 倍精度++, ++倍精度, 倍精度];"));
        Assert.Equal(new[] { 1.5f, 3.5f, 3.5f }, result.Elements.Take(3).Select(x => Assert.IsType<FloatObj>(x).Value));
        Assert.Equal(new[] { 2.5d, 4.5d, 4.5d }, result.Elements.Skip(3).Select(x => Assert.IsType<DoubleObj>(x).Value));
    }

    [Theory]
    [InlineData("索引++")]
    [InlineData("++索引")]
    public void IncrementWorksInLoopHeadersAndBodies(string update) {
        string input = $"變數 次數 = 0; 迴圈 (變數 索引 = 0; 索引 < 5; {update}) {{ 次數++; }} 次數;";
        Assert.Equal(5, Assert.IsType<IntegerObj>(Evaluate(input)).Value);
    }

    [Fact]
    public void IncrementUpdatesTheOwningScopeAndRespectsShadowing() {
        ArrayObj result = Assert.IsType<ArrayObj>(Evaluate(@"
            變數 計數 = 0;
            變數 增加 = 函式() { 回傳 ++計數; };
            增加(); 增加();
            變數 區域 = 函式(計數) { 回傳 ++計數; };
            [計數, 區域(10), 計數];"));
        Assert.Equal(new[] { 2, 11, 2 }, result.Elements.Select(x => Assert.IsType<IntegerObj>(x).Value));
    }

    [Fact]
    public void ChineseClosuresAndConditionalsWork() {
        Assert.Equal(12, Assert.IsType<IntegerObj>(Evaluate(@"
            變數 建立加法 = 函式(基數) { 回傳 函式(加數) {
                如果 (假) { 回傳 0; }
                否則如果 (真) { 回傳 基數 + 加數; }
                否則 { 回傳 1; }
            }; };
            變數 加五 = 建立加法(5);
            加五(7);" )).Value);
    }

    [Fact]
    public void BreakAndReturnStopTheCorrectStatements() {
        Assert.Equal(2, Assert.IsType<IntegerObj>(Evaluate(@"
            變數 計數 = 0;
            迴圈 (變數 索引 = 0; 索引 < 5; 索引++) {
                如果 (索引 == 2) { 中斷; 計數++; }
                計數++;
            }
            計數;" )).Value);
        Assert.Equal(7, Assert.IsType<IntegerObj>(Evaluate(@"
            變數 測試 = 函式() {
                迴圈 (變數 外層 = 0; 外層 < 2; 外層++) {
                    迴圈 (變數 內層 = 0; 內層 < 2; ++內層) { 回傳 7; }
                }
                回傳 99;
            }; 測試();" )).Value);
    }

    [Theory]
    [InlineData("++5;")]
    [InlineData("5++;")]
    [InlineData("++(甲 + 乙);")]
    [InlineData("[1][0]++;")]
    [InlineData("函式() { 1; }()++;")]
    [InlineData("變數 計數 = 0; 計數++++;")]
    public void InvalidIncrementTargetsProduceParserErrors(string input) {
        Parser parser = Parser.NewInstance(Lexer.NewInstance(input));
        parser.ParseAST();
        Assert.Contains("++ 的運算元必須是變數名稱。", parser.Errors);
    }

    [Fact]
    public void ChineseFunctionInspectionIncludesParameters() {
        FunctionObj function = Assert.IsType<FunctionObj>(Evaluate("函式(甲, 乙) { 甲 + 乙; };"));
        Assert.StartsWith("函式(甲, 乙)", function.Inspect());
        Assert.Equal("真", new BooleanObj(true).Inspect());
        Assert.Equal("假", new BooleanObj(false).Inspect());
    }

    [Theory]
    [InlineData("未宣告++;")]
    [InlineData("++未宣告;")]
    [InlineData("變數 文字 = \"你好\"; 文字++;")]
    [InlineData("變數 條件 = 真; ++條件;")]
    public void InvalidIncrementValuesProduceRuntimeErrors(string input) {
        Assert.IsType<ErrorObj>(Evaluate(input));
    }

    [Theory]
    [InlineData("計數++ + 2", "((計數++) + 2)")]
    [InlineData("++計數 * 2", "((++計數) * 2)")]
    [InlineData("-計數++", "(-(計數++))")]
    public void IncrementHasTheCorrectPrecedence(string input, string expected) {
        Parser parser = Parser.NewInstance(Lexer.NewInstance(input));
        AbstractSyntaxTree tree = parser.ParseAST();
        Assert.Empty(parser.Errors);
        Assert.Equal(expected, tree.String());
    }

    private static IObject Evaluate(string input) {
        Parser parser = Parser.NewInstance(Lexer.NewInstance(input));
        AbstractSyntaxTree tree = parser.ParseAST();
        Assert.Empty(parser.Errors);
        return CompiledEvaluator.NewInstance(new Builtins()).Eval(tree, Environment.NewEnvironment());
    }
}
