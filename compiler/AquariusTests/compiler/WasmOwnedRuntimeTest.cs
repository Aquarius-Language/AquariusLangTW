using AquariusLang.Wasm;

namespace AquariusTests.Wasm;

/// <summary>Values owned by the guest runtime (arrays, hashes, scopes, frames) on the native runtime. The C# host's
/// mirrors of guest values, its array/hash transfer counters, host-created environments and its disposal of Wasmtime
/// instances no longer exist: the native host reads guest values in place (runtime/host/values.c) and syncs exposed
/// arrays itself. The language-observable halves of those tests stay.</summary>
public class WasmOwnedRuntimeTest
{
    [RuntimeFact]
    public void 雜湊表寫入保留別名與型別鍵()
    {
        Assert.Equal(new TestValue("array", "[7, 7, 8, 9, 10, 4]"), WasmTestHost.Eval("""
            變數 資料={"值":1};變數 別名=資料;資料["值"]=7;資料[1]=8;資料[1.0d]=9;資料[真]=10;
            [資料["值"],別名["值"],別名[1],別名[1.0d],別名[真],長度(別名)];
            """));
    }

    [RuntimeFact]
    public void 整數陣列保留極值與別名()
    {
        Assert.Equal(new TestValue("array", "[-2147483648, 2147483647, 繁體中文, 8192]"), WasmTestHost.Eval("""
            變數 a=陣列.建立(8192,0);迴圈(變數 i=0;i<8192;i++){a[i]=i;}
            變數 b=a;a[0]=0-2147483647-1;a[8191]=2147483647;
            變數 檢查=函式(陣){陣[3]="繁體中文";};檢查(a);
            [b[0],b[8191],b[3],長度(b)];
            """));
    }

