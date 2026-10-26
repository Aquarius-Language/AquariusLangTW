// Web Workers for the Thread / 執行緒 library (runtime/host/threads_web.c): each thread is a worker running the
// application's module from a snapshot of its memory (worker.mjs). This file only starts, messages, times and ends
// workers; the guest runtime decides what a worker may do and encodes its result.
import { registerGlue } from '../core/glue.mjs';

const LIMIT = 64;

registerGlue('thread', (context) => {
    const threads = new Map(); // id -> { worker, state: 0 running | 1 finished | 2 failed, result: Uint8Array, cancelled, waiters }
    let next = 1;
    const settle = (thread, state, result) => {
        if (thread.state) return;
        thread.state = state;
        thread.result = result;
        thread.worker.terminate();
        for (const waiter of thread.waiters) waiter();
        thread.waiters.clear();
    };
    const settled = (token, finished) => {
        context.call('aqua_web_thread_settled', token, finished ? 1 : 0);
        context.wake();
    };
    return {
        /** Starts a worker on a copy of [snapshot, snapshot + length); returns its id, or 0 when LIMIT threads are open. */
        thread_start(snapshot, length, closure, items, count) {
            if (threads.size >= LIMIT) return 0;
            const id = next++;
            const worker = new Worker(new URL('./worker.mjs', import.meta.url), { type: 'module' });
            const thread = { worker, state: 0, result: null, cancelled: false, waiters: new Set() };
            threads.set(id, thread);
            const text = (message) => new TextEncoder().encode(message);
            worker.onmessage = ({ data }) => data.packet ? settle(thread, 1, new Uint8Array(data.packet)) : settle(thread, 2, text(data.error));
            worker.onerror = (event) => { event.preventDefault(); settle(thread, 2, text(event.message || 'Thread worker failed.')); };
            worker.onmessageerror = () => settle(thread, 2, text('Invalid thread worker message.'));
            const bytes = context.bytes.slice(snapshot, snapshot + length).buffer;
            worker.postMessage({ module: context.program, snapshot: bytes, closure, items, count }, [bytes]);
            return id;
        },
        thread_state: (id) => threads.get(id)?.state ?? 1,
        /** The result packet, or the failure's text, in malloc'd memory; its length goes to *length. */
        thread_result(id, length) {
            const result = threads.get(id)?.result ?? new Uint8Array();
            const [pointer] = context.allocate(result);
            context.view.setUint32(length, result.length, true);
            return pointer;
        },
        thread_cancel(id) {
            const thread = threads.get(id);
            if (!thread || thread.state || thread.cancelled) return 0;
            thread.cancelled = true;
            thread.worker.postMessage({ cancel: true });
            return 1;
        },
        thread_close(id) {
            const thread = threads.get(id);
            if (!thread) return;
            thread.worker.terminate();
            threads.delete(id);
        },
        /** Settles `token` when thread `id` ends (finished 1) or after timeout ms (finished 0); id 0 is a plain timer. */
        thread_wait(id, timeout, token) {
            const thread = threads.get(id);
            if (id === 0 || !thread) { setTimeout(() => settled(token, true), Math.max(0, timeout)); return; }
            if (thread.state) { queueMicrotask(() => settled(token, true)); return; }
            let timer = 0;
            const done = () => { clearTimeout(timer); settled(token, true); };
            thread.waiters.add(done);
            if (timeout >= 0) timer = setTimeout(() => { thread.waiters.delete(done); settled(token, false); }, timeout);
        },
    };
});
