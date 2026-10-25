namespace AquariusTests.Wasm;

/// <summary>The optimizing emitter's guarded fast paths against the generic emitter, on the native runtime. Tests of the C#
/// host's collection and scheduling internals (collection counters, liveness checks, fuel budgets, ABI 2 artifacts) were
/// removed: the native host owns them (runtime/host/session.c, scheduler.c) and ABI 2 loading no longer exists.</summary>
public class WasmPerformanceTest
{
    // Check language behavior against the generic emitter, including fallback cases.
    private static readonly string[] Programs = [
        "變數 f=函式(a,b){a+=b;[a++,++b,a,b];};f(3,4);",
        "變數 f=函式(a,a,b){[a,b];};f(1,2,40);",
        "變數 f=函式(n){如果(n>0){變數 skipped=9;}變數 x=40;x+=2;[n,x];};f(0);",
        "變數 x=1;變數 a=[0,0,0];迴圈(變數 i=0;i<3;i++){變數 x=i+10;a[i]=函式(){x++;x;};};[a[0](),a[1](),a[2](),x];",
        "變數 s=0;迴圈(變數 i=0;i<3;i++){迴圈(變數 j=0;j<3;j++){如果(j==1){中斷;}s+=i+j;}}s;",
        "變數 n=2147483647;[n++,n,++n];",
        "變數 n=2147483647;n+=1;n;",
        "變數 n=0-2147483647-1;n-=1;n;",
        "變數 n=1;n+=1.75d;變數 f=1.0f;f+=0.1d;[n,f];",
        "變數 n=1;n/=0;n;",
        "變數 d=0.0d;d/=0;[d,d==d];",
        "變數 f=1.0f/0.0f;f++;f;",
        "變數 n=7;n/=(-2);n;",
        "變數 s=\"繁\";s+=\"體\";s;",
        "變數 s=真;s+=1;s;",
        "變數 s=\"繁\";s++;s;",
        "變數 a=[4,5];[a[0],a[1],a[-1],a[2],a[2147483647]];",
        "變數 a=[4];a[0.0d];",
        "變數 a=[4];a[真];",
        "變數 h={\"值\":42,1:7};[h[\"值\"],h[1],h[99],\"繁體\"[1]];",
        "變數 a=[1];變數 alias=a;a[0]=a;[alias==a,a[0]==a];",
        "變數 a=[1,2];變數 x=0;x=(a[0]+a[1])*14;x;",
        "變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};f(20000);",
    ];

    [RuntimeFact]
    public void GuardedFastPathsMatchTheGenericEmitter()
    {
        var results = EmitterTiers.AssertSame(Programs);
        Assert.Equal(new TestValue("string", "繁體"), results[Array.IndexOf(Programs, "變數 s=\"繁\";s+=\"體\";s;")]);
        Assert.Equal(new TestValue("integer", "20000"), results[^1]);
    }

    [RuntimeFact]
    public void CompoundAssignmentsResolveTheBindingAfterCallSideEffects()
    {
        // The C# host rebound x from a builtin; a guest function assigning the global has the same effect: the compound
        // assignment must read x after the call on its right-hand side.
        var results = EmitterTiers.AssertSame(["變數 x=1;變數 edit=函式(){x=10;2;};x+=edit();x;"]);
        Assert.Equal(new TestValue("integer", "12"), results[0]);
    }

    [RuntimeFact]
    public void SlotGuardsHandleInsertionAndReentry()
    {
        // A loop body declaration shadows a global part way through each iteration and a callback updates the shadow
        // (the C# host also forced a 16-unit fuel quantum here; the native host's quantum is fixed).
        var results = EmitterTiers.AssertSame([
            "變數 unrelated=99;變數 reenter=函式(callback){callback();};變數 x=1;變數 total=0;" +
            "迴圈(變數 i=0;i<3;i++){total+=x;變數 x=i+10;reenter(函式(){x+=1;});total+=x;};[total,x];",
        ]);
        Assert.Equal(new TestValue("array", "[39, 1]"), results[0]);
    }

    [RuntimeFact]
    public void HostCapabilitiesSurviveSchedulerYieldsAndCollections()
    {
        // A host function value kept in a variable across a loop long enough to yield to the scheduler (and allocate
        // enough to collect) still works afterwards. The C# test also counted scheduler entries and collections.
        Assert.Equal("[integer, 1000000, 3]\n", WasmTestHost.Output(
            "變數 probe=型別;probe(1);變數 sum=0;迴圈(變數 i=0;i<1000000;i++){sum++;如果(i%1000==0){變數 junk=陣列.建立(100,i);}};[probe(sum),sum,長度(probe(\"\"))-3];"));
    }

    [RuntimeFact]
    public void ManyShortLivedClosuresAreCollectedWhileTheLastOneSurvives()
    {
        // The C# test made a host builtin return 100 new native functions per run for 25 runs and counted collections and
        // native liveness checks; here a program creates 100000 closures that capture a host function value and drops all
        // but the last, which still works afterwards. (Native handle recycling itself: ResourceLifetimeTest.)
        Assert.Equal("[42, 100000, integer]\n", WasmTestHost.Output(
            "變數 make=函式(i){變數 probe=型別;變數 data=[i,i];函式(){[42,probe(data[0])];};};變數 n=0;變數 last=0;" +
            "迴圈(變數 i=0;i<100000;i++){last=make(i);n++;}變數 r=last();[r[0],n,r[1]];"));
    }
}
