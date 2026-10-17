using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Wasm;
using AquariusLang.Desktop.runtime;
using System.Buffers.Binary;
using System.Text;

namespace AquariusTests.Wasm;

public class WasmNumericFunctionTest
{
    [Theory]
    [InlineData("變數 f=函式(a,b){a+b;};[f(2,3),f(2.5f,3),f(2,3.25d)];")]
    [InlineData("變數 f=函式(a,a){a;};f(1,42);")]
    [InlineData("變數 f=函式(a){如果(a>0){回傳 a+2;}回傳 a-3;};[f(4),f(-2)];")]
    [InlineData("變數 f=函式(a){變數 b=a*3;變數 c=b/2;c+b;};f(7);")]
    [InlineData("變數 f=函式(a){a+=1.75d;[a];};f(1);")]
    [InlineData("變數 f=函式(a){a+=1.75d;a;};[f(1),f(1.0f)];")]
    [InlineData("變數 f=函式(a){變數 old=a++;變數 next=++a;old+next+a;};f(2147483647);")]
    [InlineData("變數 f=函式(a,b){a+b;};f(2147483647,1);")]
    [InlineData("變數 f=函式(a,b){a*b;};f(2147483647,2147483647);")]
    [InlineData("變數 f=函式(a,b){a/b;};[f(1,0),f(-2147483647-1,-1),f(7,-2)];")]
    [InlineData("變數 f=函式(a,b){a/b;};[f(0.0d,0.0d),f(1.0f,0.0f)];")]
    [InlineData("變數 f=函式(a){-a;};f(-2147483647-1);")]
    [InlineData("變數 f=函式(a,b){a==b;};[f(1,1.0d),f(1,真),f(真,真),f(真,假)];")]
    [InlineData("變數 f=函式(a,b){a!=b;};[f(1,1.0d),f(1,真),f(0.0d/0.0d,0.0d/0.0d)];")]
    [InlineData("變數 f=函式(a,b){a&&b;};[f(真,假),f(真,真)];")]
    [InlineData("變數 f=函式(a,b){a||b;};[f(假,假),f(真,假)];")]
    [InlineData("變數 f=函式(a){!a;};[f(真),f(假)];")]
    [InlineData("變數 f=函式(a){如果(a){回傳 42;}7;};[f(真),f(假)];")]
    [InlineData("變數 f=函式(a){如果(a){變數 x=42;}x;};f(假);")]
    [InlineData("變數 x=99;變數 f=函式(a){如果(a){變數 x=42;}x;};[f(假),f(真)];")]
    [InlineData("變數 x=10;變數 f=函式(a){如果(a){變數 x=1;}x+=2;x;};[f(假),x];")]
    [InlineData("變數 f=函式(a,b){a+b;};f(\"繁\",\"體\");")]
    [InlineData("變數 f=函式(a,b){a+b;};f(真,1);")]
    [InlineData("變數 f=函式(a){如果(a){1;}2;};f(1);")]
    [InlineData("變數 f=函式(a){a;};f([1,2]);")]
    [InlineData("變數 f=函式(){42;};f();")]
    [InlineData("變數 f=函式(a){a;};f();")]
    [InlineData("變數 fib=函式(n){如果(n<2){回傳 n;}回傳 fib(n-1)+fib(n-2);};fib(18);")]
    [InlineData("變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};f(20000);")]
    [InlineData("變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};變數 old=f;f=函式(n){100;};old(3);")]
    [InlineData("變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};變數 old=f;f=42;old(2);")]
    [InlineData("變數 make=函式(x){函式(y){x+y;};};make(10)(3);")]
    [InlineData("變數 f=函式(a){a+=2;a++;a;};f(1.5f);")]
    [InlineData("變數 f=函式(a){變數 x=a;x=x*3;x-=2;變數 old=x++;變數 next=++x;old+next+x;};f(2147483647);")]
    [InlineData("變數 f=函式(a){如果(a<0&&a>=-7){回傳 -a;}回傳 a/2;};[f(-7),f(8)];")]
    [InlineData("變數 f=函式(a){變數 x=1;如果(a<0){x=假;}如果(x==真){回傳 2;}回傳 a;};[f(-1),f(1)];")]
    [InlineData("變數 f=函式(a){如果(a>0){變數 x=a;}否則{變數 x=-a;}x;};[f(-7),f(7)];")]
    public void ScalarFunctionsPreserveGenericSemantics(string source)
    {
        foreach (int quantum in new[] { 16, WasmAbi.DesktopQuantum }) {
            using var generic = new WasmRuntime(quantum);
            using var optimized = new WasmRuntime(quantum);
            var expected = generic.Execute(new WasmCompiler(false).Compile(source));
            var actual = optimized.Execute(new WasmCompiler().Compile(source));
            Assert.Equal(expected?.GetType(), actual?.GetType());
            Assert.Equal(expected?.Inspect(), actual?.Inspect());
        }
    }

