using System.Collections.Generic;
using System.Linq;
using AquariusLang.ast;
using AquariusLang.lexer;
using AquariusLang.token;
using AquariusLang.utils;

namespace AquariusLang.parser {

    public sealed class ParseError {
        public string Message { get; }
        public Token Token { get; }
        public ParseError(string message, Token token) { Message = message; Token = token; }
    }

    public static class Precedence {
        /// <summary>
        /// These are operator precedence.
        /// Here we use iota to give the following constants incrementing numbers as values.
        /// </summary>
        public enum OperatorPrecedence {
            LOWEST = 1,
            PLUS_EQ = 2,
            ASSIGN = 3,
            AND_AND = 4,
            // Integer bitwise operators bind like C# and JavaScript: | below ^ below & below equality, shifts between
            // comparisons and sums.
            BIT_OR = 5,
            BIT_XOR = 6,
            BIT_AND = 7,
            EQUALS = 8,
            LESS_GREATER = 9,
            SHIFT = 10,
            SUM = 11,
            PRODUCT = 12,
            PREFIX = 13,
            DOT = 14, // For accessing functions in module.
            CALL = 15,
            INDEX = 16, //  array[index].
            POSTFIX = 17,
        }

        /// <summary>
        /// Mapping operator token types to precedence. This table is useful for helper methods
        /// peekPrecedence() and  curPrecedence().
        /// </summary>
        private static Dictionary<string, OperatorPrecedence> precedencesMap = new Dictionary<string, OperatorPrecedence>() {
            { TokenType.ASSIGN, OperatorPrecedence.ASSIGN },
            { TokenType.EQ, OperatorPrecedence.EQUALS },
            { TokenType.NOT_EQ, OperatorPrecedence.EQUALS },
            { TokenType.LT, OperatorPrecedence.LESS_GREATER },
            { TokenType.GT, OperatorPrecedence.LESS_GREATER },
            { TokenType.LT_ET, OperatorPrecedence.LESS_GREATER },
            { TokenType.GT_ET, OperatorPrecedence.LESS_GREATER },
            { TokenType.PLUS, OperatorPrecedence.SUM },
            { TokenType.MINUS, OperatorPrecedence.SUM },
            { TokenType.SLASH, OperatorPrecedence.PRODUCT },
            { TokenType.ASTERISK, OperatorPrecedence.PRODUCT },
            { TokenType.PERCENT, OperatorPrecedence.PRODUCT },
            { TokenType.LPAREN, OperatorPrecedence.CALL },
            { TokenType.LBRACKET, OperatorPrecedence.INDEX },
            { TokenType.PLUS_EQ, OperatorPrecedence.PLUS_EQ },
            { TokenType.MINUS_EQ, OperatorPrecedence.PLUS_EQ },
            { TokenType.ASTERISK_EQ, OperatorPrecedence.PLUS_EQ },
            { TokenType.SLASH_EQ, OperatorPrecedence.PLUS_EQ },
            { TokenType.PERCENT_EQ, OperatorPrecedence.PLUS_EQ },
            { TokenType.AND_AND, OperatorPrecedence.AND_AND },
            { TokenType.OR_OR, OperatorPrecedence.AND_AND },
            { TokenType.BIT_OR, OperatorPrecedence.BIT_OR },
            { TokenType.BIT_XOR, OperatorPrecedence.BIT_XOR },
            { TokenType.BIT_AND, OperatorPrecedence.BIT_AND },
            { TokenType.SHIFT_LEFT, OperatorPrecedence.SHIFT },
            { TokenType.SHIFT_RIGHT, OperatorPrecedence.SHIFT },
            { TokenType.SHIFT_RIGHT_UNSIGNED, OperatorPrecedence.SHIFT },
            { TokenType.BIT_AND_EQ, OperatorPrecedence.PLUS_EQ },
            { TokenType.BIT_OR_EQ, OperatorPrecedence.PLUS_EQ },
            { TokenType.BIT_XOR_EQ, OperatorPrecedence.PLUS_EQ },
            { TokenType.SHIFT_LEFT_EQ, OperatorPrecedence.PLUS_EQ },
            { TokenType.SHIFT_RIGHT_EQ, OperatorPrecedence.PLUS_EQ },
            { TokenType.SHIFT_RIGHT_UNSIGNED_EQ, OperatorPrecedence.PLUS_EQ },
            { TokenType.DOT, OperatorPrecedence.DOT },
            { TokenType.INCREMENT, OperatorPrecedence.POSTFIX },
        };

        public static int PrecedenceFor(string tokenType) {
            bool hasValue = precedencesMap.TryGetValue(tokenType, out OperatorPrecedence value);
            return hasValue ? (int)value : -1;
        }
    }

    /// <summary>
    /// A Pratt parser’s main idea is the association of parsing functions (which Pratt calls “semantic
    /// code”) with token types. Whenever this token type is encountered, the parsing functions are
    /// called to parse the appropriate expression and return an AST node that represents it. Each
    /// token type can have up to two parsing functions associated with it, depending on whether the
    /// token is found in a prefix or an infix position.
    /// 
    /// A prefix operator is an operator “in front of” its operand. Example: --5
    /// An infix operator sits between its operands, like this: 5 * 8
    /// Increment supports both prefix (++i) and postfix (i++) forms.
    /// 
    /// The infixParseFn takes an argument: another ast.Expression. This argument is “left side” of
    /// the infix operator that’s being parsed.
    /// 
    /// prefixParseFns gets called when we encounter the associated token
    /// type in prefix position and infixParseFn gets called when we encounter the token type in infix
    /// position.
    /// </summary>
    public delegate IExpression PrefixParseFn();

    public delegate IExpression InfixParseFn(IExpression expression);


    public class Parser {
        private Lexer lexer;
        private List<string> errors;
        private readonly List<ParseError> diagnostics = new List<ParseError>();
        private int expressionDepth;
        private bool tooDeep;
        public IReadOnlyList<ParseError> Diagnostics => diagnostics;
        private void addError(string message, Token token) {
            if (tooDeep) return;
            errors.Add(message);
            diagnostics.Add(new ParseError(message, token));
        }

        /// <summary>
        /// curToken and peekToken act exactly like the two “pointers” our lexer has: position and peekPosition.
        /// But instead of pointing to a character in the input, they point to the current and the next token.
        /// </summary>
        private Token currToken;
        private Token peekToken;
        private Token previousToken;
        // Whether each enclosing function literal is async, innermost on top; empty at the top level of a script.
        private readonly Stack<bool> functions = new Stack<bool>();

