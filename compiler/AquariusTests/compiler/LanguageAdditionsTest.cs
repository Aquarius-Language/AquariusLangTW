namespace AquariusTests.Compiler;

public class LanguageAdditionsTest {
    [RuntimeFact]
    public void 型別回傳穩定的英文型別標記() {
        LanguageCases.Expect(("""
            [型別(1), 型別(1.5f), 型別(2.5d), 型別("文"), 型別(真), 型別({}["無"]), 型別([1]), 型別({"a": 1}),
             型別(函式() { 1; }), typeOf(型別), 型別([1][5]), 型別(字串), typeOf(Array.Make), 型別(印出)];
            """, "array", "[integer, float, double, string, boolean, null, array, hash, function, function, null, module, function, function]"));
        LanguageCases.ExpectErrors("typeOf();");
    }

    [RuntimeFact]
    public void MemberResultsCanBeIndexedWithoutParentheses() => LanguageCases.Expect(("""
        變數 文 = 匯入("字串");
        變數 表 = {"模組": 文, "雜湊": 雜湊};
        [字串.分割("甲,乙,丙", ",")[1], String.Split("a-b", "-")[0], 表["模組"].分割("x|y", "|")[1], 表["雜湊"].鍵列表({"k": 1})[0],
         雜湊.項目列表({"鍵": 2})[0][1], 文.分割("一二", "")[0]];
        """, "array", "[乙, a, y, k, 2, 一]"));
}
