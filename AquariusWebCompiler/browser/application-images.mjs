// Codecs run in a disposable worker. Cancellation never leaves a live native decode behind.
export class BrowserImages {
  constructor() {
    this.next = 0;
    this.pending = new Map();
    this.worker = null;
  }
  run(operation, args, signal) {
    signal?.throwIfAborted();
    if (!this.worker) {
      this.worker = new Worker(new URL("./codec-worker.mjs", import.meta.url), {
        type: "module",
      });
      this.worker.onmessage = (e) => {
        const p = this.pending.get(e.data.id);
        if (!p) return;
        this.pending.delete(e.data.id);
        p.cleanup();
        e.data.error
          ? p.reject(new Error(e.data.error))
          : p.resolve(e.data.value);
      };
      this.worker.onerror = (e) => this.dispose(new Error(e.message));
    }
    return new Promise((resolve, reject) => {
      const id = ++this.next,
        abort = () =>
          this.dispose(
            signal.reason ?? new DOMException("Cancelled", "AbortError"),
          ),
        timeout = setTimeout(
          () => this.dispose(new Error("Codec time limit exceeded")),
          35000,
        );
      const cleanup = () => {
        clearTimeout(timeout);
        signal?.removeEventListener("abort", abort);
      };
      this.pending.set(id, { resolve, reject, cleanup });
      signal?.addEventListener("abort", abort, { once: true });
      this.worker.postMessage({ id, operation, ...args });
    });
  }
  inspect(data, signal) {
    return this.run("inspect", { data }, signal);
  }
  decode(data, frame, signal) {
    return this.run("decode", { data, frame }, signal);
  }
  encode(image, format, options, signal) {
    return this.run("encode", { image, format, options }, signal);
  }
  dispose(error = new DOMException("Cancelled", "AbortError")) {
    this.worker?.terminate();
    this.worker = null;
    for (const p of this.pending.values()) {
      p.cleanup();
      p.reject(error);
    }
    this.pending.clear();
  }
}
