namespace AquariusTests.Compiler;

/// <summary>Core language semantics of compiled programs on the native runtime: numbers, booleans, loops,
/// conditionals, returns, scopes, closures, strings, builtins, arrays, hashes and runtime errors.</summary>
public class CompiledEvaluatorTest {
    [RuntimeFact]
    public void TestEvalNumberExpression() => LanguageCases.ExpectNumbers(2,
        ("5", "integer", 5),
        ("10", "integer", 10),
        ("-5", "integer", -5),
        ("-10", "integer", -10),
        ("5 + 5 + 5 + 5 - 10", "integer", 10),
        ("2 * 2 * 2 * 2 * 2", "integer", 32),
        ("-50 + 100 + -50", "integer", 0),
        ("5 * 2 + 10", "integer", 20),
        ("5 + 2 * 10", "integer", 25),
        ("20 + 2 * -10", "integer", 0),
        ("50 / 2 * 2 + 10", "integer", 60),
        ("2 * (5 + 10)", "integer", 30),
        ("3 * 3 * 3 + 10", "integer", 37),
        ("3 * (3 * 3) + 10", "integer", 37),
        ("(5 + 10 * 2 + 15 / 3) * 2 + -10", "integer", 50),
        ("(7.3f + 2.1f) * 2 + -10.2f", "float", 8.6),
        ("1.f / 5.f * 3 + 11", "float", 11.6),
        ("(7.3f + 2.1d) * 2 + -10.2f", "double", 8.6),
        ("1.d / 5.f * 3 + 11", "double", 11.6),
        ("1.f * 33 + 2", "float", 35.0));

    [RuntimeFact]
    public void TestNumberPlusMinusMultDivEquals() => LanguageCases.ExpectNumbers(2,
        ("變數 a = 5; a += 10; a;", "integer", 15),
        ("變數 b = 25; b -= 5; b -= 19; b;", "integer", 1),
        ("變數 c = 1; c *= 5; c;", "integer", 5),
        ("變數 d = 20; d /= 4; d;", "integer", 5),
        ("變數 e = 25.4f; e /= 2.d; e;", "float", 12.7),
        ("變數 f = 18.3d; f *= 3; f;", "double", 54.9));

    private static (string, string, string) Boolean(string source, bool expected) => (source, "boolean", expected ? "真" : "假");

    [RuntimeFact]
    public void TestEvalBooleanExpression() => LanguageCases.Expect(
        Boolean("真", true),
        Boolean("假", false),
        Boolean("1 < 2", true),
        Boolean("1 > 2.5f", false),
        Boolean("1 < 1", false),
        Boolean("1 > 1.1d", false),
        Boolean("1 == 1", true),
        Boolean("1 != 1", false),
        Boolean("1 == 2.f", false),
        Boolean("1 != 2", true),
        Boolean("真 == 真", true),
        Boolean("假 == 假", true),
        Boolean("真 == 假", false),
        Boolean("真 != 假", true),
        Boolean("假 != 真", true),
        Boolean("(1 < 2) == 真", true),
        Boolean("(1 < 2) == 假", false),
        Boolean("(1 > 2) == 真", false),
        Boolean("(1 > 2) == 假", true),
        Boolean("真 && 假", false),
        Boolean("真 || 假", true),
        Boolean("假 || 假", false),
        Boolean("假 && 假", false),
        Boolean("真 && 真", true),
        Boolean("真 && 假", false),
        Boolean("!5 && 假", false),
        Boolean("123.301f < 123.302f", true),
        Boolean("0.9d > 0.91d", false),
        Boolean("1.111f > 1.11d", true),
        Boolean("1.111f != 1.11d", true),
        Boolean("1.111f != 1.111d", true));

    [RuntimeFact]
    public void TestBangOperator() => LanguageCases.Expect(
        Boolean("!真", false),
        Boolean("!假", true),
        Boolean("!5", false),
        Boolean("!!真", true),
        Boolean("!!假", false),
        Boolean("!!5", true),
        Boolean("!!6.3f", true));

