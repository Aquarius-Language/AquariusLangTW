using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Wasm;
using AquariusLang.Desktop.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusTests.Wasm;

public class WasmOwnedRuntimeTest
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ExecuteTemporaryRuntime(Counter counter)
    {
        Assert.Equal("42", new WasmRuntime(engine: counter).Execute(new WasmCompiler().Compile("42;")).Inspect());
    }
    [Fact]
    public void NativeCallbackHandlesDoNotKeepUnownedSessionsAlive()
    {
        var counter = new Counter(); ExecuteTemporaryRuntime(counter);
        for (int i = 0; i < 3 && counter.Disposals == 0; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.Equal(1, counter.Disposals);
    }
    [Fact]
    public void BindingCachesInvalidateForShadowingGrowthAndCallbackReentry()
    {
        var builtins = new Builtins(); WasmRuntime runtime = null!;
        builtins.BuiltinFuncs["reenter"] = new BuiltinObj(args => runtime.Invoke(args[0]));
        using (runtime = new WasmRuntime(builtins))
        {
            var source = "變數 value=1;變數 total=0;迴圈(變數 i=0;i<3;i++){total+=value;變數 value=i+10;total+=value;reenter(函式(){value+=1;});total+=value;};[total,value];";
            Assert.Equal("[72, 1]", runtime.Execute(new WasmCompiler().Compile(source)).Inspect());
        }
    }
    [Fact]
    public void SharedArrayStoragePreservesSnapshotsAliasesAndHostWrites()
    {
        var builtins = new Builtins();
        builtins.BuiltinFuncs["push"] = BuiltinObj.FromPortable(4, _ => throw new Exception("Host push used."));
        builtins.BuiltinFuncs["edit"] = new BuiltinObj(args => { ((ArrayObj)args[0]).Elements[0] = new IntegerObj(7); return null!; });
        using var runtime = new WasmRuntime(builtins);
        var program = new WasmCompiler().Compile("""
            變數 a=[1];變數 alias=a;變數 b=push(a,2);變數 c=push(a,3);
            a[0]=9;變數 d=push(b,4);b[0]=8;edit(c);
            [a,alias,b,c,d];
            """);
        Assert.Equal("[[9], [9], [8, 2], [7, 3], [1, 2, 4]]", runtime.Execute(program).Inspect());
    }
    [Fact]
    public void TypedNaNHashKeysAndSignedZeroRemainLookupable()
    {
        using var runtime = new WasmRuntime();
        Assert.Equal("[3, 4, 7, 假]", runtime.Execute(new WasmCompiler().Compile("變數 d=0.0d/0.0d;變數 f=0.0f/0.0f;變數 h={d:3,f:4,-0.0d:7};[h[d],h[f],h[0.0d],d==d];")).Inspect());
    }
    [Fact]
    public void IndexedScopesPreserveBindingsAfterGrowthAndIterationReset()
    {
        var declarations = string.Concat(Enumerable.Range(0, 100).Select(i => $"變數 binding{i}={i};"));
        var terms = string.Join("+", Enumerable.Range(0, 100).Select(i => $"binding{i}"));
        using var runtime = new WasmRuntime();
        Assert.Equal("9900", runtime.Execute(new WasmCompiler().Compile(declarations + $"變數 total=0;迴圈(變數 i=0;i<2;i++){{變數 local={terms};total+=local;}};total;")).Inspect());
    }
    [Fact]
    public void AnonymousRunsReuseHeapWithoutDependingOnManagedCollection()
    {
        var builtins = new Builtins();
        builtins.BuiltinFuncs["push"] = BuiltinObj.FromPortable(4, _ => throw new Exception("Host push used."));
        var counter = new Counter(); using var runtime = new WasmRuntime(builtins, counter);
        var program = new WasmCompiler().Compile("變數 a=[];迴圈(變數 i=0;i<1000;i++){a=push(a,i);};a[999];");
        for (int i = 0; i < 20; i++) Assert.Equal("999", runtime.Execute(program).Inspect());
        Assert.InRange(counter.Instance!.Invoke("aqua_heap_bytes"), 1, 32 * 1024 * 1024);
        Assert.Equal(0, counter.NativeCalls);
    }
    [Fact]
    public void DisposalReleasesOwnedInstancesAndRejectsRetainedCallbacks()
    {
        var counter = new Counter(); var runtime = new WasmRuntime(engine: counter);
        var program = new WasmCompiler().Compile("函式(){回傳 42;};");
        var callback = runtime.Execute(program);
        Assert.Equal("42", runtime.Invoke(callback).Inspect());
        runtime.Dispose(); runtime.Dispose();
        Assert.Equal(1, counter.Disposals);
        Assert.Throws<ObjectDisposedException>(() => runtime.Execute(program));
        using var other = new WasmRuntime();
        Assert.Throws<ObjectDisposedException>(() => other.Invoke(callback));
    }
    [Fact]
    public void FramesAreSizedByLiveOperandsAcrossLoopsAndCalls()
    {
        var source = "變數 f=函式(n){" + string.Concat(Enumerable.Repeat("n=n+1;", 1000)) + "回傳 n;};" +
            "變數 result=0;迴圈(變數 i=0;i<2;i++){如果(i==1){中斷;}result=f(i);};result;";
        var program = new WasmCompiler().Compile(source);
        Assert.All(program.Metadata.Functions, function => Assert.Equal(16, function.StackCapacity));
        Assert.Equal("1000", new WasmRuntime().Execute(program).Inspect());
        var wide = new WasmCompiler().Compile("[" + string.Join(",", Enumerable.Range(0, 100)) + "];");
        Assert.Equal(100, wide.Metadata.Functions[0].StackCapacity);
        Assert.Equal(100, Assert.IsType<ArrayObj>(new WasmRuntime().Execute(wide)).Elements.Length);
    }
    private sealed class Counter : IWasmEngine
    {
        public int NativeCalls, Lookups, ArrayTransfers, Disposals;
        public IWasmInstance? Instance;
        private sealed class Counted(IWasmInstance inner, Counter owner) : IWasmInstance
        {
            public int Invoke(string name, params int[] arguments) { if (name == "aqua_array_values") owner.ArrayTransfers++; return inner.Invoke(name, arguments); }
            public Span<byte> Memory(int address, int length) => inner.Memory(address, length);
            public void Dispose() { owner.Disposals++; inner.Dispose(); }
        }
        public IWasmInstance Instantiate(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, Delegate> imports)
        {
            Assert.Equal(new[] { "service" }, imports.Keys);
            var service = Assert.IsType<Func<int, int, int, int, int, int, int, int>>(imports["service"]);
            return Instance = new Counted(new WasmtimeEngine().Instantiate(bytes, new Dictionary<string, Delegate>
            {
                ["service"] = (Func<int, int, int, int, int, int, int, int>)((op, context, subject, name, arguments, count, output) =>
                {
                    if (op == 1) NativeCalls++; else if (op == 0) Lookups++;
                    return service(op, context, subject, name, arguments, count, output);
                })
            }), this);
        }
    }
    [Fact]
    public void PixelBuffersAreTransferredToTheirOwnerInsteadOfEveryScalarCapability()
    {
        var pixels = new ArrayObj(Enumerable.Range(0, 1000).Select(_ => (IObject)new IntegerObj(0)).ToArray());
        var image = AquaEnvironment.NewEnvironment(); image.Create("pixels", pixels);
        image.Create("update", new BuiltinObj(_ => pixels.Elements[999]) { RetainedEnvironment = image });
        var math = AquaEnvironment.NewEnvironment(); math.Create("scalar", new BuiltinObj(args => args[0]) { RetainedEnvironment = math });
        var scope = AquaEnvironment.NewEnvironment(); scope.Create("image", new ModuleObj(image)); scope.Create("math", new ModuleObj(math));
        var counter = new Counter();
        Assert.Equal("999", new WasmRuntime(engine: counter).Execute(new WasmCompiler().Compile("變數 pixels=image.pixels;迴圈(變數 i=0;i<1000;i++){pixels[i]=math.scalar(i);};image.update();"), scope).Inspect());
        Assert.Equal(1001, counter.NativeCalls); Assert.InRange(counter.ArrayTransfers, 1, 10);
    }
    [Fact]
    public void NumericLoopsClosuresCollectionsAndPortableBuiltinsExecuteWithoutNativeCalls()
    {
        var builtins = new Builtins();
        builtins.BuiltinFuncs["len"] = BuiltinObj.FromPortable(1, _ => throw new Exception("Host length implementation was called."));
        builtins.BuiltinFuncs["push"] = BuiltinObj.FromPortable(4, _ => throw new Exception("Host array implementation was called."));
        var counter = new Counter();
        var program = new WasmCompiler().Compile("""
            變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};
            變數 a=push([f(10000)],42);變數 total=0;
            迴圈(變數 i=0;i<10000;i++){total+=i;};
            [a[0],a[1],len(a),total];
            """);
        Assert.Equal("[10000, 42, 2, 49995000]", new WasmRuntime(builtins, counter).Execute(program).Inspect());
        Assert.Equal(0, counter.NativeCalls); Assert.Equal(2, counter.Lookups);
    }
    [Fact]
    public void HostRetainedArraysObserveGuestEditsWithoutBeingPassedAgain()
    {
        var pixels = new ArrayObj(new IObject[] { new IntegerObj(0) });
        var builtins = new Builtins(); builtins.BuiltinFuncs["check"] = new BuiltinObj(_ => pixels.Elements[0]);
        var scope = AquaEnvironment.NewEnvironment(); scope.Create("pixels", pixels);
        Assert.Equal("42", new WasmRuntime(builtins).Execute(new WasmCompiler().Compile("pixels[0]=42;check();"), scope).Inspect());
    }
    [Fact]
    public void CyclicArraysCrossTheCapabilityBoundaryWithoutRecursingOnTheHostStack()
    {
        var cycle = new ArrayObj(new IObject[1]); cycle.Elements[0] = cycle;
        var scope = AquaEnvironment.NewEnvironment(); scope.Create("cycle", cycle);
        var builtins = new Builtins(); builtins.BuiltinFuncs["check"] = new BuiltinObj(args =>
        {
            Assert.Same(cycle, args[0]); Assert.Same(cycle, cycle.Elements[0]); return new IntegerObj(42);
        });
        Assert.Equal("42", new WasmRuntime(builtins).Execute(new WasmCompiler().Compile("check(cycle);"), scope).Inspect());
    }
    [Fact]
    public void HashProjectionPreservesOriginalKeysRatherThanClrHashCodes()
    {
        var result = Assert.IsType<HashObj>(new WasmRuntime().Execute(new WasmCompiler().Compile("{\"first\":1,\"second\":2,1.0d:3,1.0f:4};")));
        Assert.Equal("first", new StringObj("first").HashKey().Value);
        Assert.Equal(1d, new DoubleObj(1d).HashKey().Value);
        Assert.Equal(4, result.Pairs.Count);
        Assert.Equal("3", result.Pairs[new DoubleObj(1d).HashKey()].Value.Inspect());
    }
    [Fact]
    public void NativeArrayResizeAndHashMutationPreserveGuestAliases()
    {
        var builtins = new Builtins();
        builtins.BuiltinFuncs["edit"] = new BuiltinObj(args => {
            ((ArrayObj)args[0]).Elements = new IObject[] { new IntegerObj(7), new IntegerObj(42) };
            var key = new StringObj("new"); ((HashObj)args[1]).Pairs[key.HashKey()] = new HashPair(key, new IntegerObj(99));
            return null!;
        });
        Assert.Equal("[42, 99]", new WasmRuntime(builtins).Execute(new WasmCompiler().Compile("變數 a=[0];變數 alias=a;變數 h={\"old\":1};edit(a,h);[alias[1],h[\"new\"]];")).Inspect());
    }
    [Fact]
    public void ComputedNativeMembersRetainTheirProjectedModuleScopeAcrossCalls()
    {
        var module = AquaEnvironment.NewEnvironment();
        module.Create("functions", new ArrayObj(new IObject[] { new BuiltinObj(args => new IntegerObj(((IntegerObj)args[0]).Value + 2)) }));
        var scope = AquaEnvironment.NewEnvironment(); scope.Create("m", new ModuleObj(module));
        var program = new WasmCompiler().Compile("m.functions[0](40);"); var runtime = new WasmRuntime();
        Assert.Equal("42", runtime.Execute(program, scope).Inspect()); Assert.Equal("42", runtime.Execute(program, scope).Inspect());
    }
}
