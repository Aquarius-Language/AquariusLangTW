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
            { TokenType.LPAREN, OperatorPrecedence.CALL },
            { TokenType.LBRACKET, OperatorPrecedence.INDEX },
            { TokenType.PLUS_EQ, OperatorPrecedence.PLUS_EQ },
            { TokenType.MINUS_EQ, OperatorPrecedence.PLUS_EQ },
            { TokenType.ASTERISK_EQ, OperatorPrecedence.PLUS_EQ },
            { TokenType.SLASH_EQ, OperatorPrecedence.PLUS_EQ },
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

            if (peekTokenIs(TokenType.COMMA)) {
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
            statement.Expression = parseExpression((int)Precedence.OperatorPrecedence.LOWEST);
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
            return expression;
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
            if (!(operand is Identifier)) {
                addError("++ 的運算元必須是變數名稱。", currToken);
                return null;
            }
            return new IncrementExpression(token, operand, isPrefix);
        }

        private IExpression parseBoolean() {
            return new BooleanLiteral(currToken, currTokenIs(TokenType.TRUE));
        }

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

            while (peekTokenIs(TokenType.ELSE_IF)) { // "elif"s are optional.
                nextToken();
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

            if (peekTokenIs(TokenType.ELSE)) { // "else" is optional.
                nextToken();
                if (!expectPeek(TokenType.LBRACE)) {
                    return null;
                }

                expression.LastResort = parseBlockStatement();
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

            while (peekTokenIs(TokenType.COMMA)) {
                nextToken();
                nextToken();
                if (!currTokenIs(TokenType.IDENT)) {
                    addError("Expected a parameter name.", currToken);
                    return null;
                }
                identifier = new Identifier(currToken, currToken.Literal);
                identifiers.Add(identifier);
            }

            if (!expectPeek(TokenType.RPAREN)) {
                return null;
            }

            return identifiers.ToArray();
        }

        private IExpression parseCallExpression(IExpression function) {
            CallExpression callExpression = new CallExpression(currToken, function);
            callExpression.Arguments = parseExpressionList(TokenType.RPAREN);
            return callExpression;
        }

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
