namespace AquariusTests.Packaging;

/// <summary>String indexing is portable (the Aquarius-source libraries' parsers rely on it) and follows the array index
/// rules: characters by position, null outside the string.</summary>
public class StringIndexTest
{
    [RuntimeFact]
    public void StringIndexIsPortableAndBoundsMatchArrayIndex()
    {
        var values = WasmTestHost.EvalMany([
            "變數 s=\"星泉\";[s[0],s[1],s[-1],s[2]];",
            "var s=\"ab\"; [typeOf(s[0]), s[1] + s[0]];",
            "變數 a=[1,2];[a[-1],a[2]];",
        ]);
        Assert.Equal(("array", "[星, 泉, null, null]"), (values[0].Type, values[0].Text));
        Assert.Equal("[string, ba]", values[1].Text);
        Assert.Equal("[null, null]", values[2].Text);
    }
}
