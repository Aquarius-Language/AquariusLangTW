using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AquariusLang.Camera;

/// <summary>Explicit capability absence on hosts without mobile camera hardware.</summary>
public sealed class UnavailableCameraBackend : ICameraBackend
{
    public bool IsSupported => false;
    public ValueTask<IReadOnlyList<CameraDevice>> Enumerate(CancellationToken cancellation = default) => new(System.Array.Empty<CameraDevice>());
    public ValueTask<ICameraSession> Open(string deviceId, CameraOptions options, CancellationToken cancellation = default) =>
        ValueTask.FromException<ICameraSession>(new CameraFailure(CameraFailureKind.Unsupported, "此平台請使用網路攝影機。"));
}
