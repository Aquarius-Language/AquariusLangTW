// Shared host libraries use this WebAssembly surface; every guest runs in Wasmtime.
(() => {
  const api = globalThis.__wasmtime;
  delete globalThis.__wasmtime;
  const handles = new WeakMap(), binaries = new WeakMap(), objects = new Map();
  const bytes = value => ArrayBuffer.isView(value) ? new Uint8Array(value.buffer, value.byteOffset, value.byteLength) : new Uint8Array(value);
  const handle = value => handles.get(value) ?? value;
  class CompileError extends Error {}
  class LinkError extends Error {}
  class RuntimeError extends Error {}
  function wrap({id, kind}) {
    if (objects.has(id)) return objects.get(id);
    const value = kind === 'function' ? (...args) => api.call(id, args) : Object.create(({memory: Memory, table: Table, global: Global})[kind].prototype);
    handles.set(value, id); objects.set(id, value); return value;
  }
  class Module {
    constructor(input) {
      const data = bytes(input).slice();
      try { handles.set(this, api.compile(data)); } catch (e) { throw new CompileError(e.message); }
      binaries.set(this, data);
    }
    static imports(module) { return api.describe(handles.get(module), true); }
    static exports(module) { return api.describe(handles.get(module), false); }
    static customSections(module, name) {
      const data = binaries.get(module), result = []; let offset = 8;
      const leb = () => { let value = 0, shift = 0, part; do { part = data[offset++]; value += (part & 127) * 2 ** shift; shift += 7; } while (part & 128); return value; };
      while (offset < data.length) {
        const kind = data[offset++], size = leb(), end = offset + size;
        if (kind === 0) { const length = leb(), section = new TextDecoder().decode(data.subarray(offset, offset + length)); offset += length; if (section === name) result.push(data.slice(offset, end).buffer); }
        offset = end;
      }
      return result;
    }
  }
  class Instance {
    constructor(module, imports = {}) {
      const values = Module.imports(module).map(({module, name}) => {
        const value = imports[module]?.[name];
        if (value === undefined) throw new LinkError(`Missing import: ${module}.${name}`);
        return handle(value);
      });
      const exports = Object.create(null);
      for (const record of api.instantiate(handles.get(module), values)) exports[record.name] = wrap(record);
      this.exports = Object.freeze(exports);
    }
  }
  class Memory {
    constructor(descriptor) { const id = api.memory(descriptor.initial, descriptor.maximum); handles.set(this, id); objects.set(id, this); }
    get buffer() { return api.buffer(handles.get(this)); }
    grow(pages) { return api.growMemory(handles.get(this), pages); }
  }
  class Table {
    constructor(descriptor, value = null) { const id = api.table(descriptor.initial, descriptor.maximum, handle(value)); handles.set(this, id); objects.set(id, this); }
    get length() { return api.tableSize(handles.get(this)); }
    get(index) { const value = api.tableGet(handles.get(this), index); return value === null ? null : wrap(value); }
    set(index, value) { api.tableSet(handles.get(this), index, handle(value)); }
    grow(count, value = null) { return api.tableGrow(handles.get(this), count, handle(value)); }
  }
  class Global {
    constructor(descriptor, value = 0) { const id = api.global(descriptor.value, !!descriptor.mutable, value); handles.set(this, id); objects.set(id, this); }
    get value() { return api.globalGet(handles.get(this)); }
    set value(value) { api.globalSet(handles.get(this), value); }
    valueOf() { return this.value; }
  }
  globalThis.WebAssembly = {Module, Instance, Memory, Table, Global, CompileError, LinkError, RuntimeError,
    validate(input) { try { new Module(input); return true; } catch { return false; } },
    async compile(input) { return new Module(input); },
    async instantiate(input, imports) { if (input instanceof Module) return new Instance(input, imports); const module = new Module(input); return {module, instance: new Instance(module, imports)}; }
  };
})();
