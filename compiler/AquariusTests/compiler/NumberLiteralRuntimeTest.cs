namespace AquariusTests.Wasm;

/// <summary>Number literals at run time: their types and values, the same in both languages.</summary>
public class NumberLiteralRuntimeTest {
    [RuntimeFact]
    public void LiteralsHaveTheirTypes() => LanguageCases.Expect(
        ("[1.5,1e-9,2.5e3f,2d,2f,0xFF,0b1010,1_000_000,0xFFFFFFFF,-0x8000_0000,1_000.25,0.1+0.2==0.30000000000000004];", "array",
            "[1.5, 1E-09, 2500, 2, 2, 255, 10, 1000000, -1, -2147483648, 1000.25, 真]"),
        ("[型別(1.5),型別(1e3),型別(2.5e3f),型別(2d),型別(2f),型別(0xFF),型別(1_000)];", "array", "[double, double, float, double, float, integer, integer]"),
        // Unsuffixed decimals are doubles, so they widen arithmetic like 1.5d did.
        ("變數 x: 雙精度 = 1.5; 變數 n = 3; [x * n, n * 0.5, 型別(n * 0.5)];", "array", "[4.5, 1.5, double]"));

    [RuntimeFact]
    public void MalformedLiteralsAreCompileErrors() => LanguageCases.ExpectErrors(
        ("0xFG;", "Invalid number literal 0xFG"), ("1e400;", "1e400"), ("0x1_0000_0000;", "0x1_0000_0000"), ("1__0;", "Invalid number literal"));
}