        /// <summary>
        /// In order for our parser to get the correct prefixParseFn or infixParseFn for the current token type.
        /// </summary>
        private Dictionary<string, PrefixParseFn> prefixParseFns;
        private Dictionary<string, InfixParseFn> infixParseFns;

        /// <summary>
        /// Singleton.
        /// </summary>
        /// <returns>Parser instance.</returns>
        public static Parser NewInstance(Lexer lexer) {
            Parser parser = new Parser() { lexer = lexer, errors = new List<string>(), };

            parser.prefixParseFns = new Dictionary<string, PrefixParseFn>();
            parser.registerPrefix(TokenType.IDENT, parser.parseIdentifier);
            parser.registerPrefix(TokenType.INT, parser.parseIntegerLiteral);
            parser.registerPrefix(TokenType.FLOAT, parser.parseFloatLiteral);
            parser.registerPrefix(TokenType.DOUBLE, parser.parseDoubleLiteral);
            parser.registerPrefix(TokenType.LONG, parser.parseLongLiteral);
            parser.registerPrefix(TokenType.BANG, parser.parsePrefixExpression);
            parser.registerPrefix(TokenType.MINUS, parser.parsePrefixExpression);
            parser.registerPrefix(TokenType.BIT_NOT, parser.parsePrefixExpression);
            parser.registerPrefix(TokenType.INCREMENT, parser.parsePrefixIncrementExpression);
            parser.registerPrefix(TokenType.TRUE, parser.parseBoolean);
            parser.registerPrefix(TokenType.FALSE, parser.parseBoolean);
            parser.registerPrefix(TokenType.NULL, parser.parseNull);
            parser.registerPrefix(TokenType.LPAREN, parser.parseGroupedExpression);
            parser.registerPrefix(TokenType.IF, parser.parseIfExpression);
            parser.registerPrefix(TokenType.FOR, parser.parseForLoopLiteral);
            parser.registerPrefix(TokenType.FUNCTION, parser.parseFunctionLiteral);
            parser.registerPrefix(TokenType.STRING, parser.parseStringLiteral);
            parser.registerPrefix(TokenType.LBRACKET, parser.parseArrayLiteral);
            parser.registerPrefix(TokenType.LBRACE, parser.parseHashLiteral);

            parser.infixParseFns = new Dictionary<string, InfixParseFn>();
            parser.registerInfix(TokenType.PLUS, parser.parseInfixExpression);
            parser.registerInfix(TokenType.MINUS, parser.parseInfixExpression);
            parser.registerInfix(TokenType.SLASH, parser.parseInfixExpression);
            parser.registerInfix(TokenType.ASTERISK, parser.parseInfixExpression);
            parser.registerInfix(TokenType.PERCENT, parser.parseInfixExpression);
            parser.registerInfix(TokenType.EQ, parser.parseInfixExpression);
            parser.registerInfix(TokenType.NOT_EQ, parser.parseInfixExpression);
            parser.registerInfix(TokenType.LT, parser.parseInfixExpression);
            parser.registerInfix(TokenType.GT, parser.parseInfixExpression);
            parser.registerInfix(TokenType.LT_ET, parser.parseInfixExpression);
            parser.registerInfix(TokenType.GT_ET, parser.parseInfixExpression);
            parser.registerInfix(TokenType.ASSIGN, parser.parseInfixExpression);

            parser.registerInfix(TokenType.LPAREN, parser.parseCallExpression);
            parser.registerInfix(TokenType.LBRACKET, parser.parseIndexExpression);
            parser.registerInfix(TokenType.PLUS_EQ, parser.parseInfixExpression);
            parser.registerInfix(TokenType.MINUS_EQ, parser.parseInfixExpression);
            parser.registerInfix(TokenType.ASTERISK_EQ, parser.parseInfixExpression);
            parser.registerInfix(TokenType.SLASH_EQ, parser.parseInfixExpression);
            parser.registerInfix(TokenType.PERCENT_EQ, parser.parseInfixExpression);
            parser.registerInfix(TokenType.AND_AND, parser.parseInfixExpression);
            parser.registerInfix(TokenType.OR_OR, parser.parseInfixExpression);
            foreach (string bitwise in new[] { TokenType.BIT_AND, TokenType.BIT_OR, TokenType.BIT_XOR, TokenType.SHIFT_LEFT, TokenType.SHIFT_RIGHT,
                         TokenType.SHIFT_RIGHT_UNSIGNED, TokenType.BIT_AND_EQ, TokenType.BIT_OR_EQ, TokenType.BIT_XOR_EQ, TokenType.SHIFT_LEFT_EQ,
                         TokenType.SHIFT_RIGHT_EQ, TokenType.SHIFT_RIGHT_UNSIGNED_EQ })
                parser.registerInfix(bitwise, parser.parseInfixExpression);
            parser.registerInfix(TokenType.DOT, parser.parseInfixExpression);
            parser.registerInfix(TokenType.INCREMENT, parser.parsePostfixIncrementExpression);

            // Read two tokens, so curToken and peekToken are both set.
            parser.nextToken();
            parser.nextToken();

            return parser;
        }

        public AbstractSyntaxTree ParseAST() {
            List<IStatement> statements = new List<IStatement>();
            while (!currTokenIs(TokenType.EOF)) {
                IStatement statement = parseStatement();
                if (statement != null) {
                    statements.Add(statement);
                }

                nextToken();
            }

            AbstractSyntaxTree ast = new AbstractSyntaxTree(statements.ToArray());
            return ast;
        }

        private void nextToken() {
            previousToken = currToken;
            currToken = peekToken;
            peekToken = lexer.NextToken();
        }

        private bool currTokenIs(string tokenType) {
            return currToken.Type == tokenType;
        }

        private bool peekTokenIs(string tokenType) {
            return peekToken.Type == tokenType;
        }

        /// <summary>
        /// Peek and move to next token only if the next token matches expected.
        /// </summary>
        /// <param name="tokenType"></param>
        /// <returns></returns>
        private bool expectPeek(string tokenType) {
            if (peekTokenIs(tokenType)) {
                nextToken();
                return true;
            }

            peekError(tokenType);
            return false;
        }

