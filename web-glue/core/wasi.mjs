// The WASI preview 1 calls the C library makes on the web: console output, clocks and randomness. Files do not exist
// here (libraries reach browser storage through their own Web API glue); every other call reports "not supported".

const ENOSYS = 52, EBADF = 8, ESPIPE = 70, SUCCESS = 0;

/** @param {{memory: () => WebAssembly.Memory, write: (stream: number, bytes: Uint8Array) => void}} context */
export function wasiImports(context) {
    const view = () => new DataView(context.memory().buffer);
    const bytes = () => new Uint8Array(context.memory().buffer);
    const unsupported = () => ENOSYS;
    const imports = {
        fd_write(fd, iovs, count, written) {
            if (fd !== 1 && fd !== 2) return EBADF;
            const v = view(); let total = 0;
            for (let i = 0; i < count; i++) {
                const pointer = v.getUint32(iovs + i * 8, true), length = v.getUint32(iovs + i * 8 + 4, true);
                context.write(fd, bytes().slice(pointer, pointer + length));
                total += length;
            }
            v.setUint32(written, total, true);
            return SUCCESS;
        },
        fd_fdstat_get(fd, stat) {
            if (fd > 2) return EBADF;
            const v = view();
            for (let i = 0; i < 24; i += 4) v.setUint32(stat + i, 0, true);
            v.setUint8(stat, 2); // character device
            return SUCCESS;
        },
        fd_close: (fd) => (fd > 2 ? EBADF : SUCCESS),
        fd_seek: () => ESPIPE,
        fd_read: () => EBADF,
        fd_prestat_get: () => EBADF,
        clock_time_get(id, precision, out) {
            const ns = id === 0 ? BigInt(Date.now()) * 1000000n : BigInt(Math.round(performance.now() * 1e6));
            view().setBigUint64(out, ns, true);
            return SUCCESS;
        },
        clock_res_get(id, out) { view().setBigUint64(out, 1000n, true); return SUCCESS; },
        random_get(pointer, length) {
            for (let offset = 0; offset < length; offset += 65536)
                crypto.getRandomValues(bytes().subarray(pointer + offset, pointer + Math.min(length, offset + 65536)));
            return SUCCESS;
        },
        args_sizes_get(count, size) { const v = view(); v.setUint32(count, 0, true); v.setUint32(size, 0, true); return SUCCESS; },
        args_get: () => SUCCESS,
        environ_sizes_get(count, size) { const v = view(); v.setUint32(count, 0, true); v.setUint32(size, 0, true); return SUCCESS; },
        environ_get: () => SUCCESS,
        sched_yield: () => SUCCESS,
        proc_exit(code) { throw new Error(`The program called exit(${code}).`); },
    };
    return new Proxy(imports, { get: (target, name) => target[name] ?? unsupported });
}
