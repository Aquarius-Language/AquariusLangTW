using AquariusLang.token;
using AquariusLang.utils;
using Xunit;

namespace AquariusLang.lexer;

/// <summary>Number literals: unsuffixed decimals are doubles, exponents, hexadecimal and binary integers, `_` digit
/// separators and suffixes on integer digits; malformed numbers are lexical errors.</summary>
public class NumberLiteralTest {
    private static (string Type, string Literal)[] Tokens(string input) {
        var lexer = Lexer.NewInstance(input);
        var tokens = new List<(string, string)>();
        for (var token = lexer.NextToken(); token.Type != TokenType.EOF; token = lexer.NextToken()) tokens.Add((token.Type, token.Literal));
        return tokens.ToArray();
    }

    [Theory]
    [InlineData("1.5", "DOUBLE"), InlineData("1e-9", "DOUBLE"), InlineData("1E+3", "DOUBLE"), InlineData("2.5e3f", "FLOAT"), InlineData("2d", "DOUBLE"),
     InlineData("2f", "FLOAT"), InlineData("1.5f", "FLOAT"), InlineData("1.5d", "DOUBLE"), InlineData("1.f", "FLOAT"), InlineData("0.d", "DOUBLE"),
     InlineData("0xFF", "INT"), InlineData("0b1010", "INT"), InlineData("1_000_000", "INT"), InlineData("0xFF_FF", "INT"), InlineData("1_000.25", "DOUBLE"),
     InlineData("42", "INT")]
    public void NumbersLexAsOneToken(string literal, string type) {
        var tokens = Tokens(literal + ";");
        Assert.Equal([(type, literal), (TokenType.SEMICOLON, ";")], tokens);
    }

    [Theory]
    [InlineData("0xFG"), InlineData("0b102"), InlineData("1__0"), InlineData("1_"), InlineData("0x"), InlineData("0x_1"), InlineData("1.5x"), InlineData("2df")]
    public void MalformedNumbersAreLexicalErrors(string literal) {
        var lexer = Lexer.NewInstance(literal + ";");
        var token = lexer.NextToken();
        Assert.Equal(TokenType.ILLEGAL, token.Type);
        Assert.Equal(literal, token.Literal);
        Assert.Contains(lexer.Errors, e => e.Message.Contains("Invalid number literal"));
    }

    [Fact]
    public void MembersAndRangesAfterNumbersStayTokens() {
        // A dot not followed by a digit (or a lone suffix) is member access; an exponent needs digits.
        Assert.Equal([("INT", "1"), (".", "."), ("IDENT", "x")], Tokens("1.x"));
        Assert.Equal([("[", "["), ("INT", "0"), ("]", "]"), (".", "."), ("IDENT", "f")], Tokens("[0].f"));
        Assert.Equal([("ILLEGAL", "1e")], Tokens("1e"));
    }

    [Fact]
    public void LiteralValues() {
        Assert.Equal(255, Utils.StringToInteger("0xFF"));
        Assert.Equal(-1, Utils.StringToInteger("0xFFFFFFFF"));
        Assert.Equal(int.MinValue, Utils.StringToInteger("0x8000_0000"));
        Assert.Equal(10, Utils.StringToInteger("0b1010"));
        Assert.Null(Utils.StringToInteger("0x1_0000_0000"));
        Assert.Null(Utils.StringToInteger("2147483648"));
        Assert.Equal(1000000, Utils.StringToInteger("1_000_000"));
        Assert.Equal(1e-9, Utils.StringToDouble("1e-9"));
        Assert.Equal(0.1, Utils.StringToDouble("0.1"));
        Assert.Equal(2500f, Utils.StringToFloat("2.5e3f"));
        Assert.Equal(0.1f, Utils.StringToFloat("0.1f"));
        Assert.Null(Utils.StringToDouble("1e400"));
        Assert.Null(Utils.StringToFloat("3.5e38f"));
    }

    [Fact]
    public void BitwiseOperatorsLexAsOneToken() {
        Assert.Equal([("IDENT", "a"), ("&", "&"), ("IDENT", "b"), ("&&", "&&"), ("IDENT", "c"), ("&=", "&="), ("INT", "1")], Tokens("a & b && c &= 1"));
        Assert.Equal([("|", "|"), ("||", "||"), ("|=", "|="), ("^", "^"), ("^=", "^="), ("~", "~")], Tokens("| || |= ^ ^= ~"));
        Assert.Equal([("<<", "<<"), ("<<=", "<<="), ("<", "<"), ("<=", "<="), (">>", ">>"), (">>=", ">>="), (">>>", ">>>"), (">>>=", ">>>="), (">", ">"), (">=", ">=")],
            Tokens("<< <<= < <= >> >>= >>> >>>= > >="));
    }
}
