using AquariusLang.ast;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using Environment = AquariusLang.Object.Environment;

namespace AquariusLang.evaluator; 

public class EvaluatorTest {
    /// <summary>
    /// For logging outputs during testing.
    /// </summary>
    private readonly ITestOutputHelper _testOutputHelper;

    public EvaluatorTest(ITestOutputHelper testOutputHelper) {
        _testOutputHelper = testOutputHelper;
    }
    
    struct EvalNumberTest {
        public string input;
        public object expected;
    }
    [Fact]
    public void TestEvalNumberExpression() {
        EvalNumberTest[] tests = {
            new () {input = "5", expected = 5},
            new () {input = "10", expected = 10},
            new () {input = "-5", expected = -5},
            new () {input = "-10", expected = -10},
            new () {input = "5 + 5 + 5 + 5 - 10", expected = 10},
            new () {input = "2 * 2 * 2 * 2 * 2", expected = 32},
            new () {input = "-50 + 100 + -50", expected = 0},
            new () {input = "5 * 2 + 10", expected = 20},
            new () {input = "5 + 2 * 10", expected = 25},
            new () {input = "20 + 2 * -10", expected = 0},
            new () {input = "50 / 2 * 2 + 10", expected = 60},
            new () {input = "2 * (5 + 10)", expected = 30},
            new () {input = "3 * 3 * 3 + 10", expected = 37},
            new () {input = "3 * (3 * 3) + 10", expected = 37},
            new () {input = "(5 + 10 * 2 + 15 / 3) * 2 + -10", expected = 50},
            new () {input = "(7.3f + 2.1f) * 2 + -10.2f", expected = 8.6f},
            new () {input = "1.f / 5.f * 3 + 11", expected = 11.6f},
            new () {input = "(7.3f + 2.1d) * 2 + -10.2f", expected = 8.6},
            new () {input = "1.d / 5.f * 3 + 11", expected = 11.6},
            new () {input = "1.f * 33 + 2", expected = 35.0f},
        };

        foreach (var test in tests) {
            IObject evaluated = testEval(test.input);
            if (test.expected is int _expected) {
                Assert.IsType<IntegerObj>(evaluated);
                Assert.True(testIntegerObject(evaluated, (int?)test.expected));
            } else if (test.expected is float __expected) {
                Assert.IsType<FloatObj>(evaluated);
                Assert.True(testFloatObject(evaluated, (float?)test.expected, 2));
            } else if (test.expected is double ___expected) {
                Assert.IsType<DoubleObj>(evaluated);
                Assert.True(testDoubleObject(evaluated, (double?)test.expected, 2));
            }
        }
    }

    struct NumberPlusMinusMultDivEqualsTest {
        public string input;
        public object expected;
    }
    [Fact]
    public void TestNumberPlusMinusMultDivEquals() {
        NumberPlusMinusMultDivEqualsTest[] tests = {
            new() {input = "變數 a = 5; a += 10; a;", expected = 15},
            new() {input = "變數 b = 25; b -= 5; b -= 19; b;", expected = 1},
            new() {input = "變數 c = 1; c *= 5; c;", expected = 5},
            new() {input = "變數 d = 20; d /= 4; d;", expected = 5},
            new() {input = "變數 e = 25.4f; e /= 2.d; e;", expected = 12.7f},
            new() {input = "變數 f = 18.3d; f *= 3; f;", expected = 54.9},
        };
        foreach (var test in tests) {
            IObject evaluated = testEval(test.input);
            if (test.expected is int _expected) {
                Assert.IsType<IntegerObj>(evaluated);
                Assert.True(testIntegerObject(evaluated, _expected));
            } else if (test.expected is float __expected) {
                Assert.IsType<FloatObj>(evaluated);
                Assert.True(testFloatObject(evaluated, __expected, 2));
            } else if (test.expected is double ___expected) {
                Assert.IsType<DoubleObj>(evaluated);
                Assert.True(testDoubleObject(evaluated, ___expected, 2));
            }
        }
    }

    struct EvalBooleanTest {
        public string input;
        public bool expected;
    }

