// The registry every part of the glue joins. A domain (core, webgpu, webgl, canvas, input, audio, network, storage,
// ...) registers a factory returning the functions it exposes; together they form the `aqua_web` import module the
// host core and every library (default or external) import Web APIs from. Each domain lives in its own folder of
// web-glue and touches only the Web API it exposes; library logic stays in the C, C++ or Rust code that calls it.
//
// Naming: functions are `<domain>_<operation>` (gpu_create_buffer, canvas_measure_text); the core domain's functions
// have no prefix. Adding a Web API means adding a domain folder and registering it in ../index.mjs.

const domains = new Map();

/** @param {string} name @param {(context: GlueContext) => Object<string, Function>} factory */
export function registerGlue(name, factory) {
    if (domains.has(name)) throw new Error(`Glue domain ${name} is registered twice.`);
    domains.set(name, factory);
}

const encoder = new TextEncoder(), decoder = new TextDecoder();

/** Shared helpers for every domain: memory views, text, JavaScript object handles and calls back into C. */
export class GlueContext {
    constructor() {
        this.memory = null;      // the application's WebAssembly.Memory, set before any module loads
        this.linker = null;      // core/loader.mjs Linker
        this.objects = [null];   // handle -> JavaScript object (0 is no object)
        this.freeHandles = [];
        this.wake = () => {};    // asks the runtime loop for a turn
        this.write = (stream, bytes) => {};
    }
    get bytes() { return new Uint8Array(this.memory.buffer); }
    get view() { return new DataView(this.memory.buffer); }
    get f32() { return new Float32Array(this.memory.buffer); }
    get u32() { return new Uint32Array(this.memory.buffer); }
    /** UTF-8 text at [pointer, pointer + length); a negative length reads up to the NUL. */
    text(pointer, length = -1) {
        if (!pointer) return '';
        const bytes = this.bytes;
        if (length < 0) { length = 0; while (bytes[pointer + length]) length++; }
        return decoder.decode(bytes.subarray(pointer, pointer + length));
    }
    /** Copies `text` (or bytes) into memory from malloc, NUL-terminated; returns [pointer, length]. */
    allocate(value) {
        const data = typeof value === 'string' ? encoder.encode(value) : value;
        const pointer = this.call('malloc', data.length + 1);
        const bytes = this.bytes;
        bytes.set(data, pointer);
        bytes[pointer + data.length] = 0;
        return [pointer, data.length];
    }
    /** Calls an exported C function by symbol name (malloc, free, a library's callback entry...). */
    call(symbol, ...args) {
        const fn = this.linker.symbol(symbol);
        if (!fn) throw new Error(`The glue needs ${symbol}, which no loaded module exports.`);
        return fn(...args);
    }
    /** Calls a C function pointer (an index of the shared function table). */
    callPointer(pointer, ...args) { return this.linker.table.get(pointer)(...args); }
    retain(object) {
        const handle = this.freeHandles.length ? this.freeHandles.pop() : this.objects.length;
        this.objects[handle] = object;
        return handle;
    }
    object(handle) { return this.objects[handle]; }
    release(handle) {
        if (handle > 0 && handle < this.objects.length && this.objects[handle] !== undefined) {
            this.objects[handle] = undefined;
            this.freeHandles.push(handle);
        }
    }
}

/** The `aqua_web` import namespace: every registered domain's functions, bound to one context. */
export function glueImports(context) {
    const imports = {};
    for (const [name, factory] of domains) {
        for (const [key, fn] of Object.entries(factory(context))) {
            if (key in imports) throw new Error(`Glue function ${key} of ${name} is already defined.`);
            imports[key] = fn;
        }
    }
    return imports;
}
