using AquariusTests;
using Xunit;

namespace AquariusLang.Object;

public class ObjectTest {
    /// <summary>The C# runtime's HashKey struct is gone: hash keys live in the guest runtime (runtime/guest/hashes.h). The
    /// same rules, observed from a program: keys keep their type and value, equal keys replace, others stay apart.</summary>
    [RuntimeFact]
    public void HashKeysRetainTypeAndValueEqualityAcrossDictionaryOperations() {
        Assert.Equal(new TestValue("array", "[3, updated, float, negative, null, null]"), WasmTestHost.Eval("""
            變數 values={42:"integer",42.0f:"float",-42:"negative"};
            values[40+2]="updated";
            [長度(values),values[42],values[42.0f],values[-42],values[42.0d],values["42"]];
            """));
    }

    [RuntimeFact]
    public void TestStringHashKey() {
        Assert.Equal(new TestValue("array", "[1, 2, 2, null]"), WasmTestHost.Eval("""
            變數 hello1="Hello World";變數 hello2="Hello"+" World";變數 diff1="My name is Johnny";
            變數 h={hello1:1,diff1:2};
            [h[hello2],h["My name is "+"Johnny"],長度(h),h["hello world"]];
            """));
    }

    /// <summary>The compiler's IR constants: what the emitter writes into a program's data segment.</summary>
    [Fact]
    public void ConstantValuesReportTheirTypesAndDisplayText() {
        Assert.Equal((ObjectType.INTEGER_OBJ, "-42"), (new IntegerObj(-42).Type(), new IntegerObj(-42).Inspect()));
        Assert.Equal((ObjectType.FLOAT_OBJ, "1.5"), (new FloatObj(1.5f).Type(), new FloatObj(1.5f).Inspect()));
        Assert.Equal((ObjectType.DOUBLE_OBJ, "0.25"), (new DoubleObj(0.25).Type(), new DoubleObj(0.25).Inspect()));
        Assert.Equal((ObjectType.STRING_OBJ, "繁體中文"), (new StringObj("繁體中文").Type(), new StringObj("繁體中文").Inspect()));
        Assert.Equal("真", RepeatedPrimitives.TRUE.Inspect()); Assert.Equal("假", RepeatedPrimitives.FALSE.Inspect());
        Assert.Equal(ObjectType.BOOLEAN_OBJ, RepeatedPrimitives.TRUE.Type());
        Assert.Equal((ObjectType.NULL_OBJ, "null"), (RepeatedPrimitives.NULL.Type(), RepeatedPrimitives.NULL.Inspect()));
        Assert.Equal((ObjectType.BREAK_OBJ, "中斷"), (RepeatedPrimitives.BREAK.Type(), RepeatedPrimitives.BREAK.Inspect()));
    }
}
