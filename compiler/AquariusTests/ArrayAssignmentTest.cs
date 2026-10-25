namespace AquariusTests;

/// <summary>Indexed array writes: alias mutation, single evaluation of operands, nesting and language errors.</summary>
public class ArrayAssignmentTest {
    [RuntimeFact] public void IndexedWritesMutateAliasesAndEvaluateOperandsOnce() => LanguageCases.Expect(
        (LanguageCases.Typed(@"變數 a=[0,0];變數 alias=a;變數 count=0;變數 index=0;
            變數 container=函式(){count++;回傳 a;};container()[index++]=17;[alias[0],count,index];"),
            "array", "[[17, 1, 1], [integer, integer, integer]]"));

    [RuntimeFact] public void IndexedWritesSupportNestedArraysAndPropagateValueErrors() {
        Assert.Equal(9, WasmTestHost.Eval("變數 a=[[0]];a[0][0]=9;a[0][0];").Integer);
        LanguageCases.ExpectErrors(("變數 a=[0];a[0]=未知;", "Identifier not found"));
    }

    [RuntimeFact] public void InvalidWritesReturnLanguageErrors() => LanguageCases.ExpectErrors(
        ("變數 a=[0];a[-1]=9;", "bounds"),
        ("變數 a=[0];a[1]=9;", "bounds"),
        ("變數 a=[0];a[0.5d]=9;", "integer"),
        ("變數 a=\"abc\";a[0]=9;", "array"));
}
