using AquariusLang.Object;
using AquariusLang.Wasm;
using AquariusLang.runtime;
using AquariusLang.Desktop.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusTests.Threading;

public class ThreadTest
{
    [Fact]
    public void AllHostTransportTokensExposeTheSameBilingualFunction()
    {
        var module=AquariusLang.Threading.ThreadCatalog.CreateModule();
        foreach(var function in AquariusLang.Threading.ThreadCatalog.Functions) {
            var english=Assert.IsType<BuiltinObj>(module._Environment.GetOwned(function.EnglishName));
            Assert.Same(english,module._Environment.GetOwned(function.TraditionalChineseName));
            Assert.InRange(english.PortableId,100,107);
        }
        Assert.Throws<ArgumentOutOfRangeException>(()=>BuiltinObj.FromPortable(99,_=>new IntegerObj(0)));
    }
    private static IObject Run(string source) { using var vm = new WasmRuntime(); return vm.Execute(new WasmCompiler().Compile(source)); }
    [Fact]
    public void FunctionsArgumentsCapturesAndIsolatedMutation()
    {
        Assert.Equal("[42, 10, 1, 真]", Run("""
            變數 倍數=2;變數 資料=[1];變數 轉換=函式(x){回傳 x*倍數;};
            變數 工作=Thread.Start(函式(f,a){a[0]=9;回傳 f(21);},[轉換,資料]);
            倍數=10;Thread.Wait(工作);變數 答案=Thread.Result(工作);Thread.Close(工作);
            [答案,倍數,資料[0],Thread==執行緒];
            """).Inspect());
    }
    [Fact]
    public void ChineseAndEnglishImportsAndExtractedFunctions()
    {
        Assert.Equal("[42, 真, 真]", Run("""
            變數 t=匯入("執行緒");變數 e=import("Thread");變數 開始=t.啟動;
            變數 h=開始(函式(){回傳 42;});t.等待(h);變數 結果=e.Result(h);
            [結果,t.關閉(h),e.Close(h)];
            """).Inspect());
    }
    [Fact]
    public void TimeoutPollCancelAndRepeatedClose()
    {
        Assert.Equal("[假, 假, 真, 真, 真]", Run("""
            變數 h=執行緒.啟動(函式(){執行緒.休眠(60000);回傳 1;});
            變數 poll=執行緒.等待(h,0);變數 timed=執行緒.等待(h,1);變數 cancel=執行緒.取消(h);
            [poll,timed,cancel,執行緒.關閉(h,5000),執行緒.關閉(h)];
            """).Inspect());
    }
    [Fact]
    public void UncooperativeLoopCanBeClosedAtSchedulerCheckpoint()
    {
        Assert.Equal("真", Run("變數 h=Thread.Start(函式(){迴圈(變數 i=0;真;i++){};});Thread.Close(h,5000);").Inspect());
    }
    [Fact]
    public void ResultsPreserveGraphsUnicodeAndNumericTypes()
    {
        Assert.Equal("[真, 真, 1.25, 繁體中文, 9]", Run("""
            變數 h=Thread.Start(函式(){變數 a=[0];a[0]=a;變數 map={"自己":0};map["自己"]=map;
                回傳 [a,map,1.25f,"繁體中文",{1:9}];});
            變數 r=Thread.Result(h);變數 x=[r[0]==r[0][0],r[1]==r[1]["自己"],r[2],r[3],r[4][1]];
            Thread.Close(h);x;
            """).Inspect());
    }
    [Theory]
    [InlineData("Thread.Start(1);", "function")]
    [InlineData("Thread.Start(函式(x){回傳 x;});", "argument count")]
    [InlineData("Thread.Wait(1);", "handle")]
    [InlineData("Thread.Sleep(-1);", "timeout")]
    [InlineData("Thread.Sleep(1.5d);", "timeout")]
    [InlineData("Thread.IsDone();", "argument count")]
    [InlineData("變數 h=Thread.Start(函式(){回傳 1;});Thread.Wait(h,-2);", "timeout")]
    [InlineData("變數 h=Thread.Start(函式(){回傳 1;});Thread.Close(h);Thread.Result(h);", "closed")]
    [InlineData("變數 h=Thread.Start(函式(){回傳 unknown;});Thread.Result(h);", "capability")]
    [InlineData("變數 h=Thread.Start(函式(){回傳 函式(){};});Thread.Result(h);", "portable data")]
    [InlineData("變數 h=Thread.Start(函式(){回傳 1+真;});Thread.Result(h);", "INTEGER")]
    [InlineData("變數 h=Thread.Start(函式(){回傳 Thread.Start(函式(){});});Thread.Result(h);", "other threads")]
    [InlineData("變數 h=Thread.Start(函式(){Thread.Wait(1);});Thread.Result(h);", "other threads")]
    public void InvalidCallsAndWorkerFailuresAreReported(string source,string message)
    { Assert.Contains(message, Assert.IsType<ErrorObj>(Run(source)).Message, StringComparison.OrdinalIgnoreCase); }
    [Fact]
    public void IndependentWorkersAndRepeatedResults()
    {
        Assert.Equal("[42, 42, 99]", Run("""
            變數 a=Thread.Start(函式(){回傳 [42];});變數 b=Thread.Start(函式(){回傳 99;});
            變數 r=Thread.Result(a);r[0]=1;變數 again=Thread.Result(a);變數 value=Thread.Result(b);
            Thread.Close(a);Thread.Close(b);[again[0],again[0],value];
            """).Inspect());
    }
    [Fact]
    public void RepeatedCloseReleasesWorkerSlots()
    { Assert.Equal("100",Run("變數 i=0;迴圈(變數 j=0;j<100;j++){變數 h=Thread.Start(函式(){回傳 1;});Thread.Wait(h);Thread.Close(h);i++;}i;").Inspect()); }
    [Fact]
    public void DisposeJoinsOutstandingWorkers()
    {
        var vm=new WasmRuntime();var env=AquaEnvironment.NewEnvironment();
        Assert.Equal("1",vm.Execute(new WasmCompiler().Compile("變數 h=Thread.Start(函式(){迴圈(變數 i=0;真;i++){};});1;"),env).Inspect());
        vm.Dispose();vm.Dispose();
        Assert.Throws<ObjectDisposedException>(()=>vm.Execute(new WasmCompiler().Compile("1;")));
    }
    [Fact]
    public void BackendRunsOnDistinctOsThreadAndJoinsBeforeCompletion()
    {
        int caller=System.Environment.CurrentManagedThreadId,workerId=caller;
        using var worker=new DesktopThreadBackend().Start(token=>{workerId=System.Environment.CurrentManagedThreadId;return new byte[]{42};});
        Assert.True(worker.Wait(5000));Assert.NotEqual(caller,workerId);Assert.Equal(new byte[]{42},worker.Result());
        Assert.True(worker.IsDone);Assert.False(worker.Cancel());
    }
    [Fact]
    public void BackendCancellationWakesStartedWorkerAndJoinsIt()
    {
        using var entered=new ManualResetEventSlim();
        using var worker=new DesktopThreadBackend().Start(token=>{entered.Set();token.WaitHandle.WaitOne();return new byte[]{42};});
        Assert.True(entered.Wait(5000));Assert.False(worker.Wait(0));Assert.True(worker.Cancel());Assert.False(worker.Cancel());
        Assert.True(worker.Wait(5000));Assert.Equal(new byte[]{42},worker.Result());
    }
    [Fact]
    public void WorkerCannotInvokeCapturedHostCapability()
    {
        int calls=0;var builtins=new Builtins();builtins.BuiltinFuncs["host"]=new BuiltinObj(_=>{calls++;return new IntegerObj(1);});
        using var vm=new WasmRuntime(builtins);
        var result=vm.Execute(new WasmCompiler().Compile("變數 callback=host;變數 h=Thread.Start(函式(){回傳 callback();});Thread.Result(h);"));
        Assert.Contains("host handles",Assert.IsType<ErrorObj>(result).Message);Assert.Equal(0,calls);
    }
}
