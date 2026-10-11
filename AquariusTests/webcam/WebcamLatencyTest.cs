using System.Threading.Channels;
using AquariusLang.Desktop.Webcam;
using AquariusLang.Webcam;

namespace AquariusTests.Webcam;

public class WebcamLatencyTest {
    /// <summary>每次推送都等待接收器開始下一次讀取，確保測試不依賴排程或延遲猜測。</summary>
    private sealed class FrameStream : Stream {
        private readonly Channel<(byte[] Bytes, TaskCompletionSource<bool> Processed)> input = Channel.CreateUnbounded<(byte[], TaskCompletionSource<bool>)>();
        private (byte[] Bytes, TaskCompletionSource<bool> Processed)? pending;
        private int offset;
        public int Closes;
        public async Task Push(params byte[] bytes) {
            var processed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await input.Writer.WriteAsync((bytes, processed)); await processed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default) {
            if (pending is { } previous && offset == previous.Bytes.Length) { previous.Processed.TrySetResult(true); pending = null; offset = 0; }
            pending ??= await input.Reader.ReadAsync(cancellation);
            var current = pending.Value; int length = Math.Min(buffer.Length, current.Bytes.Length - offset);
            current.Bytes.AsMemory(offset, length).CopyTo(buffer); offset += length;
            return length;
        }
        protected override void Dispose(bool disposing) { if (disposing) { Closes++; input.Writer.TryComplete(); } base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    [Fact(DisplayName = "慢速讀取只取得最新影格，暫停後不重播累積畫面")]
    public async Task SlowConsumerSkipsOldFramesAndOwnsSnapshots() {
        var stream = new FrameStream(); using var session = new DesktopWebcamSession(stream, new WebcamOptions(1, 1));
        var opening = session.Prime(CancellationToken.None); await stream.Push(1, 0, 0, 255); await opening;
        var first = await session.Capture(); Assert.Equal(1, first.Sequence);
        for (byte value = 2; value <= 100; value++) await stream.Push(value, 0, 0, 255);
        var newest = await session.Capture(); Assert.Equal(100, newest.Sequence); Assert.Equal(100, newest.Image.Pixels.Span[0]);
        Assert.Equal(1, first.Image.Pixels.Span[0]); Assert.True(newest.TimestampMilliseconds >= first.TimestampMilliseconds);
        var waiting = session.Capture().AsTask(); Assert.False(waiting.IsCompleted);
        await stream.Push(101, 0, 0, 255); Assert.Equal(101, (await waiting).Sequence);
        session.Dispose(); session.Dispose(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, stream.Closes);
    }
    [Fact(DisplayName = "分段傳入的影格在完整前不會取代最新畫面")]
    public async Task IncompleteFramesAreNeverPublished() {
        var stream = new FrameStream(); using var session = new DesktopWebcamSession(stream, new WebcamOptions(1, 1));
        await stream.Push(1, 2); var waiting = session.Capture().AsTask(); Assert.False(waiting.IsCompleted);
        await stream.Push(3, 255); var frame = await waiting;
        Assert.Equal(new byte[] {1,2,3,255}, frame.Image.Pixels.ToArray()); Assert.Equal(1, frame.Sequence);
    }
    [Fact(DisplayName = "等待新影格時仍能取消、釋放及拒絕同時擷取")]
    public async Task PendingCapturesRespectCancellationAndDisposal() {
        var stream = new FrameStream(); using var session = new DesktopWebcamSession(stream, new WebcamOptions(1, 1));
        var waiting = session.Capture().AsTask();
        Assert.Equal(WebcamFailureKind.Unavailable, (await Assert.ThrowsAsync<WebcamFailure>(() => session.Capture().AsTask())).Kind);
        await stream.Push(1, 2, 3, 255); await waiting;
        using var cancellation = new CancellationTokenSource(); var cancelled = session.Capture(cancellation.Token).AsTask(); cancellation.Cancel();
        Assert.Equal(WebcamFailureKind.Cancelled, (await Assert.ThrowsAsync<WebcamFailure>(() => cancelled)).Kind);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(session.IsOpen); Assert.Equal(1, stream.Closes);
        var other = new FrameStream(); using var closing = new DesktopWebcamSession(other, new WebcamOptions(1, 1));
        var pending = closing.Capture().AsTask(); closing.Dispose();
        Assert.Equal(WebcamFailureKind.Closed, (await Assert.ThrowsAsync<WebcamFailure>(() => pending)).Kind);
        await closing.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, other.Closes);
    }
    [Fact(DisplayName = "背景接收逾時會喚醒等待者並關閉串流")]
    public async Task ReceiverTimeoutClosesSourceAndWakesWaiters() {
        var stream = new FrameStream(); using var session = new DesktopWebcamSession(stream, new WebcamOptions(1, 1, 30, 50));
        Assert.Equal(WebcamFailureKind.Timeout, (await Assert.ThrowsAsync<WebcamFailure>(() => session.Prime(CancellationToken.None).AsTask())).Kind);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(session.IsOpen); Assert.Equal(1, stream.Closes);
    }
    [Fact(DisplayName = "虛擬攝影機以即時速度輸出，慢速讀取不會增加影格延遲")]
    public async Task RealtimeVirtualDeviceStaysCurrentWithSlowConsumer() {
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_WEBCAM_SMOKE") != "1") return;
        var backend = new DesktopWebcamBackend();
        using var session = await backend.OpenInput("lavfi", "testsrc=size=16x12:rate=30", new WebcamOptions(16, 12));
        var previous = await session.Capture();
        for (int round = 0; round < 3; round++) {
            await Task.Delay(400); var frame = await session.Capture();
            Assert.True(frame.Sequence >= previous.Sequence + 3, "慢速讀取應略過已過時的影格。");
            Assert.True(frame.TimestampMilliseconds > previous.TimestampMilliseconds); previous = frame;
        }
    }
}
