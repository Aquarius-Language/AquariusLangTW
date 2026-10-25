namespace AquariusTests;

/// <summary>Tables of language-semantics cases evaluated on the native runtime (<see cref="WasmTestHost"/>). Values
/// are batched into one runtime run; programs expected to fail run on their own, in parallel, because an error stops
/// the whole program.</summary>
internal static class LanguageCases
{
    /// <summary>Evaluates every case in one runtime run and checks each value's <c>型別</c> tag and display text.</summary>
    public static void Expect(params (string Source, string Type, string Text)[] cases)
    {
        var values = WasmTestHost.EvalMany(cases.Select(c => c.Source).ToArray());
        var failures = new List<string>();
        for (int i = 0; i < cases.Length; i++)
            if (values[i] != new TestValue(cases[i].Type, cases[i].Text))
                failures.Add($"{cases[i].Source}\n    expected {cases[i].Type}: {cases[i].Text}\n    actual   {values[i]}");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>Evaluates numeric cases in one runtime run and checks each type tag and the value rounded to
    /// <paramref name="decimals"/> places (floats display their own shortest digits, such as 8.599999).</summary>
    public static void ExpectNumbers(int decimals, params (string Source, string Type, double Value)[] cases)
    {
        var values = WasmTestHost.EvalMany(cases.Select(c => c.Source).ToArray());
        var failures = new List<string>();
        for (int i = 0; i < cases.Length; i++)
            if (values[i].Type != cases[i].Type || Math.Round(values[i].Number, decimals) != Math.Round(cases[i].Value, decimals))
                failures.Add($"{cases[i].Source}\n    expected {cases[i].Type}: {cases[i].Value}\n    actual   {values[i]}");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>Runs each program on its own and checks that it stops with an error whose message contains the
    /// expected text (an empty expectation accepts any error).</summary>
    public static void ExpectErrors(params (string Source, string Message)[] cases)
    {
        var values = cases.AsParallel().AsOrdered().Select(c => WasmTestHost.Eval(c.Source)).ToArray();
        var failures = new List<string>();
        for (int i = 0; i < cases.Length; i++)
            if (!values[i].IsError || !values[i].Message.Contains(cases[i].Message, StringComparison.Ordinal))
                failures.Add($"{cases[i].Source}\n    expected an error containing \"{cases[i].Message}\"\n    actual   {values[i]}");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>Runs each program on its own and checks that it stops with an error.</summary>
    public static void ExpectErrors(params string[] sources) => ExpectErrors(sources.Select(s => (s, "")).ToArray());

    /// <summary>A program whose value is <c>[array, [型別 of each element]]</c> for a program whose value is an array,
    /// so integer, float and double elements stay distinguishable (they display alike: 2 and 2.5).</summary>
    public static string Typed(string source) => $$"""
        變數 __值 = (函式() {
        {{source}}
        })();
        變數 __型別 = [];
        迴圈 (變數 __i = 0; __i < 長度(__值); __i++) { __型別 = 加入(__型別, 型別(__值[__i])); };
        [__值, __型別];
        """;
}
