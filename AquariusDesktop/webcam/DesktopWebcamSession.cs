using System.Diagnostics;
using AquariusLang.Webcam;

namespace AquariusLang.Desktop.Webcam;

/// <summary>持續排空擷取管線，以兩個固定緩衝區保留最新完整影格，避免慢速繪圖累積延遲。</summary>
internal sealed class DesktopWebcamSession : IWebcamSession {
    private readonly Stream source;
    private readonly Action closeSource;
    private readonly WebcamOptions options;
    private readonly CancellationTokenSource closed = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Task<string> diagnostic;
    private readonly object gate = new();
    private readonly Task receiver;
    private TaskCompletionSource<bool> changed = Notification();
    private byte[]? latest;
    private long sequence, delivered;
    private double timestamp;
    private WebcamFailure? failure;
    private int capturing, disposed;
    public DesktopWebcamSession(Process process, WebcamOptions options) : this(process.StandardOutput.BaseStream, options,
        () => { DesktopWebcamBackend.Stop(process); process.Dispose(); }, DesktopWebcamBackend.ReadDiagnostic(process.StandardError)) { }
    /// <summary>測試可注入原始影格串流，不需要啟用實體攝影機。</summary>
    internal DesktopWebcamSession(Stream source, WebcamOptions options, Action? closeSource = null, Task<string>? diagnostic = null) {
        options.Validate(); this.source = source; this.options = options;
        this.closeSource = closeSource ?? source.Dispose; this.diagnostic = diagnostic ?? Task.FromResult("");
        receiver = Task.Run(ReceiveFrames);
    }
    public bool IsOpen => Volatile.Read(ref disposed) == 0;
    internal Task Completion => receiver;
    private static TaskCompletionSource<bool> Notification() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal async ValueTask Prime(CancellationToken cancellation) {
        try { await WaitForFrame(cancellation, consume: false); }
        catch (OperationCanceledException) { var error = new WebcamFailure(WebcamFailureKind.Cancelled, "攝影機開啟已取消。"); Close(error); throw error; }
    }
    public async ValueTask<WebcamFrame> Capture(CancellationToken cancellation = default) {
        if (Interlocked.Exchange(ref capturing, 1) != 0) throw new WebcamFailure(WebcamFailureKind.Unavailable, "同一工作階段不能同時擷取多張影格。");
        try {
            return (await WaitForFrame(cancellation, consume: true))!;
        } catch (OperationCanceledException) { var error = new WebcamFailure(WebcamFailureKind.Cancelled, "攝影機擷取已取消。"); Close(error); throw error; }
        catch (WebcamFailure error) { Close(error); throw; }
        finally { Volatile.Write(ref capturing, 0); }
    }
    private async ValueTask<WebcamFrame?> WaitForFrame(CancellationToken cancellation, bool consume) {
        while (true) {
            cancellation.ThrowIfCancellationRequested(); Task notification;
            lock (gate) {
                if (disposed != 0) throw failure ?? new WebcamFailure(WebcamFailureKind.Closed, "攝影機工作階段已關閉。");
                if (latest != null && (!consume || sequence > delivered)) {
                    if (!consume) return null;
                    // 在交換緩衝區前完成快照複製，避免背景接收覆寫已交付的像素。
                    var frame = new WebcamFrame(options.Width, options.Height, latest, sequence, timestamp);
                    delivered = sequence; return frame;
                }
                notification = changed.Task;
            }
            await notification.WaitAsync(cancellation);
        }
    }
    private async Task ReceiveFrames() {
        int size = checked(options.Width * options.Height * 4);
        var incoming = new byte[size]; var spare = new byte[size];
        try {
            while (!closed.IsCancellationRequested) {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
                timeout.CancelAfter(options.TimeoutMilliseconds);
                await source.ReadExactlyAsync(incoming, timeout.Token);
                TaskCompletionSource<bool> notification;
                lock (gate) {
                    if (disposed != 0) return;
                    var previous = latest ?? spare; latest = incoming; incoming = previous;
                    sequence++; timestamp = clock.Elapsed.TotalMilliseconds;
                    notification = changed; changed = Notification();
                }
                notification.TrySetResult(true);
            }
        } catch (OperationCanceledException) {
            if (!closed.IsCancellationRequested) Close(new(WebcamFailureKind.Timeout, "攝影機等待新影格逾時。"));
        } catch (IOException) {
            string text = diagnostic.IsCompletedSuccessfully ? diagnostic.Result : "";
            Close(new(text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || text.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
                ? WebcamFailureKind.PermissionDenied : WebcamFailureKind.Unavailable, "攝影機無法提供完整影格；請確認權限、裝置連線、影格率或是否被其他程式占用。"));
        } catch (ObjectDisposedException) {
            Close(new(WebcamFailureKind.Closed, "攝影機工作階段已關閉。"));
        } catch (Exception) {
            Close(new(WebcamFailureKind.Unavailable, "攝影機影格接收失敗。"));
        }
    }
    private void Close(WebcamFailure reason) {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        TaskCompletionSource<bool> notification;
        lock (gate) { failure = reason; latest = null; notification = changed; }
        closed.Cancel();
        try { closeSource(); }
        finally {
            notification.TrySetResult(true);
            _ = diagnostic.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
    public void Dispose() => Close(new(WebcamFailureKind.Closed, "攝影機工作階段已關閉。"));
}
