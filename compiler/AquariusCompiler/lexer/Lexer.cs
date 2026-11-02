using System.Globalization;
using AquariusLang.token;

namespace AquariusLang.lexer {

    public sealed class LexicalError {
        public string Message { get; }
        public int Start { get; }
        public int Length { get; }
        public LexicalError(string message, int start, int length) {
            Message = message; Start = start; Length = length;
        }
    }

    public class Lexer {
        private string input = "";
        private int position; // current position in input (points to current char)
        private int readPosition; // current reading position in input (after current char)
        private char ch; // current char under examination
        private readonly System.Collections.Generic.List<LexicalError> lexicalErrors = new System.Collections.Generic.List<LexicalError>();
        public System.Collections.Generic.IReadOnlyList<LexicalError> Errors => lexicalErrors;

        /// <summary>
        /// Singleton.
        /// </summary>
        /// <param name="input"></param>
        public static Lexer NewInstance(string? input) {
            Lexer l = new Lexer() { input = input ?? "" };
            l.readChar();
            return l;
        }

        public Token NextToken() {
            // Avoid recursive comment skipping in large documents.
            skipWhitespace();
            while (ch == '#') {
                skipComments();
                skipWhitespace();
            }
            int start = position;
            Token token = new Token() {
                Type = TokenType.ILLEGAL,
                Literal = ""
            };

            switch (ch) {
                case '=':
                    if (peekChar() == '=') {
                        readChar();
                        string literal = "==";
                        token = newToken(TokenType.EQ, literal);
                    } else {
                        token = newToken(TokenType.ASSIGN, ch);
                    }

                    break;
                case '+':
                    if (peekChar() == '+') {
                        readChar();
                        token = newToken(TokenType.INCREMENT, "++");
                    } else if (peekChar() == '=') {
                        readChar();
                        string literal = "+=";
                        token = newToken(TokenType.PLUS_EQ, literal);
                    } else {
                        token = newToken(TokenType.PLUS, ch);
                    }

                    break;
                case '-':
                    if (peekChar() == '=') {
                        readChar();
                        string literal = "-=";
                        token = newToken(TokenType.MINUS_EQ, literal);
                    } else {
                        token = newToken(TokenType.MINUS, ch);
                    }

                    break;
                case '!':
                    // Two-character token: '!='.
                    if (peekChar() == '=') {
                        readChar();
                        string literal = "!=";
                        token = newToken(TokenType.NOT_EQ, literal);
                    } else {
                        token = newToken(TokenType.BANG, ch);
                    }

                    break;
                case '/':
                    if (peekChar() == '=') {
                        readChar();
                        string literal = "/=";
                        token = newToken(TokenType.SLASH_EQ, literal);
                    } else {
                        token = newToken(TokenType.SLASH, ch);
                    }

                    break;
                case '%':
                    if (peekChar() == '=') {
                        readChar();
                        token = newToken(TokenType.PERCENT_EQ, "%=");
                    } else {
                        token = newToken(TokenType.PERCENT, ch);
                    }

                    break;
                case '*':
                    if (peekChar() == '=') {
                        readChar();
                        string literal = "*=";
                        token = newToken(TokenType.ASTERISK_EQ, literal);
                    } else {
                        token = newToken(TokenType.ASTERISK, ch);
                    }

                    break;
                case '<':
                    if (peekChar() == '=') {
                        readChar();
                        string literal = "<=";
                        token = newToken(TokenType.LT_ET, literal);
                    } else if (peekChar() == '<') {
                        readChar();
                        token = peekChar() == '=' ? newToken(TokenType.SHIFT_LEFT_EQ, "<<=") : newToken(TokenType.SHIFT_LEFT, "<<");
                        if (token.Type == TokenType.SHIFT_LEFT_EQ) readChar();
                    } else {
                        token = newToken(TokenType.LT, ch);
                    }

                    break;
                case '>':
                    if (peekChar() == '=') {
                        readChar();
                        string literal = ">=";
                        token = newToken(TokenType.GT_ET, literal);
                    } else if (peekChar() == '>') {
                        // >>, >>=, >>> and >>>=.
                        readChar();
                        bool logical = peekChar() == '>';
                        if (logical) readChar();
                        bool compound = peekChar() == '=';
                        if (compound) readChar();
                        token = logical ? newToken(compound ? TokenType.SHIFT_RIGHT_UNSIGNED_EQ : TokenType.SHIFT_RIGHT_UNSIGNED, compound ? ">>>=" : ">>>")
                            : newToken(compound ? TokenType.SHIFT_RIGHT_EQ : TokenType.SHIFT_RIGHT, compound ? ">>=" : ">>");
                    } else {
                        token = newToken(TokenType.GT, ch);
                    }

                    break;
                case ';':
                    token = newToken(TokenType.SEMICOLON, ch);
                    break;
                case ',':
                    token = newToken(TokenType.COMMA, ch);
                    break;
                case '{':
                    token = newToken(TokenType.LBRACE, ch);
                    break;
                case '}':
                    token = newToken(TokenType.RBRACE, ch);
                    break;
                case '(':
                    token = newToken(TokenType.LPAREN, ch);
                    break;
                case ')':
                    token = newToken(TokenType.RPAREN, ch);
                    break;
                case '"':
                    int quote = position;
                    token = newToken(TokenType.STRING, readString());
                    if (ch == 0) {
                        token.Type = TokenType.ILLEGAL;
                        lexicalErrors.Add(new LexicalError("字串缺少結尾的 \" / Unterminated string.", quote, input.Length - quote));
                    }
                    break;
                case '[':
                    token = newToken(TokenType.LBRACKET, ch);
                    break;
                case ']':
                    token = newToken(TokenType.RBRACKET, ch);
                    break;
                case ':':
                    token = newToken(TokenType.COLON, ch);
                    break;
                case '&':
                    if (peekChar() == '&') {
                        readChar();
                        token = newToken(TokenType.AND_AND, "&&");
                    } else if (peekChar() == '=') {
                        readChar();
                        token = newToken(TokenType.BIT_AND_EQ, "&=");
                    } else {
                        token = newToken(TokenType.BIT_AND, ch);
                    }

                    break;
                case '|':
                    if (peekChar() == '|') {
                        readChar();
                        token = newToken(TokenType.OR_OR, "||");
                    } else if (peekChar() == '=') {
                        readChar();
                        token = newToken(TokenType.BIT_OR_EQ, "|=");
                    } else {
                        token = newToken(TokenType.BIT_OR, ch);
                    }

                    break;
                case '^':
                    if (peekChar() == '=') {
                        readChar();
                        token = newToken(TokenType.BIT_XOR_EQ, "^=");
                    } else {
                        token = newToken(TokenType.BIT_XOR, ch);
                    }

                    break;
                case '~':
                    token = newToken(TokenType.BIT_NOT, ch);
                    break;
                case '.':
                    token = newToken(TokenType.DOT, '.');
                    break;
                case (char)0:
                    token = newToken(TokenType.EOF, "");
                    break;
                default:
                    if (isIdentifierStart()) { // Check if it is identifier token or a keyword token.
                        string literal = readIdentifier();
                        string type = TokenLookup.LookupIdentifier(literal);
                        token = newToken(type, literal);
                        token.Start = start;
                        token.Length = position - start;
                        return token;
                    } else if (isDigit(ch)) { // Check if is number. (int, float, double...)
                        string literal = readNumber(out string numberType);
                        token = newToken(numberType, literal);
                        token.Start = start;
                        token.Length = position - start;
                        return token;
                    } else {
                        token = newToken(TokenType.ILLEGAL, ch);
                    }

                    break;
            }

            readChar();
            token.Start = System.Math.Min(start, input.Length);
            token.Length = token.Type == TokenType.EOF ? 0 : System.Math.Min(position, input.Length) - start;
            return token;
        }

