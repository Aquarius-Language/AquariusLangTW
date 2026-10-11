using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AquariusLang.Application;

namespace AquariusLang.Webcam;

/// <summary>攝影機的宿主能力邊界；核心不持有原生裝置、程序或瀏覽器物件。</summary>
public interface IWebcamBackend {
    bool IsSupported { get; }
    ValueTask<IReadOnlyList<WebcamDevice>> Enumerate(CancellationToken cancellation = default);
    /// <summary>僅在呼叫此方法時啟用攝影機；失敗或取消必須釋放已取得的資源。</summary>
    ValueTask<IWebcamSession> Open(string deviceId, WebcamOptions options, CancellationToken cancellation = default);
}

public interface IWebcamSession : IDisposable {
    bool IsOpen { get; }
    /// <summary>取得最新且尚未交付的完整影格，慢速讀取可略過舊影格；不允許同時擷取，取消或逾時後關閉工作階段。</summary>
    ValueTask<WebcamFrame> Capture(CancellationToken cancellation = default);
}

public sealed record WebcamDevice(string Id, string Name);

/// <summary>輸出尺寸固定；影格率是裝置的偏好值，不能保證硬體一定支援。</summary>
public sealed record WebcamOptions(int Width = 640, int Height = 480, int FramesPerSecond = 30, int TimeoutMilliseconds = 10000) {
    public const int MaxSessions = 4;
    public void Validate() {
        if (Width is < 1 or > 1920 || Height is < 1 or > 1080)
            throw new WebcamFailure(WebcamFailureKind.InvalidArgument, "寬度必須介於 1 至 1920，高度必須介於 1 至 1080。");
        if (FramesPerSecond is < 1 or > 60)
            throw new WebcamFailure(WebcamFailureKind.InvalidArgument, "影格率必須介於 1 至 60。");
        if (TimeoutMilliseconds is < 1 or > 60000)
            throw new WebcamFailure(WebcamFailureKind.InvalidArgument, "逾時時間必須介於 1 至 60000 毫秒。");
    }
}

/// <summary>獨立且不可變的快照；像素由上而下排列，格式為不透明 RGBA8。</summary>
public sealed class WebcamFrame {
    public PixelImage Image { get; }
    public long Sequence { get; }
    public double TimestampMilliseconds { get; }
    public WebcamFrame(int width, int height, ReadOnlySpan<byte> pixels, long sequence, double timestampMilliseconds) {
        new WebcamOptions(width, height).Validate();
        if (pixels.Length != checked(width * height * 4) || sequence < 1 || !double.IsFinite(timestampMilliseconds) || timestampMilliseconds < 0)
            throw new WebcamFailure(WebcamFailureKind.Unavailable, "攝影機傳回無效影格。");
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 255) throw new WebcamFailure(WebcamFailureKind.Unavailable, "攝影機影格必須是不透明的 RGBA8 像素。");
        Image = new PixelImage(width, height, pixels, AlphaRepresentation.Opaque);
        Sequence = sequence; TimestampMilliseconds = timestampMilliseconds;
    }
}

public enum WebcamFailureKind { Unsupported, PermissionDenied, NotFound, Unavailable, Timeout, Cancelled, Closed, InvalidArgument }

/// <summary>跨平台錯誤種類及繁體中文訊息，不將原生錯誤或裝置資訊寫入使用者介面。</summary>
public sealed class WebcamFailure : Exception {
    public WebcamFailureKind Kind { get; }
    public WebcamFailure(WebcamFailureKind kind, string message) : base(message) { Kind = kind; }
    public static string Name(WebcamFailureKind kind) => kind switch {
        WebcamFailureKind.Unsupported => "不支援", WebcamFailureKind.PermissionDenied => "權限遭拒",
        WebcamFailureKind.NotFound => "找不到裝置", WebcamFailureKind.Unavailable => "裝置無法使用",
        WebcamFailureKind.Timeout => "擷取逾時", WebcamFailureKind.Cancelled => "已取消",
        WebcamFailureKind.Closed => "已關閉", _ => "參數無效"
    };
}
