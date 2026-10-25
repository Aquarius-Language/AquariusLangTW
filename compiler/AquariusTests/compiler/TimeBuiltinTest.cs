using System.Diagnostics;
using System.Globalization;

namespace AquariusTests.Compiler;

/// <summary>The core clock globals 目前時間 / currentTime and 目前日期 / currentDate.</summary>
public class TimeBuiltinTest {
    [RuntimeFact]
    public void 目前日期採主機本地西元日期且拒絕參數() {
        var before = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var actual = WasmTestHost.Eval("目前日期();");
        var after = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal("string", actual.Type);
        Assert.True(actual.Text == before || actual.Text == after, actual.Text);
        LanguageCases.ExpectErrors(("currentDate(1);", "expects 0 arguments, got 1"));
    }

    [RuntimeFact]
    public void SubtractionMeasuresMilliseconds() {
        long outerStart = Stopwatch.GetTimestamp();
        var result = WasmTestHost.Eval("""
            變數 start = 目前時間();
            變數 waited = start;
            迴圈 (變數 i = 0; waited - start < 20; i++) { waited = currentTime(); }
            變數 end = currentTime();
            [start, end, end - start, (end - start) / 1000.0d, 型別(start), 型別(end), currentTime == 目前時間];
            """);
        double outer = Stopwatch.GetElapsedTime(outerStart).TotalMilliseconds;
        Assert.Equal("array", result.Type);
        var items = result.Text.TrimStart('[').TrimEnd(']').Split(", ");
        Assert.Equal(["double", "double", "真"], items[4..]);
        var values = items[..4].Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        Assert.All(values, value => Assert.True(double.IsFinite(value) && value >= 0, result.Text));
        Assert.Equal(values[1] - values[0], values[2]);
        Assert.Equal(values[2] / 1000, values[3]);
        // The program waited at least 20 ms on the clock, and no longer than its whole run took.
        Assert.InRange(values[2], 20, outer);
    }

    [RuntimeFact]
    public void ReadingsShareAnOriginAcrossCallsAndModuleImports() {
        var run = WasmTestHost.RunModules(new Dictionary<string, string> {
            ["時鐘.aqua"] = "變數 匯入時間 = 目前時間(); 變數 讀取 = 函式() { currentTime(); };",
            ["main.aqua"] = """
                變數 前 = currentTime();
                變數 時鐘 = 匯入("時鐘.aqua");
                變數 後 = 時鐘.讀取();
                變數 遞增 = 真;
                變數 上次 = 後;
                迴圈 (變數 i = 0; i < 1000; i++) { 變數 現在 = 目前時間(); 如果 (現在 < 上次) { 遞增 = 假; } 上次 = 現在; }
                印出(前 <= 時鐘.匯入時間, 時鐘.匯入時間 <= 後, 遞增);
                """,
        }, "main.aqua");
        Assert.Equal("真 真 真\n", run.Succeeded().Output);
    }

    [RuntimeTheory]
    [InlineData("currentTime")]
    [InlineData("目前時間")]
    public void ExtraArgumentsFailBeforeLaterSideEffects(string name) {
        var run = WasmTestHost.Run($"變數 n = 0; 印出(\"n\", n); {name}(1); n++; 印出(\"n\", n);");
        Assert.Equal(1, run.ExitCode);
        // The C# runtime said "Expected 0 arguments, got 1"; the native runtime names the function.
        Assert.StartsWith("n 0\nERROR: ", run.Output);
        Assert.Contains("currentTime expects 0 arguments, got 1", run.Output);
        Assert.Equal(2, run.Lines.Count);
    }

    [RuntimeFact]
    public void ClockFunctionsCanBeStoredAndShadowed() => LanguageCases.Expect(
        ("變數 read = 目前時間; 型別(read());", "string", "double"),
        ("""
        變數 currentTime = 函式() { 7; };
        變數 f = 函式(目前時間) { 目前時間(); };
        [currentTime(), f(函式() { 9; })];
        """, "array", "[7, 9]"));
}