        /// <summary>A string literal's text. \" and \\ are its only escapes, so any other backslash (Windows paths,
        /// regular expressions) stays as written.</summary>
        private string readString() {
            var text = new System.Text.StringBuilder();
            while (true) {
                readChar();
                if (ch == '"' || ch == 0) {
                    break;
                }
                if (ch == '\\' && peekChar() is '"' or '\\') readChar();
                text.Append(ch);
            }

            return text.ToString();
        }

        private void skipComments() {
            int commentStart = position;
            if (ch == '#') {
                if (peekChar() != '#') {
                    while (ch != '\n' && ch != 0) {
                        readChar();
                    }
                } else {
                    readChar();
                    readChar();
                    while (ch != 0 && !(ch == '#' && peekChar() == '#')) {
                        readChar();
                    }
                    if (ch == 0) {
                        lexicalErrors.Add(new LexicalError("Unterminated block comment.", commentStart, input.Length - commentStart));
                    } else {
                        readChar();
                        readChar();
                    }
                }
            }
        }

        private void skipWhitespace() {
            // while (ch is ' ' or '\t' or '\n' or '\r') {
            //     readChar();
            // }
            while (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || (position == 0 && ch == '\uFEFF')) {
                readChar();
            }
        }

        private void readChar() {
            if (readPosition >= input.Length) {
                ch = (char)0;
            } else {
                ch = input[readPosition];
            }

            position = readPosition;
            readPosition++;
        }

        /// <summary>
        /// peekChar() is really similar to readChar(), except that it doesn’t increment l.position and l.readPosition.
        /// </summary>
        /// <returns></returns>
        private char peekChar() {
            return (readPosition >= input.Length) ? (char)0 : input[readPosition];
        }

        private string readIdentifier() {
            int lastPos = position;
            while (isIdentifierPart()) {
                if (char.IsHighSurrogate(ch) && char.IsLowSurrogate(peekChar())) {
                    readChar();
                }
                readChar();
            }

            return input.Substring(lastPos, position - lastPos);
        }