    [Fact]
    public void TestEvalBooleanExpression() {
        EvalBooleanTest[] tests = {
            new () {input = "真", expected = true},
            new () {input = "假", expected = false},
            new () {input = "1 < 2", expected = true},
            new () {input = "1 > 2.5f", expected = false},
            new () {input = "1 < 1", expected = false},
            new () {input = "1 > 1.1d", expected = false},
            new () {input = "1 == 1", expected = true},
            new () {input = "1 != 1", expected = false},
            new () {input = "1 == 2.f", expected = false},
            new () {input = "1 != 2", expected = true},
            new () {input = "真 == 真", expected = true},
            new () {input = "假 == 假", expected = true},
            new () {input = "真 == 假", expected = false},
            new () {input = "真 != 假", expected = true},
            new () {input = "假 != 真", expected = true},
            new () {input = "(1 < 2) == 真", expected = true},
            new () {input = "(1 < 2) == 假", expected = false},
            new () {input = "(1 > 2) == 真", expected = false},
            new () {input = "(1 > 2) == 假", expected = true},
            new () {input = "真 && 假", expected = false},
            new () {input = "真 || 假", expected = true},
            new () {input = "假 || 假", expected = false},
            new () {input = "假 && 假", expected = false},
            new () {input = "真 && 真", expected = true},
            new () {input = "真 && 假", expected = false},
            new () {input = "!5 && 假", expected = false},
            new () {input = "123.301f < 123.302f", expected = true},
            new () {input = "0.9d > 0.91d", expected = false},
            new () {input = "1.111f > 1.11d", expected = true},
            new () {input = "1.111f != 1.11d", expected = true},
            new () {input = "1.111f != 1.111d", expected = true},
        };

        foreach (var test in tests) {
            IObject evaluated = testEval(test.input);
            Assert.True(testBooleanObject(evaluated, test.expected));
        }
    }

    [Fact]
    public void TestBangOperator() {
        EvalBooleanTest[] tests = {
            new () {input = "!真", expected = false},
            new () {input = "!假", expected = true},
            new () {input = "!5", expected = false},
            new () {input = "!!真", expected = true},
            new () {input = "!!假", expected = false},
            new () {input = "!!5", expected = true},
            new () {input = "!!6.3f", expected = true},
        };
        foreach (var test in tests) {
            IObject evaluated = testEval(test.input);
            Assert.True(testBooleanObject(evaluated, test.expected));
        }
    }

    struct ForLoopTest {
        public string input;
        public object expected;
    }