        /// <summary>
        /// This function is important for parsers. All errors of incorrect next token are found during this function.
        ///
        /// Errors get printed and appended to errors[] list here.
        ///
        /// This only gets called from expectPeek() function. As all possible errors of incorrect next token
        /// can be caught when peeked during parsing.
        /// </summary>
        /// <param name="tokenType"></param>
        private void peekError(string tokenType) {
            string msg = $"Expected next token to be {tokenType}, got {peekToken.Type} instead.";
            addError(msg, peekToken);
        }

        private void noPrefixParseFnError(Token token) {
            string msg = $"No prefix parse function for {token.Type}({token.Literal}) found.";
            addError(msg, token);
        }

        private IStatement parseStatement() {
            int start = currToken.Start;
            IStatement statement = parseStatementCore();
            // Statements end at their last token (a declaration's value; a closing semicolon is not part of it).
            SourceRanges.Set(statement, start, lastEnd());
            return statement;
        }
        // The end offset of the current token, or of the one before when the current one is a semicolon.
        private int lastEnd() {
            var token = currToken.Type == TokenType.SEMICOLON && previousToken.Length > 0 ? previousToken : currToken;
            return token.Start + token.Length;
        }

        private IStatement parseStatementCore() {
            switch (currToken.Type) {
                case TokenType.LET:
                    return parseLetStatement();
                case TokenType.RETURN:
                    return parseReturnStatement();
                case TokenType.BREAK:
                    return parseBreakStatement();
                case TokenType.CONTINUE: {
                    var statement = new ContinueStatement(currToken);
                    if (peekTokenIs(TokenType.SEMICOLON)) nextToken();
                    return statement;
                }
                // 結構 / struct is a keyword only where a declaration starts (two names in a row are no expression),
                // so programs using these words as names keep working.
                case TokenType.IDENT when currToken.Literal is "結構" or "struct" && peekTokenIs(TokenType.IDENT):
                    return parseStructStatement();
                // 常數 / const likewise: a declaration like 變數 / var whose binding is never written again.
                case TokenType.IDENT when currToken.Literal is "常數" or "const" && peekTokenIs(TokenType.IDENT):
                    return parseConstantStatement();
                // 匯出 / export likewise: only before a declaration (直接 or after ("name")), so it stays a name elsewhere.
                case TokenType.IDENT when currToken.Literal is "匯出" or "export" && exportStarts():
                    return parseExportStatement();
                default:
                    return parseExpressionStatement();
            }
        }

        private LetStatement parseConstantStatement() {
            var constant = parseLetStatement();
            if (constant != null) constant.Constant = true;
            return constant;
        }

        /// <summary>Whether a token (the one after <c>匯出</c>, or after its <c>("name")</c>) starts a declaration the keyword
        /// can mark: 變數 / var, or 常數 / const and 結構 / struct followed by a name (<paramref name="next"/>).</summary>
        private static bool declarationStarts(Token token, Token next) =>
            token.Type == TokenType.LET
            || token.Type == TokenType.IDENT && token.Literal is "常數" or "const" or "結構" or "struct" && next.Type == TokenType.IDENT;

        private bool exportStarts() {
            if (declarationStarts(peekToken, lexer.Peek())) return true;
            return peekTokenIs(TokenType.LPAREN) && lexer.Peek().Type == TokenType.STRING && lexer.Peek(2).Type == TokenType.RPAREN
                && declarationStarts(lexer.Peek(3), lexer.Peek(4));
        }

        /// <summary><c>匯出 宣告</c> or <c>匯出("name") 宣告</c>: marks a top-level function or struct for native library outputs
        /// (the TypeChecker enforces where and what may be exported).</summary>
        private IStatement parseExportStatement() {
            Token keyword = currToken;
            string? name = null;
            if (peekTokenIs(TokenType.LPAREN)) {
                nextToken(); nextToken();
                name = currToken.Literal;
                nextToken();
            }
            nextToken();
            if (currToken.Type == TokenType.LET || currToken.Literal is "常數" or "const") {
                var declaration = currToken.Type == TokenType.LET ? parseLetStatement() : parseConstantStatement();
                if (declaration == null) return null;
                declaration.Export = new ExportMark(keyword, name);
                // An exported binding is never written again, whichever keyword declared it.
                declaration.Constant = true;
                return declaration;
            }
            var shape = parseStructStatement();
            if (shape != null) shape.Export = new ExportMark(keyword, name);
            return shape;
        }

        private LetStatement parseLetStatement() {
            LetStatement statement = new LetStatement(currToken);

            if (!expectPeek(TokenType.IDENT)) {
                return null;
            }

            statement.Name = new Identifier(currToken, currToken.Literal);
            declared(statement.Name.Value);

            if (peekTokenIs(TokenType.COLON)) {
                statement.Name.Type = parseTypeAnnotation();
                if (statement.Name.Type == null) return null;
            } else if (peekTokenIs(TokenType.COMMA)) {
                nextToken();
                if (!expectPeek(TokenType.IDENT)) return null;
                statement.Alias = new Identifier(currToken, currToken.Literal);
                if (statement.Alias.Value == statement.Name.Value)
                    addError("Function names must be distinct.", currToken);
            }

            if (!expectPeek(TokenType.ASSIGN)) {
                return null;
            }

            nextToken();

            statement.Value = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);

            if (statement.Alias != null && !(statement.Value is FunctionLiteral or AsyncFunctionExpression))
                addError("Two names can only be declared for a function literal.", statement.Alias.Token);

            /*
             * Advance the current token if the next token is semicolon.
             * (When the scope returns back to ParseProgram(), nextToken() gets called again, so the
             * semicolon gets skipped in the end.)
             */
            if (peekTokenIs(TokenType.SEMICOLON)) {
                nextToken();
            }

            return statement;
        }

