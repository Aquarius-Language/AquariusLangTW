using AquariusLang.ast;
using AquariusLang.lexer;
using AquariusLang.token;
using Xunit;
using Xunit.Abstractions;

namespace AquariusLang.parser; 

public class ParserTest {
    /// <summary>
    /// For logging outputs during testing.
    /// </summary>
    private readonly ITestOutputHelper _testOutputHelper;
    
    public ParserTest(ITestOutputHelper testOutputHelper) {
        _testOutputHelper = testOutputHelper;
    }
    
    struct LetStatementTest {
        public string input;
        public string expectedIdentifier;
        public object expectedValue;
    }
    [Fact]
    public void TestLetStatements() {
        LetStatementTest[] tests = new[] {
            new LetStatementTest() { input = "變數 x = 5;", expectedIdentifier = "x", expectedValue = 5 },
            new LetStatementTest() { input = "變數 y = 真;", expectedIdentifier = "y", expectedValue = true },
            new LetStatementTest() { input = "變數 foobar = y;", expectedIdentifier = "foobar", expectedValue = "y" }
        };
        foreach (var test in tests) {
            Lexer lexer = Lexer.NewInstance(test.input);
            Parser parser = Parser.NewInstance(lexer);
            AbstractSyntaxTree tree = parser.ParseAST();
            Assert.False(checkParserErrors(parser));

            if (tree.Statements.Length != 1) {
                _testOutputHelper.WriteLine($"tree.Statements does not contain 1 statements. Got={tree.Statements.Length}");
            }

            IStatement statement = tree.Statements[0];
            
            Assert.IsType<LetStatement>(statement);
            
            Assert.True(testLetStatement(statement, test.expectedIdentifier));

            IExpression value = ((LetStatement)statement).Value;
            Assert.True(testLiteralExpression(value, test.expectedValue));
        }
    }

    struct ReturnStatementTest {
        public string input;
        public object expectedValue;
    }
    [Fact]
    public void TestReturnStatements() {
        ReturnStatementTest[] tests = {
            new ReturnStatementTest() { input = "回傳 5;", expectedValue = 5 },
            new ReturnStatementTest() { input = "回傳 真;", expectedValue = true },
            new ReturnStatementTest() { input = "回傳 foobar;", expectedValue = "foobar" }
        };
        foreach (var test in tests) {
            Lexer lexer = Lexer.NewInstance(test.input);
            Parser parser = Parser.NewInstance(lexer);
            AbstractSyntaxTree tree = parser.ParseAST();
            if (tree.Statements.Length != 1) {
                _testOutputHelper.WriteLine($"tree.Statements does not contain 1 statement. Got={tree.Statements.Length}");
            }

            IStatement statement = tree.Statements[0];
            Assert.IsType(typeof(ReturnStatement), statement);

            ReturnStatement returnStatement = (ReturnStatement)statement;
            Assert.Equal("回傳", returnStatement.TokenLiteral());
            Assert.True(testLiteralExpression(returnStatement.ReturnValue, test.expectedValue));
        }
    }

    struct AndOrBoolTest {
        public string input;
        public string expected;
    }
    [Fact]
    public void TestAndOrBooleanOperations() {
        AndOrBoolTest[] tests = {
            new(){input = "真 || 假", expected = "(真 || 假)"},
            new(){input = "真 && 假", expected = "(真 && 假)"}
        };

        foreach (AndOrBoolTest test in tests) {
            Lexer lexer = Lexer.NewInstance(test.input);
            Parser parser = Parser.NewInstance(lexer);
            AbstractSyntaxTree tree = parser.ParseAST();
            
            Assert.Single(tree.Statements);
            Assert.IsType<ExpressionStatement>(tree.Statements[0]);
            Assert.Equal(test.expected, tree.String());
        }
    }

    [Fact]
    public void TestIdentifierExpression() {
        string input = "foobar;";
        
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        
        Assert.NotEqual(checkParserErrors(parser), true);

        Assert.Equal(1, tree.Statements.Length);
        Assert.IsType<ExpressionStatement>(tree.Statements[0]);
        
        ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];
        
        Assert.IsType<Identifier>(statement.Expression);
        