    [Fact]
    public void TestEvalForLoop() {
        ForLoopTest[] tests = {
            new () {
                input = 
                    @"
                    # This one tests 迴圈 中斷 statement in 迴圈 loop.
                    變數 a = 0;
                    迴圈 (變數 j = 0; j <= 10; j += 2) {
                        a += 2;
                        如果 (j == 8){
                            a += 10;
                            中斷;
                        }
                    }
                    a;
                    ",
                expected = 20,
            },
            new () {
                input = 
                    @"
                    # This one tests 迴圈 回傳 statement in 迴圈 loop.
                    變數 a = """";
                    a = 函式() {
                        迴圈 (變數 j = 0; j <= 10; j += 2) {
                            a += ""XD!"";
                            如果 (j == 4){
                                a += ""Wow!"";
                                回傳 a;
                            }
                        }
                    }();
                    a;
                    ",
                expected = "XD!XD!XD!Wow!",
            },
            new () {
                input = @"
                    # Empty 迴圈 loop.
                    迴圈 (變數 i = 0; i < 5; i+=1) {
                    }
                    ",
                expected = null,
            }
        };
        foreach (ForLoopTest test in tests) {
            IObject result = testEval(test.input);
            if (result == null) {
                continue;
            }
            if (result.Type() == ObjectType.INTEGER_OBJ) {
                Assert.Equal(test.expected, ((IntegerObj)result).Value);
            } else if (result.Type() == ObjectType.STRING_OBJ) {
                Assert.Equal(test.expected, ((StringObj)result).Value);
            }
        }
    }

    struct IfElseTest {
        public string input;
        public object expected;
    }
    [Fact]
    public void TestIfElifElseExpressions() {
        IfElseTest[] tests = {
            new () {input = "如果 (真) { 10 }", expected = 10},
            new () {input = "如果 (假) { 10 }", expected = null},
            new () {input = "如果 (1) { 10 }", expected = 10},
            new () {input = "如果 (1 < 2) { 10 }", expected = 10},
            new () {input = "如果 (1 > 2) { 10 }", expected = null},
            new () {input = "如果 (1 > 2) { 10 } 否則 { 20.12f }", expected = 20.12f},
            new () {input = "如果 (1 < 2) { 10 } 否則 { 20 }", expected = 10},
            new () {
                input = @"
                        變數 a = 假;
                        變數 b = 真;

                        變數 result = 函式() {
                            如果 (a) {
                                回傳 12;
                            } 否則如果 (10 > 12) {
                                回傳 真;
                            } 否則如果 (b) {
                                回傳 ""YES!"";
                            } 否則 {
                                回傳 ""Wow!"";
                            }
                        }();

                        result;
                        ",
                expected = "YES!"
            },
        };
        foreach (var test in tests) {
            IObject evaluated = testEval(test.input);
            _testOutputHelper.WriteLine(test.input);
            if (test.expected is int) {
                int integer = (int)test.expected;
                Assert.True(testIntegerObject(evaluated, integer));
            } else if (test.expected is float _expected) {
                Assert.True(testFloatObject(evaluated, _expected, 2));
            } else if (test.expected is string) {
                Assert.True(testStringObject(evaluated, (string?)test.expected));
            } else {
                /*
				 When a conditional doesn’t evaluate to a value it’s supposed to
				 return NULL, e.g.: if (false) { 10 }
			    */
                Assert.True(testNullObject(evaluated));
            }
        }
    }

    struct VarReassignmentTest {
        public string input;
        public int expected;
    }

    [Fact]
    public void TestVarReassignments() {
        VarReassignmentTest[] tests = {
            new () {input = "變數 a = 0; a = 5; a;", expected = 5},
            new () {
            input = @"
                    變數 a = ""Wow!"";
                    變數 func = 函式(){迴圈(變數 a = 5; a < 10; a+=1) {迴圈 (變數 b = 0; b < 5; b+=1){如果 (a > 6) {回傳 a;}}}}
                    a = func();
                    a;
                    ",
            expected = 7
            },
        };
        foreach (VarReassignmentTest test in tests) {
            IObject result = testEval(test.input);
            Assert.IsType<IntegerObj>(result);
            Assert.True(testIntegerObject(result, test.expected));
        }
    }

    struct ReturnStatementTest {
        public string input;
        public int expected;
    }
    [Fact]
    public void TestReturnStatements() {
        ReturnStatementTest[] tests = {
            new () {input = "回傳 10;", expected = 10},
            new () {input = "回傳 10; 9;", expected = 10},
            new () {input = "回傳 2 * 5; 9;", expected = 10},
            new () {input = "9; 回傳 2 * 5; 9;", expected = 10},
            new () {input = "如果 (10 > 1) { 回傳 10; }", expected = 10},
            new () {
                input = @"
                如果 (10 > 1) {
                    如果 (10 > 1) {
                        回傳 10;
                    }
                    回傳 1;
                }
                ",
                expected = 10,
            },
            new () {
                input = 
                @"
                變數 f = 函式(x) {
                    回傳 x;
                    x + 10;
                };
                f(10);",
                expected = 10,
            },
            new () {
                input = 
                @"
                變數 f = 函式(x) {
                    變數 result = x + 10;
                    回傳 result;
                    回傳 10;
                };
                f(10);",
                expected = 20,
            },
            new () { // Return from for loop to out of function.
                input = 
                @"
                變數 func = 函式(){
                    迴圈(變數 a = 5; a < 10; a+=1) {
                        如果 (a > 6) {
                            回傳 a;
                        }
                    }
                }
                func();
                ",
                expected = 7,
            },
            new () { // Return from nested for loop to out of function.
            input = 
            @"
                變數 func = 函式(){
                    迴圈(變數 a = 5; a < 10; a+=1) {
                        迴圈 (變數 b = 0; b < 5; b+=1) {
                            如果 (a > 6) {
                                回傳 a;
                            }
                        }
                    }
                }
                func();
                ",
            expected = 7,
            }
        };

        foreach (var test in tests) {
            IObject evaluated = testEval(test.input);
            Assert.True(testIntegerObject(evaluated, test.expected));
        }
    }

    struct ErrorHandlingTest {
        public string input;
        public string expectedMessage;
    }

    [Fact]
    public void TestErrorHandling() {
        ErrorHandlingTest[] tests = {
            new() { input = "5 + 真;", expectedMessage = "Type mismatch: INTEGER + BOOLEAN", },
            new() { input = "5 + 真; 5;", expectedMessage = "Type mismatch: INTEGER + BOOLEAN", },
            new() { input = "-真", expectedMessage = "Unknown operator: -BOOLEAN", },
            new() { input = "真 + 假;", expectedMessage = "Unknown operator: BOOLEAN + BOOLEAN", },
            new() { input = "真 + 假 + 真 + 假;", expectedMessage = "Unknown operator: BOOLEAN + BOOLEAN", },
            new() { input = "5; 真 + 假; 5", expectedMessage = "Unknown operator: BOOLEAN + BOOLEAN", },
            new() { input = "如果 (10 > 1) { 真 + 假; }", expectedMessage = "Unknown operator: BOOLEAN + BOOLEAN", },
            new() {
                input = @"
                如果 (10 > 1) {
                    如果 (10 > 1) {
                        回傳 真 + 假;
                    }

                    回傳 1;
                }
                ",
                expectedMessage = "Unknown operator: BOOLEAN + BOOLEAN",
            },
            new() { input = "foobar", expectedMessage = "Identifier not found: foobar", },
            new() { input = "\"Hello\" - \"World\"", expectedMessage = "Unknown operator: STRING - STRING" },
            new() { input = "{\"name\": \"Monkey\"}[函式(x) { x }];", expectedMessage = "Unusable as hash key: FUNCTION" }, 
        };

        foreach (var test in tests) {
            IObject evaluated = testEval(test.input);
            Assert.IsType<ErrorObj>(evaluated);

            ErrorObj errorObj = (ErrorObj)evaluated;
            
            Assert.Equal(test.expectedMessage, errorObj.Message);
        }
    }

    struct LetStatementTest {
        public string input;
        public int expected;
    }

    [Fact]
    public void TestLetStatements() {
        LetStatementTest[] tests = {
            new () {input = "變數 a = 5; a;", expected = 5},
            new () {input = "變數 a = 5 * 5; a;", expected = 25},
            new () {input = "變數 a = 5; 變數 b = a; b;", expected = 5},
            new () {input = "變數 a = 5; 變數 b = a; 變數 c = a + b + 5; c;", expected = 15},
        };

        foreach (var test in tests) {
            Assert.True(testIntegerObject(testEval(test.input), test.expected));
        }
    }
    
    
    struct VariableReassignmentTest {
        public string input;
        public object expectedValue;
    }
    [Fact]
    public void TestVariableReassignment() {
        VariableReassignmentTest[] tests = {
            new() { input = "變數 a = 14; a = a + 1; a;", expectedValue = 15 },
            new() { input = "變數 b = \"Hello, world!\"; b = \"Wow!\"; b;", expectedValue = "Wow!"},
        };
        foreach (var test in tests) {
            IObject evalResult = testEval(test.input);
            if (evalResult.Type() == ObjectType.INTEGER_OBJ) {
                Assert.True(testIntegerObject(evalResult, (int?)test.expectedValue));
            } else if (evalResult.Type() == ObjectType.STRING_OBJ) {
                Assert.True(testStringObject(evalResult, (string?)test.expectedValue));
            }
        }
    }

    [Fact]
    public void TestFunctionObject() {
        string input = "函式(x) { x + 2; };";
        IObject evaluated = testEval(input);

        Assert.IsType<FunctionObj>(evaluated);
        FunctionObj functionObj = (FunctionObj)evaluated;
        
        Assert.Single(functionObj.Parameters);
        
        Assert.Equal("x", functionObj.Parameters[0].String());
        
        Assert.Equal("(x + 2)", functionObj.Body.String());
    }

    struct FunctionApplicationTest {
        public string input;
        public int expected;
    }
    [Fact]
    public void TestFunctionApplication() {
        FunctionApplicationTest[] tests = {
            new() { input = "變數 identity = 函式(x) { x; }; identity(5);", expected = 5 },
            new() { input = "變數 identity = 函式(x) { 回傳 x; }; identity(5);", expected = 5 },
            new() { input = "變數 double = 函式(x) { x * 2; }; double(5);", expected = 10 },
            new() { input = "變數 add = 函式(x, y) { x + y; }; add(5, 5);", expected = 10 },
            new() { input = "變數 add = 函式(x, y) { x + y; }; add(5 + 5, add(5, 5));", expected = 20 },
            new() { input = "函式(x) { x; }(5)", expected = 5 },
        };

        foreach (var test in tests) {
            Assert.True(testIntegerObject(testEval(test.input), test.expected));
        }
    }

    [Fact]
    public void TestEnclosingEnvironment() {
        string input = @"
            變數 first = 10;
            變數 second = 10;
            變數 third = 10;

            變數 ourFunction = 函式(first) {
              變數 second = 20;

              first + second + third;
            };

            ourFunction(20) + first + second;
        ";
        Assert.True(testIntegerObject(testEval(input), 70));
    }

    struct VariableScopeAndOwnershipTest {
        public string input;
        public object expected;
    }
    [Fact]
    public void TestVariableScopeAndOwnership() {
        VariableScopeAndOwnershipTest[] tests = {
            new () {
                input = 
                    @"
                    變數 a = 5;
                    變數 func = 函式(){變數 a = 10;}
                    func();
                    a;
                    ",
                expected = 5
            },
            new () {
                input = 
                    @"
                    變數 a = 5;
                    變數 func = 函式(){a = 10;}
                    func();
                    a;
                    ",
                expected = 10
            },
            new () {
                input = 
                    @"
                    變數 a = ""Hello"";
                    變數 func = 函式(){
                        a += "" World!"";
                    }
                    func();
                    a;
                    ",
                expected = "Hello World!",
            },
            new () {
                input = 
                    @"
                    變數 a = 10;
                    
                    變數 func = 函式() {
                        迴圈 (變數 i = 0; i < 5; i+=1) {
                            迴圈 (變數 j = 0; j < 5; j+=1) {
                                a += i;                                
                            }
                        }
                    }
                    func();
                    a;
                    ",
                expected = 60,
            }
        };
        foreach (VariableScopeAndOwnershipTest test in tests) {
            IObject result = testEval(test.input);
            Assert.IsNotType<ErrorObj>(result);
            switch (result.Type()) {
                case ObjectType.INTEGER_OBJ:
                    Assert.True(testIntegerObject(result, (int) test.expected));
                    break;
                case ObjectType.STRING_OBJ:
                    Assert.True(testStringObject(result, (string) test.expected));
                    break;
            }
        }
    }

    [Fact]
    public void TestStringLiteral() {
        string input = "\"Hello World!\"";
        IObject evaluated = testEval(input);
        Assert.IsType<StringObj>(evaluated);
        StringObj stringObj = (StringObj)evaluated;
        Assert.Equal("Hello World!", stringObj.Value);
    }

    [Fact]
    public void TestStringConcatenation() {
        string input = "\"Hello\" + \" \" + \"World!\"";
        IObject evaluated = testEval(input);
        
        Assert.IsType<StringObj>(evaluated);
        StringObj stringObj = (StringObj)evaluated;
        
        Assert.Equal("Hello World!", stringObj.Value);
    }

    struct BuiltinFunctionTest {
        public string input;
        public object expected;
    }

    [Fact]
    public void TestBuiltinFunctions() {
        BuiltinFunctionTest[] tests = {
            new () {input = "長度(\"\")", expected = 0},
            new () {input = "長度(\"four\")", expected = 4},
            new () {input = "長度(\"hello world\")", expected = 11},
            new () {input = "長度(1)", expected = "Argument to `長度` not supported, got INTEGER"},
            new () {input = "長度(\"one\", \"two\")", expected = "Wrong number of arguments. got=2, want=1"},
        };

        foreach (var test in tests) {
            IObject evaluated = testEval(test.input);
            Type evaluatedType = evaluated.GetType();
            if (evaluatedType == typeof(int)) {
                Assert.True(testIntegerObject(evaluated, (int)test.expected));
            } else if (evaluatedType == typeof(string)) {
                Assert.IsType<ErrorObj>(evaluated);

                ErrorObj errorObj = (ErrorObj)evaluated;
                Assert.Equal(test.expected, errorObj.Message);
            }
        }
    }

    [Fact]
    public void TestArrayLiterals() {
        string input = "[1, 2 * 2, 3 + 3]";
        IObject evaluated = testEval(input);
        
        Assert.IsType<ArrayObj>(evaluated);
        ArrayObj result = (ArrayObj)evaluated;
        
        Assert.Equal(3, result.Elements.Length);
        
        Assert.True(testIntegerObject(result.Elements[0], 1));
        Assert.True(testIntegerObject(result.Elements[1], 4));
        Assert.True(testIntegerObject(result.Elements[2], 6));
    }

    struct ArrayIndexTest {
        public string input;
        public object expected;
    }

    [Fact]
    public void TestArrayIndexExpressions() {
        ArrayIndexTest[] tests = {
            new () { input = "[1, 2, 3][0]", expected = 1, },
            new () { input = "[1, 2, 3][1]", expected = 2, },
            new () { input = "[1, 2, 3][2]", expected = 3, },
            new () { input = "變數 i = 0; [1][i];", expected = 1, },
            new () { input = "[1, 2, 3][1 + 1];", expected = 3, },
            new () { input = "變數 myArray = [1, 2, 3]; myArray[2];", expected = 3, },
            new () { input = "變數 myArray = [1, 2, 3]; myArray[0] + myArray[1] + myArray[2];", expected = 6, },
            new () { input = "變數 myArray = [1, 2, 3]; 變數 i = myArray[0]; myArray[i]", expected = 2, },
            new () { input = "[1, 2, 3][3]", expected = null, },
            new () { input = "[1, 2, 3][-1]", expected = null, },
        };

        foreach (var test in tests) {
            IObject evaluated = testEval(test.input);
            if (test.expected == null) {
                Assert.True(testNullObject(evaluated));
            } else {
                Assert.True(testIntegerObject(evaluated, (int)test.expected));
            }
        }
    }

    [Fact]
    public void TestHashLiterals() {
        string input = @"
        變數 two = ""two"";
        {
            ""one"": 10 - 9,
            two: 1 + 1,
            ""thr"" + ""ee"": 6 / 2,
            4: 4,
            真: 5,
            假: 6
        }
        ";
        IObject evaluated = testEval(input);
        Assert.IsType<HashObj>(evaluated);
        HashObj result = (HashObj)evaluated;

        Dictionary<HashKey, int> expected = new() {
            { (new StringObj("one")).HashKey(), 1 },
            { (new StringObj("two")).HashKey(), 2 },
            { (new StringObj("three")).HashKey(), 3 },
            { (new IntegerObj(4)).HashKey(), 4 },
            { RepeatedPrimitives.TRUE.HashKey(), 5 },
            { RepeatedPrimitives.FALSE.HashKey(), 6 }
        };
        
        Assert.Equal(expected.Count, result.Pairs.Count);
        
        foreach (var _expected in expected) {
            Assert.True(result.Pairs.TryGetValue(_expected.Key, out HashPair value));
            Assert.True(testIntegerObject(value.Value, _expected.Value));
        }
    }

    struct HashIndexExpressionTest {
        public string input;
        public int? expected;
    }
    [Fact]
    public void TestHashIndexExpressions() {
        HashIndexExpressionTest[] tests = {
            new() {
                input = "{\"foo\": 5}[\"foo\"]",
                expected = 5,
            },
            new() {
                input = "{\"foo\": 5}[\"bar\"]",
                expected = null,
            },
            new() {
                input = "變數 key = \"foo\"; {\"foo\": 5}[key]",
                expected = 5,
            },
            new() {
                input = "{}[\"foo\"]",
                expected = null,
            },
            new() {
                input = "{5: 5}[5]",
                expected = 5,
            },
            new() {
                input = "{真: 5}[真]",
                expected = 5,
            },
            new() {
                input = "{假: 5}[假]",
                expected = 5,
            },
        };

        foreach (var test in tests) {
            _testOutputHelper.WriteLine($"input: {test.input}, expected: {test.expected}");
            IObject evaluated = testEval(test.input);
            if (test.expected != null) {
                Assert.True(testIntegerObject(evaluated, test.expected));
            } else {
                Assert.True(testNullObject(evaluated));
            }
        }
    }

    [Fact]
    public void TestExceptionForIncorrectScript() {
        string[] scripts = {
            @"
            # Use of undeclared array.
            迴圈 (變數 i = 0; i < 長度(array); i+=1) {
                印出(array[i])
            }
            ",
        };
        foreach (string script in scripts) {
            IObject evaluated = testEval(script);
            Assert.IsType<ErrorObj>(evaluated);
        }
    }

    private IObject testEval(string input) {
        Lexer lexer = Lexer.NewInstance(input);
        Parser parser = Parser.NewInstance(lexer);
        AbstractSyntaxTree tree = parser.ParseAST();
        Environment environment = Environment.NewEnvironment();
        Evaluator evaluator = Evaluator.NewInstance(new Builtins());
        return evaluator.Eval(tree, environment);
    }

    private bool testBooleanObject(IObject obj, bool expected) {
        if (obj is BooleanObj booleanObj) {
            if (booleanObj.Value != expected) {
                _testOutputHelper.WriteLine($"obj has wrong value. Got={booleanObj.Value}, want={expected}.");
                return false;
            }

            return true;
        }

        _testOutputHelper.WriteLine($"obj is not BooleanObj. Got={obj}");
        return false;
    }

    private bool testNullObject(IObject obj) {
        if (obj != RepeatedPrimitives.NULL) {
            _testOutputHelper.WriteLine($"object is not NULL. Got={obj}");
            return false;
        }

        return true;
    }

    private bool testIntegerObject(IObject obj, int? expected) {
        if (obj is IntegerObj integerObj) {
            if (integerObj.Value != expected) {
                _testOutputHelper.WriteLine($"Object has wrong value. Got={integerObj.Value}, want={expected}");
                return false;
            }

            return true;
        }
        _testOutputHelper.WriteLine($"Object is not IntegerObj. Got={obj}");
        return false;
    }

    private bool testFloatObject(IObject obj, float? expected, int roundPlace) {
        if (obj is FloatObj floatObj) {
            if (Math.Round(floatObj.Value, roundPlace) != Math.Round((float)expected, roundPlace)) {
                _testOutputHelper.WriteLine($"Object has wrong value. Got={floatObj.Value}, want={expected}");
                return false;
            }

            return true;
        }
        
        _testOutputHelper.WriteLine($"Object is not FloatObj. Got={obj}");
        return false;
    }

    private bool testDoubleObject(IObject obj, double? expected, int roundPlace) {
        if (obj is DoubleObj doubleObj) {
            if (Math.Round(doubleObj.Value, roundPlace) != Math.Round((double)expected, roundPlace)) {
                _testOutputHelper.WriteLine($"Object has wrong value. Got={doubleObj.Value}, want={expected}");
                return false;
            }

            return true;
        }
        
        _testOutputHelper.WriteLine($"Object is not DoubleObj. Got={obj}");
        return false;
    }

    private bool testStringObject(IObject obj, string? expected) {
        if (obj is StringObj stringObj) {
            if (stringObj.Value != expected) {
                _testOutputHelper.WriteLine($"Object has wrong value. Got={stringObj.Value}, want={expected}");
                return false;
            }

            return true;
        }
        _testOutputHelper.WriteLine($"Object is not StringObj. Got={obj}");
        return false;
    }
}