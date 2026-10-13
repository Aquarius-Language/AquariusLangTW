using AquariusLang.Application;
using AquariusLang.Input;
using AquariusLang.Object;

namespace AquariusTests.Android;

public class AndroidInputTest
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidVibrationNeverReachesHost(double duration) { var backend = new Backend(); var runtime = new AndroidInputRuntime(backend); runtime.TryImport("Android輸入", out var m); Assert.IsType<ErrorObj>(Call(m, "震動", new DoubleObj(duration))); Assert.Equal(0, backend.Calls); }
    [Theory]
    [InlineData("accept")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("Exit")]
    public void InvalidBackDecisionNeverReachesHost(string value) { var backend = new Backend(); var runtime = new AndroidInputRuntime(backend); runtime.TryImport("AndroidInput", out var m); Assert.IsType<ErrorObj>(Call(m, "ResolveBack", new StringObj(value))); Assert.Equal(0, backend.Calls); }
    [Fact] public void NativeInputPreservesLogicalCoordinatesPressureAndInsets() { var backend = new Backend(); var runtime = new AndroidInputRuntime(backend); runtime.TryImport("AndroidInput", out var m); var snapshot = Assert.IsType<ModuleObj>(Call(m, "Poll")); var touches = Assert.IsType<ArrayObj>(snapshot._Environment.Get("touches", out _)); var touch = Assert.IsType<ModuleObj>(touches.Elements[0]); Assert.Equal(12.5, Assert.IsType<DoubleObj>(touch._Environment.Get("x", out _)).Value); Assert.Equal(.7, Assert.IsType<DoubleObj>(touch._Environment.Get("pressure", out _)).Value); }
    [Fact] public void AliasIdentityAndCapabilityAbsenceAreExplicit() { var runtime = new AndroidInputRuntime(new UnavailableAndroidInputBackend()); Assert.True(runtime.TryImport("AndroidInput", out var a)); Assert.True(runtime.TryImport("Android輸入", out var b)); Assert.Same(a, b); Assert.False(Assert.IsType<BooleanObj>(Call(a, "是否支援")).Value); Assert.IsType<ErrorObj>(Call(a, "讀取輸入")); }
    private static IObject Call(ModuleObj m, string name, params IObject[] args) => ((BuiltinObj)m._Environment.Get(name, out _)).Fn(args);
    private sealed class Backend : IAndroidInputBackend
    {
        public int Calls; public bool IsSupported => true;
        public AndroidInputSnapshot Poll() => new(new[] { new AndroidTouch(2, "move", 12.5, 8, .7, "pen", 2, Array.Empty<PointerSample>()) }, new(0, 24, 0, 16), true, false, "portrait");
        public void ShowKeyboard(bool value) => Calls++; public void Vibrate(int value) => Calls++; public void ResolveBack(CloseDecision value) => Calls++;
    }
}
