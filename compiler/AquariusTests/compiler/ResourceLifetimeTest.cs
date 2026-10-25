namespace AquariusTests.Wasm;

/// <summary>Lifetimes of native resources (host objects such as thread handles, host functions and module members) seen
/// from programs. The C# tests inspected WasmRuntime.Reachable, the C# host's root set; natively the guest collector
/// decides reachability and the host frees unreachable handles after collections (runtime/host/session.c
/// handles_sweep), so the tests check what a program can observe: values in use stay usable, dropped ones are released.</summary>
public class ResourceLifetimeTest {
    [RuntimeFact] public void ResourcesPassedToCallbacksStayAliveAfterTheirBindingIsCleared() {
        // The only reference left to the thread handle is the callback's parameter while the callback allocates enough to
        // collect; the handle must still work.
        Assert.Equal(new TestValue("array", "[42, 真]"), WasmTestHost.Eval("""
            變數 resource=Thread.Start(函式(){回傳 42;});
            變數 hold=函式(r,callback){resource=0;回傳 callback(r);};
            hold(resource,函式(local){
                迴圈(變數 i=0;i<2000;i++){變數 junk=陣列.建立(1000,i);};
                [Thread.Result(local),Thread.Close(local)];
            });
            """));
    }

    [RuntimeFact] public void ExtractedNativeMethodsRetainTheirOwnerAndCyclesTerminate() {
        // A method taken from a module keeps working after the module binding is gone, and an array cycle holding it
        // can be inspected by host functions.
        Assert.Equal(new TestValue("array", "[array, function, 7, 真]"), WasmTestHost.Eval("""
            變數 module=匯入("執行緒");變數 start=module.啟動;變數 result=module.結果;module=0;
            迴圈(變數 i=0;i<2000;i++){變數 junk=陣列.建立(1000,i);}
            變數 cycle=[0,start];cycle[0]=cycle;
            變數 h=cycle[0][0][1](函式(){回傳 7;});
            [型別(cycle),型別(cycle[1]),result(h),Thread.Close(h)];
            """));
    }

    [RuntimeFact] public void DroppedThreadHandlesReleaseTheirWorkers() {
        // More workers than may run at once (64), none closed: each handle is dropped and the collector's sweep cancels and
        // reclaims its worker. Sweeps happen between scheduler slices, so each iteration runs long enough to yield.
        Assert.Equal("100\n", WasmTestHost.Output(
            "變數 n=0;迴圈(變數 i=0;i<100;i++){變數 h=Thread.Start(函式(){回傳 1;});Thread.Wait(h);變數 junk=陣列.建立(100000,0);迴圈(變數 k=0;k<100000;k++){}n++;};n;"));
    }

    [RuntimeFact] public void FailedExecutionsReleaseTheirResources() {
        // Failing workers, closed or dropped, leave no worker slots behind (the C# test checked the failed run's roots).
        Assert.Equal(new TestValue("integer", "100"), WasmTestHost.Eval("""
            變數 n=0;迴圈(變數 i=0;i<100;i++){變數 h=Thread.Start(函式(){回傳 未知;});Thread.Wait(h);Thread.Close(h);n++;};n;
            """));
    }
}
