using System.Text.Json;
using AquariusLang.Compiler;

namespace AquariusTests.Wasm;

/// <summary>The portable 陣列 / Array module of the guest runtime: the shared vectors in tests/array-library.json in
/// English and Traditional Chinese, its names, and alias and snapshot semantics.</summary>
public class ArrayLibraryTest {
    internal static JsonDocument Vectors(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AquariusInstallation.Require().Root, "tests", name + ".json")));

    /// <summary>A portable module's English and Chinese names and those of its functions, exactly as the guest runtime
    /// declares them (runtime/guest/arrays.h, hashes.h, texts.h).</summary>
    internal sealed record PortableModule(string English, string Chinese, params (string English, string Chinese)[] Functions);

    internal static readonly PortableModule[] PortableModules = [
        new("Array", "陣列", ("Make", "建立"), ("Len", "長度"), ("Cap", "容量"), ("Grow", "擴充"), ("Append", "追加"), ("AppendArray", "追加陣列"),
            ("Copy", "複製"), ("Clone", "克隆"), ("Slice", "切片"), ("Fill", "填入"), ("Delete", "刪除"), ("Reverse", "反轉"), ("Index", "尋找"), ("Contains", "包含")),
        new("Hash", "雜湊", ("Keys", "鍵列表"), ("Values", "值列表"), ("Has", "包含鍵"), ("Get", "取得"), ("Delete", "刪除鍵"), ("Len", "長度"),
            ("Clone", "克隆"), ("Clear", "清空"), ("Merge", "合併"), ("Entries", "項目列表")),
        new("String", "字串", ("From", "轉文字"), ("Slice", "切片"), ("Find", "尋找"), ("Contains", "包含"), ("StartsWith", "開頭是"), ("EndsWith", "結尾是"),
            ("Split", "分割"), ("Join", "合併"), ("Replace", "取代"), ("Trim", "修剪"), ("Upper", "轉大寫"), ("Lower", "轉小寫"), ("Repeat", "重複"),
            ("Code", "字元碼"), ("FromCode", "從字元碼"), ("ParseInt", "解析整數")),
    ];

    internal static PortableModule Module(string english) => PortableModules.Single(m => m.English == english);

    /// <summary>The type tag a vector's expected display text implies.</summary>
    internal static string TypeOf(string expected) =>
        expected is "真" or "假" ? "boolean" : expected.StartsWith('[') ? "array" : expected.StartsWith('{') ? "hash"
        : int.TryParse(expected, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _) ? "integer"
        : "string";

    private static string Chinese(string source) {
        foreach (var function in Module("Array").Functions.OrderByDescending(f => f.English.Length))
            source = source.Replace("Array." + function.English, "陣列." + function.Chinese)
                .Replace("[" + function.English + "]", "[" + function.Chinese + "]");
        return source.Replace("Array.(", "陣列.(").Replace("變數 Array=", "變數 陣列=").Replace(",Array];", ",陣列];");
    }

    [RuntimeFact]
    public void PortableOperations() {
        using var json = Vectors("array-library");
        var cases = new List<(string, string, string)>();
        foreach (var item in json.RootElement.GetProperty("valid").EnumerateArray()) {
            string source = item[1].GetString()!, expected = item[2].GetString()!;
            cases.Add((source, TypeOf(expected), expected));
            cases.Add((Chinese(source), TypeOf(expected), expected));
        }
        Assert.Equal(52, cases.Count);
        Assert.Contains(cases, c => c.Item1.Contains("陣列.追加陣列("));
        LanguageCases.Expect(cases.ToArray());
    }

    [RuntimeFact]
    public void InvalidArgumentsReportArrayErrors() {
        // Runtime errors stop the program, so whether the array kept its contents is no longer observable from it.
        using var json = Vectors("array-library");
        var cases = json.RootElement.GetProperty("invalid").EnumerateArray().Select(i => i.GetString()!)
            .SelectMany(source => new[] { source, Chinese(source) })
            .Select(source => ("變數 a=[1,2];" + source, "Array")).ToArray();
        Assert.Equal(56, cases.Length);
        LanguageCases.ExpectErrors(cases);
    }

    [RuntimeFact]
    public void ImportAndNamesExposeBothLanguages() {
        var module = Module("Array");
        Assert.Equal(14, module.Functions.Length);
        // Every function answers to its English and Chinese name in either module name, and functions are distinct.
        var other = (string name) => name == "Make" ? "Len" : "Make";
        string checks = string.Join(",\n", module.Functions.Select(f =>
            $"Array.{f.English}==陣列.{f.Chinese} && 陣列.{f.English}==Array.{f.Chinese} && 型別(Array.{f.English})==\"function\" && Array.{f.English}!=Array.{other(f.English)}"));
        LanguageCases.Expect(
            ("變數 arrays=匯入(\"Array\");arrays.建立(2,7);", "array", "[7, 7]"),
            ("變數 arrays=匯入(\"陣列\");arrays.Make(2,7);", "array", "[7, 7]"),
            ("[匯入(\"Array\")==匯入(\"陣列\"),匯入(\"Array\")==陣列,Array==陣列];", "array", "[真, 真, 真]"),
            ($"[{checks}];", "array", "[" + string.Join(", ", Enumerable.Repeat("真", 14)) + "]"));
    }

    [RuntimeFact]
    public void FunctionsSurviveTransportAndModuleProjection() => LanguageCases.Expect(
        ("變數 identity=函式(x){x;};變數 make=identity(Array.Make);make(2,8);", "array", "[8, 8]"),
        ("變數 h={\"make\":陣列.建立};[h[\"make\"](1,3),[Array.Make][0](1,4)];", "array", "[[3], [4]]"),
        ("變數 m=Array;[型別(Array),型別(陣列),m.Make(1,5),m==Array];", "array", "[module, module, [5], 真]"));

    [RuntimeFact]
    public void RetainedArrayReceivesLengthAndContentChanges() => LanguageCases.Expect(
        // A closure keeps the array; the library's in-place changes reach it.
        ("變數 a=[1,2];變數 check=(函式(kept){函式(){kept;};})(a);Array.Append(a,3);Array.Delete(a,0,1);Array.Copy(a,[9]);[check(),Array.Len(check())];",
            "array", "[[9, 3], 2]"));

    [RuntimeFact]
    public void CollectionsRetainNestedValuesAndSnapshotSemantics() => LanguageCases.Expect(("""
        變數 child=[7];變數 a=Array.Make(100,child);變數 snapshot=push(a,9);
        迴圈(變數 i=0;i<100000;i++){變數 garbage=[i,i,i,i];}
        Array.Delete(a,0,99);Array.Append(a,8);child[0]=42;
        [a[0][0],a[1],snapshot[0][0],snapshot[100],Array.Len(snapshot)];
        """, "array", "[42, 8, 42, 9, 101]"));
}
