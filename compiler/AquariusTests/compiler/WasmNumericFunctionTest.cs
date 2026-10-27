using AquariusLang.Wasm;

namespace AquariusTests.Wasm;

/// <summary>The typed scalar (numeric/integer) function tier against the generic emitter, on the native runtime. The C#
/// host's instance counters (aqua_numeric_fast_calls, aqua_numeric_deopts, aqua_heap_bytes) are not reachable from the
/// native runtime; tier selection is checked on the compiled program instead and the results on the runtime.</summary>
public class WasmNumericFunctionTest
{
    private static readonly string[] ScalarPrograms = [
        "變數 f=函式(a,b){a+b;};[f(2,3),f(2.5f,3),f(2,3.25d)];",
        "變數 f=函式(a,a){a;};f(1,42);",
        "變數 f=函式(a){如果(a>0){回傳 a+2;}回傳 a-3;};[f(4),f(-2)];",
        "變數 f=函式(a){變數 b=a*3;變數 c=b/2;c+b;};f(7);",
        "變數 f=函式(a){a+=1.75d;[a];};f(1);",
        "變數 f=函式(a){a+=1.75d;a;};[f(1),f(1.0f)];",
        "變數 f=函式(a){變數 old=a++;變數 next=++a;old+next+a;};f(2147483647);",
        "變數 f=函式(a,b){a+b;};f(2147483647,1);",
        "變數 f=函式(a,b){a*b;};f(2147483647,2147483647);",
        "變數 f=函式(a,b){a/b;};[f(1,0),f(-2147483647-1,-1),f(7,-2)];",
        "變數 f=函式(a,b){a/b;};[f(0.0d,0.0d),f(1.0f,0.0f)];",
        "變數 f=函式(a){-a;};f(-2147483647-1);",
        "變數 f=函式(a,b){a==b;};[f(1,1.0d),f(1,真),f(真,真),f(真,假)];",
        "變數 f=函式(a,b){a!=b;};[f(1,1.0d),f(1,真),f(0.0d/0.0d,0.0d/0.0d)];",
        "變數 f=函式(a,b){a&&b;};[f(真,假),f(真,真)];",
        "變數 f=函式(a,b){a||b;};[f(假,假),f(真,假)];",
        "變數 f=函式(a){!a;};[f(真),f(假)];",
        "變數 f=函式(a){如果(a){回傳 42;}7;};[f(真),f(假)];",
        "變數 f=函式(a){如果(a){變數 x=42;}x;};f(假);",
        "變數 x=99;變數 f=函式(a){如果(a){變數 x=42;}x;};[f(假),f(真)];",
        "變數 x=10;變數 f=函式(a){如果(a){變數 x=1;}x+=2;x;};[f(假),x];",
        "變數 f=函式(a,b){a+b;};f(\"繁\",\"體\");",
        "變數 f=函式(a,b){a+b;};f(真,1);",
        "變數 f=函式(a){如果(a){1;}2;};f(1);",
        "變數 f=函式(a){a;};f([1,2]);",
        "變數 f=函式(){42;};f();",
        "變數 f=函式(a){a;};f();",
        "變數 fib=函式(n){如果(n<2){回傳 n;}回傳 fib(n-1)+fib(n-2);};fib(18);",
        "變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};f(20000);",
        "變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};變數 old=f;f=函式(n){100;};old(3);",
        "變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};變數 old=f;f=42;old(2);",
        "變數 make=函式(x){函式(y){x+y;};};make(10)(3);",
        "變數 f=函式(a){a+=2;a++;a;};f(1.5f);",
        "變數 f=函式(a){變數 x=a;x=x*3;x-=2;變數 old=x++;變數 next=++x;old+next+x;};f(2147483647);",
        "變數 f=函式(a){如果(a<0&&a>=-7){回傳 -a;}回傳 a/2;};[f(-7),f(8)];",
        "變數 f=函式(a){變數 x=1;如果(a<0){x=假;}如果(x==真){回傳 2;}回傳 a;};[f(-1),f(1)];",
        "變數 f=函式(a){如果(a>0){變數 x=a;}否則{變數 x=-a;}x;};[f(-7),f(7)];",
    ];

    // (The C# host also repeated these with a 16-unit fuel quantum; the native host's quantum is fixed.)
    [RuntimeFact]
    public void ScalarFunctionsPreserveGenericSemantics()
    {
        var results = EmitterTiers.AssertSame(ScalarPrograms);
        Assert.Equal(new TestValue("integer", "2584"), results[Array.IndexOf(ScalarPrograms, "變數 fib=函式(n){如果(n<2){回傳 n;}回傳 fib(n-1)+fib(n-2);};fib(18);")]);
        Assert.Equal(new TestValue("string", "繁體"), results[Array.IndexOf(ScalarPrograms, "變數 f=函式(a,b){a+b;};f(\"繁\",\"體\");")]);
    }

