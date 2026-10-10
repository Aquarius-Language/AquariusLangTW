using System.Collections.Generic;

namespace AquariusLang.token {

    /// <summary>
    /// These string constants are used as string members for Token struct. 
    /// </summary>
    public struct TokenType {
        public const string ILLEGAL = "ILLEGAL"; // ILLEGAL signifies a token/character we don’t know about.
        public const string EOF = "EOF"; // EOF stands for “end of file”.
        // Identifiers + literals
        public const string IDENT = "IDENT"; // add, foobar, x, y, ... etc identifiers.

        public const string INT = "INT"; // 1343456
        public const string FLOAT = "FLOAT"; // 234.234f, 0.34124f...
        public const string DOUBLE = "DOUBLE"; // 234.234d, 0.34124d...

        // Operators
        public const string ASSIGN = "=";
        public const string PLUS = "+";
        public const string INCREMENT = "++";
        public const string MINUS = "-";
        public const string BANG = "!";
        public const string ASTERISK = "*";
        public const string SLASH = "/";
        public const string LT = "<";
        public const string GT = ">";
        public const string GT_ET = ">=";
        public const string LT_ET = "<=";
        public const string EQ = "==";
        public const string NOT_EQ = "!=";
        public const string PLUS_EQ = "+=";
        public const string MINUS_EQ = "-=";
        public const string ASTERISK_EQ = "*=";
        public const string SLASH_EQ = "/=";
        public const string AND_AND = "&&";
        public const string OR_OR = "||";
        public const string DOT = ".";

        // Delimiters
        public const string COMMA = ",";
        public const string SEMICOLON = ";";
        public const string LPAREN = "(";
        public const string RPAREN = ")";
        public const string LBRACE = "{";
        public const string RBRACE = "}";
        // Keywords
        public const string FUNCTION = "FUNCTION";
        public const string LET = "LET";
        public const string TRUE = "TRUE";
        public const string FALSE = "FALSE";
        public const string IF = "IF";
        public const string ELSE = "ELSE";
        public const string ELSE_IF = "ELIF";
        public const string RETURN = "RETURN";
        public const string FOR = "FOR";
        public const string BREAK = "BREAK";

        public const string STRING = "STRING";
        public const string LBRACKET = "[";
        public const string RBRACKET = "]";
        public const string COLON = ":";
    }

    /// <summary>
    /// Utilities for looking up keywords and or identifiers for scanned string literals.
    /// </summary>
    public static class TokenLookup {
        /// <summary>
        /// keywords table maps corresponded to keywords constants above.
        /// </summary>
        private static readonly Dictionary<string, string> Keywords = new Dictionary<string, string>() {
            { "函式", TokenType.FUNCTION },
            { "變數", TokenType.LET },
            { "真", TokenType.TRUE },
            { "假", TokenType.FALSE },
            { "如果", TokenType.IF },
            { "否則", TokenType.ELSE },
            { "否則如果", TokenType.ELSE_IF },
            { "回傳", TokenType.RETURN },
            { "迴圈", TokenType.FOR },
            { "中斷", TokenType.BREAK },
        };

        /// <summary>
        ///     LookupIdent checks the keywords table to see whether the given literal is in fact a keyword.
        /// If it is, it returns the keyword’s TokenType constant. If it isn’t, we just get back token.IDENT,
        /// which is the TokenType for all user-defined identifiers.
        /// </summary>
        /// <param name="literal"></param>
        public static string LookupIdentifier(string literal) {
            return Keywords.ContainsKey(literal) ? Keywords[literal] : TokenType.IDENT;
        }
    }

    public struct Token {
        /// <summary>
        /// Example token types: "=", "INT", "<", etc. It's corresponded to constants above in TokenType.
        /// </summary>
        public string Type;

        public string Literal;

        // UTF-16 source offsets, matching the default LSP position encoding.
        public int Start;
        public int Length;

        public override string ToString() {
            return $"Type: {Type}, Literal: {Literal}";
        }
    }
}
