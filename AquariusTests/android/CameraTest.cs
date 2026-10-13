using AquariusLang.Camera;
using AquariusLang.Object;
using AquariusLang.runtime;

namespace AquariusTests.Android;

public class CameraTest
{
    [Theory]
    [InlineData(0, 480, 30)]
    [InlineData(1921, 480, 30)]
    [InlineData(640, 0, 30)]
    [InlineData(640, 1081, 30)]
    [InlineData(640, 480, 0)]
    [InlineData(640, 480, 61)]
    public void InvalidOptionsNeverReachTheHost(int width, int height, int fps)
    {
        var backend = new FakeBackend(); using var runtime = new CameraRuntime(backend, _ => RepeatedPrimitives.NULL);
        runtime.TryImport("Camera", out var module);
        var result = Call(module, "開啟", new StringObj(""), new IntegerObj(width), new IntegerObj(height), new IntegerObj(fps));
        Assert.IsType<ErrorObj>(result); Assert.Equal(0, backend.Opens);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(60001)]
    public void InvalidTimeoutIsRejected(int value) => Assert.Throws<CameraFailure>(() => new CameraOptions(TimeoutMilliseconds: value).Validate());
    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, -1)]
    [InlineData(1, double.NaN)]
    [InlineData(1, double.PositiveInfinity)]
    public void FrameRejectsMalformedData(long sequence, double timestamp)
    {
        if (sequence == 1 && timestamp == 0) Assert.Throws<CameraFailure>(() => new CameraFrame(1, 1, new byte[3], sequence, timestamp, CameraFacing.Back));
        else Assert.Throws<CameraFailure>(() => new CameraFrame(1, 1, new byte[] { 1, 2, 3, 255 }, sequence, timestamp, CameraFacing.Back));
    }
    [Fact]
    public void FramesAreOwnedSnapshotsWithExplicitFacing()
    {
        byte[] pixels = { 1, 2, 3, 255 }; var frame = new CameraFrame(1, 1, pixels, 1, 5, CameraFacing.Front); pixels[0] = 99;
        Assert.Equal(1, frame.Image.Pixels.Span[0]); Assert.Equal(CameraFacing.Front, frame.Facing);
        Assert.Throws<CameraFailure>(() => new CameraFrame(1, 1, new byte[] { 1, 2, 3, 0 }, 1, 0, CameraFacing.Back));
    }
    [Fact]
    public void UprightPortraitFrameRetainsTheRequestedPixelBudget()
    {
        var bytes = new byte[1080 * 1920 * 4]; for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
        var frame = new CameraFrame(1080, 1920, bytes, 1, 0, CameraFacing.Back); Assert.Equal(1920, frame.Image.Height);
        Assert.Throws<CameraFailure>(() => new CameraFrame(1920, 1920, ReadOnlySpan<byte>.Empty, 1, 0, CameraFacing.Back));
    }
    [Fact]
    public void CameraAndWebcamContractsAreSeparateAndAliasesShareOneModule()
    {
        Assert.False(typeof(AquariusLang.Webcam.IWebcamBackend).IsAssignableFrom(typeof(ICameraBackend)));
        using var runtime = new CameraRuntime(new FakeBackend(), _ => RepeatedPrimitives.NULL);
        Assert.True(runtime.TryImport("Camera", out var a)); Assert.True(runtime.TryImport("相機", out var b)); Assert.Same(a, b);
        Assert.False(runtime.TryImport("攝影機", out _));
        Assert.Same(a._Environment.Get("Open", out _), a._Environment.Get("開啟", out _));
        Assert.NotNull(LibraryCatalog.Find("相機", "開啟")); Assert.NotNull(LibraryCatalog.Find("Android輸入", "讀取輸入"));
    }
    [Fact]
    public void EnumerationDoesNotOpenHardwareAndCloseIsIdempotent()
    {
        var backend = new FakeBackend(); using var runtime = new CameraRuntime(backend, _ => RepeatedPrimitives.NULL); runtime.TryImport("相機", out var module);
        Assert.IsType<ArrayObj>(Call(module, "Enumerate")); Assert.Equal(0, backend.Opens);
        var session = Assert.IsType<ModuleObj>(Call(module, "Open", new StringObj("back"), new IntegerObj(1), new IntegerObj(1), new IntegerObj(30)));
        var frame = Assert.IsType<ModuleObj>(Call(session, "Capture")); Assert.IsType<ArrayObj>(Call(frame, "GetPixels"));
        Call(session, "Dispose"); Call(session, "Dispose"); Assert.Equal(1, backend.Sessions[0].Closes);
        Assert.IsType<ErrorObj>(Call(session, "Capture"));
    }
    [Fact]
    public void SessionLimitAndRuntimeCleanupAreEnforced()
    {
        var backend = new FakeBackend(); var runtime = new CameraRuntime(backend, _ => RepeatedPrimitives.NULL); runtime.TryImport("Camera", out var module);
        for (int i = 0; i < 4; i++) Assert.IsType<ModuleObj>(Call(module, "Open", new StringObj(""), new IntegerObj(1), new IntegerObj(1), new IntegerObj(30)));
        Assert.IsType<ErrorObj>(Call(module, "Open", new StringObj(""), new IntegerObj(1), new IntegerObj(1), new IntegerObj(30))); Assert.Equal(4, backend.Opens);
        runtime.Dispose(); runtime.Dispose(); Assert.All(backend.Sessions, s => Assert.Equal(1, s.Closes)); Assert.IsType<ErrorObj>(Call(module, "IsSupported"));
    }
    [Fact]
    public void CameraIsAnExplicitlyUnavailableCapabilityOnDesktop()
    {
        using var runtime = new CameraRuntime(new UnavailableCameraBackend(), _ => RepeatedPrimitives.NULL); runtime.TryImport("Camera", out var m);
        Assert.False(Assert.IsType<BooleanObj>(Call(m, "IsSupported")).Value); Assert.Empty(Assert.IsType<ArrayObj>(Call(m, "Enumerate")).Elements);
        Assert.IsType<ErrorObj>(Call(m, "Open", new StringObj(""), new IntegerObj(1), new IntegerObj(1), new IntegerObj(30)));
    }
    private static IObject Call(ModuleObj module, string name, params IObject[] args) => ((BuiltinObj)module._Environment.Get(name, out _)).Fn(args);
    private sealed class FakeBackend : ICameraBackend
    {
        public bool IsSupported => true; public int Opens; public List<Session> Sessions = new();
        public ValueTask<IReadOnlyList<CameraDevice>> Enumerate(CancellationToken cancellation = default) => new(new[] { new CameraDevice("back", "Back", CameraFacing.Back) });
        public ValueTask<ICameraSession> Open(string id, CameraOptions options, CancellationToken cancellation = default) { Opens++; var s = new Session(); Sessions.Add(s); return new(s); }
    }
    private sealed class Session : ICameraSession
    {
        public int Closes; public bool IsOpen => Closes == 0;
        public ValueTask<CameraFrame> Capture(CancellationToken cancellation = default) => new(new CameraFrame(1, 1, new byte[] { 1, 2, 3, 255 }, 1, 0, CameraFacing.Back));
        public void Dispose() => Closes++;
    }
}