    [RuntimeFact]
    public void DirectScalarCallsUseTheTypedTier()
    {
        const string source = "變數 fib=函式(n){如果(n<2){回傳 n;}回傳 fib(n-1)+fib(n-2);};fib(20);";
        // The optimizing emitter gives the recursive function a typed scalar entry calling itself directly by name.
        var fib = new WasmCompiler().Compile(source).Functions.Single(f => f.Parameters.SequenceEqual(new[] { "n" }));
        Assert.True(fib.NumericTable > 0); Assert.Equal("fib", fib.NumericRecursiveName);
        Assert.Equal(0, new WasmCompiler(false).Compile(source).Functions.Single(f => f.Parameters.SequenceEqual(new[] { "n" })).NumericTable);
        Assert.Equal("6765\n", WasmTestHost.Output(source));
    }

    [RuntimeFact]
    public void DepthLimitReturnsToStacklessRecursion()
    {
        // The integer specialization deoptimizes its native activations into generic frames at its depth bound, so deep
        // recursion neither overflows the native stack nor restarts.
        Assert.Equal("[20000, 200000]\n", WasmTestHost.Output("變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};[f(20000),f(200000)];"));
    }

    [RuntimeFact]
    public void CapabilityEffectsNeverRunInsideRestartableScalarCode()
    {
        // A host capability (印出) and a guest side effect inside a function the scalar tier could speculate on run once.
        var run = WasmTestHost.Run("變數 calls=0;變數 effect=函式(){calls++;calls;};變數 f=函式(n){印出(\"effect\");effect()+n;};[f(2),calls];").Succeeded();
        Assert.Equal(new[] { "effect", "[3, 1]" }, run.Lines);
    }

    private static readonly string[] OperatorValues = [
        "0-2147483647-1", "2147483647", "0", "-7", "0.1f", "1.75d", "0.0d/0.0d", "1.0d/0.0d", "真", "假", "\"繁體\"",
    ];

    [RuntimeFact]
    public void ScalarOperatorsMatchAcrossNumericTypesAndDynamicFallbacks()
    {
        string[] operators = ["+", "-", "*", "/", "<", ">", "<=", ">=", "==", "!=", "&&", "||"];
        int[] numeric = Enumerable.Range(0, 8).ToArray(), boolean = [8, 9], all = Enumerable.Range(0, OperatorValues.Length).ToArray();
        IEnumerable<(int Left, int Right)> Pairs(int[] left, int[] right) => left.SelectMany(l => right.Select(r => (l, r)));
        // Every numeric pairing; equality across every type; Boolean logic; string concatenation.
        var cases = operators.SelectMany((op, o) => (op switch {
            "==" or "!=" => Pairs(all, all),
            "&&" or "||" => Pairs(boolean, boolean),
            "+" => Pairs(numeric, numeric).Append((10, 10)),
            // Integer division by zero is an error (checked below).
            "/" => Pairs(numeric, numeric).Where(p => !(p.Item1 <= 3 && p.Item2 == 2)),
            _ => Pairs(numeric, numeric),
        }).Select(p => (Operator: o, Left: p.Item1, Right: p.Item2))).ToList();
        var results = EmitterTiers.AssertSameCases(OperatorPrelude(operators, OperatorValues, cases), "fs[ops[k]](values[lefts[k]],values[rights[k]])",
            cases.Count, i => $"{OperatorValues[cases[i].Left]} {operators[cases[i].Operator]} {OperatorValues[cases[i].Right]}");
        Assert.DoesNotContain(results, v => v.IsError);
        Assert.Equal(new TestValue("string", "繁體繁體"), results[cases.IndexOf((0, 10, 10))]);
        Assert.Equal(new TestValue("double", "NaN"), results[cases.IndexOf((0, 6, 0))]);
        // Mismatched operands fall back to the generic operator and fail the same way. An error stops a program, so each
        // runs on its own.
        (int Operator, int Left, int Right)[] mismatched = [(0, 0, 8), (1, 10, 10), (2, 8, 9), (4, 10, 3), (10, 2, 8), (11, 10, 9), (3, 1, 2), (3, 2, 2)];
        var errors = EmitterTiers.AssertSame(mismatched.Select(c => $"變數 values=[{string.Join(",", OperatorValues)}];" +
            $"變數 f=函式(a,b){{a{operators[c.Operator]}b;}};f(values[{c.Left}],values[{c.Right}]);").ToArray());
        Assert.All(errors, error => Assert.True(error.IsError, error.ToString()));
    }

