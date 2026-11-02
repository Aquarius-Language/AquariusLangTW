using AquariusLang.token;

namespace AquariusLang.ast {

/// <summary>A 長整數 / long literal: integer digits with the suffix <c>L</c> (<c>5000000000L</c>, <c>0xFFL</c>).</summary>
public sealed class LongLiteral : IExpression {
    public LongLiteral(Token token, long value = 0) { Token = token; Value = value; }
    public Token Token { get; set; }
    public long Value { get; set; }
    public string TokenLiteral() => Token.Literal;
    public string String() => Token.Literal;
    public void ExpressionNode() { }
}
}
