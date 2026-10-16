using System;
using System.Threading;
using AquariusLang.runtime;
using AquariusLang.Object;

namespace AquariusLang.Threading;

/// <summary>A worker owns its engine/store. Completion includes releasing those resources.</summary>
public interface IThreadBackend { IThreadWorker Start(Func<CancellationToken, byte[]> work); }
public interface IThreadWorker : IDisposable
{
    bool IsDone { get; }
    bool Wait(int milliseconds);
    bool Cancel();
    byte[] Result();
}
public static class ThreadCatalog
{
    public static bool IsName(string name) => name is "Thread" or "執行緒";
    public static ModuleObj CreateModule() {
        var environment = AquariusLang.Object.Environment.NewEnvironment();
        for (int i = 0; i < Functions.Length; i++) {
            var token = Resolve(100 + i)!;
            foreach (var name in Functions[i].Names) environment.Create(name, token);
        }
        return new ModuleObj(environment);
    }
    public static BuiltinObj? Resolve(int id) => id is >= 100 and <= 107 ? BuiltinObj.FromPortable(id, _ => new ErrorObj("Thread requires compiled Wasm execution.")) : null;
    public static readonly LibraryFunction[] Functions = {
        new("Thread", "Start", "啟動", 1, 2, "Thread.Handle"), new("Thread", "Wait", "等待", 1, 2),
        new("Thread", "Result", "結果", 1, 1), new("Thread", "Cancel", "取消", 1, 1),
        new("Thread", "Close", "關閉", 1, 2), new("Thread", "IsDone", "是否完成", 1, 1),
        new("Thread", "Sleep", "休眠", 1, 1), new("Thread", "IsCancellationRequested", "是否要求取消", 0, 0)
    };
}