    [RuntimeFact]
    public void TestEvalForLoop() => LanguageCases.Expect(
        (@"
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
            ", "integer", "20"),
        (@"
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
            ", "string", "XD!XD!XD!Wow!"),
        // A loop has no value: 型別 reports null and the empty (void) value displays as nothing.
        (@"
            # Empty 迴圈 loop.
            迴圈 (變數 i = 0; i < 5; i++) {
            }
            ", "null", ""));

    [RuntimeFact]
    public void TestIfElifElseExpressions() => LanguageCases.Expect(
        ("如果 (真) { 10 }", "integer", "10"),
        ("如果 (假) { 10 }", "null", "null"),
        ("如果 (1) { 10 }", "integer", "10"),
        ("如果 (1 < 2) { 10 }", "integer", "10"),
        ("如果 (1 > 2) { 10 }", "null", "null"),
        ("如果 (1 > 2) { 10 } 否則 { 20.12f }", "float", "20.12"),
        ("如果 (1 < 2) { 10 } 否則 { 20 }", "integer", "10"),
        (@"
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
            ", "string", "YES!"));

    [RuntimeFact]
    public void TestVarReassignments() => LanguageCases.Expect(
        ("變數 a = 0; a = 5; a;", "integer", "5"),
        (@"
            變數 a = ""Wow!"";
            變數 func = 函式(){迴圈(變數 a = 5; a < 10; a++) {迴圈 (變數 b = 0; b < 5; b++){如果 (a > 6) {回傳 a;}}}}
            a = func();
            a;
            ", "integer", "7"));

    [RuntimeFact]
    public void TestReturnStatements() => LanguageCases.Expect(
        ("回傳 10;", "integer", "10"),
        ("回傳 10; 9;", "integer", "10"),
        ("回傳 2 * 5; 9;", "integer", "10"),
        ("9; 回傳 2 * 5; 9;", "integer", "10"),
        ("如果 (10 > 1) { 回傳 10; }", "integer", "10"),
        (@"
            如果 (10 > 1) {
                如果 (10 > 1) {
                    回傳 10;
                }
                回傳 1;
            }
            ", "integer", "10"),
        (@"
            變數 f = 函式(x) {
                回傳 x;
                x + 10;
            };
            f(10);", "integer", "10"),
        (@"
            變數 f = 函式(x) {
                變數 result = x + 10;
                回傳 result;
                回傳 10;
            };
            f(10);", "integer", "20"),
        // Return from a for loop to out of the function.
        (@"
            變數 func = 函式(){
                迴圈(變數 a = 5; a < 10; a++) {
                    如果 (a > 6) {
                        回傳 a;
                    }
                }
            }
            func();
            ", "integer", "7"),
        // Return from a nested for loop to out of the function.
        (@"
            變數 func = 函式(){
                迴圈(變數 a = 5; a < 10; a++) {
                    迴圈 (變數 b = 0; b < 5; b++) {
                        如果 (a > 6) {
                            回傳 a;
                        }
                    }
                }
            }
            func();
            ", "integer", "7"));

    [RuntimeFact]
    public void TestErrorHandling() => LanguageCases.ExpectErrors(
        ("5 + 真;", "Type mismatch: INTEGER + BOOLEAN"),
        ("5 + 真; 5;", "Type mismatch: INTEGER + BOOLEAN"),
        ("-真", "Unknown operator: -BOOLEAN"),
        ("真 + 假;", "Unknown operator: BOOLEAN + BOOLEAN"),
        ("真 + 假 + 真 + 假;", "Unknown operator: BOOLEAN + BOOLEAN"),
        ("5; 真 + 假; 5", "Unknown operator: BOOLEAN + BOOLEAN"),
        ("如果 (10 > 1) { 真 + 假; }", "Unknown operator: BOOLEAN + BOOLEAN"),
        (@"
            如果 (10 > 1) {
                如果 (10 > 1) {
                    回傳 真 + 假;
                }

                回傳 1;
            }
            ", "Unknown operator: BOOLEAN + BOOLEAN"),
        ("foobar", "Identifier not found: foobar"),
        ("\"Hello\" - \"World\"", "Unknown operator: STRING - STRING"),
        ("{\"name\": \"Monkey\"}[函式(x) { x }];", "Unusable as hash key: FUNCTION"));

    [RuntimeFact]
    public void TestLetStatements() => LanguageCases.Expect(
        ("變數 a = 5; a;", "integer", "5"),
        ("變數 a = 5 * 5; a;", "integer", "25"),
        ("變數 a = 5; 變數 b = a; b;", "integer", "5"),
        ("變數 a = 5; 變數 b = a; 變數 c = a + b + 5; c;", "integer", "15"));

    [RuntimeFact]
    public void TestVariableReassignment() => LanguageCases.Expect(
        ("變數 a = 14; a = a + 1; a;", "integer", "15"),
        ("變數 b = \"Hello, world!\"; b = \"Wow!\"; b;", "string", "Wow!"));

    [RuntimeFact]
    public void TestFunctionObject() {
        // A compiled function keeps its parameters for display; its body is compiled code, shown as "...".
        var values = WasmTestHost.EvalMany(["函式(x) { x + 2; };", "變數 f = 函式(x) { x + 2; }; f(40);"]);
        Assert.Equal(new TestValue("function", "函式(x) {\n...\n}"), values[0]);
        Assert.Equal(42, values[1].Integer);
    }

    [RuntimeFact]
    public void TestFunctionApplication() => LanguageCases.Expect(
        ("變數 identity = 函式(x) { x; }; identity(5);", "integer", "5"),
        ("變數 identity = 函式(x) { 回傳 x; }; identity(5);", "integer", "5"),
        ("變數 double = 函式(x) { x * 2; }; double(5);", "integer", "10"),
        ("變數 add = 函式(x, y) { x + y; }; add(5, 5);", "integer", "10"),
        ("變數 add = 函式(x, y) { x + y; }; add(5 + 5, add(5, 5));", "integer", "20"),
        ("函式(x) { x; }(5)", "integer", "5"));

    [RuntimeFact]
    public void TestEnclosingEnvironment() => LanguageCases.Expect((@"
            變數 first = 10;
            變數 second = 10;
            變數 third = 10;

            變數 ourFunction = 函式(first) {
              變數 second = 20;

              first + second + third;
            };

            ourFunction(20) + first + second;
        ", "integer", "70"));

    [RuntimeFact]
    public void TestVariableScopeAndOwnership() => LanguageCases.Expect(
        (@"
            變數 a = 5;
            變數 func = 函式(){變數 a = 10;}
            func();
            a;
            ", "integer", "5"),
        (@"
            變數 a = 5;
            變數 func = 函式(){a = 10;}
            func();
            a;
            ", "integer", "10"),
        (@"
            變數 a = ""Hello"";
            變數 func = 函式(){
                a += "" World!"";
            }
            func();
            a;
            ", "string", "Hello World!"),
        (@"
            變數 a = 10;

            變數 func = 函式() {
                迴圈 (變數 i = 0; i < 5; i++) {
                    迴圈 (變數 j = 0; j < 5; j++) {
                        a += i;
                    }
                }
            }
            func();
            a;
            ", "integer", "60"));

    [RuntimeFact]
    public void TestStringLiteralAndConcatenation() => LanguageCases.Expect(
        ("\"Hello World!\"", "string", "Hello World!"),
        ("\"Hello\" + \" \" + \"World!\"", "string", "Hello World!"));

    [RuntimeFact]
    public void TestBuiltinFunctions() {
        LanguageCases.Expect(
            ("長度(\"\")", "integer", "0"),
            ("長度(\"four\")", "integer", "4"),
            ("長度(\"hello world\")", "integer", "11"));
        // The C# runtime's messages were "Argument to `長度` not supported, got INTEGER" and "Wrong number of
        // arguments. got=2, want=1"; the native runtime words them differently.
        LanguageCases.ExpectErrors(
            ("長度(1)", "Length requires string, array or hash"),
            ("長度(\"one\", \"two\")", "argument count"));
    }

    [RuntimeFact]
    public void TestArrayLiterals() => LanguageCases.Expect(
        (LanguageCases.Typed("[1, 2 * 2, 3 + 3]"), "array", "[[1, 4, 6], [integer, integer, integer]]"));

    [RuntimeFact]
    public void TestArrayIndexExpressions() => LanguageCases.Expect(
        ("[1, 2, 3][0]", "integer", "1"),
        ("[1, 2, 3][1]", "integer", "2"),
        ("[1, 2, 3][2]", "integer", "3"),
        ("變數 i = 0; [1][i];", "integer", "1"),
        ("[1, 2, 3][1 + 1];", "integer", "3"),
        ("變數 myArray = [1, 2, 3]; myArray[2];", "integer", "3"),
        ("變數 myArray = [1, 2, 3]; myArray[0] + myArray[1] + myArray[2];", "integer", "6"),
        ("變數 myArray = [1, 2, 3]; 變數 i = myArray[0]; myArray[i]", "integer", "2"),
        ("[1, 2, 3][3]", "null", "null"),
        ("[1, 2, 3][-1]", "null", "null"));

    [RuntimeFact]
    public void TestHashLiterals() {
        const string hash = @"
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
        LanguageCases.Expect(
            (hash, "hash", "{one: 1, two: 2, three: 3, 4: 4, 真: 5, 假: 6}"),
            // Keys keep their types: each typed key finds its own entry.
            ($"變數 h = (函式() {{ {hash} }})(); [h[\"one\"], h[\"two\"], h[\"three\"], h[4], h[真], h[假], 長度(h), h[\"4\"], h[4.0d]];",
                "array", "[1, 2, 3, 4, 5, 6, 6, null, null]"),
            ($"變數 h = (函式() {{ {hash} }})(); 變數 keys = 雜湊.鍵列表(h); [型別(keys[0]), 型別(keys[3]), 型別(keys[4]), 型別(keys[5])];",
                "array", "[string, integer, boolean, boolean]"));
    }

    [RuntimeFact]
    public void TestHashIndexExpressions() => LanguageCases.Expect(
        ("{\"foo\": 5}[\"foo\"]", "integer", "5"),
        ("{\"foo\": 5}[\"bar\"]", "null", "null"),
        ("變數 key = \"foo\"; {\"foo\": 5}[key]", "integer", "5"),
        ("{}[\"foo\"]", "null", "null"),
        ("{5: 5}[5]", "integer", "5"),
        ("{真: 5}[真]", "integer", "5"),
        ("{假: 5}[假]", "integer", "5"));

    [RuntimeFact]
    public void TestExceptionForIncorrectScript() => LanguageCases.ExpectErrors((@"
            # Use of undeclared array.
            迴圈 (變數 i = 0; i < 長度(array); i++) {
                印出(array[i])
            }
            ", "Identifier not found: array"));
}