        /// <summary><c>結構 名稱 { 欄位: 型別; 欄位 }</c>: fields separated by semicolons or commas, annotations optional; a
        /// second name may follow the first (<c>結構 點, Point { ... }</c>), like a function's.</summary>
        private StructStatement parseStructStatement() {
            var statement = new StructStatement(currToken);
            nextToken();
            statement.Name = new Identifier(currToken, currToken.Literal);
            if (peekTokenIs(TokenType.COMMA)) {
                nextToken();
                if (!expectPeek(TokenType.IDENT)) return null;
                statement.Alias = new Identifier(currToken, currToken.Literal);
                if (statement.Alias.Value == statement.Name.Value) addError("結構的兩個名稱必須不同 / A struct's two names must be distinct.", currToken);
            }
            if (!expectPeek(TokenType.LBRACE)) return null;
            var fields = new List<Identifier>();
            while (!peekTokenIs(TokenType.RBRACE)) {
                if (!expectPeek(TokenType.IDENT)) return null;
                var field = new Identifier(currToken, currToken.Literal);
                if (fields.Exists(f => f.Value == field.Value)) addError($"欄位 {field.Value} 重複 / Duplicate field {field.Value}.", currToken);
                if (peekTokenIs(TokenType.COLON)) {
                    field.Type = parseTypeAnnotation();
                    if (field.Type == null) return null;
                }
                if (peekTokenIs(TokenType.ASSIGN) && (field.Default = parseDefault()) == null) return null;
                fields.Add(field);
                if (peekTokenIs(TokenType.SEMICOLON) || peekTokenIs(TokenType.COMMA)) nextToken();
                else if (!peekTokenIs(TokenType.RBRACE)) { expectPeek(TokenType.RBRACE); return null; }
            }
            nextToken();
            statement.Fields = fields.ToArray();
            if (peekTokenIs(TokenType.SEMICOLON)) nextToken();
            return statement;
        }

        private ReturnStatement parseReturnStatement() {
            ReturnStatement statement = new ReturnStatement(currToken);
            // A bare `return;` (or `return }`) returns null, as in the languages scripts are written after.
            if (peekTokenIs(TokenType.SEMICOLON) || peekTokenIs(TokenType.RBRACE)) {
                statement.ReturnValue = new NullLiteral(currToken);
                if (peekTokenIs(TokenType.SEMICOLON)) nextToken();
                return statement;
            }
            nextToken();
            statement.ReturnValue = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);

            if (peekTokenIs(TokenType.SEMICOLON)) {
                nextToken();
            }

