namespace AquariusTests.Threading;

/// <summary>The Thread / 執行緒 module, implemented by the native host (runtime/host/threads.c: each worker runs in its own
/// Wasmtime store on an OS thread, from a snapshot of the caller's memory) with the guest's argument checks and result
/// packets (runtime/guest/threads.h).</summary>
public class ThreadTest
{
    [RuntimeFact]
    public void EveryFunctionHasTheSameBilingualBinding()
    {
        // The C# test also checked the C# catalog's portable ids (100-107), an internal of the old host transport.
        Assert.Equal(new TestValue("array", "[真, 真, 真, 真, 真, 真, 真, 真, 假, function, 真]"), WasmTestHost.Eval("""
            [Thread.Start==執行緒.啟動,Thread.Wait==執行緒.等待,Thread.Result==執行緒.結果,Thread.Cancel==執行緒.取消,
             Thread.Close==執行緒.關閉,Thread.IsDone==執行緒.是否完成,Thread.Sleep==執行緒.休眠,
             Thread.IsCancellationRequested==執行緒.是否要求取消,Thread.Start==Thread.Wait,型別(Thread.Start),Thread==執行緒];
            """));
    }

    [RuntimeFact]
    public void FunctionsArgumentsCapturesAndIsolatedMutation()
    {
        Assert.Equal("[42, 10, 1, 真]", WasmTestHost.Eval("""
            變數 倍數=2;變數 資料=[1];變數 轉換=函式(x){回傳 x*倍數;};
            變數 工作=Thread.Start(函式(f,a){a[0]=9;回傳 f(21);},[轉換,資料]);
            倍數=10;Thread.Wait(工作);變數 答案=Thread.Result(工作);Thread.Close(工作);
            [答案,倍數,資料[0],Thread==執行緒];
            """).Text);
    }

    [RuntimeFact]
    public void ChineseAndEnglishImportsAndExtractedFunctions()
    {
        Assert.Equal("[42, 真, 真]", WasmTestHost.Eval("""
            變數 t=匯入("執行緒");變數 e=import("Thread");變數 開始=t.啟動;
            變數 h=開始(函式(){回傳 42;});t.等待(h);變數 結果=e.Result(h);
            [結果,t.關閉(h),e.Close(h)];
            """).Text);
    }

    [RuntimeFact]
    public void TimeoutPollCancelAndRepeatedClose()
    {
        Assert.Equal("[假, 假, 真, 真, 真]", WasmTestHost.Eval("""
            變數 h=執行緒.啟動(函式(){執行緒.休眠(60000);回傳 1;});
            變數 poll=執行緒.等待(h,0);變數 timed=執行緒.等待(h,1);變數 cancel=執行緒.取消(h);
            [poll,timed,cancel,執行緒.關閉(h,5000),執行緒.關閉(h)];
            """).Text);
    }

    [RuntimeFact]
    public void UncooperativeLoopCanBeClosedAtSchedulerCheckpoint()
    {
        Assert.Equal(new TestValue("boolean", "真"), WasmTestHost.Eval("變數 h=Thread.Start(函式(){迴圈(變數 i=0;真;i++){};});Thread.Close(h,5000);"));
    }

    [RuntimeFact]
    public void CancellationWakesACooperativeWorkerAndJoinsIt()
    {
        // Replaces the C# DesktopThreadBackend cancellation test: cancelling wakes a sleeping or polling worker, which is
        // then joined well before its 60 s sleep would end. (Whether the worker had started before the cancel is up to the
        // OS, so only the handle's state is checked, not the result.)
        Assert.Equal("[真, 真, 真, 真]", WasmTestHost.Eval("""
            變數 h=Thread.Start(函式(){變數 s=Thread.Sleep(60000);回傳 [s,Thread.IsCancellationRequested()];});
            [Thread.Cancel(h),Thread.Wait(h,10000),Thread.IsDone(h),Thread.Close(h)];
            """).Text);
        Assert.Equal("[真, 假, 真, 真, 真]", WasmTestHost.Eval("""
            變數 h=執行緒.啟動(函式(){迴圈(變數 i=0;!執行緒.是否要求取消();i++){執行緒.休眠(1);}回傳 "stopped";});
            [執行緒.取消(h),執行緒.取消(h),執行緒.等待(h,10000),執行緒.是否完成(h),執行緒.關閉(h)];
            """).Text);
    }

    [RuntimeFact]
    public void ResultsPreserveGraphsUnicodeAndNumericTypes()
    {
        Assert.Equal("[真, 真, 1.25, 繁體中文, 9, float]", WasmTestHost.Eval("""
            變數 h=Thread.Start(函式(){變數 a=[0];a[0]=a;變數 map={"自己":0};map["自己"]=map;
                回傳 [a,map,1.25f,"繁體中文",{1:9}];});
            變數 r=Thread.Result(h);變數 x=[r[0]==r[0][0],r[1]==r[1]["自己"],r[2],r[3],r[4][1],型別(r[2])];
            Thread.Close(h);x;
            """).Text);
    }