    [RuntimeFact]
    public void 雜湊表無效鍵先拒絕且成長循環可回收()
    {
        // The invalid key fails before the right-hand side runs (its side effect would print).
        var run = WasmTestHost.Run("變數 副作用=函式(){印出(\"副作用\");1;};變數 表={};表[[]]=副作用();");
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["ERROR: Unusable as hash key: ARRAY"], run.Lines);
        var values = WasmTestHost.EvalMany([
            """
            變數 表={};變數 別名=表;迴圈(變數 索=0;索<1000;索++){表[索]=索;}
            表["自己"]=表;[別名[999],表["自己"]==表,長度(別名)];
            """,
            """
            變數 表={};變數 倍=0.0d/0.0d;變數 單=0.0f/0.0f;
            表[倍]=4;表[單]=5;表[-0.0d]=6;[表[倍],表[單],表[0.0d]];
            """,
        ]);
        Assert.Equal(new TestValue("array", "[999, 真, 1001]"), values[0]);
        Assert.Equal(new TestValue("array", "[4, 5, 6]"), values[1]);
    }

    [RuntimeFact]
    public void BindingCachesInvalidateForShadowingGrowthAndCallbackReentry()
    {
        // The C# test re-entered the runtime from a host builtin; a guest function calling the callback has the same
        // scope effects. Runs at top level, where the scope is dynamic.
        var results = EmitterTiers.AssertSame([
            "變數 reenter=函式(f){f();};變數 value=1;變數 total=0;迴圈(變數 i=0;i<3;i++){total+=value;變數 value=i+10;total+=value;reenter(函式(){value+=1;});total+=value;};[total,value];",
        ]);
        Assert.Equal(new TestValue("array", "[72, 1]"), results[0]);
    }

    [RuntimeFact]
    public void SharedArrayStoragePreservesSnapshotsAndAliases()
    {
        // push copies (the C# test also checked that the host's push was never used and that a host write reached the
        // guest; here the guest writes c itself).
        Assert.Equal(new TestValue("array", "[[9], [9], [8, 2], [7, 3], [1, 2, 4]]"), WasmTestHost.Eval("""
            變數 a=[1];變數 alias=a;變數 b=push(a,2);變數 c=push(a,3);
            a[0]=9;變數 d=push(b,4);b[0]=8;c[0]=7;
            [a,alias,b,c,d];
            """));
    }

    [RuntimeFact]
    public void TypedNaNHashKeysAndSignedZeroRemainLookupable()
    {
        Assert.Equal(new TestValue("array", "[3, 4, 7, 假]"), WasmTestHost.Eval("變數 d=0.0d/0.0d;變數 f=0.0f/0.0f;變數 h={d:3,f:4,-0.0d:7};[h[d],h[f],h[0.0d],d==d];"));
    }

    [RuntimeFact]
    public void IndexedScopesPreserveBindingsAfterGrowthAndIterationReset()
    {
        var declarations = string.Concat(Enumerable.Range(0, 100).Select(i => $"變數 binding{i}={i};"));
        var terms = string.Join("+", Enumerable.Range(0, 100).Select(i => $"binding{i}"));
        // Top level, so the 100 bindings grow the dynamic global scope.
        Assert.Equal("9900\n", WasmTestHost.Output(declarations + $"變數 total=0;迴圈(變數 i=0;i<2;i++){{變數 local={terms};total+=local;}};total;"));
    }

    [RuntimeFact]
    public void RepeatedAllocationReusesTheHeap()
    {
        // The C# test ran the program 20 times on one instance and checked the guest heap stayed under 32 MiB and that no
        // host call happened; natively each run is a process, so one program repeats the work 20 times instead.
        Assert.Equal("[999, 20]\n", WasmTestHost.Output(
            "變數 last=0;變數 runs=0;迴圈(變數 run=0;run<20;run++){變數 a=[];迴圈(變數 i=0;i<1000;i++){a=push(a,i);};last=a[999];runs++;};[last,runs];"));
    }

    [Fact]
    public void FramesAreSizedByLiveOperandsAcrossLoopsAndCalls()
    {
        var program = new WasmCompiler().Compile(DeepFrameSource);
        Assert.All(program.Functions, function => Assert.Equal(16, function.StackCapacity));
        Assert.Equal(100, new WasmCompiler().Compile(WideArraySource).Functions[0].StackCapacity);
    }

    private static readonly string DeepFrameSource = "變數 f=函式(n){" + string.Concat(Enumerable.Repeat("n=n+1;", 1000)) + "回傳 n;};" +
        "變數 result=0;迴圈(變數 i=0;i<2;i++){如果(i==1){中斷;}result=f(i);};result;";
    private static readonly string WideArraySource = "[" + string.Join(",", Enumerable.Range(0, 100)) + "];";

    [RuntimeFact]
    public void FramesSizedByLiveOperandsRun()
    {
        Assert.Equal("1000\n", WasmTestHost.Output(DeepFrameSource));
        Assert.Equal(new TestValue("integer", "100"), WasmTestHost.Eval("長度(" + WideArraySource.TrimEnd(';') + ");"));
    }

    [RuntimeFact]
    public void 修改雜湊欄位與呼叫主機函式()
    {
        // The C# test counted array transfers to the host; here the hash with a 1000-element array is updated between host
        // calls (目前時間, 目前日期) and keeps its contents.
        Assert.Equal(new TestValue("array", "[499, 1000, 999]"), WasmTestHost.Eval("""
            變數 卡片=陣列.建立(1000,0);迴圈(變數 i=0;i<1000;i++){卡片[i]=i;}
            變數 資料={"卡片":卡片};
            迴圈(變數 i=0;i<500;i++){資料["計數"]=i;目前時間();目前日期();};
            [資料["計數"],長度(資料["卡片"]),資料["卡片"][999]];
            """));
    }

    [RuntimeFact]
    public void NumericLoopsClosuresCollectionsAndPortableBuiltins()
    {
        // The C# test also checked that none of this called the host (len and push run in the guest runtime).
        Assert.Equal(new TestValue("array", "[10000, 42, 2, 49995000]"), WasmTestHost.Eval("""
            變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};
            變數 a=push([f(10000)],42);變數 total=0;
            迴圈(變數 i=0;i<10000;i++){total+=i;};
            [a[0],a[1],len(a),total];
            """));
    }

    [RuntimeFact]
    public void CyclicArraysCrossTheHostBoundaryWithoutRecursingOnTheHostStack()
    {
        // 型別 and 印出 are host functions; both receive the cyclic array (印出 elides the repeated nesting).
        var run = WasmTestHost.Run("變數 cycle=[0];cycle[0]=cycle;印出(型別(cycle));印出(cycle);cycle[0][0][0]==cycle;").Succeeded();
        Assert.Equal("array", run.Lines[0]);
        Assert.StartsWith("[[[", run.Lines[1]); Assert.Contains("...", run.Lines[1]); Assert.EndsWith("]]]", run.Lines[1]);
        Assert.Equal("真", run.Lines[2]);
    }

    [RuntimeFact]
    public void HashesPreserveOriginalTypedKeys()
    {
        // The C# test checked the C# projection's keys. Keys keep their type: 1.0d, 1.0f and 1 are three different keys.
        Assert.Equal(new TestValue("array", "[{first: 1, second: 2, 1: 3, 1: 4}, 4, 3, 4, null]"),
            WasmTestHost.Eval("變數 h={\"first\":1,\"second\":2,1.0d:3,1.0f:4};[h,長度(h),h[1.0d],h[1.0f],h[1]];"));
    }

    [RuntimeFact]
    public void ArrayResizeAndHashMutationPreserveAliases()
    {
        // The C# test resized the array from the host; the in-place Array functions do it natively.
        Assert.Equal(new TestValue("array", "[42, 99, [7, 42]]"), WasmTestHost.Eval("""
            變數 a=[0];變數 alias=a;變數 h={"old":1};
            變數 edit=函式(array,hash){陣列.追加(array,42);array[0]=7;hash["new"]=99;};
            edit(a,h);[alias[1],h["new"],alias];
            """));
    }

    [RuntimeFact]
    public void ComputedModuleMembersKeepTheirModuleScopeAcrossCalls()
    {
        // The C# test projected a host module holding an array of builtins; a packaged module does the same.
        var run = WasmTestHost.RunModules(new Dictionary<string, string> {
            ["main.aqua"] = "變數 m=匯入(\"m.aqua\");[m.functions[0](40),m.functions[0](40),m.functions[1]()];",
            ["m.aqua"] = "變數 offset=2;變數 functions=[函式(x){x+offset;},函式(){offset++;offset;}];",
        }, "main.aqua").Succeeded();
        Assert.Equal("[42, 42, 3]\n", run.Output);
    }
}