    /// <summary>The operands and functions as arrays the cases index, so the compiler cannot see the operand types (the
    /// C# host passed them in as globals) and the program stays small.</summary>
    private static string OperatorPrelude(string[] operators, IEnumerable<string> values, IReadOnlyList<(int Operator, int Left, int Right)> cases) =>
        "變數 values=[" + string.Join(",", values) + "];" +
        string.Concat(operators.Select((op, i) => $"變數 f{i}=函式(a,b){{a{op}b;}};")) +
        "變數 fs=[" + string.Join(",", operators.Select((_, i) => "f" + i)) + "];" +
        "變數 ops=[" + string.Join(",", cases.Select(c => c.Operator)) + "];" +
        "變數 lefts=[" + string.Join(",", cases.Select(c => c.Left)) + "];" +
        "變數 rights=[" + string.Join(",", cases.Select(c => c.Right)) + "];";

    [RuntimeFact]
    public void IntegerRepresentationWrapsBoundaryArithmetic()
    {
        int[] values = [int.MinValue, int.MinValue + 1, -65537, -1, 0, 1, 65537, int.MaxValue - 1, int.MaxValue];
        string[] operators = ["+", "-", "*", "/"];
        var literals = values.Select(v => v == int.MinValue ? "0-2147483647-1" : v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        // Division by zero is an error, not a value; every other case wraps like C#'s unchecked int arithmetic.
        var cases = (from o in Enumerable.Range(0, operators.Length) from l in Enumerable.Range(0, values.Length)
                     from r in Enumerable.Range(0, values.Length) where !(operators[o] == "/" && values[r] == 0) select (o, l, r)).ToList();
        var results = EmitterTiers.AssertSameCases(OperatorPrelude(operators, literals, cases), "fs[ops[k]](values[lefts[k]],values[rights[k]])",
            cases.Count, i => $"{values[cases[i].l]} {operators[cases[i].o]} {values[cases[i].r]}");
        static int Expected(string op, int a, int b) => unchecked(op switch {
            "+" => a + b, "-" => a - b, "*" => a * b, _ => b == -1 ? -a : a / b });
        for (int i = 0; i < cases.Count; i++)
            Assert.Equal(new TestValue("integer", Expected(operators[cases[i].o], values[cases[i].l], values[cases[i].r]).ToString(System.Globalization.CultureInfo.InvariantCulture)), results[i]);
    }

    [RuntimeFact]
    public void RecursiveNonIntegerArgumentsRetainResumableExecution()
    {
        var results = EmitterTiers.AssertSame(["變數 f=函式(n){如果(n<2){回傳 n;}回傳 f(n-1)+f(n-2);};f(12.0d);"]);
        Assert.Equal("144", results[0].Text);
    }

    [RuntimeFact]
    public void AncestorSlotsFallBackAfterARuntimeInnerShadow()
    {
        // The C# test let a host builtin add the shadowing binding; here a declaration the compiler cannot prove runs
        // (its condition is a call) creates the inner shadow at run time.
        var results = EmitterTiers.AssertSame(["變數 x=1;變數 total=0;變數 shadow=函式(){真;};迴圈(變數 i=0;i<2;i++){如果(shadow()){變數 x=99;}total+=x;};[total,x];"]);
        Assert.Equal(new TestValue("array", "[198, 1]"), results[0]);
    }

    [RuntimeFact]
    public void AncestorSlotsPreserveShadowingAndCapturedLoopScopes()
    {
        EmitterTiers.AssertSame([
            "變數 x=40;迴圈(變數 i=0;i<2;i++){迴圈(變數 j=0;j<2;j++){x++;}}x;",
            "變數 x=10;迴圈(變數 i=0;i<2;i++){如果(i==1){變數 x=99;}迴圈(變數 j=0;j<2;j++){x++;}}x;",
            "變數 f=函式(n){變數 total=0;迴圈(變數 i=0;i<n;i++){迴圈(變數 j=0;j<n;j++){total+=i+j;}}total;};f(4);",
            "變數 a=[0,0];迴圈(變數 i=0;i<2;i++){變數 x=i;迴圈(變數 j=0;j<2;j++){a[i]=函式(){x++;x;};}};[a[0](),a[1]()];",
        ]);
    }

    [RuntimeFact]
    public void FailedSpeculationRetainsCallStateWhenARecursiveBindingChanges()
    {
        // The C# test rebound f from the host at a scheduler yield; here the recursion rebinds f itself part way through,
        // so the typed tier's speculation on the recursive name fails while activations are in flight.
        var results = EmitterTiers.AssertSame([
            "變數 replacement=函式(n){100;};變數 f=函式(n){如果(n==6){f=replacement;}如果(n<2){回傳 n;}回傳 f(n-1)+f(n-2);};f(12);",
            "變數 f=函式(n){如果(n<2){回傳 n;}回傳 f(n-1)+f(n-2);};變數 replacement=函式(n){100;};變數 g=函式(n){f=replacement;f(n);};[f(12),g(12),f(3)];",
        ]);
        Assert.Equal(new TestValue("array", "[144, 100, 100]"), results[1]);
    }
}
