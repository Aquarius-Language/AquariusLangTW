using System;

namespace AquariusLang.Wasm;

/// <summary>
/// Typed runtime exports of one session, bound on first use. Each call is a direct delegate call with no
/// name lookup or argument array. Binding is lazy so an older ABI 2 artifact that lacks an additive export
/// fails only if the host actually calls it; feature probes use <see cref="WasmProgram.HasExport"/>.
/// </summary>
internal sealed class WasmRuntimeExports(IWasmInstance instance)
{
    private Func<int>? weakMirrors, takeReleased, pending;
    private Func<int>? value, takeDirtyArray, takeDirtyHash, collectionDue, collectionGeneration, collect, threadModule, threadSnapshotSize;
    private Func<int, int>? pin, unpin, text, scope, scopeCount, arrayCount, arrayValues, array, arrayExpose, hash, hashPairs, hashCount,
        hashExpose, result, releaseExecution, nativeLive, allocate, initialize, threadDecode, threadFreePacket;
    private Func<int, int>? arrayItems;
    private Func<int, int, int>? scopeGet, scopeBinding, arrayResize, hashResize, run, stale;
    private Func<int, int, int, int>? scopeSet, scopeAssign, invoke;
    private Func<int, int, int, int, int>? execute;

    public int WeakMirrors() => (weakMirrors ??= instance.Export("aqua_weak_mirrors"))();
    public int Pending() => (pending ??= instance.Export("aqua_pending"))();
    public int TakeReleased() => (takeReleased ??= instance.Export("aqua_take_released"))();
    public int Value() => (value ??= instance.Export("aqua_value"))();
    public int TakeDirtyArray() => (takeDirtyArray ??= instance.Export("aqua_take_dirty_array"))();
    public int TakeDirtyHash() => (takeDirtyHash ??= instance.Export("aqua_take_dirty_hash"))();
    public int CollectionDue() => (collectionDue ??= instance.Export("aqua_collection_due"))();
    public int CollectionGeneration() => (collectionGeneration ??= instance.Export("aqua_collection_generation"))();
    public int Collect() => (collect ??= instance.Export("aqua_collect"))();
    public int ThreadModule() => (threadModule ??= instance.Export("aqua_thread_module"))();
    public int ThreadSnapshotSize() => (threadSnapshotSize ??= instance.Export("aqua_thread_snapshot_size"))();

    public int Pin(int cell) => (pin ??= instance.Export1("aqua_pin"))(cell);
    public int Unpin(int cell) => (unpin ??= instance.Export1("aqua_unpin"))(cell);
    public int Text(int length) => (text ??= instance.Export1("aqua_text"))(length);
    public int Scope(int outer) => (scope ??= instance.Export1("aqua_scope"))(outer);
    public int ScopeCount(int scope) => (scopeCount ??= instance.Export1("aqua_scope_count"))(scope);
    public int ArrayCount(int array) => (arrayCount ??= instance.Export1("aqua_array_count"))(array);
    public int ArrayValues(int array) => (arrayValues ??= instance.Export1("aqua_array_values"))(array);
    public int ArrayItems(int array) => (arrayItems ??= instance.Export1("aqua_array_items"))(array);
    public int Array(int count) => (array ??= instance.Export1("aqua_array"))(count);
    public int ArrayExpose(int array) => (arrayExpose ??= instance.Export1("aqua_array_expose"))(array);
    public int Hash(int count) => (hash ??= instance.Export1("aqua_hash"))(count);
    public int HashPairs(int hash) => (hashPairs ??= instance.Export1("aqua_hash_pairs"))(hash);
    public int HashCount(int hash) => (hashCount ??= instance.Export1("aqua_hash_count"))(hash);
    public int HashExpose(int hash) => (hashExpose ??= instance.Export1("aqua_hash_expose"))(hash);
    public int Result(int execution) => (result ??= instance.Export1("aqua_result"))(execution);
    public int ReleaseExecution(int execution) => (releaseExecution ??= instance.Export1("aqua_release_execution"))(execution);
    public int NativeLive(int id) => (nativeLive ??= instance.Export1("aqua_native_live"))(id);
    public int Allocate(int size) => (allocate ??= instance.Export1("aqua_allocate"))(size);
    public int Initialize(int heapStart) => (initialize ??= instance.Export1("aqua_initialize"))(heapStart);
    public int ThreadDecode(int packet) => (threadDecode ??= instance.Export1("aqua_thread_decode"))(packet);
    public int ThreadFreePacket(int packet) => (threadFreePacket ??= instance.Export1("aqua_thread_free_packet"))(packet);

    public int ScopeGet(int scope, int name) => (scopeGet ??= instance.Export2("aqua_scope_get"))(scope, name);
    public int ScopeBinding(int scope, int index) => (scopeBinding ??= instance.Export2("aqua_scope_binding"))(scope, index);
    public int ArrayResize(int array, int count) => (arrayResize ??= instance.Export2("aqua_array_resize"))(array, count);
    public int HashResize(int hash, int count) => (hashResize ??= instance.Export2("aqua_hash_resize"))(hash, count);
    public int Stale(int tag, int reference) => (stale ??= instance.Export2("aqua_stale"))(tag, reference);
    public int Run(int execution, int budget) => (run ??= instance.Export2("aqua_run"))(execution, budget);

    public int ScopeSet(int scope, int name, int cell) => (scopeSet ??= instance.Export3("aqua_scope_set"))(scope, name, cell);
    public int ScopeAssign(int scope, int name, int cell) => (scopeAssign ??= instance.Export3("aqua_scope_assign"))(scope, name, cell);
    public int Invoke(int closure, int arguments, int count) => (invoke ??= instance.Export3("aqua_invoke"))(closure, arguments, count);

    public int Execute(int program, int function, int scope, int context) => (execute ??= instance.Export4("aqua_execute"))(program, function, scope, context);
}
