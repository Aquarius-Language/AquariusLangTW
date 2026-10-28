using System.Collections.Generic;
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
            EQUALS = 5,
            LESS_GREATER = 6,
            SUM = 7,
            PRODUCT = 8,
            PREFIX = 9,
            DOT = 10, // For accessing functions in module.
            CALL = 11,
            INDEX = 12, //  array[index].
            POSTFIX = 13,
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
        public IReadOnlyList<ParseError> Diagnostics => diagnostics;
        private void addError(string message, Token token) {
            errors.Add(message);
            diagnostics.Add(new ParseError(message, token));
        }

        /// <summary>
        /// curToken and peekToken act exactly like the two “pointers” our lexer has: position and peekPosition.
        /// But instead of pointing to a character in the input, they point to the current and the next token.
        /// </summary>
        private Token currToken;
        private Token peekToken;

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
            parser.registerPrefix(TokenType.BANG, parser.parsePrefixExpression);
            parser.registerPrefix(TokenType.MINUS, parser.parsePrefixExpression);
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
            switch (currToken.Type) {
                case TokenType.LET:
                    return parseLetStatement();
                case TokenType.RETURN:
                    return parseReturnStatement();
                case TokenType.BREAK:
                    return parseBreakStatement();
                // 結構 / struct is a keyword only where a declaration starts (two names in a row are no expression),
                // so programs using these words as names keep working.
                case TokenType.IDENT when currToken.Literal is "結構" or "struct" && peekTokenIs(TokenType.IDENT):
                    return parseStructStatement();
                default:
                    return parseExpressionStatement();
            }
        }

        private LetStatement parseLetStatement() {
            LetStatement statement = new LetStatement(currToken);

            if (!expectPeek(TokenType.IDENT)) {
                return null;
            }

            statement.Name = new Identifier(currToken, currToken.Literal);

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

            if (statement.Alias != null && !(statement.Value is FunctionLiteral))
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

        /// <summary><c>結構 名稱 { 欄位: 型別; 欄位 }</c>: fields separated by semicolons or commas, annotations optional.</summary>
        private StructStatement parseStructStatement() {
            var statement = new StructStatement(currToken);
            nextToken();
            statement.Name = new Identifier(currToken, currToken.Literal);
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
                throw new System.InvalidOperationException("Expression nesting exceeds the limit of 256.");
            }
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

            IExpression leftExp = prefixParseFn();

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
            return new Identifier(currToken, currToken.Literal);
        }

        private IExpression parseIntegerLiteral() {
            IntegerLiteral integerLiteral = new IntegerLiteral(currToken);
            bool parseSuccess = int.TryParse(currToken.Literal, out int value);
            if (!parseSuccess) {
                string msg = $"Could not parse{currToken.Literal} as integer.";
                addError(msg, currToken);
                return null;
            }

            integerLiteral.Value = value;
            return integerLiteral;
        }

        private IExpression parseFloatLiteral() {
            FloatLiteral floatLiteral = new FloatLiteral(currToken);
            float? value = Utils.StringToFloat(currToken.Literal);
            if (value != null) {
                floatLiteral.Value = (float)value;
                return floatLiteral;
            }

            string msg = $"Could not parse{currToken.Literal} as float.";
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

            string msg = $"Could not parse{currToken.Literal} as double.";
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
            nextToken();
            IExpression expression = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);

            if (!expectPeek(TokenType.RPAREN)) {
                return null;
            }

            return expression;
        }

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
            ForLoopLiteral forLoopLiteral = new ForLoopLiteral(currToken);
            if (!expectPeek(TokenType.LPAREN)) {
                return null;
            }

            if (!expectPeek(TokenType.LET)) return null;
            forLoopLiteral.DeclareStatement = parseLetStatement();
            if (!currTokenIs(TokenType.SEMICOLON)) {
                addError("Expected ; after loop variable declaration.", currToken);
                return null;
            }
            nextToken();
            forLoopLiteral.ConditionalExpression = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
            if (!expectPeek(TokenType.SEMICOLON)) return null;
            nextToken();
            forLoopLiteral.ValueChangeStatement = parseStatement();
            if (!expectPeek(TokenType.RPAREN)) return null;
            if (!expectPeek(TokenType.LBRACE)) return null;
            forLoopLiteral.Body = parseBlockStatement();

            return forLoopLiteral;
        }

        private IExpression parseFunctionLiteral() {
            FunctionLiteral functionLiteral = new FunctionLiteral(currToken);
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

            functionLiteral.Body = parseBlockStatement();

            return functionLiteral;
        }

        private Identifier[] parseFunctionParameters() {
            List<Identifier> identifiers = new List<Identifier>();
            if (peekTokenIs(TokenType.RPAREN)) {
                nextToken();
                return identifiers.ToArray();
            }

            nextToken();
            if (!currTokenIs(TokenType.IDENT)) {
                addError("Expected a parameter name.", currToken);
                return null;
            }
            Identifier identifier = new Identifier(currToken, currToken.Literal);
            identifiers.Add(identifier);
            if (peekTokenIs(TokenType.COLON) && (identifier.Type = parseTypeAnnotation()) == null) return null;

            while (peekTokenIs(TokenType.COMMA)) {
                nextToken();
                nextToken();
                if (!currTokenIs(TokenType.IDENT)) {
                    addError("Expected a parameter name.", currToken);
                    return null;
                }
                identifier = new Identifier(currToken, currToken.Literal);
                identifiers.Add(identifier);
                if (peekTokenIs(TokenType.COLON) && (identifier.Type = parseTypeAnnotation()) == null) return null;
            }

            if (!expectPeek(TokenType.RPAREN)) {
                return null;
            }

            return identifiers.ToArray();
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
            // Arguments, all positional or all named (名稱: 值, a struct construction).
            var arguments = new List<IExpression>(); var names = new List<string>();
            if (peekTokenIs(TokenType.RPAREN)) { nextToken(); callExpression.Arguments = arguments.ToArray(); return callExpression; }
            do {
                nextToken();
                var argument = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
                if (argument is Identifier name && peekTokenIs(TokenType.COLON)) {
                    if (arguments.Count > names.Count) { addError("具名與位置引數不能混用 / Named and positional arguments cannot be mixed.", name.Token); return null; }
                    if (names.Contains(name.Value)) addError($"引數 {name.Value} 重複 / Duplicate argument {name.Value}.", name.Token);
                    nextToken(); nextToken();
                    names.Add(name.Value);
                    argument = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
                } else if (names.Count > 0) { addError("具名與位置引數不能混用 / Named and positional arguments cannot be mixed.", currToken); return null; }
                arguments.Add(argument);
            } while (peekTokenIs(TokenType.COMMA) && advance());
            if (!expectPeek(TokenType.RPAREN)) return null;
            callExpression.Arguments = arguments.ToArray();
            if (names.Count > 0) callExpression.ArgumentNames = names.ToArray();
            return callExpression;
        }
        private bool advance() { nextToken(); return true; }

        private IExpression parseArrayLiteral() {
            ArrayLiteral array = new ArrayLiteral(currToken);
            array.Elements = parseExpressionList(TokenType.RBRACKET);
            return array;
        }

        private IExpression[] parseExpressionList(string endTokenType, bool checkEndToken = true) {
            List<IExpression> list = new List<IExpression>();

            if (peekTokenIs(endTokenType)) {
                nextToken();
                return list.ToArray();
            }

            nextToken();
            list.Add(parseExpression((int)Precedence.OperatorPrecedence.LOWEST));

            while (peekTokenIs(TokenType.COMMA)) {
                nextToken();
                nextToken();
                list.Add(parseExpression((int)Precedence.OperatorPrecedence.LOWEST));
            }

            if (checkEndToken) {
                if (!expectPeek(endTokenType)) {
                    return null;
                }
            }

            return list.ToArray();
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
