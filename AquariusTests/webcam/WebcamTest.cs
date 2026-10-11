using AquariusLang.Compiler;
using AquariusLang.Desktop;
using AquariusLang.Desktop.Graphics;
using AquariusLang.Desktop.Webcam;
using AquariusLang.External;
using AquariusLang.Object;
using AquariusLang.Webcam;

namespace AquariusTests.Webcam;

public class WebcamTest {
    private static IObject Call(ModuleObj module, string name, params IObject[] args) => ((BuiltinObj)module._Environment.Get(name, out _)).Fn(args);
    private sealed class Backend : IWebcamBackend {
        public int Opens;
        public bool IsSupported => true;
        public readonly List<Session> Sessions = new();
        public ValueTask<IReadOnlyList<WebcamDevice>> Enumerate(CancellationToken cancellation = default) => new(new[] { new WebcamDevice("測試裝置", "測試攝影機") });
        public ValueTask<IWebcamSession> Open(string id, WebcamOptions options, CancellationToken cancellation = default) {
            Opens++; var session = new Session(options); Sessions.Add(session); return new((IWebcamSession)session);
        }
    }
    private sealed class Session(WebcamOptions options) : IWebcamSession {
        public int Disposals; private long sequence;
        public bool IsOpen => Disposals == 0;
        public ValueTask<WebcamFrame> Capture(CancellationToken cancellation = default) {
            cancellation.ThrowIfCancellationRequested(); var bytes = new byte[options.Width * options.Height * 4];
            for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
            return new(new WebcamFrame(options.Width, options.Height, bytes, ++sequence, sequence));
        }
        public void Dispose() { Disposals++; }
    }
    [Fact(DisplayName = "匯入及無效參數不會啟用攝影機")]
    public void ImportAndValidationNeverOpenHardware() {
        var backend = new Backend(); using var runtime = new WebcamRuntime(backend, _ => new NullObj());
        Assert.True(runtime.TryImport("攝影機", out var module)); Assert.False(runtime.TryImport("未知", out _));
        Assert.IsType<ErrorObj>(Call(module, "開啟"));
        foreach (IObject invalid in new IObject[] { new DoubleObj(1.5), new DoubleObj(double.NaN), new DoubleObj(double.PositiveInfinity), new IntegerObj(0), new IntegerObj(1921), new StringObj("640") })
            Assert.Contains("參數無效", Assert.IsType<ErrorObj>(Call(module, "開啟", new StringObj(""), invalid, new IntegerObj(480), new IntegerObj(30))).Message);
        Assert.Equal(0, backend.Opens);
        Assert.Equal(ExternalLibraryProfile.HostCapability, ExternalLibraries.Webcam.Profile);
        Assert.All(WebcamCatalog.Functions, f => { Assert.Single(f.Names); Assert.Contains("參數數量", f.Detail); });
        Assert.DoesNotContain(typeof(WebcamRuntime).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Wasmtime") || a.Name.StartsWith("Silk") || a.Name.StartsWith("ClearScript"));
    }
    [Fact(DisplayName = "影格具有獨立快照並驗證像素格式")]
    public void FrameOwnsPixelsAndRejectsInvalidData() {
        byte[] bytes = { 1, 2, 3, 255 }; var frame = new WebcamFrame(1, 1, bytes, 1, 0); bytes[0] = 9;
        Assert.Equal(1, frame.Image.Pixels.Span[0]);
        Assert.Throws<WebcamFailure>(() => new WebcamFrame(1, 1, new byte[3], 1, 0));
        Assert.Throws<WebcamFailure>(() => new WebcamFrame(1, 1, new byte[4], 1, 0));
        Assert.Throws<WebcamFailure>(() => new WebcamFrame(1, 1, new byte[] {1,2,3,255}, 0, 0));
        Assert.Throws<WebcamFailure>(() => new WebcamFrame(1, 1, new byte[] {1,2,3,255}, 1, double.NaN));
    }
    [Fact(DisplayName = "工作階段上限、重複釋放及關閉後操作")]
    public void OwnershipAndSessionLimit() {
        var backend = new Backend(); var runtime = new WebcamRuntime(backend, _ => new NullObj()); runtime.TryImport("攝影機", out var module);
        var sessions = Enumerable.Range(0, 4).Select(_ => Assert.IsType<ModuleObj>(Call(module, "開啟", new StringObj(""), new IntegerObj(1), new IntegerObj(1), new IntegerObj(30)))).ToArray();
        Assert.IsType<ErrorObj>(Call(module, "開啟", new StringObj(""), new IntegerObj(1), new IntegerObj(1), new IntegerObj(30)));
        var frame = Assert.IsType<ModuleObj>(Call(sessions[0], "擷取影格")); Assert.Equal(4, Assert.IsType<ArrayObj>(Call(frame, "取得像素")).Elements.Length);
        Call(sessions[0], "釋放"); Call(sessions[0], "釋放"); Assert.Equal(1, backend.Sessions[0].Disposals);
        Assert.False(Assert.IsType<BooleanObj>(Call(sessions[0], "是否開啟")).Value);
        Assert.Contains("已關閉", Assert.IsType<ErrorObj>(Call(sessions[0], "擷取影格")).Message);
        runtime.Dispose(); runtime.Dispose(); Assert.All(backend.Sessions, s => Assert.Equal(1, s.Disposals));
        Assert.IsType<ErrorObj>(Call(module, "是否支援"));
    }
    [Fact(DisplayName = "繁體中文腳本透過 Wasm 完成攝影機工作階段")]
    public void ChineseScriptSmoke() {
        var backend = new Backend(); using var graphics = new GraphicsRuntime(webcamBackend: backend); using var builtins = new DesktopBuiltins(graphics);
        var result = new CompiledRuntime(builtins).Execute(new LoweringCompiler().Compile(@"
變數 攝影機 = 匯入(""攝影機"");
變數 裝置 = 攝影機.列出裝置();
變數 工作階段 = 攝影機.開啟(裝置[0].識別碼, 2, 2, 30);
變數 影格 = 工作階段.擷取影格();
變數 像素 = 影格.取得像素();
變數 成功 = 攝影機.是否支援() && 工作階段.是否開啟() && 影格.寬度 == 2 && 影格.高度 == 2 && 長度(像素) == 16 && 像素[3] == 255 && 影格.序號 == 1;
工作階段.釋放(); 工作階段.釋放();
成功 && !工作階段.是否開啟();"));
        Assert.True(Assert.IsType<BooleanObj>(result).Value); Assert.Equal(1, backend.Sessions[0].Disposals);
    }
    [Fact(DisplayName = "桌面裝置清單只包含影像裝置且保留不透明識別碼")]
    public void DeviceParsingAndArgumentBoundaries() {
        var windows = DesktopWebcamBackend.ParseDevices("[dshow @ 0] \"攝影機一\" (video)\n[dshow @ 0] Alternative name \"@device_一\"\n[dshow @ 0] \"麥克風\" (audio)\n[dshow @ 0] Alternative name \"@audio\"", true);
        var device = Assert.Single(windows); Assert.Equal("video=@device_一", device.Id); Assert.Equal("攝影機一", device.Name);
        var mac = DesktopWebcamBackend.ParseDevices("[AVFoundation @ 0] AVFoundation video devices:\n[AVFoundation @ 0] [0] 攝影機二\n[AVFoundation @ 0] AVFoundation audio devices:\n[AVFoundation @ 0] [0] 麥克風", false);
        Assert.Equal("0:none", Assert.Single(mac).Id);
        var info = DesktopWebcamBackend.CreateStartInfo("ffmpeg", new[] {"-i", "video=攝影機 & 空白; $(文字)"});
        Assert.False(info.UseShellExecute); Assert.True(info.CreateNoWindow); Assert.Equal("video=攝影機 & 空白; $(文字)", info.ArgumentList[1]);
        Assert.False(new DesktopWebcamBackend(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")).IsSupported);
    }
    [Fact(DisplayName = "桌面虛擬裝置冒煙測試及取消清理")]
    public async Task DesktopVirtualDeviceSmoke() {
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_WEBCAM_SMOKE") != "1") return;
        var backend = new DesktopWebcamBackend(); Assert.True(backend.IsSupported);
        using var session = await backend.OpenInput("lavfi", "testsrc=size=16x12:rate=30", new WebcamOptions(16, 12));
        var first = await session.Capture(); var next = await session.Capture();
        Assert.Equal(768, first.Image.Pixels.Length); Assert.True(first.Sequence >= 1); Assert.True(next.Sequence > first.Sequence);
        Assert.True(next.TimestampMilliseconds >= first.TimestampMilliseconds); Assert.Contains(first.Image.Pixels.ToArray(), b => b != 0 && b != 255);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Equal(WebcamFailureKind.Cancelled, (await Assert.ThrowsAsync<WebcamFailure>(() => session.Capture(cancel.Token).AsTask())).Kind);
        Assert.False(session.IsOpen); session.Dispose();
        var timeout = await Assert.ThrowsAsync<WebcamFailure>(() => backend.OpenInput("lavfi", "color=size=16x12:rate=30,select=0", new WebcamOptions(16, 12, 30, 250)).AsTask());
        Assert.Equal(WebcamFailureKind.Timeout, timeout.Kind);
        var invalid = await Assert.ThrowsAsync<WebcamFailure>(() => backend.OpenInput("lavfi", "不存在的虛擬來源", new WebcamOptions(16, 12)).AsTask());
        Assert.Equal(WebcamFailureKind.Unavailable, invalid.Kind);
    }
}