        Identifier identifier = (Identifier)statement.Expression;
        Assert.Equal("foobar", identifier.Value);
        Assert.Equal("foobar", identifier.TokenLiteral());
    }

    [Fact]
    public void TestIntegerLiteralExpression() {
        string input = "5;";
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        Assert.NotEqual(checkParserErrors(parser), true);
        
        Assert.Equal(tree.Statements.Length, 1);
        Assert.IsType(typeof(ExpressionStatement), tree.Statements[0]);
        
        ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];
        Assert.IsType(typeof(IntegerLiteral), statement.Expression);

        IntegerLiteral literal = (IntegerLiteral)statement.Expression;
        Assert.Equal(literal.Value, 5);
        Assert.Equal(literal.TokenLiteral(), "5");
    }

    struct PrefixExpressionsTest {
        public string input;
        public string _operator;
        public object value;
    }
    [Fact]
    public void TestParsingPrefixExpressions() {
        PrefixExpressionsTest[] tests = {
            new PrefixExpressionsTest() { input = "!5;", _operator = "!", value = 5 },
            new PrefixExpressionsTest() { input = "-15;", _operator = "-", value = 15 },
            new PrefixExpressionsTest() { input = "!foobar;", _operator = "!", value = "foobar" },
            new PrefixExpressionsTest() { input = "-foobar;", _operator = "-", value = "foobar" },
            new PrefixExpressionsTest() { input = "!真;", _operator = "!", value = true },
            new PrefixExpressionsTest() { input = "!假;", _operator = "!", value = false },
            new PrefixExpressionsTest() { input = "-2.84f;", _operator = "-", value = 2.84f },
            new PrefixExpressionsTest() { input = "-2.9d;", _operator = "-", value = 2.9 },
        };

        foreach (var test in tests) {
            Lexer lexer = Lexer.NewInstance(test.input);
            Parser parser = Parser.NewInstance(lexer);
            AbstractSyntaxTree tree = parser.ParseAST();
            
            Assert.False(checkParserErrors(parser));
            Assert.Single(tree.Statements);
            Assert.IsType<ExpressionStatement>(tree.Statements[0]);

            ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];
            Assert.IsType<PrefixExpression>(statement.Expression);
            
            PrefixExpression expression = (PrefixExpression)statement.Expression;
            Assert.Equal(expression.Operator, test._operator);
            
            Assert.True(testLiteralExpression(expression.Right, test.value));
        }
    }

    struct InfixExpressionsTest {
        public string input;
        public object leftValue;
        public string _operator;
        public object rightValue;
    }

    [Fact]
    public void TestParsingInfixExpressions() {
        InfixExpressionsTest[] tests = {
            new() { input = "5 + 5;", leftValue = 5, _operator = "+", rightValue = 5 },
            new() { input = "5 - 5;", leftValue = 5, _operator = "-", rightValue = 5 },
            new() { input = "5 * 5;", leftValue = 5, _operator = "*", rightValue = 5 },
            new() { input = "5 / 5;", leftValue = 5, _operator = "/", rightValue = 5 },
            new() { input = "5 > 5;", leftValue = 5, _operator = ">", rightValue = 5 },
            new() { input = "5 < 5;", leftValue = 5, _operator = "<", rightValue = 5 },
            new() { input = "5 == 5;", leftValue = 5, _operator = "==", rightValue = 5 },
            new() { input = "5 != 5;", leftValue = 5, _operator = "!=", rightValue = 5 },
            new() { input = "foobar + barfoo;", leftValue = "foobar", _operator = "+", rightValue = "barfoo" },
            new() { input = "foobar - barfoo;", leftValue = "foobar", _operator = "-", rightValue = "barfoo" },
            new() { input = "foobar * barfoo;", leftValue = "foobar", _operator = "*", rightValue = "barfoo" },
            new() { input = "foobar / barfoo;", leftValue = "foobar", _operator = "/", rightValue = "barfoo" },
            new() { input = "foobar > barfoo;", leftValue = "foobar", _operator = ">", rightValue = "barfoo" },
            new() { input = "foobar < barfoo;", leftValue = "foobar", _operator = "<", rightValue = "barfoo" },
            new() { input = "foobar == barfoo;", leftValue = "foobar", _operator = "==", rightValue = "barfoo" },
            new() { input = "foobar != barfoo;", leftValue = "foobar", _operator = "!=", rightValue = "barfoo" },
            new() { input = "真 == 真", leftValue = true, _operator = "==", rightValue = true },
            new() { input = "真 != 假", leftValue = true, _operator = "!=", rightValue = false },
            new() { input = "假 == 假", leftValue = false, _operator = "==", rightValue = false },
            new() { input = "3 <= 4", leftValue = 3, _operator = "<=", rightValue = 4 },
            new() { input = "8 >= 6", leftValue = 8, _operator = ">=", rightValue = 6 },
            new() { input = "真 && 假", leftValue = true, _operator = "&&", rightValue = false },
            new() { input = "假 || 真", leftValue = false, _operator = "||", rightValue = true },
            new() { input = "假 || 真", leftValue = false, _operator = "||", rightValue = true },
            // new() { input = "module.callFunc();", leftValue = new Identifier(new Token() {
            //     Type = TokenType.IDENT, Literal = "module",
            // }, "module"), _operator = ".", rightValue = new CallExpression(new Token() {
            //     Type = TokenType.FUNCTION, Literal = "callFunc()",
            // }, new ) },
        };
        foreach (var test in tests) {
            Lexer lexer = Lexer.NewInstance(test.input);
            Parser parser = Parser.NewInstance(lexer);
            AbstractSyntaxTree tree = parser.ParseAST();
            
            Assert.False(checkParserErrors(parser));
            Assert.Single(tree.Statements);
            Assert.IsType<ExpressionStatement>(tree.Statements[0]);

            ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];
            Assert.True(testInfixExpression(statement.Expression, test.leftValue, test._operator, test.rightValue));
        }
    }

    struct OperatorPrecedenceTest {
        public string input;
        public string expected;
    }
    [Fact]
    public void TestOperatorPrecedenceParsing() {
        OperatorPrecedenceTest[] tests = {
            new() { input = "-a * b", expected = "((-a) * b)", }, 
            new() { input = "!-a", expected = "(!(-a))", }, 
            new() { input = "a + b + c", expected = "((a + b) + c)", },
            new() { input = "a + b - c", expected = "((a + b) - c)", }, 
            new() { input = "a * b * c", expected = "((a * b) * c)", }, 
            new() { input = "a * b / c", expected = "((a * b) / c)", },
            new() { input = "a + b / c", expected = "(a + (b / c))", }, 
            new() { input = "a + b * c + d / e - f", expected = "(((a + (b * c)) + (d / e)) - f)", },
            new() { input = "3 + 4; -5 * 5", expected = "(3 + 4)((-5) * 5)", }, 
            new() { input = "5 > 4 == 3 < 4", expected = "((5 > 4) == (3 < 4))", },
            new() { input = "5 < 4 != 3 > 4", expected = "((5 < 4) != (3 > 4))", },
            new() { input = "3 + 4 * 5 == 3 * 1 + 4 * 5", expected = "((3 + (4 * 5)) == ((3 * 1) + (4 * 5)))", }, 
            new() { input = "真", expected = "真", },
            new() { input = "假", expected = "假", }, 
            new() { input = "3 > 5 == 假", expected = "((3 > 5) == 假)", },
            new() { input = "3 < 5 == 真", expected = "((3 < 5) == 真)", }, 
            new() { input = "1 + (2 + 3) + 4", expected = "((1 + (2 + 3)) + 4)", },
            new() { input = "(5 + 5) * 2", expected = "((5 + 5) * 2)", }, 
            new() { input = "2 / (5 + 5)", expected = "(2 / (5 + 5))", },
            new() { input = "(5 + 5) * 2 * (5 + 5)", expected = "(((5 + 5) * 2) * (5 + 5))", }, 
            new() { input = "-(5 + 5)", expected = "(-(5 + 5))", },
            new() { input = "!(真 == 真)", expected = "(!(真 == 真))", }, 
            new() { input = "a + add(b * c) + d", expected = "((a + add((b * c))) + d)", },
            new() { input = "add(a, b, 1, 2 * 3, 4 + 5, add(6, 7 * 8))", expected = "add(a, b, 1, (2 * 3), (4 + 5), add(6, (7 * 8)))", },
            new() { input = "add(a + b + c * d / f + g)", expected = "add((((a + b) + ((c * d) / f)) + g))", },
            new() { input = "a * [1, 2, 3, 4][b * c] * d", expected = "((a * ([1, 2, 3, 4][(b * c)])) * d)", },
            new() { input = "add(a * b[2], b[1], 2 * [1, 2][1])", expected = "add((a * (b[2])), (b[1]), (2 * ([1, 2][1])))", },
            new() { input = "a += 3 + 4", expected = "(a += (3 + 4))", },
            new() { input = "a <= 5 - 6", expected = "(a <= (5 - 6))", },
            new() { input = "a *= 5 - 6", expected = "(a *= (5 - 6))", },
            new() { input = "a /= 5 - 6", expected = "(a /= (5 - 6))", },
            new() { input = "真 && 5 < 6", expected = "(真 && (5 < 6))", },
            new() { input = "2.3f * (5 + 5.d)", expected = "(2.3f * (5 + 5.d))", },
        };
        foreach (var test in tests) {
            Lexer lexer = Lexer.NewInstance(test.input);
            Parser parser = Parser.NewInstance(lexer);
            AbstractSyntaxTree tree = parser.ParseAST();
            Assert.False(checkParserErrors(parser));
            string actual = tree.String();
            Assert.Equal(test.expected, actual);
        }
    }

    struct BooleanExpressionTest {
        public string input;
        public bool expectedBoolean;
    }

    [Fact]
    public void TestBooleanExpression() {
        BooleanExpressionTest[] tests = {
            new BooleanExpressionTest() { input = "真;", expectedBoolean = true },
            new BooleanExpressionTest() { input = "假;", expectedBoolean = false },
        };
        foreach (var test in tests) {
            Lexer lexer = Lexer.NewInstance(test.input);
            Parser parser = Parser.NewInstance(lexer);
            AbstractSyntaxTree tree = parser.ParseAST();
            Assert.NotEqual(checkParserErrors(parser), true);
            
            Assert.Equal(tree.Statements.Length, 1);
            Assert.IsType(typeof(ExpressionStatement), tree.Statements[0]);

            ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];
            Assert.IsType(typeof(BooleanLiteral), statement.Expression);

            BooleanLiteral booleanLiteral = (BooleanLiteral)statement.Expression;
            Assert.Equal(booleanLiteral.Value, test.expectedBoolean);
        }
    }

    [Fact]
    public void TestIfExpression() {
        string input = "如果 (x < y) { x }";
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        
        Assert.NotEqual(checkParserErrors(parser), true);
        
        Assert.IsType(typeof(ExpressionStatement), tree.Statements[0]);
        ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];
        
        Assert.IsType(typeof(IfExpression), statement.Expression);
        IfExpression expression = (IfExpression)statement.Expression;
        
        Assert.Equal(testInfixExpression(expression.Condition, "x", "<", "y"), true);
        Assert.Equal(expression.Consequence.Statements.Length, 1);
        
        Assert.IsType(typeof(ExpressionStatement), expression.Consequence.Statements[0]);
        ExpressionStatement consequenceStatement = (ExpressionStatement)expression.Consequence.Statements[0];
        Assert.Equal(testIdentifier(consequenceStatement.Expression, "x"), true);
        Assert.Null(expression.Alternatives);
    }

    [Fact]
    public void TestIfElseExpression() {
        string input = "如果 (x < y) { x } 否則 { y }";
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        Assert.False(checkParserErrors(parser));
        Assert.Single(tree.Statements);
        
        Assert.IsType<ExpressionStatement>(tree.Statements[0]);
        ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];
        
        Assert.IsType<IfExpression>(statement.Expression);
        IfExpression expression = (IfExpression)statement.Expression;
        
        Assert.True(testInfixExpression(expression.Condition, "x", "<", "y"));
        Assert.Single(expression.Consequence.Statements);
        
        Assert.IsType<ExpressionStatement>(expression.Consequence.Statements[0]);
        ExpressionStatement consequence = (ExpressionStatement)expression.Consequence.Statements[0];
        
        Assert.True(testIdentifier(consequence.Expression, "x"));
        
        Assert.Single(expression.LastResort.Statements);
        
        Assert.IsType<ExpressionStatement>(expression.LastResort.Statements[0]);
        ExpressionStatement alternative = (ExpressionStatement)expression.LastResort.Statements[0];
        Assert.True(testIdentifier(alternative.Expression, "y"));
    }

    [Fact]
    public void TestIfElifElseExpression() {
        string input =
            @"
            如果 (a <= b) {
                a;
            } 否則如果 (x == y) {
                y;
            } 否則如果 (x != y) {
                j;
            } 否則 {
                b;
            }
            ";
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        Assert.False(checkParserErrors(parser));
        Assert.Single(tree.Statements);
        
        Assert.IsType<ExpressionStatement>(tree.Statements[0]);
        ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];
        
        Assert.IsType<IfExpression>(statement.Expression);
        IfExpression expression = (IfExpression)statement.Expression;
        
        Assert.True(testInfixExpression(expression.Condition, "a", "<=", "b"));
        Assert.Single(expression.Consequence.Statements);
        
        Assert.IsType<ExpressionStatement>(expression.Consequence.Statements[0]);
        ExpressionStatement consequence = (ExpressionStatement)expression.Consequence.Statements[0];
        
        Assert.True(testIdentifier(consequence.Expression, "a"));

        Assert.NotNull(expression.AlternativeConditions);
        Assert.NotNull(expression.Alternatives);

        Assert.Equal(2, expression.Alternatives.Length);
        Assert.Equal(2, expression.AlternativeConditions.Length);

        Assert.True(testInfixExpression(expression.AlternativeConditions[0], "x", "==", "y"));
        Assert.IsType<ExpressionStatement>(expression.Alternatives[0].Statements[0]);
        ExpressionStatement alternative1 = (ExpressionStatement)expression.Alternatives[0].Statements[0];
        Assert.True(testIdentifier(alternative1.Expression, "y"));
        
        Assert.True(testInfixExpression(expression.AlternativeConditions[1], "x", "!=", "y"));
        Assert.IsType<ExpressionStatement>(expression.Alternatives[1].Statements[0]);
        ExpressionStatement alternative2 = (ExpressionStatement)expression.Alternatives[1].Statements[0];
        Assert.True(testIdentifier(alternative2.Expression, "j"));
        
        Assert.Single(expression.LastResort.Statements);
        
        Assert.IsType<ExpressionStatement>(expression.LastResort.Statements[0]);
        ExpressionStatement alternative = (ExpressionStatement)expression.LastResort.Statements[0];
        Assert.True(testIdentifier(alternative.Expression, "b"));
    }

    [Fact]
    public void TestFunctionLiteralParsing() {
        string input = "函式(x, y) { x + y; }";
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        Assert.False(checkParserErrors(parser));
        Assert.Single(tree.Statements);
        
        Assert.IsType<ExpressionStatement>(tree.Statements[0]);
        ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];
        
        Assert.IsType<FunctionLiteral>(statement.Expression);
        FunctionLiteral functionLiteral = (FunctionLiteral)statement.Expression;
        
        Assert.Equal(2, functionLiteral.Parameters.Length);
        
        Assert.True(testLiteralExpression(functionLiteral.Parameters[0], "x"));
        Assert.True(testLiteralExpression(functionLiteral.Parameters[1], "y"));
        
        Assert.Single(functionLiteral.Body.Statements);
        
        Assert.IsType<ExpressionStatement>(functionLiteral.Body.Statements[0]);
        ExpressionStatement bodyStatement = (ExpressionStatement)functionLiteral.Body.Statements[0];
        
        Assert.True(testInfixExpression(bodyStatement.Expression, "x", "+", "y"));
    }

    struct ForLoopLiteralTest {
        public string input;
        public string expected;
    }
    [Fact]
    public void TestForLoopLiteralParsing() {
        ForLoopLiteralTest[] tests = { 
            new () { 
                input = @"
                迴圈 (變數 i = 0; i < 5; i++) {
                    變數 a = 0;
                }
                ", 
                expected = "迴圈((變數 i = 0)(i < 5)(i++)){(變數 a = 0)}"
            }, 
            new () {
                input = @"
                        迴圈(變數 a = 5; a < 10; a++) {
                            迴圈 (變數 b = 0; b < 5; b++){
                                如果 (a > 6) {
                                    中斷;
                                }
                            }
                        } ",
                expected = "迴圈((變數 a = 5)(a < 10)(a++)){迴圈((變數 b = 0)(b < 5)(b++)){如果(a > 6) {(中斷)}}}"
            }
        };
        foreach (ForLoopLiteralTest test in tests) {
            Lexer lexer = Lexer.NewInstance(test.input);
            Parser parser = Parser.NewInstance(lexer);
            AbstractSyntaxTree tree = parser.ParseAST();
        
            Assert.NotNull(tree);
            Assert.NotNull(tree.Statements[0]);

        
            Assert.NotNull(tree.Statements[0]);
        
            Assert.IsType<ExpressionStatement>(tree.Statements[0]);
            ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];
            Assert.NotNull(statement);

            Assert.IsType<ForLoopLiteral>(statement.Expression);
            ForLoopLiteral forLoopLiteral = (ForLoopLiteral)statement.Expression;
            Assert.NotNull(forLoopLiteral);
        
            Assert.NotNull(forLoopLiteral.DeclareStatement);
            Assert.NotNull(forLoopLiteral.ConditionalExpression);
            Assert.NotNull(forLoopLiteral.ValueChangeStatement);
            Assert.NotNull(forLoopLiteral.Body);

            _testOutputHelper.WriteLine(forLoopLiteral.String());
        
            Assert.Equal(test.expected, forLoopLiteral.String());
        }

    }

    struct FunctionParameterTest {
        public string input;
        public string[] expectedParams;
    }

    [Fact]
    public void TestFunctionParameterParsing() {
        FunctionParameterTest[] tests = {
            new() { input = "函式() {};", expectedParams = new string[] { } },
            new() { input = "函式(x) {};", expectedParams = new string[] { "x" } },
            new() { input = "函式(x, y, z) {};", expectedParams = new string[] { "x", "y", "z" } },
        };
        foreach (var test in tests) {
            Lexer lexer = Lexer.NewInstance(test.input);
            Parser parser = Parser.NewInstance(lexer);
            AbstractSyntaxTree tree = parser.ParseAST();
            Assert.NotEqual(checkParserErrors(parser), true);
            
            Assert.IsType(typeof(ExpressionStatement), tree.Statements[0]);
            ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];
            
            Assert.IsType(typeof(FunctionLiteral), statement.Expression);
            FunctionLiteral functionLiteral = (FunctionLiteral)statement.Expression;
            
            Assert.Equal(functionLiteral.Parameters.Length, test.expectedParams.Length);
            
            for (var i = 0; i < test.expectedParams.Length; i++) {
                Assert.Equal(testLiteralExpression(functionLiteral.Parameters[i], test.expectedParams[i]), true);
            }
        }
    }

    [Fact]
    public void TestCallExpressionParsing() {
        string input = "add(1, 2 * 3, 4 + 5);";
        
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        Assert.False(checkParserErrors(parser));
        
        Assert.Single(tree.Statements);
        
        Assert.IsType<ExpressionStatement>(tree.Statements[0]);
        ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];
        
        Assert.IsType<CallExpression>(statement.Expression);
        CallExpression expression = (CallExpression)statement.Expression;
        
        Assert.True(testIdentifier(expression.Function, "add"));
        
        Assert.Equal(3, expression.Arguments.Length);
        
        Assert.True(testLiteralExpression(expression.Arguments[0], 1));
        Assert.True(testInfixExpression(expression.Arguments[1], 2, "*", 3));
        Assert.True(testInfixExpression(expression.Arguments[2], 4, "+", 5));
    }

    [Fact]
    public void TestModuleCallExpressionParsing() {
        string input = "module.callFunc(8, \"Wow!\");";
        
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        Assert.False(checkParserErrors(parser));
        
        Assert.Single(tree.Statements);
        
        Assert.IsType<ExpressionStatement>(tree.Statements[0]);
        ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];
        
        Assert.IsType<InfixExpression>(statement.Expression);
        InfixExpression expression = (InfixExpression)statement.Expression;

        Assert.IsType<Identifier>(expression.Left);
        Assert.Equal("module", expression.Left.String());
        
        Assert.Equal(".", expression.Operator);
        
        Assert.IsType<CallExpression>(expression.Right);
        CallExpression callExpression = (CallExpression)expression.Right;
        
        Assert.True(testLiteralExpression(callExpression.Arguments[0], 8));
        Assert.Equal("Wow!", callExpression.Arguments[1].String());
    }
    
    
    struct CallExpressionParameterTest {
        public string input;
        public string expectedIdent;
        public string[] expectedArgs;
    }

    [Fact]
    public void TestCallExpressionParameterParsing() {
        CallExpressionParameterTest[] tests = {
            new() { input = "add();", expectedIdent = "add", expectedArgs = new string[] { }, },
            new() { input = "add(1);", expectedIdent = "add", expectedArgs = new string[] { "1" }, },
            new() {
                input = "add(1, 2 * 3, 4 + 5);",
                expectedIdent = "add",
                expectedArgs = new string[] { "1", "(2 * 3)", "(4 + 5)" },
            },
        };
        foreach (var test in tests) {
            Lexer lexer = Lexer.NewInstance(test.input);
            Parser parser = Parser.NewInstance(lexer);
            AbstractSyntaxTree tree = parser.ParseAST();
            Assert.NotEqual(checkParserErrors(parser), true);
            
            Assert.IsType(typeof(ExpressionStatement), tree.Statements[0]);
            ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];
            
            Assert.IsType(typeof(CallExpression), statement.Expression);
            CallExpression expression = (CallExpression)statement.Expression;
            
            Assert.Equal(testIdentifier(expression.Function, test.expectedIdent), true);
            
            Assert.Equal(expression.Arguments.Length, test.expectedArgs.Length);
            
            for (var i = 0; i < test.expectedArgs.Length; i++) {
                Assert.Equal(expression.Arguments[i].String(), test.expectedArgs[i]);
            }
        }
    }

    [Fact]
    public void TestStringLiteralExpression() {
        string input = "\"hello world\";";
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        Assert.False(checkParserErrors(parser));
        
        Assert.IsType<ExpressionStatement>(tree.Statements[0]);
        ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];

        Assert.IsType<StringLiteral>(statement.Expression);
        StringLiteral expression = (StringLiteral)statement.Expression;
        
        Assert.Equal("hello world", expression.Value);
    }

    [Fact]
    public void TestParsingArrayLiterals() {
        string input = "[1, 2 * 2, 3 + 3]";
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        Assert.False(checkParserErrors(parser));
        
        Assert.IsType<ExpressionStatement>(tree.Statements[0]);
        ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];

        Assert.IsType<ArrayLiteral>(statement.Expression);
        ArrayLiteral array = (ArrayLiteral)statement.Expression;

        Assert.Equal(3, array.Elements.Length);
        
        Assert.True(testIntegerLiteral(array.Elements[0], 1));
        Assert.True(testInfixExpression(array.Elements[1], 2, "*", 2));
        Assert.True(testInfixExpression(array.Elements[2], 3, "+", 3));
    }

    [Fact]
    public void TestParsingIndexExpressions() {
        string input = "myArray[1 + 1]";
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        Assert.False(checkParserErrors(parser));

        Assert.IsType<ExpressionStatement>(tree.Statements[0]);
        ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];

        Assert.IsType<IndexExpression>(statement.Expression);
        IndexExpression indexExpression = (IndexExpression)statement.Expression;
        
        Assert.True(testIdentifier(indexExpression.Left, "myArray"));
        Assert.True(testInfixExpression(indexExpression.Index, 1, "+", 1));
    }

    [Fact]
    public void TestParsingHashLiteralStringKeys() {
        string input = "{\"one\": 1, \"two\": 2, \"three\": 3}";
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        Assert.False(checkParserErrors(parser));

        ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];

        Assert.IsType<HashLiteral>(statement.Expression);
        HashLiteral hashLiteral = (HashLiteral)statement.Expression;
        
        Assert.Equal(3, hashLiteral.Pairs.Count);

        Dictionary<string, int> expected = new() {
            {"one", 1},
            {"two", 2},
            {"three", 3},
        };
        
        foreach (var hashLiteralPair in hashLiteral.Pairs) {
            Assert.IsType<StringLiteral>(hashLiteralPair.Key);
            StringLiteral key = (StringLiteral)hashLiteralPair.Key;

            int _expected = expected[key.String()];
            
            Assert.True(testIntegerLiteral(hashLiteralPair.Value, _expected));
        }
    }

    [Fact]
    public void TestParsingEmptyHashLiteral() {
        string input = "{}";
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        Assert.False(checkParserErrors(parser));

        Assert.IsType<ExpressionStatement>(tree.Statements[0]);
        ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];

        Assert.IsType<HashLiteral>(statement.Expression);
        HashLiteral hashLiteral = (HashLiteral)statement.Expression;
        
        Assert.Empty(hashLiteral.Pairs);
    }

    public delegate void ParsingHashLiteralsTestFunc(IExpression expression);
    
    [Fact]
    public void TestParsingHashLiteralsWithExpressions() {
        string input = "{\"one\": 0 + 1, \"two\": 10 - 8, \"three\": 15 / 5}";
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        Assert.False(checkParserErrors(parser));
        
        Assert.IsType<ExpressionStatement>(tree.Statements[0]);
        ExpressionStatement statement = (ExpressionStatement)tree.Statements[0];

        Assert.IsType<HashLiteral>(statement.Expression);
        HashLiteral hashLiteral = (HashLiteral)statement.Expression;
        
        Assert.Equal(3, hashLiteral.Pairs.Count);

        Dictionary<string, ParsingHashLiteralsTestFunc> tests = new() {
            {
                "one", expression => {
                    Assert.True(testInfixExpression(expression, 0, "+", 1));
                }
            }, {
                "two", expression => {
                    Assert.True(testInfixExpression(expression, 10, "-", 8));
                }
            }, {
                "three", expression => {
                    Assert.True(testInfixExpression(expression, 15, "/", 5));
                }
            }
        };
        
        foreach (var hashLiteralPair in hashLiteral.Pairs) {
            Assert.IsType<StringLiteral>(hashLiteralPair.Key);
            StringLiteral literal = (StringLiteral)hashLiteralPair.Key;
            
            Assert.True(tests.TryGetValue(literal.String(), out ParsingHashLiteralsTestFunc func));

            func(hashLiteralPair.Value);
        }
    }

    [Fact]
    public void TestExceptionForIncorrectScript() {
        string[] scripts = {
            @"
            # ++ 的運算元必須是變數，不能是數值常值。
            迴圈 (變數 索引 = 0; 索引 < 5; 5++) {
            }
            ",
        };
        foreach (string script in scripts) {
            Lexer lexer = Lexer.NewInstance(script);
            Parser parser = Parser.NewInstance(lexer);
            AbstractSyntaxTree tree = parser.ParseAST();
            Assert.True(checkParserErrors(parser));
        }
    }

    private bool testInfixExpression(IExpression expression, object left, string _operator, object right) {
        if (expression.GetType() != typeof(InfixExpression)) {
            _testOutputHelper.WriteLine($"expression is not InfixExpression. Got={expression}");
            return false;
        }

        InfixExpression operatorExpression = (InfixExpression)expression;
        if (!testLiteralExpression(operatorExpression.Left, left)) {
            return false;
        }

        if (operatorExpression.Operator != _operator) {
            _testOutputHelper.WriteLine($"operatorExpression.Operator is not {_operator}. Got={operatorExpression.Operator}.");
            return false;
        }

        if (!testLiteralExpression(operatorExpression.Right, right)) {
            return false;
        }

        return true;
    }

    private bool testLiteralExpression(IExpression expression, object expected) {
        Type typeOfExpected = expected.GetType();
        if (typeOfExpected == typeof(int)) {
            return testIntegerLiteral(expression, (int)expected);
        } else if (typeOfExpected == typeof(string)) {
            return testIdentifier(expression, (string)expected);
        } else if (typeOfExpected == typeof(bool)) {
            return testBooleanLiteral(expression, (bool)expected);
        } else if (typeOfExpected == typeof(float)) {
            return testFloatLiteral(expression, (float)expected);
        } else if (typeOfExpected == typeof(double)) {
            return testDoubleLiteral(expression, (double)expected);
        } /*else if (typeOfExpected == typeof(Identifier)) {
            return testIdentifier(expression, ((Identifier)expected).Value);
        }*/
        _testOutputHelper.WriteLine($"Type of expression not handled. Got={expression}");
        return false;
    }

    private bool testDoubleLiteral(IExpression expression, double value) {
        if (expression.GetType() != typeof(DoubleLiteral)) {
            _testOutputHelper.WriteLine($"expression not DoubleLiteral. Got = {expression}");
            return false;
        }

        DoubleLiteral doubleLiteral = (DoubleLiteral)expression;

        if (doubleLiteral.Value != value) {
            _testOutputHelper.WriteLine($"doubleLiteral.Value not {value}. Got ={doubleLiteral.Value}");
            return false;
        }
        
        /*
         * It's hard to compare TokenLiteral() for double. e.x. "2.4" != "2.4d".
         */
        // if (doubleLiteral.TokenLiteral() != value.ToString()) {
        //     _testOutputHelper.WriteLine($"doubleLiteral.TokenLiteral() not {value}. Got = {doubleLiteral.TokenLiteral()}");
        //     return false;
        // }
        
        return true;
    }

    private bool testFloatLiteral(IExpression expression, float value) {
        if (expression.GetType() != typeof(FloatLiteral)) {
            _testOutputHelper.WriteLine($"expression not FloatLiteral. Got = {expression}");
            return false;
        }

        FloatLiteral floatLiteral = (FloatLiteral)expression;

        if (floatLiteral.Value != value) {
            _testOutputHelper.WriteLine($"floatLiteral.Value not {value}. Got ={floatLiteral.Value}");
            return false;
        }
        
        /*
         * It's hard to compare TokenLiteral() for float. e.x. "2.4" != "2.4f".
         */
        // if (floatLiteral.TokenLiteral() != value.ToString()) {
        //     _testOutputHelper.WriteLine($"integerLiteral.TokenLiteral() incorrect. Got = {floatLiteral.TokenLiteral()}");
        //     return false;
        // }
        
        return true;
    }

    private bool testIntegerLiteral(IExpression expression, int value) {
        if (expression.GetType() != typeof(IntegerLiteral)) {
            _testOutputHelper.WriteLine($"expression not IntegerLiteral. Got = {expression}");
            return false;
        }

        IntegerLiteral integerLiteral = (IntegerLiteral)expression;

        if (integerLiteral.Value != value) {
            _testOutputHelper.WriteLine($"integerLiteral.Value not {value}. Got ={integerLiteral.Value}");
            return false;
        }
        
        if (integerLiteral.TokenLiteral() != value.ToString()) {
            _testOutputHelper.WriteLine($"integerLiteral.TokenLiteral() not {value}. Got = {integerLiteral.TokenLiteral()}");
            return false;
        }
        
        return true;
    }

    private bool testIdentifier(IExpression expression, string value) {
        if (expression.GetType() != typeof(Identifier)) {
            _testOutputHelper.WriteLine($"expression not Identifier. Got = {expression}");
            return false;
        }

        Identifier identifier = (Identifier)expression;

        if (identifier.Value != value) {
            _testOutputHelper.WriteLine($"identifier.Value not {value}. Got ={identifier.Value}");
            return false;
        }

        if (identifier.TokenLiteral() != value) {
            _testOutputHelper.WriteLine($"identifier.TokenLiteral() not {value}. Got = {identifier.TokenLiteral()}");
            return false;
        }

        return true;
    }

    private bool testBooleanLiteral(IExpression expression, bool value) {
        if (expression.GetType() != typeof(BooleanLiteral)) {
            _testOutputHelper.WriteLine($"expression not BooleanLiteral type. Got={expression}");
            return false;
        }
        
        BooleanLiteral booleanLiteral = (BooleanLiteral)expression;

        if (booleanLiteral.Value != value) {
            _testOutputHelper.WriteLine($"booleanLiteral.Value not {value}. Got={booleanLiteral.Value}");
            return false;
        }

        // Verify the Traditional Chinese boolean literal as well as its value.
        if (booleanLiteral.TokenLiteral() != (value ? "真" : "假")) {
            _testOutputHelper.WriteLine($"booleanLiteral.TokenLiteral() not {value}. Got={booleanLiteral.TokenLiteral()}");
            return false;
        }
        
        return true;
    }

    private bool testLetStatement(IStatement statement, string name) {
        if (statement.TokenLiteral() != "變數") {
            _testOutputHelper.WriteLine($"statement.TokenLiteral() not '變數'. Got={statement.TokenLiteral()}");
            return false;
        }
        

        LetStatement letStatement = (LetStatement)statement;
        if (letStatement.Name.Value != name) {
            _testOutputHelper.WriteLine($"letStatement.Name.Value not {name}. Got={letStatement.Name.Value}.");
            return false;
        }

        if (letStatement.Name.TokenLiteral() != name) {
            _testOutputHelper.WriteLine($"letStatement.Name not {name}. Got={letStatement.Name}");
            return false;
        }

        return true;
    }

    private bool checkParserErrors(Parser parser) {
        string[] errors = parser.Errors.ToArray();
        if (errors.Length == 0) return false;
        
        _testOutputHelper.WriteLine($"Parser has {errors.Length} errors.");
        foreach (var error in errors) {
            _testOutputHelper.WriteLine($"Parser error: {error}");
        }

        return true;
    }
}
