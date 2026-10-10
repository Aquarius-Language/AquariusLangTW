using Xunit;

namespace AquariusLang.Object; 

public class ObjectTest {
    [Fact]
    public void HashKeysRetainTypeAndValueEqualityAcrossDictionaryOperations() {
        var first = new HashKey(ObjectType.INTEGER_OBJ, 42);
        var equal = new HashKey(new string(ObjectType.INTEGER_OBJ.ToCharArray()), 42);
        Assert.True(first.Equals(equal));
        Assert.True(first.Equals((object)equal));
        Assert.Equal(first.GetHashCode(), equal.GetHashCode());
        Assert.False(first.Equals(null));
        Assert.False(first.Equals(new HashKey(ObjectType.FLOAT_OBJ, 42)));
        Assert.False(first.Equals(new HashKey(ObjectType.INTEGER_OBJ, -42)));

        var values = new Dictionary<HashKey, string> {
            [first] = "integer", [new HashKey(ObjectType.FLOAT_OBJ, 42)] = "float",
            [new HashKey(ObjectType.INTEGER_OBJ, -42)] = "negative", [default] = "default"
        };
        values[equal] = "updated";
        Assert.Equal(4, values.Count);
        Assert.Equal("updated", values[first]);
        Assert.Equal("float", values[new HashKey(ObjectType.FLOAT_OBJ, 42)]);
        Assert.Equal("negative", values[new HashKey(ObjectType.INTEGER_OBJ, -42)]);
        Assert.Equal("default", values[new HashKey()]);
    }

    [Fact]
    public void TestStringHashKey() {
        StringObj hello1 = new StringObj("Hello World");
        StringObj hello2 = new StringObj("Hello World");
        StringObj diff1 = new StringObj("My name is Johnny");
        StringObj diff2 = new StringObj("My name is Johnny");
        
        Assert.Equal(hello1.HashKey(), hello2.HashKey());
        Assert.Equal(diff1.HashKey(), diff2.HashKey());
        Assert.NotEqual(hello1.HashKey(), diff1.HashKey());
    }
}
