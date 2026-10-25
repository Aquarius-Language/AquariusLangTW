using AquariusLang.Cli;

namespace AquariusTests.CommandLine;

/// <summary><c>aqua repl</c> (<see cref="Repl.Run"/>): each input reruns the accepted earlier inputs on the native runtime
/// and shows only what the new input prints and its value; inputs that fail are shown and left out of the session.</summary>
public class ReplTest
{
    /// <summary>What the REPL printed after its banner, one entry per prompt (the text after each "&gt;&gt; ").</summary>
    private static string[] Session(params string[] inputs)
    {
        using var output = new StringWriter();
        Assert.Equal(0, Repl.Run(new StringReader(string.Join("\n", inputs) + "\n"), output));
        string text = output.ToString().Replace("\r\n", "\n");
        Assert.StartsWith("AquariusLang REPL.", text);
        string[] parts = text[(text.IndexOf('\n') + 1)..].Split(Repl.Prompt);
        Assert.Equal("", parts[0]);
        Assert.Equal("\n", parts[^1]); // End of input.
        return parts[1..^1];
    }

    [RuntimeFact]
    public void ValuesEchoAndDefinitionsPersist()
    {
        Assert.Equal(["", "42\n", "hi\n", "80\n", "", "[41, 真]\n"], Session(
            "變數 x = 40;", "x + 2;", "印出(\"hi\");", "x * 2;", "var 數 = 函式(n) { n + 1; };", "[數(x), x == 40];"));
    }

    [RuntimeFact]
    public void OnlyTheNewInputsOutputIsShown()
    {
        // Earlier inputs run again for every input, but what they print is not shown again.
        Assert.Equal(["once\n", "1\n", "2\n"], Session("印出(\"once\");", "1;", "1 + 1;"));
    }

    [RuntimeFact]
    public void ErrorsDoNotEnterTheSession()
    {
        var shown = Session("變數 x = 1;", "x + ;", "不存在;", "變數 x = 未定義;", "x + 1;", "變數 = ;", "x;");
        Assert.Equal("", shown[0]);
        Assert.NotEqual("", shown[1]); Assert.DoesNotContain("ERROR", shown[1]); // A parse error is reported, not run.
        Assert.Equal("ERROR: Identifier not found: 不存在\n", shown[2]);
        Assert.Equal("ERROR: Identifier not found: 未定義\n", shown[3]);
        Assert.Equal("2\n", shown[4]);
        Assert.NotEqual("", shown[5]);
        Assert.Equal("1\n", shown[6]);
    }

    [RuntimeFact]
    public void UnbalancedBracesContinueTheInput()
    {
        var shown = Session("變數 f = 函式(a) {", "  如果 (a > 0) { a + 1; } 否則 { 0; }", "};", "f(41);", "\"{\";");
        Assert.Equal(Repl.Continuation + Repl.Continuation, shown[0]);
        Assert.Equal("42\n", shown[1]);
        Assert.Equal("{\n", shown[2]); // A brace in a string does not open a block.
    }

    [RuntimeFact]
    public void EmptyInputsAreSkippedAndEndOfInputEndsTheSession()
    {
        Assert.Equal(["", "", "3\n"], Session("", "   ", "1 + 2;"));
        using var output = new StringWriter();
        Assert.Equal(0, Repl.Run(new StringReader(""), output));
    }
}
