// Links WebAssembly side modules (clang -fPIC -shared, `dylink.0`) into the application's memory: the host core,
// libc/libc++ and every library share one memory and one function table, and call each other's exports directly.
// Each module's data lives in memory the application's runtime reserves (aqua_reserve), so the guest heap and the
// libraries never overlap. This is instantiation and symbol binding only; no library logic runs in JavaScript.

const decoder = new TextDecoder();

function readDylink(module) {
    const sections = WebAssembly.Module.customSections(module, 'dylink.0');
    const info = { memorySize: 0, memoryAlign: 0, tableSize: 0, tableAlign: 0, needed: [] };
    if (!sections.length) return info;
    const bytes = new Uint8Array(sections[0]);
    let p = 0;
    const leb = () => { let r = 0, s = 0, b; do { b = bytes[p++]; r |= (b & 127) << s; s += 7; } while (b & 128); return r >>> 0; };
    const string = () => { const n = leb(); const s = decoder.decode(bytes.subarray(p, p + n)); p += n; return s; };
    while (p < bytes.length) {
        const type = bytes[p++], size = leb(), end = p + size;
        if (type === 1) { info.memorySize = leb(); info.memoryAlign = leb(); info.tableSize = leb(); info.tableAlign = leb(); }
        else if (type === 2) { for (let i = leb(); i > 0; i--) info.needed.push(string()); }
        p = end;
    }
    return info;
}

export class Linker {
    /** @param {{memory: WebAssembly.Memory, reserve: (bytes: number, align: number) => number, modules: Object<string, Object>, fetchModule: (name: string) => Promise<WebAssembly.Module>, stackBytes?: number}} options */
    constructor({ memory, reserve, modules, fetchModule, stackBytes = 2 << 20 }) {
        this.memory = memory;
        this.reserve = reserve;
        this.modules = modules;             // import namespaces outside `env` (guest, aqua_web, wasi_snapshot_preview1)
        this.fetchModule = fetchModule;
        this.table = new WebAssembly.Table({ element: 'anyfunc', initial: 1 });
        this.functions = new Map();         // symbol -> exported function
        this.addresses = new Map();         // symbol -> absolute data address
        this.slots = new Map();             // function -> table index
        this.unresolvedMemory = [];         // [name, WebAssembly.Global]
        this.unresolvedFunctions = [];
        this.loaded = new Map();            // module name -> instance
        const stackBase = reserve(stackBytes, 16);
        this.stackPointer = new WebAssembly.Global({ value: 'i32', mutable: true }, stackBase + stackBytes);
        this.addresses.set('__stack_low', stackBase);
        this.addresses.set('__stack_high', stackBase + stackBytes);
        // libc's allocator starts in a reserved region, then grows the memory itself (runtime/guest steps over it).
        const heapBytes = 1 << 20, heapBase = reserve(heapBytes, 16);
        this.addresses.set('__heap_base', heapBase);
        this.addresses.set('__heap_end', heapBase + heapBytes);
    }

    tableIndex(fn) {
        let index = this.slots.get(fn);
        if (index === undefined) {
            index = this.table.length;
            this.table.grow(1);
            this.table.set(index, fn);
            this.slots.set(fn, index);
        }
        return index;
    }

    /** Loads a module by name (and first everything it lists as needed); returns its instance. */
    async load(name) {
        if (this.loaded.has(name)) return this.loaded.get(name);
        const module = await this.fetchModule(name);
        const info = readDylink(module);
        for (const dependency of info.needed) await this.load(dependency);
        if (this.loaded.has(name)) return this.loaded.get(name);
        const memoryBase = info.memorySize ? this.reserve(info.memorySize, 1 << info.memoryAlign) : 0;
        new Uint8Array(this.memory.buffer, memoryBase, info.memorySize).fill(0);
        const tableBase = this.table.length;
        if (info.tableSize) this.table.grow(info.tableSize);
        const env = {
            memory: this.memory,
            __indirect_function_table: this.table,
            __stack_pointer: this.stackPointer,
            __memory_base: new WebAssembly.Global({ value: 'i32', mutable: false }, memoryBase),
            __table_base: new WebAssembly.Global({ value: 'i32', mutable: false }, tableBase),
        };
        const got = { 'GOT.mem': {}, 'GOT.func': {} };
        const imports = { env, ...got };
        for (const item of WebAssembly.Module.imports(module)) {
            if (item.module === 'env') {
                if (item.name in env) continue;
                // A C API the glue implements in JavaScript (webgpu.h's wgpu*, OpenGL ES's gl*) binds by its own name.
                if (item.kind === 'function') env[item.name] = this.functions.get(item.name) ?? this.modules.aqua_web?.[item.name] ?? this.lazy(item.name);
                else if (item.kind === 'global') env[item.name] = new WebAssembly.Global({ value: 'i32', mutable: false }, this.addresses.get(item.name) ?? 0);
            } else if (item.module === 'GOT.mem') {
                const address = this.addresses.get(item.name);
                const global = new WebAssembly.Global({ value: 'i32', mutable: true }, address ?? 0);
                if (address === undefined) this.unresolvedMemory.push([item.name, global]);
                got['GOT.mem'][item.name] = global;
            } else if (item.module === 'GOT.func') {
                const fn = this.functions.get(item.name);
                const global = new WebAssembly.Global({ value: 'i32', mutable: true }, fn ? this.tableIndex(fn) : 0);
                if (!fn) this.unresolvedFunctions.push([item.name, global]);
                got['GOT.func'][item.name] = global;
            } else {
                const namespace = this.modules[item.module];
                if (!namespace) throw new Error(`${name} imports from unknown module ${item.module}`);
                imports[item.module] ??= {};
                if (!(item.name in imports[item.module]))
                    imports[item.module][item.name] = namespace[item.name] ?? (() => { throw new Error(`${name} needs ${item.module}.${item.name}, which this page does not provide`); });
            }
        }
        const instance = await WebAssembly.instantiate(module, imports);
        for (const [symbol, value] of Object.entries(instance.exports)) {
            if (typeof value === 'function') { if (!this.functions.has(symbol)) this.functions.set(symbol, value); }
            else if (value instanceof WebAssembly.Global && !this.addresses.has(symbol)) this.addresses.set(symbol, memoryBase + value.value);
        }
        this.resolvePending();
        this.loaded.set(name, instance);
        instance.exports.__wasm_apply_data_relocs?.();
        (instance.exports._initialize ?? instance.exports.__wasm_call_ctors)?.();
        return instance;
    }

    resolvePending() {
        this.unresolvedMemory = this.unresolvedMemory.filter(([symbol, global]) => {
            const address = this.addresses.get(symbol);
            if (address === undefined) return true;
            global.value = address;
            return false;
        });
        this.unresolvedFunctions = this.unresolvedFunctions.filter(([symbol, global]) => {
            const fn = this.functions.get(symbol);
            if (!fn) return true;
            global.value = this.tableIndex(fn);
            return false;
        });
    }

    // A call to a symbol defined later (or by the importing module itself) binds on first use.
    lazy(symbol) {
        let target = null;
        return (...args) => {
            target ??= this.functions.get(symbol);
            if (!target) throw new Error(`Undefined symbol ${symbol}`);
            return target(...args);
        };
    }

    exports(name) { return this.loaded.get(name)?.exports; }
    symbol(name) { return this.functions.get(name); }
}
