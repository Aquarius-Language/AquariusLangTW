namespace AquariusTests.Wasm;

/// <summary>The portable 數學 / Math module of the guest runtime: the shared vectors in tests/math-library.json in English
/// and Traditional Chinese, its errors and names.</summary>
public class MathLibraryTest {
    private static string Chinese(string source) {
        var module = ArrayLibraryTest.Module("Math");
        foreach (var function in module.Functions.OrderByDescending(f => f.English.Length))
            source = source.Replace($"Math.{function.English}(", $"數學.{function.Chinese}(");
        return source.Replace("Math.", "數學.");
    }

    [RuntimeFact]
    public void Vectors() {
        var cases = new List<(string, string, string)>();
        using var json = ArrayLibraryTest.Vectors("math-library");
        foreach (var item in json.RootElement.GetProperty("valid").EnumerateArray()) {
            string source = item[1].GetString()!, expected = item[2].GetString()!;
            cases.Add((source, "array", expected));
            cases.Add((Chinese(source), "array", expected));
        }
        Assert.Equal(2 * 11, cases.Count);
        Assert.Contains(cases, c => c.Item1.Contains("數學.四捨五入取整數("));
        LanguageCases.Expect(cases.ToArray());
    }

    [RuntimeFact]
    public void InvalidCallsReportErrors() {
        using var json = ArrayLibraryTest.Vectors("math-library");
        var sources = json.RootElement.GetProperty("invalid").EnumerateArray().Select(i => i.GetString()!).ToArray();
        Assert.Equal(7, sources.Length);
        LanguageCases.ExpectErrors(sources.Concat(sources.Select(Chinese)).ToArray());
        LanguageCases.ExpectErrors(("Math.Sqrt(\"a\");", "Math.Sqrt requires numbers / 數學.平方根 需要數字: STRING"));
    }

    [RuntimeFact]
    public void ImportAndNamesExposeBothLanguages() {
        var module = ArrayLibraryTest.Module("Math");
        Assert.Equal(37, module.Functions.Length);
        string checks = string.Join(",", module.Functions.Select(f =>
            $"Math.{f.English}==數學.{f.Chinese} && 數學.{f.English}==Math.{f.Chinese} && 型別(Math.{f.English})==\"function\""));
        LanguageCases.Expect(
            ("[匯入(\"Math\")==匯入(\"數學\"),匯入(\"數學\")==Math,型別(數學)];", "array", "[真, 真, module]"),
            ($"[{checks}];", "array", "[" + string.Join(", ", Enumerable.Repeat("真", module.Functions.Length)) + "]"),
            ("變數 m=匯入(\"數學\");[m.PI==m.圓周率,m.E==m.自然常數,m.TAU==m.二倍圓周率,m.Infinity==m.無限大,m.IsNaN(m.非數)];", "array", "[真, 真, 真, 真, 真]"),
            // A local binding shadows the module like any other global.
            ("變數 數學=1;數學+1;", "integer", "2"));
    }

    [RuntimeFact]
    public void WorkersUseTheModule() => LanguageCases.Expect(
        ("變數 t=Thread.Start(函式(x){Math.Sqrt(x)+Math.Floor(2.5d);},[16]);Thread.Result(t);", "double", "6"));
}
