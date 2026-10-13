using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AquariusLang.Application;

namespace AquariusLang.Camera;

public enum CameraFacing { Back, Front, External }
public sealed record CameraDevice(string Id, string Name, CameraFacing Facing);
public sealed record CameraOptions(int Width = 640, int Height = 480, int FramesPerSecond = 30,
    int TimeoutMilliseconds = 10000)
{
    public const int MaxSessions = 4;
    public void Validate()
    {
        if (Width is < 1 or > 1920 || Height is < 1 or > 1080 || FramesPerSecond is < 1 or > 60 || TimeoutMilliseconds is < 1 or > 60000)
            throw new CameraFailure(CameraFailureKind.InvalidArgument, "相機尺寸、影格率或逾時無效。");
    }
}
/// <summary>Mobile camera capability. Enumeration must not request permission or activate a sensor.</summary>
public interface ICameraBackend
{
    bool IsSupported { get; }
    ValueTask<IReadOnlyList<CameraDevice>> Enumerate(CancellationToken cancellation = default);
    ValueTask<ICameraSession> Open(string deviceId, CameraOptions options, CancellationToken cancellation = default);
}
public interface ICameraSession : IDisposable
{
    bool IsOpen { get; }
    ValueTask<CameraFrame> Capture(CancellationToken cancellation = default);
}
/// <summary>Independent, upright RGBA8 snapshot. Mirroring is a presentation choice, never applied to inference pixels.</summary>
public sealed class CameraFrame
{
    public PixelImage Image { get; }
    public long Sequence { get; }
    public double TimestampMilliseconds { get; }
    public CameraFacing Facing { get; }
    public CameraFrame(int width, int height, ReadOnlySpan<byte> pixels, long sequence, double timestampMilliseconds, CameraFacing facing)
    {
        if (width is < 1 or > 1920 || height is < 1 or > 1920 || (long)width * height > 1920L * 1080)
            throw new CameraFailure(CameraFailureKind.Unavailable, "相機影格尺寸無效。");
        if (pixels.Length != checked(width * height * 4) || sequence < 1 || !double.IsFinite(timestampMilliseconds) || timestampMilliseconds < 0 || !Enum.IsDefined(facing))
            throw new CameraFailure(CameraFailureKind.Unavailable, "相機傳回無效影格。");
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 255) throw new CameraFailure(CameraFailureKind.Unavailable, "相機影格必須是不透明 RGBA8。");
        Image = new PixelImage(width, height, pixels, AlphaRepresentation.Opaque);
        Sequence = sequence; TimestampMilliseconds = timestampMilliseconds; Facing = facing;
    }
}
public enum CameraFailureKind { Unsupported, PermissionDenied, NotFound, Unavailable, Timeout, Cancelled, Closed, InvalidArgument }
public sealed class CameraFailure(CameraFailureKind kind, string message) : Exception(message)
{
    public CameraFailureKind Kind { get; } = kind;
}
