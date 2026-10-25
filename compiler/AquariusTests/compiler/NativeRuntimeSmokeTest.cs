namespace AquariusTests.Compiler;

/// <summary>The test host itself: values, output and errors of programs run on the native runtime pack.</summary>
public class NativeRuntimeSmokeTest
{
    [RuntimeFact]
    public void ValuesCarryTheirTypeAndDisplayText()
    {
        var values = WasmTestHost.EvalMany(["1 + 2;", "2.5d * 2;", "1.5f;", "\"文字\";", "真;", "[1, [2, 3]];", "{\"a\": 1};", "變數 x = 1;"]);
        Assert.Equal(new TestValue("integer", "3"), values[0]);
        Assert.Equal(new TestValue("double", "5"), values[1]);
        Assert.Equal(new TestValue("float", "1.5"), values[2]);
        Assert.Equal(new TestValue("string", "文字"), values[3]);
        Assert.True(values[4].Boolean);
        Assert.Equal(new TestValue("array", "[1, [2, 3]]"), values[5]);
        Assert.Equal(new TestValue("hash", "{a: 1}"), values[6]);
        Assert.Equal("null", values[7].Type);
    }

    [RuntimeFact]
    public void ErrorsStopTheProgramWithTheirMessage()
    {
        var value = WasmTestHost.Eval("印出(\"before\"); 5 + 真;");
        Assert.True(value.IsError);
        Assert.Contains("Type mismatch", value.Message);
        var run = WasmTestHost.Run("印出(\"a\"); 未知;");
        Assert.Equal(1, run.ExitCode);
        Assert.StartsWith("a\nERROR: ", run.Output);
    }

    [RuntimeFact]
    public void ProgramsPrintAndReturnTheirFinalValue() =>
        Assert.Equal("你好 3\n7.5\n", WasmTestHost.Output("印出(\"你好\", 1 + 2);\n變數 f = 函式(x) { x * 2.5d; };\nf(3);"));
}
