namespace AquariusTests.Wasm;

/// <summary>Guest memory growth seen through host calls. The C# test drove a hand-written module through the Wasmtime .NET
/// API (memory views refreshed after growth, during callbacks and re-entry, after traps; disposed instances); that API is
/// gone and the native host's engine binding (runtime/host/engine_wasmtime.c) owns those details. What a program can see
/// stays: host functions read guest memory correctly after it grows, including growth while the host runs guest code.</summary>
public class WasmtimeMemoryTest
{
    [RuntimeFact]
    public void HostCallsSeeGuestMemoryAfterGrowth()
    {
        // Each array is 1 MiB of values, so memory grows many times between and around the host calls (型別, 印出, 長度 of
        // a long string) that read the values the guest just wrote.
        var run = WasmTestHost.Run("""
            變數 kept=[];變數 text="";
            迴圈(變數 i=0;i<40;i++){
                kept=push(kept,陣列.建立(65536,i));
                text=text+"繁";
                如果(i%10==9){印出(型別(kept[i]),kept[i][65535],長度(text));}
            };
            [長度(kept),kept[0][0],kept[39][65535]];
            """).Succeeded();
        Assert.Equal(["array 9 10", "array 19 20", "array 29 30", "array 39 40", "[40, 0, 39]"], run.Lines);
    }

    [RuntimeFact]
    public void MemoryGrownByReenteredGuestCodeAndErrorsAfterGrowth()
    {
        // The imported module runs (and grows memory) while the importing host call is in progress; the error after the
        // growth still reports its message.
        var run = WasmTestHost.RunModules(new Dictionary<string, string> {
            ["main.aqua"] = "變數 before=陣列.建立(1000,7);變數 m=匯入(\"grow.aqua\");印出([before[999],長度(m.big),m.big[262143]]);變數 a=[0];a[5]=1;",
            ["grow.aqua"] = "變數 big=陣列.建立(262144,3);",
        }, "main.aqua");
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["[7, 262144, 3]", "ERROR: Array assignment index is out of bounds."], run.Lines);
    }
}
