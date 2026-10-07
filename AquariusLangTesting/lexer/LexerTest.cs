using AquariusLang.token;
using Xunit;
using Xunit.Abstractions;

namespace AquariusLang.lexer; 

public class LexerTest {
    /// <summary>
    /// For logging outputs during testing.
    /// </summary>
    private readonly ITestOutputHelper _testOutputHelper;

    public LexerTest(ITestOutputHelper testOutputHelper) {
        _testOutputHelper = testOutputHelper;
    }
    
    struct ExpectedTest {
        public string ExpectedType { get; init; }
        public string ExpectedLiteral { get; init; }
    }
    
    [Fact]
    public void TestNextToken() {
        string? input = @"
            變數 module = 匯入(""./anotherfile.aqua"");
            module.callFunc();

            變數 five = 5;
            變數 ten = -10;

            變數 floatNum = 12.345f;
            變數 doubleNum = 37.8d;

            變數 j = 12.8f / 37.2d;

            ten = ""10"";

            變數 add = 函式(x, y) {
              x + y;
            };

            變數 result = add(five, ten);
            !-*/5;
            5 < 10 > 5;

            如果 (5 < 10) {
	            回傳 真;
            } 否則如果 (假) {
                回傳 假;
            }
            否則 {
	            回傳 假;
            }

            10 == 10;
            10 != 9;
            ""foobar""
            ""foo bar""
            [1, 2];
            {""foo"": ""bar""}
            
            # This is a comment.
            迴圈 (變數 i = 0; i < 5; i++) {}

            ##
            This is also a comment.
            This should be ignored.
            ##
            真 && 真;
            假 || 假;

            12 >= 5;
            4 <= 18;

            a *= 38;
            b /= a;
        ";

        ExpectedTest[] tests = new [] {
            new ExpectedTest(){ExpectedType = TokenType.LET,       ExpectedLiteral = "變數"},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,       ExpectedLiteral = "module"},
            new ExpectedTest(){ExpectedType = TokenType.ASSIGN,       ExpectedLiteral = "="},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,       ExpectedLiteral = "匯入"},
            new ExpectedTest(){ExpectedType = TokenType.LPAREN,    ExpectedLiteral = "("},
            new ExpectedTest(){ExpectedType = TokenType.STRING,       ExpectedLiteral = "./anotherfile.aqua"},
            new ExpectedTest(){ExpectedType = TokenType.RPAREN,    ExpectedLiteral = ")"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON,    ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.IDENT,       ExpectedLiteral = "module"},
            new ExpectedTest(){ExpectedType = TokenType.DOT,       ExpectedLiteral = "."},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,       ExpectedLiteral = "callFunc"},
            new ExpectedTest(){ExpectedType = TokenType.LPAREN,       ExpectedLiteral = "("},
            new ExpectedTest(){ExpectedType = TokenType.RPAREN,       ExpectedLiteral = ")"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON,       ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.LET,       ExpectedLiteral = "變數"},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "five"},
            new ExpectedTest(){ExpectedType = TokenType.ASSIGN,    ExpectedLiteral = "="},
            new ExpectedTest(){ExpectedType = TokenType.INT,       ExpectedLiteral = "5"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.LET,       ExpectedLiteral = "變數"},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "ten"},
            new ExpectedTest(){ExpectedType = TokenType.ASSIGN,    ExpectedLiteral = "="},
            new ExpectedTest(){ExpectedType = TokenType.MINUS,       ExpectedLiteral = "-"},
            new ExpectedTest(){ExpectedType = TokenType.INT,       ExpectedLiteral = "10"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.LET,       ExpectedLiteral = "變數"},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "floatNum"},
            new ExpectedTest(){ExpectedType = TokenType.ASSIGN,    ExpectedLiteral = "="},
            new ExpectedTest(){ExpectedType = TokenType.FLOAT,       ExpectedLiteral = "12.345f"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.LET,       ExpectedLiteral = "變數"},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "doubleNum"},
            new ExpectedTest(){ExpectedType = TokenType.ASSIGN,    ExpectedLiteral = "="},
            new ExpectedTest(){ExpectedType = TokenType.DOUBLE,       ExpectedLiteral = "37.8d"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.LET,       ExpectedLiteral = "變數"},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "j"},
            new ExpectedTest(){ExpectedType = TokenType.ASSIGN,    ExpectedLiteral = "="},
            new ExpectedTest(){ExpectedType = TokenType.FLOAT,       ExpectedLiteral = "12.8f"},
            new ExpectedTest(){ExpectedType = TokenType.SLASH,       ExpectedLiteral = "/"},
            new ExpectedTest(){ExpectedType = TokenType.DOUBLE,       ExpectedLiteral = "37.2d"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},

            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "ten"},
            new ExpectedTest(){ExpectedType = TokenType.ASSIGN,    ExpectedLiteral = "="},
            new ExpectedTest(){ExpectedType = TokenType.STRING,    ExpectedLiteral = "10"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.LET,       ExpectedLiteral = "變數"},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "add"},
            new ExpectedTest(){ExpectedType = TokenType.ASSIGN,    ExpectedLiteral = "="},
            new ExpectedTest(){ExpectedType = TokenType.FUNCTION,  ExpectedLiteral = "函式"},
            new ExpectedTest(){ExpectedType = TokenType.LPAREN,    ExpectedLiteral = "("},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "x"},
            new ExpectedTest(){ExpectedType = TokenType.COMMA,     ExpectedLiteral = ","},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "y"},
            new ExpectedTest(){ExpectedType = TokenType.RPAREN,    ExpectedLiteral = ")"},
            new ExpectedTest(){ExpectedType = TokenType.LBRACE,    ExpectedLiteral = "{"},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "x"},
            new ExpectedTest(){ExpectedType = TokenType.PLUS,      ExpectedLiteral = "+"},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "y"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            new ExpectedTest(){ExpectedType = TokenType.RBRACE,    ExpectedLiteral = "}"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.LET,       ExpectedLiteral = "變數"},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "result"},
            new ExpectedTest(){ExpectedType = TokenType.ASSIGN,    ExpectedLiteral = "="},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "add"},
            new ExpectedTest(){ExpectedType = TokenType.LPAREN,    ExpectedLiteral = "("},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "five"},
            new ExpectedTest(){ExpectedType = TokenType.COMMA,     ExpectedLiteral = ","},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,     ExpectedLiteral = "ten"},
            new ExpectedTest(){ExpectedType = TokenType.RPAREN,    ExpectedLiteral = ")"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.BANG,      ExpectedLiteral = "!"},
            new ExpectedTest(){ExpectedType = TokenType.MINUS,     ExpectedLiteral = "-"},
            new ExpectedTest(){ExpectedType = TokenType.ASTERISK,     ExpectedLiteral = "*"},
            new ExpectedTest(){ExpectedType = TokenType.SLASH,     ExpectedLiteral = "/"},
            new ExpectedTest(){ExpectedType = TokenType.INT,       ExpectedLiteral = "5"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.INT,       ExpectedLiteral = "5"},
            new ExpectedTest(){ExpectedType = TokenType.LT,        ExpectedLiteral = "<"},
            new ExpectedTest(){ExpectedType = TokenType.INT,       ExpectedLiteral = "10"},
            new ExpectedTest(){ExpectedType = TokenType.GT,        ExpectedLiteral = ">"},
            new ExpectedTest(){ExpectedType = TokenType.INT,       ExpectedLiteral = "5"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.IF,        ExpectedLiteral = "如果"},
            new ExpectedTest(){ExpectedType = TokenType.LPAREN,    ExpectedLiteral = "("},
            new ExpectedTest(){ExpectedType = TokenType.INT,       ExpectedLiteral = "5"},
            new ExpectedTest(){ExpectedType = TokenType.LT,        ExpectedLiteral = "<"},
            new ExpectedTest(){ExpectedType = TokenType.INT,       ExpectedLiteral = "10"},
            new ExpectedTest(){ExpectedType = TokenType.RPAREN,    ExpectedLiteral = ")"},
            new ExpectedTest(){ExpectedType = TokenType.LBRACE,    ExpectedLiteral = "{"},
            new ExpectedTest(){ExpectedType = TokenType.RETURN,    ExpectedLiteral = "回傳"},
            new ExpectedTest(){ExpectedType = TokenType.TRUE,      ExpectedLiteral = "真"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.RBRACE,    ExpectedLiteral = "}"},
            new ExpectedTest(){ExpectedType = TokenType.ELSE_IF,    ExpectedLiteral = "否則如果"},
            new ExpectedTest(){ExpectedType = TokenType.LPAREN,    ExpectedLiteral = "("},
            new ExpectedTest(){ExpectedType = TokenType.FALSE,    ExpectedLiteral = "假"},
            new ExpectedTest(){ExpectedType = TokenType.RPAREN,    ExpectedLiteral = ")"},
            new ExpectedTest(){ExpectedType = TokenType.LBRACE,    ExpectedLiteral = "{"},
            new ExpectedTest(){ExpectedType = TokenType.RETURN,    ExpectedLiteral = "回傳"},
            new ExpectedTest(){ExpectedType = TokenType.FALSE,    ExpectedLiteral = "假"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON,    ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.RBRACE,    ExpectedLiteral = "}"},
            new ExpectedTest(){ExpectedType = TokenType.ELSE,      ExpectedLiteral = "否則"},
            new ExpectedTest(){ExpectedType = TokenType.LBRACE,    ExpectedLiteral = "{"},
            new ExpectedTest(){ExpectedType = TokenType.RETURN,    ExpectedLiteral = "回傳"},
            new ExpectedTest(){ExpectedType = TokenType.FALSE,     ExpectedLiteral = "假"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.RBRACE,    ExpectedLiteral = "}"},
            new ExpectedTest(){ExpectedType = TokenType.INT,       ExpectedLiteral = "10"},
            new ExpectedTest(){ExpectedType = TokenType.EQ,        ExpectedLiteral = "=="},
            new ExpectedTest(){ExpectedType = TokenType.INT,       ExpectedLiteral = "10"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.INT,       ExpectedLiteral = "10"},
            new ExpectedTest(){ExpectedType = TokenType.NOT_EQ,    ExpectedLiteral = "!="},
            new ExpectedTest(){ExpectedType = TokenType.INT,       ExpectedLiteral = "9"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.STRING,    ExpectedLiteral = "foobar"},
            new ExpectedTest(){ExpectedType = TokenType.STRING,    ExpectedLiteral = "foo bar"},
            new ExpectedTest(){ExpectedType = TokenType.LBRACKET,  ExpectedLiteral = "["},
            new ExpectedTest(){ExpectedType = TokenType.INT,       ExpectedLiteral = "1"},
            new ExpectedTest(){ExpectedType = TokenType.COMMA,     ExpectedLiteral = ","},
            new ExpectedTest(){ExpectedType = TokenType.INT,       ExpectedLiteral = "2"},
            new ExpectedTest(){ExpectedType = TokenType.RBRACKET,  ExpectedLiteral = "]"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON, ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.LBRACE,    ExpectedLiteral = "{"},
            new ExpectedTest(){ExpectedType = TokenType.STRING,    ExpectedLiteral = "foo"},
            new ExpectedTest(){ExpectedType = TokenType.COLON,    ExpectedLiteral = ":"},
            new ExpectedTest(){ExpectedType = TokenType.STRING,    ExpectedLiteral = "bar"},
            new ExpectedTest(){ExpectedType = TokenType.RBRACE,    ExpectedLiteral = "}"},
            new ExpectedTest(){ExpectedType = TokenType.FOR,    ExpectedLiteral = "迴圈"},
            new ExpectedTest(){ExpectedType = TokenType.LPAREN,    ExpectedLiteral = "("},
            new ExpectedTest(){ExpectedType = TokenType.LET,    ExpectedLiteral = "變數"},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,    ExpectedLiteral = "i"},
            new ExpectedTest(){ExpectedType = TokenType.ASSIGN,    ExpectedLiteral = "="},
            new ExpectedTest(){ExpectedType = TokenType.INT,    ExpectedLiteral = "0"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON,    ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.IDENT,    ExpectedLiteral = "i"},
            new ExpectedTest(){ExpectedType = TokenType.LT,    ExpectedLiteral = "<"},
            new ExpectedTest(){ExpectedType = TokenType.INT,    ExpectedLiteral = "5"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON,    ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.IDENT,    ExpectedLiteral = "i"},
            new ExpectedTest(){ExpectedType = TokenType.INCREMENT,    ExpectedLiteral = "++"},
            new ExpectedTest(){ExpectedType = TokenType.RPAREN,    ExpectedLiteral = ")"},
            new ExpectedTest(){ExpectedType = TokenType.LBRACE,    ExpectedLiteral = "{"},
            new ExpectedTest(){ExpectedType = TokenType.RBRACE,    ExpectedLiteral = "}"},
            new ExpectedTest(){ExpectedType = TokenType.TRUE, ExpectedLiteral = "真"},
            new ExpectedTest(){ExpectedType = TokenType.AND_AND, ExpectedLiteral = "&&"},
            new ExpectedTest(){ExpectedType = TokenType.TRUE, ExpectedLiteral = "真"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON,    ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.FALSE, ExpectedLiteral = "假"},
            new ExpectedTest(){ExpectedType = TokenType.OR_OR, ExpectedLiteral = "||"},
            new ExpectedTest(){ExpectedType = TokenType.FALSE, ExpectedLiteral = "假"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON,    ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.INT,    ExpectedLiteral = "12"},
            new ExpectedTest(){ExpectedType = TokenType.GT_ET,    ExpectedLiteral = ">="},
            new ExpectedTest(){ExpectedType = TokenType.INT,    ExpectedLiteral = "5"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON,    ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.INT,    ExpectedLiteral = "4"},
            new ExpectedTest(){ExpectedType = TokenType.LT_ET,    ExpectedLiteral = "<="},
            new ExpectedTest(){ExpectedType = TokenType.INT,    ExpectedLiteral = "18"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON,    ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.IDENT,    ExpectedLiteral = "a"},
            new ExpectedTest(){ExpectedType = TokenType.ASTERISK_EQ,    ExpectedLiteral = "*="},
            new ExpectedTest(){ExpectedType = TokenType.INT,    ExpectedLiteral = "38"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON,    ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.IDENT,    ExpectedLiteral = "b"},
            new ExpectedTest(){ExpectedType = TokenType.SLASH_EQ,    ExpectedLiteral = "/="},
            new ExpectedTest(){ExpectedType = TokenType.IDENT,    ExpectedLiteral = "a"},
            new ExpectedTest(){ExpectedType = TokenType.SEMICOLON,    ExpectedLiteral = ";"},
            
            new ExpectedTest(){ExpectedType = TokenType.EOF,       ExpectedLiteral = ""},
        };
        
        Lexer lexer = Lexer.NewInstance(input);

        foreach (var expectedTest in tests) {
            Token token = lexer.NextToken();
            
            _testOutputHelper.WriteLine("Token -    " + token);
            _testOutputHelper.WriteLine("Expected - " + $"Type: {expectedTest.ExpectedType}, Literal: {expectedTest.ExpectedLiteral}");

            Assert.Equal(expectedTest.ExpectedType, token.Type);
            Assert.Equal(expectedTest.ExpectedLiteral, token.Literal);
        }
    }
}
