using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Wasm;
using AquariusLang.Desktop.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusTests.Wasm;

public class WasmPerformanceTest
{
    // Check language behavior against the generic emitter, including fallback cases.
    [Theory]
    [InlineData("變數 f=函式(a,b){a+=b;[a++,++b,a,b];};f(3,4);")]
    [InlineData("變數 f=函式(a,a,b){[a,b];};f(1,2,40);")]
    [InlineData("變數 f=函式(n){如果(n>0){變數 skipped=9;}變數 x=40;x+=2;[n,x];};f(0);")]
    [InlineData("變數 x=1;變數 a=[0,0,0];迴圈(變數 i=0;i<3;i++){變數 x=i+10;a[i]=函式(){x++;x;};};[a[0](),a[1](),a[2](),x];")]
    [InlineData("變數 s=0;迴圈(變數 i=0;i<3;i++){迴圈(變數 j=0;j<3;j++){如果(j==1){中斷;}s+=i+j;}}s;")]
    [InlineData("變數 n=2147483647;[n++,n,++n];")]
    [InlineData("變數 n=2147483647;n+=1;n;")]
    [InlineData("變數 n=0-2147483647-1;n-=1;n;")]
    [InlineData("變數 n=1;n+=1.75d;變數 f=1.0f;f+=0.1d;[n,f];")]
    [InlineData("變數 n=1;n/=0;n;")]
    [InlineData("變數 d=0.0d;d/=0;[d,d==d];")]
    [InlineData("變數 f=1.0f/0.0f;f++;f;")]
    [InlineData("變數 n=7;n/=(-2);n;")]
    [InlineData("變數 s=\"繁\";s+=\"體\";s;")]
    [InlineData("變數 s=真;s+=1;s;")]
    [InlineData("變數 s=\"繁\";s++;s;")]
    [InlineData("變數 a=[4,5];[a[0],a[1],a[-1],a[2],a[2147483647]];")]
    [InlineData("變數 a=[4];a[0.0d];")]
    [InlineData("變數 a=[4];a[真];")]
    [InlineData("變數 h={\"值\":42,1:7};[h[\"值\"],h[1],h[99],\"繁體\"[1]];")]
    [InlineData("變數 a=[1];變數 alias=a;a[0]=a;[alias==a,a[0]==a];")]
    [InlineData("變數 a=[1,2];變數 x=0;x=(a[0]+a[1])*14;x;")]
    [InlineData("變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};f(20000);")]
    public void GuardedFastPathsMatchTheGenericEmitter(string source)
    {
        using var generic = new WasmRuntime(); using var optimized = new WasmRuntime();
        var expected = generic.Execute(new WasmCompiler(optimize: false).Compile(source));
        var actual = optimized.Execute(new WasmCompiler().Compile(source));
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Inspect(), actual.Inspect());
    }

    [Fact]
    public void CompoundAssignmentsResolveTheBindingAfterCallbackSideEffects()
    {
        foreach (bool optimize in new[] { false, true })
        {
            var environment = AquaEnvironment.NewEnvironment(); var builtins = new Builtins();
            builtins.BuiltinFuncs["edit"] = new BuiltinObj(_ => { environment.Create("x", new IntegerObj(10)); return new IntegerObj(2); });
            using var runtime = new WasmRuntime(builtins);
            Assert.Equal("12", runtime.Execute(new WasmCompiler(optimize).Compile("變數 x=1;x+=edit();x;"), environment).Inspect());
        }
    }

    [Fact]
    public void SlotGuardsHandleHostInsertionAndReentry()
    {
        var environment = AquaEnvironment.NewEnvironment(); environment.Create("unrelated", new IntegerObj(99));
        var builtins = new Builtins(); WasmRuntime runtime = null!;
        builtins.BuiltinFuncs["reenter"] = new BuiltinObj(args => runtime.Invoke(args[0]));
        using (runtime = new WasmRuntime(builtins, null, quantum: 16))
        {
            var program = new WasmCompiler().Compile("變數 x=1;變數 total=0;迴圈(變數 i=0;i<3;i++){total+=x;變數 x=i+10;reenter(函式(){x+=1;});total+=x;};[total,x];");
            Assert.Equal("[39, 1]", runtime.Execute(program, environment).Inspect());
        }
    }

    [Fact]
    public void NonallocatingYieldsRetainCapabilitiesWithoutRepeatedFullCollection()
    {
        int calls = 0; var builtins = new Builtins(); var counter = new Counter();
        builtins.BuiltinFuncs["probe"] = new BuiltinObj(_ => { calls++; return new IntegerObj(42); });
        using var runtime = new WasmRuntime(builtins, counter, quantum: 16);
        Assert.Equal("20042", runtime.Execute(new WasmCompiler().Compile("probe();變數 sum=0;迴圈(變數 i=0;i<20000;i++){sum++;}probe()+sum;")).Inspect());
        Assert.Equal(2, calls); Assert.True(counter.SchedulerEntries > 100);
        Assert.Equal(1, counter.Collections); Assert.Equal(16, counter.LastBudget);
    }

    [Fact]
    public void CompletionCollectionRecyclesUnusedNativeHandlesAndPreservesReturnedCallbacks()
    {
        var builtins = new Builtins(); var counter = new Counter();
        builtins.BuiltinFuncs["factory"] = new BuiltinObj(_ => new BuiltinObj(_ => new IntegerObj(42)));
        using var runtime = new WasmRuntime(builtins, counter);
        var program = new WasmCompiler().Compile("迴圈(變數 i=0;i<100;i++){factory();}函式(){42;};");
        IObject callback = null!;
        for (int i = 0; i < 25; i++) callback = runtime.Execute(program);
        Assert.Equal("42", runtime.Invoke(callback).Inspect());
        Assert.True(counter.Collections >= 26);
        Assert.InRange(counter.NativeLivenessChecks, 1, 3000);
        int before = counter.Instance!.Invoke("aqua_collection_generation");
        counter.Instance.Invoke("aqua_collect");
        Assert.NotEqual(before, counter.Instance.Invoke("aqua_collection_generation"));
    }

    [Fact]
    public void OlderAbiTwoArtifactsKeepSafeEagerCollection()
    {
        using var stream = typeof(WasmPerformanceTest).Assembly.GetManifestResourceStream("AquariusTests.Wasm.legacy-performance-v2.wasm")!;
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        var builtins = new Builtins(); builtins.BuiltinFuncs["probe"] = new BuiltinObj(_ => new IntegerObj(42));
        var counter = new Counter(); using var runtime = new WasmRuntime(builtins, counter, quantum: 16);
        Assert.Equal("499542", runtime.Execute(WasmProgram.Load(bytes.ToArray())).Inspect());
        Assert.True(counter.Collections > 10);
    }

    [Fact]
    public void StaleCollectionResultsCannotReleaseLiveNativeHandles()
    {
        var builtins = new Builtins(); builtins.BuiltinFuncs["probe"] = new BuiltinObj(_ => new IntegerObj(42));
        using var runtime = new WasmRuntime(builtins, new Counter { SuppressCollection = true });
        var error = Assert.Throws<InvalidOperationException>(() => runtime.Execute(new WasmCompiler().Compile("probe();")));
        Assert.Contains("did not refresh native liveness", error.Message);
    }

    [Fact]
    public void DesktopBudgetIsExplicitAndInvalidBudgetsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WasmRuntime(quantum: 0));
        var counter = new Counter(); using var runtime = new WasmRuntime(engine: counter);
        Assert.Equal("42", runtime.Execute(new WasmCompiler().Compile("42;")).Inspect());
        Assert.Equal(WasmAbi.DesktopQuantum, counter.LastBudget);
    }

    private sealed class Counter : IWasmEngine
    {
        public int Collections, SchedulerEntries, LastBudget, NativeLivenessChecks;
        public bool SuppressCollection;
        public IWasmInstance? Instance;
        public IWasmInstance Instantiate(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, Delegate> imports) =>
            Instance = new Counted(new WasmtimeEngine().Instantiate(bytes, imports), this);
        private sealed class Counted(IWasmInstance instance, Counter owner) : IWasmInstance
        {
            public int Invoke(string name, params int[] arguments)
            {
                if (name == "aqua_run") { owner.SchedulerEntries++; owner.LastBudget = arguments[1]; }
                if (name == "aqua_native_live") owner.NativeLivenessChecks++;
                if (name == "aqua_collect") { owner.Collections++; if (owner.SuppressCollection) return 0; }
                return instance.Invoke(name, arguments);
            }
            public Span<byte> Memory(int address, int length) => instance.Memory(address, length);
            public void Dispose() => instance.Dispose();
        }
    }
}