        /// <summary>A number literal, as written (the parser converts it): decimal digits with an optional fraction
        /// (<c>1.5</c>) and exponent (<c>1e-9</c>), then an optional suffix <c>f</c> (float) or <c>d</c> (double); without a
        /// suffix a fraction or exponent makes a double, else an integer. <c>0x</c> / <c>0b</c> start hexadecimal and binary
        /// integers. <c>_</c> may separate digits (<c>1_000_000</c>, <c>0xFF_FF</c>). The suffix <c>L</c> makes integer digits a
        /// 64-bit 長整數 / long (<c>5000000000L</c>, <c>0xFFL</c>).</summary>
        private string readNumber(out string numberType) {
            int lastPos = position;
            numberType = TokenType.INT;
            if (ch == '0' && peekChar() is 'x' or 'X' or 'b' or 'B') {
                int radix = peekChar() is 'x' or 'X' ? 16 : 2;
                readChar(); readChar();
                if (!readDigits(radix)) numberType = TokenType.ILLEGAL;
                else if (ch == 'L') { numberType = TokenType.LONG; readChar(); }
            } else {
                bool floating = false, valid = readDigits(10);
                if (ch == '.' && isDigit(peekChar())) { readChar(); valid &= readDigits(10); floating = true; }
                // `1.f` and `1.d` (a fraction without digits before a suffix) stay valid as before.
                else if (ch == '.' && peekChar() is 'f' or 'd' && !isIdentifierCharacter(peekChar(2))) { readChar(); floating = true; }
                if (ch is 'e' or 'E' && (isDigit(peekChar()) || peekChar() is '+' or '-' && isDigit(peekChar(2)))) {
                    readChar(); if (ch is '+' or '-') readChar();
                    valid &= readDigits(10); floating = true;
                }
                if (ch == 'L' && !floating) { numberType = TokenType.LONG; readChar(); }
                else if (ch == 'f') { numberType = TokenType.FLOAT; readChar(); }
                else if (ch == 'd') { numberType = TokenType.DOUBLE; readChar(); }
                else if (floating) numberType = TokenType.DOUBLE;
                if (!valid) numberType = TokenType.ILLEGAL;
            }
            // A number runs into no letter or digit: `0xFG`, `0b102`, `1.5x` and `2_` are not numbers.
            if (isIdentifierCharacter(ch) || isDigit(ch)) {
                while (isIdentifierCharacter(ch) || isDigit(ch)) readChar();
                numberType = TokenType.ILLEGAL;
            }
            string literal = input.Substring(lastPos, position - lastPos);
            if (numberType == TokenType.ILLEGAL)
                lexicalErrors.Add(new LexicalError($"無效的數字 {literal} / Invalid number literal {literal}.", lastPos, position - lastPos));
            return literal;
        }

        /// <summary>Digits of a radix with single <c>_</c> separators between them; whether at least one digit was read and
        /// every separator stood between two digits.</summary>
        private bool readDigits(int radix) {
            bool any = false, valid = true;
            while (true) {
                if (isRadixDigit(ch, radix)) { any = true; readChar(); }
                else if (ch == '_' && isRadixDigit(peekChar(), radix)) { valid &= any; readChar(); }
                else break;
            }
            return any && valid;
        }

        private static bool isRadixDigit(char c, int radix) =>
            radix == 2 ? c is '0' or '1' : radix == 16 ? c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F' : c is >= '0' and <= '9';

        private bool isIdentifierCharacter(char c) => c == '_' || char.IsLetter(c);

        private char peekChar(int ahead) {
            int at = position + ahead;
            return at < input.Length ? input[at] : (char)0;
        }

        private bool isIdentifierStart() {
            if (ch == '_') return true;
            if (position >= input.Length) return false;
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(input, position);
            return category == UnicodeCategory.UppercaseLetter
                || category == UnicodeCategory.LowercaseLetter
                || category == UnicodeCategory.TitlecaseLetter
                || category == UnicodeCategory.ModifierLetter
                || category == UnicodeCategory.OtherLetter
                || category == UnicodeCategory.LetterNumber;
        }

        private bool isIdentifierPart() {
            if (isIdentifierStart() || isDigit(ch)) return true;
            if (position >= input.Length) return false;
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(input, position);
            return category == UnicodeCategory.NonSpacingMark
                || category == UnicodeCategory.SpacingCombiningMark;
        }

        private bool isDigit(char ch) {
            // return ch is >= '0' and <= '9';
            return ch >= '0' && ch <= '9';
        }

        private Token newToken(string tokenType, string literal) {
            return new Token() { Type = tokenType, Literal = literal };
        }

        private Token newToken(string tokenType, char ch) {
            return new Token() { Type = tokenType, Literal = char.ToString(ch) };
        }
    }
}