            return statement;
        }

        private BreakStatement parseBreakStatement() {
            BreakStatement statement = new BreakStatement(currToken);
            if (peekTokenIs(TokenType.SEMICOLON)) {
                nextToken();
            }

            return statement;
        }

        private ExpressionStatement parseExpressionStatement() {
            ExpressionStatement statement = new ExpressionStatement(currToken);
            /*
             * In parseExpressionStatement we pass the lowest possible precedence to parseExpression, since
             * we didn’t parse anything yet and we can’t compare precedences.
             */
            // A statement that starts with 如果 / if or 迴圈 / for ends at its closing brace, so a next statement starting
            // with (, [, - or ++ is its own statement, not a call, index, subtraction or increment of the block's value.
            statement.Expression = currTokenIs(TokenType.IF) ? parseIfExpression()
                : currTokenIs(TokenType.FOR) ? parseForLoopLiteral()
                : parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
            if (peekTokenIs(TokenType.SEMICOLON)) {
                nextToken();
            }

            return statement;
        }

        private IExpression parseExpression(int precedence) {
            if (expressionDepth >= 256) {
                // One error, then the rest of the program parses to nothing, so the deep nesting unwinds without more.
                addError("運算式巢狀超過 256 層 / Expression nesting exceeds the limit of 256.", currToken);
                tooDeep = true;
                return null;
            }
            if (tooDeep) return null;
            expressionDepth++;
            try { return parseExpressionCore(precedence); }
            finally { expressionDepth--; }
        }

        private IExpression parseExpressionCore(int precedence) {
            // Console.WriteLine($"Get prefixParseFns for currToken.Type: {currToken.Type}");
            bool hasKey = prefixParseFns.TryGetValue(currToken.Type, out PrefixParseFn prefixParseFn);

            // Console.WriteLine($"prefixParseFn: {prefixParseFn}");

            if (prefixParseFn == null) {
                noPrefixParseFnError(currToken);
                return null;
            }

            int start = currToken.Start;
            IExpression leftExp = prefixParseFn();
            // Every expression records its extent, from its first token to its last (runtime errors underline it).
            SourceRanges.Set(leftExp, start, currToken.Start + currToken.Length);

            // Console.WriteLine($"leftExp: {leftExp.String()}");


            while (!peekTokenIs(TokenType.SEMICOLON) && precedence < peekPrecedence()) {
                InfixParseFn infixParseFn = infixParseFns[peekToken.Type];
                if (infixParseFn == null) {
                    return leftExp;
                }

                nextToken();
                /*
                 * Basically calls either parseInfixExpression() or parseCallExpression().
                 * These two functions are the current only ones bound to infixParseFns.
                 */
                leftExp = infixParseFn(leftExp);
                SourceRanges.Set(leftExp, start, currToken.Start + currToken.Length);
            }

            return leftExp;
        }

        /// <summary>
        /// Return lowest precedence if the table didn't find matching token type.
        /// </summary>
        /// <returns></returns>
        private int peekPrecedence() {
            int precedence = Precedence.PrecedenceFor(peekToken.Type);
            return (precedence != -1) ? precedence : (int)Precedence.OperatorPrecedence.LOWEST;
        }

        private int currPrecedence() {
            int precedence = Precedence.PrecedenceFor(currToken.Type);
            return (precedence != -1) ? precedence : (int)Precedence.OperatorPrecedence.LOWEST;
        }

        /// <summary>
        /// Identifier expression parsing examples:
        ///
        ///     add(foobar, barfoo); // identifiers as arguments in a function call
        ///     foobar + barfoo; // as operands in an infix expression
        ///
        ///     if (foobar) { // as a standalone expression as part of a conditional
        ///         // [...]
        ///     }
        /// </summary>
        /// <returns></returns>
        private IExpression parseIdentifier() {
            // 非同步 / async and 等待 / await are keywords only where they start an async function or an await (a name
            // followed by an operand is no other expression), so programs using these words as names keep working. A
            // member name after `.` (執行緒.等待) is always a name.
            if (previousToken.Type != TokenType.DOT) {
                if ((currToken.Literal is "非同步" or "async" && peekTokenIs(TokenType.FUNCTION))
                    || (currToken.Literal == "非同步函式" && peekTokenIs(TokenType.LPAREN))) {
                    Token keyword = currToken;
                    if (currToken.Literal != "非同步函式") nextToken();
                    return parseFunction(true) is FunctionLiteral function ? new AsyncFunctionExpression(keyword, function) : null;
                }
                if (currToken.Literal is "等待" or "await" && startsAwaitOperand()) return parseAwaitExpression();
                // x => x * 2: a short function of one parameter.
                if (peekTokenIs(TokenType.ARROW)) {
                    var parameter = new Identifier(currToken, currToken.Literal);
                    declared(parameter.Value);
                    return parseArrowFunction(currToken, new[] { parameter });
                }
            }
            return new Identifier(currToken, currToken.Literal);
        }

        private bool insideAsync => functions.Count > 0 && functions.Peek();

        // 等待 / await declared as a variable or parameter earlier in the source.
        private readonly HashSet<string> awaitNames = new HashSet<string>();
        private void declared(string name) { if (name is "等待" or "await") awaitNames.Add(name); }

        // The token after 等待 / await begins its operand. A parenthesized or array operand counts too, unless the word
        // was declared as a name outside an async function: then `等待(...)` and `等待[...]` call or index it.
        private bool startsAwaitOperand() => peekToken.Type switch {
            TokenType.IDENT or TokenType.INT or TokenType.FLOAT or TokenType.DOUBLE or TokenType.LONG or TokenType.STRING or TokenType.TRUE
                or TokenType.FALSE or TokenType.NULL or TokenType.FUNCTION or TokenType.BANG => true,
            TokenType.LPAREN or TokenType.LBRACKET => insideAsync || !awaitNames.Contains(currToken.Literal),
            _ => false
        };

        private IExpression parseAwaitExpression() {
            Token keyword = currToken;
            if (functions.Count > 0 && !functions.Peek())
                addError("等待 只能用在非同步函式或腳本最上層 / await is only valid inside an async function or at the top level of a script.", keyword);
            nextToken();
            IExpression operand = parseExpression((int)Precedence.OperatorPrecedence.PREFIX);
            return operand == null ? null : new AwaitExpression(keyword, operand);
        }

        private IExpression parseIntegerLiteral() {
            IntegerLiteral integerLiteral = new IntegerLiteral(currToken);
            if (Utils.StringToInteger(currToken.Literal) is not int value) {
                // A larger integer is most likely meant as a 長整數 / long: suggest its literal.
                string suggestion = Utils.StringToLong(currToken.Literal + "L") != null ? $"；更大的整數請用 長整數：{currToken.Literal}L" : "";
                string hint = suggestion.Length > 0 ? $"; for a larger integer use a long: {currToken.Literal}L" : "";
                string msg = $"整數 {currToken.Literal} 超出 32 位元範圍{suggestion} / Could not parse {currToken.Literal} as a 32-bit integer{hint}.";
                addError(msg, currToken);
                return null;
            }

            integerLiteral.Value = value;
            return integerLiteral;
        }

        private IExpression parseLongLiteral() {
            if (Utils.StringToLong(currToken.Literal) is long value) return new LongLiteral(currToken, value);
            addError($"長整數 {currToken.Literal} 超出 64 位元範圍 / Could not parse {currToken.Literal} as a 64-bit long.", currToken);
            return null;
        }

        private IExpression parseFloatLiteral() {
            FloatLiteral floatLiteral = new FloatLiteral(currToken);
            float? value = Utils.StringToFloat(currToken.Literal);
            if (value != null) {
                floatLiteral.Value = (float)value;
                return floatLiteral;
            }

            string msg = $"Could not parse {currToken.Literal} as float.";
            addError(msg, currToken);
            return null;
        }

        private IExpression parseDoubleLiteral() {
            DoubleLiteral doubleLiteral = new DoubleLiteral(currToken);
            double? value = Utils.StringToDouble(currToken.Literal);
            if (value != null) {
                doubleLiteral.Value = (double)value;
                return doubleLiteral;
            }

            string msg = $"Could not parse {currToken.Literal} as double.";
            addError(msg, currToken);
            return null;
        }


        /// <summary>
        /// Prefix operators:
        ///     -5;
        ///     !foobar;
        ///     5 + -10;
        /// 
        /// The structure of their usage is the following:
        ///     <prefix operator><expression>;
        /// 
        /// Any expression can follow a prefix operator as operand. These are valid:
        /// 
        ///     !isGreaterThanZero(2);
        ///     5 + -add(5, 5);
        /// 
        /// That means that an AST node for a prefix operator expression has to be flexible enough to
        /// point to any expression as its operand.
        /// </summary>
        /// <returns></returns>
        private IExpression parsePrefixExpression() {
            // The smallest integer, -2147483648, is written with a minus sign before a literal one past the largest.
            if (currTokenIs(TokenType.MINUS) && peekTokenIs(TokenType.INT) && peekToken.Literal.Replace("_", "") == "2147483648") {
                nextToken();
                return new IntegerLiteral(currToken) { Value = int.MinValue };
            }
            if (currTokenIs(TokenType.MINUS) && peekTokenIs(TokenType.LONG) && peekToken.Literal.Replace("_", "") == "9223372036854775808L") {
                nextToken();
                return new LongLiteral(currToken, long.MinValue);
            }
            PrefixExpression expression = new PrefixExpression(currToken, currToken.Literal);
            nextToken();
            expression.Right = parseExpression((int)Precedence.OperatorPrecedence.PREFIX);
            return expression;
        }

        /// <summary>
        /// Infix operator examples:
        ///
        ///     5 + 5;
        ///     5 - 5;
        ///     5 * 5;
        ///     5 / 5;
        ///     5 > 5;
        ///     5 < 5;
        ///     5 == 5;
        ///     5 != 5;
        /// 
        /// As with prefix operator expressions, we can use any expressions to the left and right of the operator:
        ///     <expression> <infix operator> <expression>
        /// 
        /// Because of the two operands (left and right) these expressions are sometimes called “binary expressions”.
        /// </summary>
        /// <param name="leftExpr"></param>
        /// <returns></returns>
        private IExpression parseInfixExpression(IExpression leftExpr) {
            InfixExpression expression = new InfixExpression(currToken, leftExpr, currToken.Literal);
            int precedence = currPrecedence();
            nextToken();
            expression.Right = parseExpression(precedence);
            return expression.Operator == "." ? indexMember(expression) : expression;
        }

        /// <summary>
        /// Indexing and postfix ++ bind tighter than member access, so <c>表.欄位[0][1]</c> first parses as a member whose right side is an
        /// index chain. It means indexing the member, innermost index first: rebuild it as <c>((表.欄位)[0])[1]</c>, so
        /// reads, assignments and compound assignments see an ordinary index expression.
        /// </summary>
        private static IExpression indexMember(InfixExpression member) {
            // A postfix ++ binds tighter than member access too: `點.x++` parses as `點.(x++)` and means `(點.x)++`.
            if (member.Right is IncrementExpression { IsPrefix: false } increment) {
                member.Right = increment.Operand; increment.Operand = indexMember(member);
                return increment;
            }
            var indices = new Stack<IndexExpression>();
            while (member.Right is IndexExpression indexed) { indices.Push(indexed); member.Right = indexed.Left; }
            IExpression result = member;
            while (indices.Count > 0) { var indexed = indices.Pop(); indexed.Left = result; result = indexed; }
            return result;
        }

        private IExpression parsePrefixIncrementExpression() {
            Token token = currToken;
            nextToken();
            IExpression operand = parseExpression((int)Precedence.OperatorPrecedence.PREFIX);
            return createIncrementExpression(token, operand, true);
        }

        private IExpression parsePostfixIncrementExpression(IExpression operand) {
            return createIncrementExpression(currToken, operand, false);
        }

        private IExpression createIncrementExpression(Token token, IExpression operand, bool isPrefix) {
            // A variable, an element (陣列[i]++) or a field (點.x++); a postfix ++ after a member is moved out of it by indexMember.
            if (!(operand is Identifier || operand is IndexExpression || operand is InfixExpression { Operator: ".", Right: Identifier })) {
                addError("++ 的運算元必須是變數、欄位或索引 / The operand of ++ must be a variable, a field or an index.", currToken);
                return null;
            }
            return new IncrementExpression(token, operand, isPrefix);
        }

        private IExpression parseBoolean() {
            return new BooleanLiteral(currToken, currTokenIs(TokenType.TRUE));
        }

        private IExpression parseNull() => new NullLiteral(currToken);

        /// <summary>
        /// This will be the magical function that makes parsing grouped expressions "()" correct
        /// and influence their precedence and thus the order in which they are evaluated in their context.
        /// </summary>
        /// <returns></returns>
        private IExpression parseGroupedExpression() {
            Token open = currToken;
            // () => ...: a short function without parameters.
            if (peekTokenIs(TokenType.RPAREN)) {
                nextToken();
                if (!peekTokenIs(TokenType.ARROW)) { peekError(TokenType.ARROW); return null; }
                return parseArrowFunction(open, System.Array.Empty<Identifier>());
            }
            nextToken();
            IExpression expression = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);

            // (a, b) => ..., (a: int) => ... and (a = 1) => ...: a parenthesized expression has no comma or annotation, and
            // one followed by => is a parameter list.
            if (peekTokenIs(TokenType.COMMA) || peekTokenIs(TokenType.COLON) || peekTokenIs(TokenType.RPAREN) && arrowAfterParenthesis()) {
                var parameters = new List<Identifier>();
                if (!(expression is Identifier or InfixExpression { Operator: "=", Left: Identifier })) {
                    addError("短函式的參數必須是名稱 / The parameters of a short function must be names.", open);
                    return null;
                }
                var first = expression as Identifier ?? (Identifier)((InfixExpression)expression).Left;
                var firstDefault = (expression as InfixExpression)?.Right;
                first = new Identifier(first.Token, first.Value);
                if (!addParameter(parameters, first)) return null;
                if (firstDefault != null) first.Default = firstDefault;
                while (peekTokenIs(TokenType.COMMA)) {
                    nextToken();
                    if (peekTokenIs(TokenType.RPAREN)) break;
                    if (!expectPeek(TokenType.IDENT)) return null;
                    if (!addParameter(parameters, new Identifier(currToken, currToken.Literal))) return null;
                }
                if (!expectPeek(TokenType.RPAREN)) return null;
                if (!peekTokenIs(TokenType.ARROW)) { peekError(TokenType.ARROW); return null; }
                return parseArrowFunction(open, parameters.ToArray());
            }

            if (!expectPeek(TokenType.RPAREN)) {
                return null;
            }

            return expression;
        }

        /// <summary>Whether <c>)</c> (the peek token) is followed by <c>=&gt;</c>. The lexer is copied, so nothing is consumed.</summary>
        private bool arrowAfterParenthesis() => lexer.Peek().Type == TokenType.ARROW;

        /// <summary>
        /// If expression example in Monkey:
        ///
        ///    if (x > y) {
        ///        return x;
        ///    } else {
        ///        return y;
        ///    }
        ///
        /// Else is optional:
        /// 
        ///     if (x > y) {
        ///         return x;
        ///     }
        /// 
        /// No need for return statements here:
        /// 
        ///     let foobar = if (x > y) { x } else { y };
        /// 
        /// Structure:
        /// 
        ///     if (<condition>) <consequence> else <alternative>
        /// </summary>
        /// <returns></returns>
        private IExpression parseIfExpression() {
            IfExpression expression = new IfExpression(currToken);
            if (!expectPeek(TokenType.LPAREN)) {
                return null;
            }

            nextToken();
            expression.Condition = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
            if (!expectPeek(TokenType.RPAREN)) {
                return null;
            }

            return parseIfRest(expression);
        }

        /// <summary>The rest of an if expression after its condition's <c>)</c>: the block and its alternatives.</summary>
        private IExpression parseIfRest(IfExpression expression) {
            if (!expectPeek(TokenType.LBRACE)) {
                return null;
            }

            expression.Consequence = parseBlockStatement();

            List<IExpression> alternativeConditions = new List<IExpression>();
            List<BlockStatement> alternatives = new List<BlockStatement>();

            // Both spellings share the same branch representation. Keeping "else"
            // and "if" as separate tokens preserves source ranges and allows comments
            // or mixed-language keywords between them.
            while (peekTokenIs(TokenType.ELSE_IF) || peekTokenIs(TokenType.ELSE)) {
                nextToken();
                if (currTokenIs(TokenType.ELSE)) {
                    if (peekTokenIs(TokenType.LBRACE)) {
                        nextToken();
                        expression.LastResort = parseBlockStatement();
                        break;
                    }
                    if (!expectPeek(TokenType.IF)) return null;
                }
                if (!expectPeek(TokenType.LPAREN)) {
                    return null;
                }

                nextToken();
                alternativeConditions.Add(parseExpression((int)Precedence.OperatorPrecedence.LOWEST));
                if (!expectPeek(TokenType.RPAREN)) {
                    return null;
                }

                if (!expectPeek(TokenType.LBRACE)) {
                    return null;
                }

                alternatives.Add(parseBlockStatement());
            }

            if (alternatives.Count > 0) {
                expression.AlternativeConditions = alternativeConditions.ToArray();
                expression.Alternatives = alternatives.ToArray();
            }

            return expression;
        }

        private BlockStatement parseBlockStatement() {
            BlockStatement block = new BlockStatement(currToken);
            nextToken();
            List<IStatement> statements = new List<IStatement>();
            while (!currTokenIs(TokenType.RBRACE) && !currTokenIs(TokenType.EOF)) {
                IStatement statement = parseStatement();
                if (statement != null) {
                    statements.Add(statement);
                }

                nextToken();
            }

            block.Statements = statements.ToArray();
            if (currTokenIs(TokenType.EOF)) addError("Expected closing } before end of file.", currToken);
            return block;
        }

        private IExpression parseStringLiteral() {
            return new StringLiteral(currToken, currToken.Literal);
        }

        private IExpression parseForLoopLiteral() {
            var loop = parseForHeader();
            if (loop == null || !expectPeek(TokenType.LBRACE)) return null;
            loop.Body = parseBlockStatement();
            return loop;
        }

        /// <summary>A loop's <c>迴圈 (宣告; 條件; 更新)</c>, leaving its <c>)</c> current; the caller parses the body.</summary>
        private ForLoopLiteral parseForHeader() {
            ForLoopLiteral forLoopLiteral = new ForLoopLiteral(currToken);
            if (!expectPeek(TokenType.LPAREN)) {
                return null;
            }

            // Each of the three parts may be left out: 迴圈 (; 條件;) { ... } and 迴圈 (;;) { ... 中斷; }.
            if (peekTokenIs(TokenType.SEMICOLON)) nextToken();
            else {
                if (!expectPeek(TokenType.LET)) return null;
                forLoopLiteral.DeclareStatement = parseLetStatement();
                if (!currTokenIs(TokenType.SEMICOLON)) {
                    addError("Expected ; after loop variable declaration.", currToken);
                    return null;
                }
            }
            nextToken();
            if (!currTokenIs(TokenType.SEMICOLON)) {
                forLoopLiteral.ConditionalExpression = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
                if (!expectPeek(TokenType.SEMICOLON)) return null;
            }
            if (!peekTokenIs(TokenType.RPAREN)) {
                nextToken();
                forLoopLiteral.ValueChangeStatement = parseStatement();
            }
            if (!expectPeek(TokenType.RPAREN)) return null;
            return forLoopLiteral;
        }

        private IExpression parseFunctionLiteral() => parseFunction(false);

        private IExpression parseFunction(bool isAsync) {
            FunctionLiteral functionLiteral = new FunctionLiteral(currToken) { IsAsync = isAsync };
            if (!expectPeek(TokenType.LPAREN)) {
                return null;
            }

            functionLiteral.Parameters = parseFunctionParameters();
            if (functionLiteral.Parameters == null) return null;

            if (peekTokenIs(TokenType.COLON)) {
                functionLiteral.ReturnType = parseTypeAnnotation();
                if (functionLiteral.ReturnType == null) return null;
            }

            if (!expectPeek(TokenType.LBRACE)) {
                return null;
            }

            functions.Push(isAsync);
            try { functionLiteral.Body = parseBlockStatement(); }
            finally { functions.Pop(); }

            return functionLiteral;
        }

        /// <summary>A parameter list after its <c>(</c>: names with optional annotations and defaults (<c>名稱: 型別 = 值</c>),
        /// a trailing comma allowed; leaves the <c>)</c> current.</summary>
        private Identifier[] parseFunctionParameters() {
            List<Identifier> identifiers = new List<Identifier>();
            while (!peekTokenIs(TokenType.RPAREN)) {
                nextToken();
                if (!currTokenIs(TokenType.IDENT)) {
                    addError("Expected a parameter name.", currToken);
                    return null;
                }
                if (!addParameter(identifiers, new Identifier(currToken, currToken.Literal))) return null;
                if (!peekTokenIs(TokenType.COMMA)) break;
                nextToken();
            }

            if (!expectPeek(TokenType.RPAREN)) {
                return null;
            }

            return identifiers.ToArray();
        }

        /// <summary>Adds a parameter whose name is the current token, then its annotation and default when they follow.
        /// Once a parameter has a default, every later one needs one too, so leaving out arguments leaves out the last ones.</summary>
        private bool addParameter(List<Identifier> identifiers, Identifier identifier) {
            declared(identifier.Value);
            if (identifiers.Exists(p => p.Value == identifier.Value))
                addError($"參數 {identifier.Value} 重複 / Duplicate parameter {identifier.Value}.", identifier.Token);
            identifiers.Add(identifier);
            if (peekTokenIs(TokenType.COLON) && (identifier.Type = parseTypeAnnotation()) == null) return false;
            if (peekTokenIs(TokenType.ASSIGN) && (identifier.Default = parseDefault()) == null) return false;
            if (identifier.Default != null && identifiers.Count(p => p.Default != null) > 63)
                addError("一個函式最多 63 個參數有預設值 / A function has at most 63 parameters with defaults.", identifier.Token);
            if (identifier.Default == null && identifiers.Count > 1 && identifiers[^2].Default != null)
                addError($"參數 {identifier.Value} 需要預設值：有預設值的參數之後都要有預設值 / Parameter {identifier.Value} needs a default: every parameter after one with a default has one.", identifier.Token);
            return true;
        }

        /// <summary>The default value after a parameter or field (<c>= 值</c>), with the <c>=</c> as the peek token.</summary>
        private IExpression parseDefault() {
            nextToken();
            nextToken();
            return parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
        }

        /// <summary>A short function after its parameter list, with the <c>=&gt;</c> as the peek token: <c>(a, b) =&gt; a + b</c>
        /// returns the expression's value, <c>() =&gt; { ... }</c> runs a block like a function body (a hash result is written
        /// in parentheses: <c>() =&gt; ({"a": 1})</c>).</summary>
        private IExpression parseArrowFunction(Token token, Identifier[] parameters) {
            nextToken();
            var function = new FunctionLiteral(token) { Parameters = parameters };
            functions.Push(false);
            try {
                if (peekTokenIs(TokenType.LBRACE)) { nextToken(); function.Body = parseBlockStatement(); return function; }
                nextToken();
                var body = new ExpressionStatement(currToken) { Expression = parseExpression((int)Precedence.OperatorPrecedence.LOWEST) };
                if (body.Expression == null) return null;
                function.Body = new BlockStatement(token) { Statements = new IStatement[] { body } };
                return function;
            } finally { functions.Pop(); }
        }

        /// <summary>
        /// Optional type annotation after a declared name, a parameter or a parameter list: <c>: 整數</c>, <c>: double[]</c>,
        /// <c>: 陣列</c> (an array of any values).
        /// Called with the colon as the peek token; leaves the last token of the type current.
        /// </summary>
        private TypeAnnotation parseTypeAnnotation() {
            nextToken();
            if (!expectPeek(TokenType.IDENT)) return null;
            Token name = currToken;
            if (TypeAnnotation.IsArrayName(name.Literal)) {
                if (peekTokenIs(TokenType.LBRACKET)) {
                    addError("陣列 已是陣列型別，元素可為任何值 / array is already an array type whose elements may be any values.", name);
                    return null;
                }
                return new TypeAnnotation(name, 0, true);
            }
            if (!TypeAnnotation.TryParseName(name.Literal, out ScalarType element)) {
                // Any other name is a struct type, resolved by the TypeChecker (an unknown one is reported there).
                if (peekTokenIs(TokenType.LBRACKET)) {
                    addError($"結構陣列請用 陣列 標註 / Annotate an array of {name.Literal} values as array (陣列).", name);
                    return null;
                }
                return new TypeAnnotation(name, 0, false) { StructName = name.Literal };
            }
            bool array = false;
            if (peekTokenIs(TokenType.LBRACKET)) {
                nextToken();
                if (!expectPeek(TokenType.RBRACKET)) return null;
                array = true;
            }
            return new TypeAnnotation(name, element, array);
        }

        private IExpression parseCallExpression(IExpression function) {
            CallExpression callExpression = new CallExpression(currToken, function);
            // Positional arguments, then named ones (名稱: 值): a struct's fields or a function's parameters by name. A
            // trailing comma is allowed.
            var arguments = new List<IExpression>(); var names = new List<string>(); bool named = false;
            while (!peekTokenIs(TokenType.RPAREN)) {
                nextToken();
                var argument = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
                if (argument is Identifier name && peekTokenIs(TokenType.COLON)) {
                    if (names.Contains(name.Value)) addError($"引數 {name.Value} 重複 / Duplicate argument {name.Value}.", name.Token);
                    nextToken(); nextToken();
                    names.Add(name.Value); named = true;
                    argument = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
                } else if (named) { addError("位置引數必須寫在具名引數之前 / Positional arguments come before named arguments.", currToken); return null; }
                else names.Add("");
                arguments.Add(argument);
                if (!peekTokenIs(TokenType.COMMA)) break;
                nextToken();
            }
            if (!expectPeek(TokenType.RPAREN)) return null;
            callExpression.Arguments = arguments.ToArray();
            // The positional arguments have the empty name.
            if (named) callExpression.ArgumentNames = names.ToArray();
            return callExpression;
        }

        private IExpression parseArrayLiteral() {
            ArrayLiteral array = new ArrayLiteral(currToken);
            var elements = new List<IExpression>();
            while (!peekTokenIs(TokenType.RBRACKET)) {
                nextToken();
                var element = parseElement();
                if (element == null) return null;
                elements.Add(element);
                if (!peekTokenIs(TokenType.COMMA)) break;
                nextToken();
            }
            if (!expectPeek(TokenType.RBRACKET)) return null;
            array.Elements = elements.ToArray();
            return array;
        }

        /// <summary>An element of an array literal: an expression, <c>...陣列</c> (its elements), <c>如果 (條件) 元素 否則 元素</c>
        /// or <c>迴圈 (...) 元素</c> (the element for each iteration). An <c>如果</c> or <c>迴圈</c> followed by a block is the
        /// ordinary expression.</summary>
        private IExpression parseElement() {
            if (currTokenIs(TokenType.ELLIPSIS)) {
                Token token = currToken;
                nextToken();
                var value = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
                return value == null ? null : new SpreadElement(token, value);
            }
            if (currTokenIs(TokenType.IF) || currTokenIs(TokenType.ELSE_IF)) {
                Token token = currToken;
                if (!expectPeek(TokenType.LPAREN)) return null;
                nextToken();
                var condition = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
                if (!expectPeek(TokenType.RPAREN)) return null;
                if (peekTokenIs(TokenType.LBRACE) && token.Type == TokenType.IF) return parseIfRest(new IfExpression(token) { Condition = condition });
                var element = new CollectionIf(token) { Condition = condition };
                nextToken();
                if ((element.Then = parseElement()) == null) return null;
                if (peekTokenIs(TokenType.ELSE_IF)) { nextToken(); return (element.Else = parseElement()) == null ? null : element; }
                if (peekTokenIs(TokenType.ELSE)) { nextToken(); nextToken(); if ((element.Else = parseElement()) == null) return null; }
                return element;
            }
            if (currTokenIs(TokenType.FOR)) {
                var loop = parseForHeader();
                if (loop == null) return null;
                if (peekTokenIs(TokenType.LBRACE)) { nextToken(); loop.Body = parseBlockStatement(); return loop; }
                nextToken();
                var element = parseElement();
                if (element == null) return null;
                loop.Body = new BlockStatement(loop.Token) { Statements = new IStatement[] { new ExpressionStatement(loop.Token, element) } };
                return new CollectionFor(loop.Token) { Loop = loop, Element = element };
            }
            return parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
        }

        private IExpression parseIndexExpression(IExpression left) {
            IndexExpression expression = new IndexExpression(currToken, left);
            nextToken();
            expression.Index = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
            if (!expectPeek(TokenType.RBRACKET)) {
                return null;
            }

            return expression;
        }

        private IExpression parseHashLiteral() {
            HashLiteral hashLiteral = new HashLiteral(currToken, new Dictionary<IExpression, IExpression>());
            while (!peekTokenIs(TokenType.RBRACE)) {
                nextToken();
                IExpression key = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
                if (!expectPeek(TokenType.COLON)) {
                    return null;
                }

                nextToken();
                IExpression value = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
                hashLiteral.Pairs[key] = value;

                if (!peekTokenIs(TokenType.RBRACE) && !expectPeek(TokenType.COMMA)) {
                    return null;
                }
            }

            if (!expectPeek(TokenType.RBRACE)) {
                return null;
            }

            return hashLiteral;
        }

        private void registerPrefix(string tokenType, PrefixParseFn fn) {
            prefixParseFns[tokenType] = fn;
        }

        private void registerInfix(string tokenType, InfixParseFn fn) {
            infixParseFns[tokenType] = fn;
        }

        public List<string> Errors => errors;
    }
}
