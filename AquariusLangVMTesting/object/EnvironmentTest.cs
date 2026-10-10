using AquariusLang.Object;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLangVMTesting.Object;

public class EnvironmentTest {
    [Fact]
    public void NullBindingsShadowOuterValuesAndRemainPresent() {
        var outer = AquaEnvironment.NewEnvironment();
        var original = new IntegerObj(42);
        outer.Create("值", original);
        outer.Create("outerOnly", original);
        var inner = AquaEnvironment.NewEnclosedEnvironment(outer);
        inner.Create("值", null!);
        var nested = AquaEnvironment.NewEnclosedEnvironment(inner);

        Assert.Null(nested.Get("值", out bool present));
        Assert.True(present);
        Assert.True(inner.Owns("值"));
        Assert.Null(inner.GetOwned("值"));
        Assert.Same(original, nested.Get("outerOnly", out present));
        Assert.True(present);
        Assert.Null(inner.GetOwned("outerOnly"));
        Assert.False(inner.Owns("outerOnly"));
        Assert.Null(nested.Get("missing", out present));
        Assert.False(present);
    }

    [Fact]
    public void InheritedWritesAndLocalShadowingKeepSeparateOwnership() {
        var outer = AquaEnvironment.NewEnvironment();
        outer.Create("counter", new IntegerObj(1));
        var inner = AquaEnvironment.NewEnclosedEnvironment(outer);
        var nested = AquaEnvironment.NewEnclosedEnvironment(inner);
        var updated = new IntegerObj(2);
        nested.Set("counter", updated);
        Assert.Same(updated, outer.GetOwned("counter"));
        Assert.Same(updated, inner.Get("counter", out _));
        Assert.Same(updated, nested.Get("counter", out _));
        Assert.Null(inner.GetOwned("counter"));
        Assert.Empty(inner.OwnedBindings);

        var local = new IntegerObj(3);
        inner.Create("counter", local);
        inner.Set("counter", local);
        Assert.Same(local, inner.GetOwned("counter"));
        Assert.Same(updated, outer.GetOwned("counter"));
    }
}
