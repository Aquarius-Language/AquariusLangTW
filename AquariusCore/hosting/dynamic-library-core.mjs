import {num} from './values.mjs';

export const ABI_VERSION = 1, MAX_BYTES = 1024 * 1024, MAX_LIBRARIES = 32;
const fail = message => { throw new Error(`DynamicLibrary / 動態函式庫：${message}`); };
export function validateSymbol(name) {
  if (typeof name !== 'string' || !/^[A-Za-z_][A-Za-z0-9_]{0,127}$/.test(name) || name.startsWith('aqua_')) fail('Invalid or reserved symbol / 無效或保留的符號名稱');
  return name;
}
function bounded(bytes) {
  if (!(bytes instanceof Uint8Array) || bytes.length > MAX_BYTES) fail('Expected bytes, at most 1 MiB / 位元組最多一 MiB');
  return bytes;
}
export function createJavaScriptLibrary(namespace) {
  if (namespace.aqua_library_abi?.() !== ABI_VERSION || typeof namespace.aqua_create_library !== 'function') fail('Unsupported ABI / 不支援的介面版本');
  let api = namespace.aqua_create_library();
  if (!api || typeof api !== 'object') fail('Invalid library factory / 無效的函式庫工廠');
  let closed = false; const calls = new Set();
  const live = () => { if (closed) fail('Library is closed / 函式庫已關閉'); };
  return {
    hasSymbol(name) { live(); validateSymbol(name); return Object.hasOwn(api, name) && typeof api[name] === 'function'; },
    async invoke(name, input) {
      live(); validateSymbol(name); bounded(input); if (!this.hasSymbol(name)) fail('Missing symbol / 找不到符號');
      const target = api, copy = input.slice(), call = Promise.resolve().then(() => target[name](copy)); calls.add(call);
      try { return bounded(await call).slice(); } finally { calls.delete(call); }
    },
    async dispose() { if (closed) return; closed = true; await Promise.allSettled([...calls]); const previous = api; api = null; await previous.aqua_dispose?.(); }
  };
}
export async function createWasmLibrary(bytes) {
  const compiled = await WebAssembly.compile(bytes);
  if (WebAssembly.Module.imports(compiled).length) fail('Wasm libraries must be import-free / Wasm 函式庫不得匯入主機函式');
  let instance = await WebAssembly.instantiate(compiled, {}), closed = false;
  let e = instance.exports;
  if (typeof e.aqua_library_abi !== 'function' || e.aqua_library_abi() !== ABI_VERSION || !(e.memory instanceof WebAssembly.Memory) || typeof e.aqua_alloc !== 'function' || typeof e.aqua_free !== 'function') fail('Unsupported Wasm ABI / 不支援的 Wasm 介面版本');
  const live = () => { if (closed) fail('Library is closed / 函式庫已關閉'); };
  const range = (pointer, size) => {
    if (!Number.isInteger(pointer) || pointer <= 0 || pointer + size > e.memory.buffer.byteLength) fail('Invalid Wasm allocation / 無效的 Wasm 記憶體配置');
    return new Uint8Array(e.memory.buffer, pointer, size);
  };
  return {
    hasSymbol(name) { live(); validateSymbol(name); return typeof e[name] === 'function'; },
    invoke(name, input) {
      live(); validateSymbol(name); bounded(input); if (!this.hasSymbol(name)) fail('Missing symbol / 找不到符號');
      let source = 0, output = 0;
      try {
        source = e.aqua_alloc(Math.max(1, input.length)); output = e.aqua_alloc(MAX_BYTES);
        range(output, MAX_BYTES); range(source, input.length).set(input);
        if (source < output + MAX_BYTES && output < source + Math.max(1, input.length)) fail('Overlapping buffers / 緩衝區重疊');
        const count = e[name](source, input.length, output, MAX_BYTES);
        if (!Number.isInteger(count) || count < 0 || count > MAX_BYTES) fail(`Library call failed (${count}) / 函式庫呼叫失敗`);
        return range(output, count).slice();
      } finally { try { if (output > 0) e.aqua_free(output); } finally { if (source > 0) e.aqua_free(source); } }
    },
    dispose() { if (closed) return; closed = true; instance = null; e = null; }
  };
}

export function registerDynamicLibrary(host, backend) {
  const m = host.library('DynamicLibrary'), handles = new Set(); let closed = false, pending = 0;
  host.modules.set('動態函式庫', m);
  const field = (target, english, chinese, value) => { host.set(target, english, value); host.set(target, chinese, value); };
  const live = () => { if (closed) fail('Runtime is closed / 執行環境已關閉'); host.signal?.throwIfAborted(); };
  field(m, 'Platform', '平台', backend.platform); field(m, 'AbiVersion', '介面版本', num(ABI_VERSION, 'int'));
  host.bind(m, 'DynamicLibrary', 'IsSupported', kind => { live(); if (typeof kind !== 'string') fail('Expected format string / 預期格式字串'); return backend.isSupported(kind); });
  host.bind(m, 'DynamicLibrary', 'Load', async path => {
    live(); if (handles.size + pending >= MAX_LIBRARIES) fail('Too many libraries / 函式庫數量超過上限');
    if (path instanceof Map) {
      const paths = new Map([...path.values()]);
      if ([...paths].some(([k,v]) => typeof k !== 'string' || typeof v !== 'string')) fail('Expected platform paths / 預期平台路徑');
      path = paths.get(backend.platform) ?? paths.get('wasm');
    }
    if (typeof path !== 'string' || !path.trim() || path.includes('\0')) fail('No valid library path for this platform / 缺少此平台的有效函式庫路徑');
    pending++;
    let library;
    try { library = await backend.load(path); try { live(); } catch (error) { await library.dispose(); throw error; } }
    finally { pending--; }
    handles.add(library); const h = host.newModule(); field(h, 'Path', '路徑', path);
    const symbol = name => { live(); if (!handles.has(library)) fail('Library is closed / 函式庫已關閉'); return validateSymbol(name); };
    host.bind(h, 'DynamicLibrary.Handle', 'HasSymbol', name => library.hasSymbol(symbol(name)));
    host.bind(h, 'DynamicLibrary.Handle', 'Invoke', async (name, values) => {
      name = symbol(name);
      if (!Array.isArray(values) || values.length > MAX_BYTES || values.some(n => !Number.isInteger(n) || n < 0 || n > 255)) fail('Expected byte array / 預期位元組陣列');
      return Array.from(bounded(await library.invoke(name, Uint8Array.from(values))), n => num(n, 'int'));
    });
    host.bind(h, 'DynamicLibrary.Handle', 'Call', async (name, text) => {
      name = symbol(name); if (typeof text !== 'string' || text.length > MAX_BYTES) fail('Expected UTF-8 text / 預期 UTF-8 文字');
      // Reject unpaired surrogates consistently with the desktop strict UTF-8 encoder.
      if (/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(text)) fail('Invalid UTF-8 text / 無效的 UTF-8 文字');
      return new TextDecoder('utf-8', {fatal:true, ignoreBOM:true}).decode(bounded(await library.invoke(name, bounded(new TextEncoder().encode(text)))));
    });
    host.bind(h, 'DynamicLibrary.Handle', 'Dispose', async () => { live(); if (handles.delete(library)) await library.dispose(); });
    return h;
  });
  host.dynamicLibraries = {async dispose() { if (closed) return; closed = true; const all = [...handles].reverse(); handles.clear(); const results = await Promise.allSettled(all.map(h => h.dispose())); const error = results.find(r => r.status === 'rejected'); if (error) throw error.reason; }};
}
