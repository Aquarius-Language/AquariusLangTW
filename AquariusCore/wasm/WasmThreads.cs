using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using AquariusLang.Object;
using AquariusLang.Threading;

namespace AquariusLang.Wasm;

public sealed partial class WasmRuntime
{
    private sealed partial class Session
    {
        private sealed class ThreadModule : IObject { public string Type() => "MODULE"; public string Inspect() => "Thread"; }
        private readonly IWasmEngine engine;
        private readonly HashSet<ThreadHandle> threads = new();
        private sealed class ThreadHandle : IObject
        {
            internal readonly Session Owner;
            internal IThreadWorker? Worker;
            internal ThreadHandle(Session owner, IThreadWorker worker) { Owner = owner; Worker = worker; }
            public string Type() => "THREAD";
            public string Inspect() => Worker == null ? "thread (closed)" : "thread";
        }
        private void CloseThreads()
        {
            foreach (var handle in threads) handle.Worker?.Cancel();
            foreach (var handle in threads) { handle.Worker?.Dispose(); handle.Worker = null; }
            threads.Clear();
        }
        private int ThreadService(int operation, int arguments, int count, int output)
        {
            try {
                if (operation == 100) {
                    if (threads.Count >= 64) throw new InvalidOperationException("Close threads before starting more (limit: 64).");
                    var backend = ThreadBackendFactory?.Invoke() ?? throw new NotSupportedException("This host has no threading backend.");
                    int closure = Int(arguments + 4), array = count == 2 ? Int(arguments + 20) : 0;
                    int items = array == 0 ? 0 : exports.ArrayValues(array), length = array == 0 ? 0 : exports.ArrayCount(array);
                    byte[] snapshot = instance.Memory(0, exports.ThreadSnapshotSize()).ToArray();
                    var handle = new ThreadHandle(this, backend.Start(token => RunThread(engine, Program, snapshot, closure, items, length, token)));
                    threads.Add(handle); Write(output, handle); return 0;
                }
                if (operation == 106) { Thread.Sleep((int)Number(arguments + 8)); Write(output, new BooleanObj(true)); return 0; }
                if (operation == 107) { Write(output, new BooleanObj(false)); return 0; }
                if (Read(arguments) is not ThreadHandle h || !ReferenceEquals(h.Owner, this)) throw new ArgumentException("Expected a thread handle from this runtime.");
                int timeout = count == 2 ? (int)Number(arguments + 24) : -1;
                if (operation == 104) {
                    if (h.Worker == null) { Write(output, new BooleanObj(true)); return 0; }
                    h.Worker.Cancel(); bool finished = h.Worker.Wait(timeout);
                    if (finished) { h.Worker.Dispose(); h.Worker = null; threads.Remove(h); }
                    Write(output, new BooleanObj(finished)); return 0;
                }
                var worker = h.Worker ?? throw new InvalidOperationException("Thread is closed.");
                if (operation == 102) {
                    byte[] packet = worker.Result(); int p = exports.Allocate(packet.Length); packet.CopyTo(instance.Memory(p, packet.Length));
                    int cell = exports.ThreadDecode(p); if (cell == 0) throw new InvalidOperationException("Invalid thread result packet.");
                    instance.Memory(cell, 16).CopyTo(instance.Memory(output, 16)); exports.ThreadFreePacket(p); return 0;
                }
                Write(output, new BooleanObj(operation switch { 101 => worker.Wait(timeout), 103 => worker.Cancel(), 105 => worker.IsDone, _ => throw new InvalidOperationException("Unknown thread operation.") }));
            } catch (Exception error) { Write(output, new ErrorObj("Thread: " + (error is OperationCanceledException ? "Execution cancelled." : error.Message))); }
            return 0;
        }
        private static byte[] RunThread(IWasmEngine engine, WasmProgram program, byte[] snapshot, int closure, int arguments, int count, CancellationToken token)
        {
            IWasmInstance? worker = null;
            int Service(int op, int context, int subject, int name, int args, int n, int output) {
                var w = worker!;
                void Raw(int tag, int reference = 0, double number = 0) { var cell = w.Memory(output, 16); cell.Clear(); BinaryPrimitives.WriteInt32LittleEndian(cell, tag); BinaryPrimitives.WriteInt32LittleEndian(cell[4..], reference); BinaryPrimitives.WriteInt64LittleEndian(cell[8..], BitConverter.DoubleToInt64Bits(number)); }
                if (op == 0) {
                    int size = BinaryPrimitives.ReadInt32LittleEndian(w.Memory(name, 4));
                    string key = System.Text.Encoding.Unicode.GetString(w.Memory(name + 4, size * 2));
                    int id = key switch { "len" or "長度" => 1, "last" or "最後一個" => 2, "rest" or "其餘" => 3, "push" or "加入" => 4, "floodFill" or "區域填色" => 5, _ => 0 };
                    if (id != 0) { Raw(14, id); return 0; }
                    throw new NotSupportedException("Worker cannot access host capability: " + key);
                }
                if (op == 4 && subject == 107) { Raw(4, number: token.IsCancellationRequested ? 1 : 0); return 0; }
                if (op == 4 && subject == 106) {
                    int ms = (int)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(w.Memory(args + 8, 8)));
                    bool cancelled = token.WaitHandle.WaitOne(ms); Raw(4, number: cancelled ? 0 : 1); return 0;
                }
                throw new NotSupportedException("Worker cannot access host handles or start/wait for other threads.");
            }
            using (worker = engine.Instantiate(program.Bytes, new Dictionary<string, Delegate> { ["service"] = (Func<int, int, int, int, int, int, int, int>)Service })) {
                token.ThrowIfCancellationRequested(); worker.Invoke("aqua_thread_reserve", snapshot.Length); snapshot.CopyTo(worker.Memory(0, snapshot.Length));
                int execution = worker.Invoke("aqua_thread_enter", closure, arguments, count);
                while (true) {
                    token.ThrowIfCancellationRequested(); int status = worker.Invoke("aqua_run", execution, WasmAbi.Quantum);
                    if (status == 1) continue;
                    if (status != 3 && status != 4) throw new InvalidOperationException("Invalid worker execution status.");
                    int packet = worker.Invoke("aqua_thread_encode", worker.Invoke("aqua_result", execution));
                    if (packet == 0) throw new InvalidOperationException("Thread results must be portable data (no functions or host handles; depth <= 128, graph <= 4096, packet <= 16 MiB).");
                    int size = BinaryPrimitives.ReadInt32LittleEndian(worker.Memory(packet, 4)); return worker.Memory(packet, size + 4).ToArray();
                }
            }
        }
    }
}
