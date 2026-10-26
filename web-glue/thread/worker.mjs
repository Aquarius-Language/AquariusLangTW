// A Thread worker: instantiates the application's module alone, restores the parent's memory snapshot and runs one
// closure (the guest's aqua_thread_enter / aqua_run / aqua_thread_encode). The guest resolves builtins and refuses
// host access itself; its only calls here are Thread.Sleep (a timer the parent's cancellation ends early) and
// Thread.IsCancellationRequested. Posts { packet } (the encoded result) or { error }.
const QUANTUM = 32768;
const STATUS = { yielded: 1, suspended: 2, failed: 3, completed: 4 };
const BOOL = 4; // the guest's value tag (runtime/guest/layout.def)
let cancelled = false, wake = null, started = false;

self.onmessage = async ({ data }) => {
    if (data.cancel) { cancelled = true; wake?.(); return; }
    if (started) return;
    started = true;
    try { const packet = await run(data); self.postMessage({ packet }, [packet]); }
    catch (error) { self.postMessage({ error: error.message }); }
};

async function run({ module, snapshot, closure, items, count }) {
    let guest, sleeping = null;
    const view = () => new DataView(guest.memory.buffer);
    const service = (operation, context, subject, name, args, n, output) => {
        if (operation === 4 && subject === 107) { writeBool(view(), output, cancelled); return 0; }
        if (operation === 4 && subject === 106) {
            const ms = view().getFloat64(args + 8, true);
            sleeping = new Promise((resolve) => {
                const timer = setTimeout(() => finish(true), ms);
                const finish = (slept) => { clearTimeout(timer); wake = null; resolve(slept && !cancelled); };
                wake = () => finish(false);
                if (cancelled) finish(false);
            });
            return 1; // the guest suspends until aqua_resume
        }
        throw new Error('Thread workers call only Sleep and IsCancellationRequested.');
    };
    guest = (await WebAssembly.instantiate(module, { aquarius_v2: { service } })).exports;
    guest.aqua_thread_reserve(snapshot.byteLength);
    new Uint8Array(guest.memory.buffer, 0, snapshot.byteLength).set(new Uint8Array(snapshot));
    const execution = guest.aqua_thread_enter(closure, items, count);
    for (;;) {
        if (cancelled) throw new Error('Execution cancelled.');
        const status = guest.aqua_run(execution, QUANTUM);
        if (status === STATUS.yielded) { await new Promise((resolve) => setTimeout(resolve, 0)); continue; }
        if (status === STATUS.suspended) {
            const slept = await sleeping;
            const cell = guest.aqua_value();
            writeBool(view(), cell, slept);
            guest.aqua_resume(execution, cell);
            continue;
        }
        if (status !== STATUS.failed && status !== STATUS.completed) throw new Error('Invalid worker execution status.');
        const packet = guest.aqua_thread_encode(guest.aqua_result(execution));
        if (!packet) throw new Error('Thread results must be portable data (no functions or host handles; depth <= 128, graph <= 4096, packet <= 16 MiB).');
        return guest.memory.buffer.slice(packet, packet + 4 + view().getUint32(packet, true));
    }
}

function writeBool(view, pointer, value) {
    view.setUint32(pointer, BOOL, true);
    view.setUint32(pointer + 4, 0, true);
    view.setFloat64(pointer + 8, value ? 1 : 0, true);
}