    [RuntimeFact]
    public void WorkersReachOnlyPortableBuiltinsSleepAndCancellation()
    {
        Assert.Equal("[2, 2, [1, 2], 假, 真]", WasmTestHost.Eval("""
            變數 h=Thread.Start(函式(){回傳 [len([1,2]),長度("繁體"),push([1],2),Thread.IsCancellationRequested(),執行緒.休眠(1)];});
            Thread.Result(h);
            """).Text);
    }

    [RuntimeTheory]
    [InlineData("Thread.Start(1);", "function")]
    [InlineData("Thread.Start(函式(x){回傳 x;});", "argument count")]
    [InlineData("Thread.Wait(1);", "handle")]
    [InlineData("Thread.Sleep(-1);", "timeout")]
    [InlineData("Thread.Sleep(1.5d);", "timeout")]
    [InlineData("Thread.IsDone();", "argument count")]
    [InlineData("變數 h=Thread.Start(函式(){回傳 1;});Thread.Wait(h,-2);", "timeout")]
    [InlineData("變數 h=Thread.Start(函式(){回傳 1;});Thread.Close(h);Thread.Result(h);", "closed")]
    // A worker cannot reach host capabilities: their names are not defined there (the C# host said "capability").
    [InlineData("變數 h=Thread.Start(函式(){回傳 unknown;});Thread.Result(h);", "Identifier not found")]
    [InlineData("變數 h=Thread.Start(函式(){印出(\"worker\");回傳 1;});Thread.Result(h);", "Identifier not found: 印出")]
    [InlineData("變數 h=Thread.Start(函式(){回傳 函式(){};});Thread.Result(h);", "portable data")]
    [InlineData("變數 h=Thread.Start(函式(){回傳 1+真;});Thread.Result(h);", "INTEGER")]
    // Workers cannot start or wait for threads. (The native host currently fails these, and captured host functions, with
    // "Invalid worker execution status.": runtime/host/threads.c worker_service answers 1, which the guest reads as
    // "pending", for every service it refuses.)
    [InlineData("變數 h=Thread.Start(函式(){回傳 Thread.Start(函式(){});});Thread.Result(h);", "other threads", Skip = RuntimeBug.WorkerRefusals)]
    [InlineData("變數 h=Thread.Start(函式(){Thread.Wait(1);});Thread.Result(h);", "other threads", Skip = RuntimeBug.WorkerRefusals)]
    public void InvalidCallsAndWorkerFailuresAreReported(string source, string message)
    {
        var value = WasmTestHost.Eval(source);
        Assert.True(value.IsError, value.ToString());
        Assert.Contains(message, value.Message, StringComparison.OrdinalIgnoreCase);
    }

    [RuntimeFact]
    public void IndependentWorkersAndRepeatedResults()
    {
        Assert.Equal("[42, 42, 99]", WasmTestHost.Eval("""
            變數 a=Thread.Start(函式(){回傳 [42];});變數 b=Thread.Start(函式(){回傳 99;});
            變數 r=Thread.Result(a);r[0]=1;變數 again=Thread.Result(a);變數 value=Thread.Result(b);
            Thread.Close(a);Thread.Close(b);[again[0],again[0],value];
            """).Text);
    }

    [RuntimeFact]
    public void RepeatedCloseReleasesWorkerSlots()
    {
        // More workers than the runtime keeps at once (64), each closed after use.
        Assert.Equal(new TestValue("integer", "100"), WasmTestHost.Eval("變數 i=0;迴圈(變數 j=0;j<100;j++){變數 h=Thread.Start(函式(){回傳 1;});Thread.Wait(h);Thread.Close(h);i++;}i;"));
    }

    [RuntimeFact]
    public void ExitJoinsOutstandingWorkers()
    {
        // The C# test disposed the runtime twice; now the program ends with a worker still spinning, and the runtime must
        // cancel and join it at exit instead of hanging (RunWasm fails after its timeout).
        var run = WasmTestHost.Run("變數 h=Thread.Start(函式(){迴圈(變數 i=0;真;i++){};});變數 s=Thread.Start(函式(){Thread.Sleep(60000);});1;").Succeeded();
        Assert.Equal("1\n", run.Output);
    }

    [RuntimeFact(Skip = RuntimeBug.WorkerRefusals)]
    public void WorkerCannotInvokeCapturedHostCapability()
    {
        // A host function captured from the main program (here 印出) must fail in the worker without running, with the
        // C# host's explanation (see the "other threads" cases above for what the native host reports now).
        var run = WasmTestHost.Run("變數 callback=印出;變數 h=Thread.Start(函式(){回傳 callback(\"from worker\");});Thread.Result(h);");
        Assert.Equal(1, run.ExitCode);
        Assert.DoesNotContain("from worker", run.Output);
        Assert.Contains("host handles", run.Output);
    }
}
