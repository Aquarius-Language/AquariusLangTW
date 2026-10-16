using AquariusLang.Threading;

namespace AquariusLang.Desktop.runtime;

/// <summary>Dedicated OS threads on Windows, Linux and macOS; never shares a Wasmtime store.</summary>
public sealed class DesktopThreadBackend : IThreadBackend
{
    public IThreadWorker Start(Func<CancellationToken, byte[]> work) => new Worker(work);
    private sealed class Worker : IThreadWorker
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly TaskCompletionSource<byte[]> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread thread;
        private bool disposed;
        public Worker(Func<CancellationToken, byte[]> work)
        {
            thread = new Thread(() => {
                try { result.SetResult(work(cancellation.Token)); }
                catch (Exception error) { result.SetException(error); }
            }) { IsBackground = true, Name = "Aquarius worker" };
            thread.Start();
        }
        public bool IsDone => !thread.IsAlive;
        public bool Wait(int milliseconds) => thread.Join(milliseconds);
        public bool Cancel() { if (disposed || IsDone || cancellation.IsCancellationRequested) return false; cancellation.Cancel(); return true; }
        public byte[] Result() { thread.Join(); return result.Task.GetAwaiter().GetResult(); }
        public void Dispose() { if (disposed) return; Cancel(); thread.Join(); _ = result.Task.Exception; disposed = true; cancellation.Dispose(); }
    }
}
