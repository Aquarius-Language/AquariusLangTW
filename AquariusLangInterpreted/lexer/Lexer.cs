using System.Globalization;
using AquariusLang.token;

namespace AquariusLang.lexer {

    public class Lexer {
        private string? input;
        private int position; // current position in input (points to current char)
        private int readPosition; // current reading position in input (after current char)
        private char ch; // current char under examination

        /// <summary>
        /// Singleton.
        /// </summary>
        /// <param name="input"></param>
        public static Lexer NewInstance(string? input) {
            Lexer l = new Lexer() { input = input };
            l.readChar();
            return l;
        }

        public Token NextToken() {
            Token token = new Token() {
                Type = TokenType.ILLEGAL,
                Literal = ""
            };

            /*
             * There might be whitespaces before and after comments.
             */
            skipWhitespace();

            switch (ch) {
                case '#':
                    skipComments();
                    return NextToken();

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
                    } else {
                        token = newToken(TokenType.LT, ch);
                    }

                    break;
                case '>':
                    if (peekChar() == '=') {
                        readChar();
                        string literal = ">=";
                        token = newToken(TokenType.GT_ET, literal);
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
                    token = newToken(TokenType.STRING, readString());
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
                    char peekedChar = peekChar();
                    if (peekedChar == '&') {
                        readChar();
                        string literal = "&&";
                        token = newToken(TokenType.AND_AND, literal);
                    } else if (peekedChar == ' ') {
                        // TODO Implement Binary And '&' feature.
                    }

                    break;
                case '|':
                    char _peekedChar = peekChar();
                    if (_peekedChar == '|') {
                        readChar();
                        string literal = "||";
                        token = newToken(TokenType.OR_OR, literal);
                    } else if (_peekedChar == ' ') {
                        // TODO Implement Binary Or '|' feature.
                    }

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
                        return token;
                    } else if (isDigit(ch)) { // Check if is number. (int, float, double...)
                        string literal = readNumber(out string numberType);
                        token = newToken(numberType, literal);
                        return token;
                    } else {
                        token = newToken(TokenType.ILLEGAL, ch);
                    }

                    break;
            }

            readChar();
            return token;
        }

        private string readString() {
            int lastPos = position + 1;
            while (true) {
                readChar();
                if (ch == '"' || ch == 0) {
                    break;
                }
            }

            return input.Substring(lastPos, position - lastPos);
        }

        private void skipComments() {
            if (ch == '#') {
                if (peekChar() != '#') {
                    while (ch != '\n' && ch != 0) {
                        readChar();
                    }
                } else {
                    readChar();
                    readChar();
                    while (ch != '#' && peekChar() != '#') {
                        readChar();
                    }

                    readChar();
                    readChar();
                    readChar();
                }
            }
        }

        private void skipWhitespace() {
            // while (ch is ' ' or '\t' or '\n' or '\r') {
            //     readChar();
            // }
            while (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r') {
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

        private string readNumber(out string numberType) {
            int lastPos = position;

            bool noDotEncounter = true;
            while (isDigit(ch) || (ch == '.' && noDotEncounter)) {
                if (ch == '.') {
                    noDotEncounter = false;
                }

                readChar();
            }

            if (noDotEncounter) {
                numberType = TokenType.INT;
            } else {
                switch (ch) {
                    case 'f':
                        numberType = TokenType.FLOAT;
                        break;
                    case 'd':
                        numberType = TokenType.DOUBLE;
                        break;
                    default:
                        numberType = TokenType.ILLEGAL;
                        break;
                }

                readChar();
            }

            return input.Substring(lastPos, position - lastPos);
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