    [Fact]
    public void DirectScalarCallsAvoidScopeAndFrameAllocation()
    {
        var engine = new ObservedEngine(); using var runtime = new WasmRuntime(null, engine);
        var result = runtime.Execute(new WasmCompiler().Compile("變數 fib=函式(n){如果(n<2){回傳 n;}回傳 fib(n-1)+fib(n-2);};fib(20);"));
        Assert.Equal("6765", result.Inspect());
        Assert.True(engine.Instance!.Invoke("aqua_numeric_fast_calls") > 0);
        Assert.True(engine.Instance.Invoke("aqua_heap_bytes") < 32768);
    }

    [Fact]
    public void DepthLimitReturnsToStacklessRecursion()
    {
        var engine = new ObservedEngine(); using var runtime = new WasmRuntime(null, engine);
        Assert.Equal("20000", runtime.Execute(new WasmCompiler().Compile("變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};f(20000);")).Inspect());
        // The integer specialization deoptimizes its native activations into generic frames at the
        // depth bound instead of abandoning the speculation, so no fallback restarts the recursion.
        Assert.Equal(0, engine.Instance!.Invoke("aqua_numeric_depth_fallbacks"));
        Assert.True(engine.Instance!.Invoke("aqua_numeric_deopts") > 0);
    }

