namespace AquariusTests.Wasm;

/// <summary>區域填色 / floodFill, the portable builtin the guest runtime executes itself.</summary>
public class PortableFloodFillTest
{
    [RuntimeFact]
    public void RuntimeRunsThePortableAlgorithm() => LanguageCases.Expect(
        ("變數 a=[0,1,0,0,1,0,1,0,0];[區域填色(a,3,3,0,0,4294967295.0d),a];", "array", "[2, [4294967295, 1, 0, 4294967295, 1, 0, 1, 0, 0]]"),
        ("變數 a=[0,0,0,0];變數 alias=a;變數 n=floodFill(a,2,2,1,1,7);[n,alias,floodFill(a,2,2,0,0,7)];", "array", "[4, [7, 7, 7, 7], 0]"),
        ("變數 a=[0,0,0,0];變數 b=加入(a,9);區域填色(a,2,2,0,0,7);[a,b];", "array", "[[7, 7, 7, 7], [0, 0, 0, 0, 9]]"),
        // The replacement keeps its own numeric type.
        ("變數 a=[0,0];區域填色(a,2,1,0,0,2.5d);[a,型別(a[0]),型別(a[1])];", "array", "[[2.5, 2.5], double, double]"));

    [RuntimeFact]
    public void InvalidArgumentsReturnALanguageError() => LanguageCases.ExpectErrors(
        ("區域填色([0],2,2,0,0,7);", "floodFill"),
        ("區域填色([0],1,1,-1,0,7);", "floodFill"),
        ("區域填色([0],1,1,0,0,\"bad\");", "floodFill"));
}
