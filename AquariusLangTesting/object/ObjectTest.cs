using Xunit;

namespace AquariusLang.Object; 

public class ObjectTest {
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