    [Fact]
    public void CapabilityEffectsNeverRunInsideRestartableScalarCode()
    {
        int calls = 0; var builtins = new Builtins();
        builtins.BuiltinFuncs["effect"] = new BuiltinObj(_ => { calls++; return new IntegerObj(calls); });
        using var runtime = new WasmRuntime(builtins);
        Assert.Equal("3", runtime.Execute(new WasmCompiler().Compile("變數 f=函式(n){effect()+n;};f(2);")).Inspect());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ScalarOperatorsMatchAcrossNumericTypesAndDynamicFallbacks()
    {
        IObject[] values = { new IntegerObj(int.MinValue), new IntegerObj(int.MaxValue), new IntegerObj(0), new IntegerObj(-7),
            new FloatObj(0.1f), new DoubleObj(1.75), new DoubleObj(double.NaN), new DoubleObj(double.PositiveInfinity),
            new BooleanObj(true), new BooleanObj(false), new StringObj("繁體") };
        foreach (string op in new[] { "+", "-", "*", "/", "<", ">", "<=", ">=", "==", "!=", "&&", "||" }) {
            string source = $"變數 f=函式(a,b){{a{op}b;}};f(left,right);";
            var genericProgram = new WasmCompiler(false).Compile(source); var optimizedProgram = new WasmCompiler().Compile(source);
            using var generic = new WasmRuntime(); using var optimized = new WasmRuntime();
            foreach (var left in values) foreach (var right in values) {
                var before = AquariusLang.Object.Environment.NewEnvironment(); before.Create("left", left); before.Create("right", right);
                var after = AquariusLang.Object.Environment.NewEnvironment(); after.Create("left", left); after.Create("right", right);
                var expected = generic.Execute(genericProgram, before); var actual = optimized.Execute(optimizedProgram, after);
                Assert.Equal(expected?.GetType(), actual?.GetType()); Assert.Equal(expected?.Inspect(), actual?.Inspect());
            }
        }
    }

    [Fact]
    public void IntegerRepresentationPreservesBoundaryArithmeticWithoutWrapping()
    {
        int[] values = { int.MinValue, int.MinValue + 1, -65537, -1, 0, 1, 65537, int.MaxValue - 1, int.MaxValue };
        foreach (string op in new[] { "+", "-", "*", "/" }) {
            string source = $"變數 f=函式(a,b){{a{op}b;}};f(left,right);";
            var genericProgram = new WasmCompiler(false).Compile(source);
            var optimizedProgram = new WasmCompiler().Compile(source);
            using var generic = new WasmRuntime(); var engine = new ObservedEngine(); using var optimized = new WasmRuntime(null, engine);
            int fastCalls = 0;
            foreach (int left in values) foreach (int right in values) {
                var before = AquariusLang.Object.Environment.NewEnvironment(); before.Create("left", new IntegerObj(left)); before.Create("right", new IntegerObj(right));
                var after = AquariusLang.Object.Environment.NewEnvironment(); after.Create("left", new IntegerObj(left)); after.Create("right", new IntegerObj(right));
                var expected = generic.Execute(genericProgram, before); var actual = optimized.Execute(optimizedProgram, after);
                Assert.IsType<IntegerObj>(actual); Assert.Equal(expected.Inspect(), actual.Inspect());
                Assert.Equal(++fastCalls, engine.Instance!.Invoke("aqua_numeric_fast_calls"));
            }
        }
    }

    [Fact]
    public void RecursiveNonIntegerArgumentsRetainResumableExecution()
    {
        const string source = "變數 f=函式(n){如果(n<2){回傳 n;}回傳 f(n-1)+f(n-2);};f(12.0d);";
        using var generic = new WasmRuntime(); var engine = new ObservedEngine(); using var optimized = new WasmRuntime(null, engine);
        var expected = generic.Execute(new WasmCompiler(false).Compile(source));
        var actual = optimized.Execute(new WasmCompiler().Compile(source));
        Assert.Equal(expected.GetType(), actual.GetType()); Assert.Equal(expected.Inspect(), actual.Inspect());
        Assert.Equal(0, engine.Instance!.Invoke("aqua_numeric_fast_calls"));
    }

    [Fact]
    public void AncestorSlotsFallBackAfterHostCreatesAnInnerShadow()
    {
        var builtins = new Builtins();
        builtins.BuiltinFuncs["edit"] = new BuiltinObj(args => {
            ((FunctionObj)args[0]).Env.Create("x", new IntegerObj(99)); return new IntegerObj(0);
        });
        const string source = "變數 x=1;變數 total=0;迴圈(變數 i=0;i<2;i++){edit(函式(){i;});total+=x;};[total,x];";
        using var runtime = new WasmRuntime(builtins);
        Assert.Equal("[198, 1]", runtime.Execute(new WasmCompiler().Compile(source)).Inspect());
    }

    [Theory]
    [InlineData("變數 x=40;迴圈(變數 i=0;i<2;i++){迴圈(變數 j=0;j<2;j++){x++;}}x;")]
    [InlineData("變數 x=10;迴圈(變數 i=0;i<2;i++){如果(i==1){變數 x=99;}迴圈(變數 j=0;j<2;j++){x++;}}x;")]
    [InlineData("變數 f=函式(n){變數 total=0;迴圈(變數 i=0;i<n;i++){迴圈(變數 j=0;j<n;j++){total+=i+j;}}total;};f(4);")]
    [InlineData("變數 a=[0,0];迴圈(變數 i=0;i<2;i++){變數 x=i;迴圈(變數 j=0;j<2;j++){a[i]=函式(){x++;x;};}};[a[0](),a[1]()];")]
    public void AncestorSlotsPreserveShadowingAndCapturedLoopScopes(string source)
    {
        using var generic = new WasmRuntime(); using var optimized = new WasmRuntime();
        Assert.Equal(generic.Execute(new WasmCompiler(false).Compile(source)).Inspect(),
            optimized.Execute(new WasmCompiler().Compile(source)).Inspect());
    }

    [Fact]
    public void FailedSpeculationRetainsCallStateWhenARecursiveBindingChangesAtYield()
    {
        const string source = "變數 f=函式(n){如果(n<2){回傳 n;}回傳 f(n-1)+f(n-2);};變數 replacement=函式(n){100;};f(12);";
        var before = new MutateAtYield(); var after = new MutateAtYield();
        using var generic = new WasmRuntime(null, before, 8); using var optimized = new WasmRuntime(null, after, 8);
        var expected = generic.Execute(new WasmCompiler(false).Compile(source));
        var actual = optimized.Execute(new WasmCompiler().Compile(source));
        Assert.True(before.Changed); Assert.True(after.Changed); Assert.Equal(expected.Inspect(), actual.Inspect());
    }

    private sealed class MutateAtYield : IWasmEngine
    {
        public bool Changed;
        public IWasmInstance Instantiate(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, Delegate> imports)
            => new Instance(new WasmtimeEngine().Instantiate(bytes, imports), this);
        private sealed class Instance(IWasmInstance inner, MutateAtYield owner) : IWasmInstance
        {
            int Int(int address) => BinaryPrimitives.ReadInt32LittleEndian(inner.Memory(address, 4));
            public int Invoke(string name, params int[] args)
            {
                int result = inner.Invoke(name, args);
                if (name == "aqua_run" && result == 1 && !owner.Changed) {
                    int scope = Int(Int(args[0] + 4) + 16);
                    while (Int(scope) != 0) scope = Int(scope);
                    int target = 0, replacement = 0, targetName = 0;
                    int count = inner.Invoke("aqua_scope_count", scope);
                    for (int i = 0; i < count; i++) {
                        int binding = inner.Invoke("aqua_scope_binding", scope, i), text = Int(binding);
                        string identifier = Encoding.Unicode.GetString(inner.Memory(text + 4, Int(text) * 2));
                        if (identifier == "f") { target = binding; targetName = text; }
                        if (identifier == "replacement") replacement = binding;
                    }
                    if (target != 0 && replacement != 0) {
                        inner.Invoke("aqua_scope_assign", scope, targetName, replacement + 8); owner.Changed = true;
                    }
                }
                return result;
            }
            public Span<byte> Memory(int address, int length) => inner.Memory(address, length);
            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class ObservedEngine : IWasmEngine
    {
        public IWasmInstance? Instance;
        public IWasmInstance Instantiate(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, Delegate> imports)
            => Instance = new WasmtimeEngine().Instantiate(bytes, imports);
    }
}
