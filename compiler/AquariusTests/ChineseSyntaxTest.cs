using AquariusLang.ast;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using AquariusLang.token;

namespace AquariusTests;

/// <summary>Traditional Chinese keywords, Unicode identifiers and the ++ / += operators.</summary>
public class ChineseSyntaxTest {
    [Fact]
    public void KeywordsAndUnicodeIdentifiersAreTokenized() {
        Lexer lexer = Lexer.NewInstance("函式 變數 真 假 如果 否則 否則如果 回傳 迴圈 中斷 計數1 中文_變數 𠀀值 é ++ += +");
        string[] types = {
            TokenType.FUNCTION, TokenType.LET, TokenType.TRUE, TokenType.FALSE,
            TokenType.IF, TokenType.ELSE, TokenType.ELSE_IF, TokenType.RETURN,
            TokenType.FOR, TokenType.BREAK, TokenType.IDENT, TokenType.IDENT,
            TokenType.IDENT, TokenType.IDENT, TokenType.INCREMENT, TokenType.PLUS_EQ,
            TokenType.PLUS, TokenType.EOF
        };
        string[] literals = {
            "函式", "變數", "真", "假", "如果", "否則", "否則如果", "回傳",
            "迴圈", "中斷", "計數1", "中文_變數", "𠀀值", "é", "++", "+=", "+", ""
        };
        for (int i = 0; i < types.Length; i++) {
            Token token = lexer.NextToken();
            Assert.Equal(types[i], token.Type);
            Assert.Equal(literals[i], token.Literal);
        }
    }

    [RuntimeFact]
    public void IncrementReturnsTheCorrectValue() => LanguageCases.Expect(new (string Input, int Expected)[] {
        ("變數 計數 = 5; 計數++;", 5),
        ("變數 計數 = 5; ++計數;", 6),
        ("變數 計數 = 5; 計數++; 計數;", 6),
        ("變數 計數 = 5; ++計數; 計數;", 6),
        ("變數 計數 = 5; 計數++ + ++計數;", 12),
        ("變數 計數 = 5; 2 * 計數++ + ++計數 * 3;", 31),
        ("變數 計數 = 5; -計數++;", -5),
        ("變數 計數 = 5; -(++計數);", -6),
        ("變數 計數 = 0; 變數 加 = 函式(甲, 乙) { 回傳 甲 + 乙; }; 加(計數++, ++計數);", 2),
        ("變數 中文_數字1 = 3; 中文_數字1 += 2; 中文_數字1++; 中文_數字1;", 6),
        ("變數 𠀀值 = 3; ++𠀀值;", 4),
        ("變數 真值 = 5; 變數 如果成立 = 6; 真值 + 如果成立;", 11),
    }.Select(c => (c.Input, "integer", c.Expected.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray());

    [RuntimeFact]
    public void NumericTypesAndOldValuesArePreserved() => LanguageCases.Expect(
        (LanguageCases.Typed("變數 小數 = 1.5f; 變數 倍精度 = 2.5d; [小數++, ++小數, 小數, 倍精度++, ++倍精度, 倍精度];"), "array",
            "[[1.5, 3.5, 3.5, 2.5, 4.5, 4.5], [float, float, float, double, double, double]]"));

    [RuntimeFact]
    public void IncrementWorksInLoopHeadersAndBodies() => LanguageCases.Expect(new[] { "索引++", "++索引" }.Select(update =>
        ($"變數 次數 = 0; 迴圈 (變數 索引 = 0; 索引 < 5; {update}) {{ 次數++; }} 次數;", "integer", "5")).ToArray());

    [RuntimeFact]
    public void IncrementUpdatesTheOwningScopeAndRespectsShadowing() => LanguageCases.Expect(
        (LanguageCases.Typed(@"
            變數 計數 = 0;
            變數 增加 = 函式() { 回傳 ++計數; };
            增加(); 增加();
            變數 區域 = 函式(計數) { 回傳 ++計數; };
            [計數, 區域(10), 計數];"), "array", "[[2, 11, 2], [integer, integer, integer]]"));

    [RuntimeFact]
    public void ChineseClosuresConditionalsBreakAndReturnWork() => LanguageCases.Expect(
        (@"
            變數 建立加法 = 函式(基數) { 回傳 函式(加數) {
                如果 (假) { 回傳 0; }
                否則如果 (真) { 回傳 基數 + 加數; }
                否則 { 回傳 1; }
            }; };
            變數 加五 = 建立加法(5);
            加五(7);", "integer", "12"),
        // Break and return stop the correct statements.
        (@"
            變數 計數 = 0;
            迴圈 (變數 索引 = 0; 索引 < 5; 索引++) {
                如果 (索引 == 2) { 中斷; 計數++; }
                計數++;
            }
            計數;", "integer", "2"),
        (@"
            變數 測試 = 函式() {
                迴圈 (變數 外層 = 0; 外層 < 2; 外層++) {
                    迴圈 (變數 內層 = 0; 內層 < 2; ++內層) { 回傳 7; }
                }
                回傳 99;
            }; 測試();", "integer", "7"));

    [Theory]
    [InlineData("++5;")]
    [InlineData("5++;")]
    [InlineData("++(甲 + 乙);")]
    [InlineData("函式() { 1; }()++;")]
    [InlineData("變數 計數 = 0; 計數++++;")]
    public void InvalidIncrementTargetsProduceParserErrors(string input) {
        Parser parser = Parser.NewInstance(Lexer.NewInstance(input));
        parser.ParseAST();
        Assert.Contains("++ 的運算元必須是變數、欄位或索引 / The operand of ++ must be a variable, a field or an index.", parser.Errors);
    }

    [RuntimeFact]
    public void ChineseFunctionInspectionIncludesParameters() {
        var values = WasmTestHost.EvalMany(["函式(甲, 乙) { 甲 + 乙; };", "[真, 假];"]);
        Assert.Equal("function", values[0].Type);
        Assert.StartsWith("函式(甲, 乙)", values[0].Text);
        Assert.Equal(new TestValue("array", "[真, 假]"), values[1]);
        // The compiler's constant values display the same way.
        Assert.Equal("真", new BooleanObj(true).Inspect());
        Assert.Equal("假", new BooleanObj(false).Inspect());
    }

    [RuntimeFact]
    public void InvalidIncrementValuesProduceRuntimeErrors() => LanguageCases.ExpectErrors(
        "未宣告++;",
        "++未宣告;",
        "變數 文字 = \"你好\"; 文字++;",
        "變數 條件 = 真; ++條件;");

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
}
