import {num} from './values.mjs';

const MAX_ELEMENTS = 16 * 1024 * 1024, MAX_MODEL = 256 * 1024 * 1024;
const TYPES = {float32: Float32Array, float64: Float64Array, int32: Int32Array, uint8: Uint8Array, bool: Uint8Array};
export class OnnxFailure extends Error {
  constructor(kind, message, cause) { super(`推論引擎：${message}`, {cause}); this.kind = kind; }
}
const invalid = message => { throw new OnnxFailure('InvalidArgument', message); };
function cancelled(signal) { if (signal?.aborted) throw new OnnxFailure('Cancelled', '推論操作已取消。'); }
export function createTensor(type, shape, data) {
  if (!Object.hasOwn(TYPES, type) || !Array.isArray(shape) || !Array.isArray(data)) invalid('張量型別、形狀或資料無效。');
  if (shape.length > 16 || shape.some(d => !Number.isInteger(d) || d < 0 || d > MAX_ELEMENTS)) invalid('張量維度必須是非負整數，最多十六個維度。');
  const count = shape.reduce((a, b) => a * b, 1);
  if (!Number.isSafeInteger(count) || count > MAX_ELEMENTS || count !== data.length) invalid('張量資料長度與形狀不符或超過上限。');
  if (data.some(n => type === 'bool' ? typeof n !== 'boolean' : typeof n !== 'number' || !Number.isFinite(n) ||
    (type === 'float32' && !Number.isFinite(Math.fround(n))) ||
    (type === 'int32' && (!Number.isInteger(n) || n < -2147483648 || n > 2147483647)) ||
    (type === 'uint8' && (!Number.isInteger(n) || n < 0 || n > 255)))) invalid('張量數值超出型別範圍。');
  return {type, shape: Object.freeze([...shape]), data: new TYPES[type](data)};
}
export function validateRun(metadata, outputMetadata, inputs, outputs) {
  if (!(inputs instanceof Map) || inputs.size !== metadata.length || metadata.some(m => !inputs.has(m.name))) invalid('必須提供每個模型輸入且不得包含未知名稱。');
  if (!Array.isArray(outputs) || !outputs.length || new Set(outputs).size !== outputs.length || outputs.some(n => !outputMetadata.some(m => m.name === n))) invalid('輸出名稱必須存在、不得重複且至少指定一個。');
  for (const m of metadata) {
    const t = inputs.get(m.name);
    if (!t || t.type !== m.type || t.shape.length !== m.shape.length || m.shape.some((d, i) => d >= 0 && d !== t.shape[i])) invalid(`輸入「${m.name}」的型別或形狀與模型不符。`);
  }
}
let runtime;
async function loadRuntime() {
  runtime ??= import('./ort.webgpu.min.mjs').then(ort => {
    ort.env.wasm.numThreads = 1;
    ort.env.wasm.wasmPaths = new URL('./', import.meta.url).href;
    return ort;
  }).catch(error => { runtime = undefined; throw error; });
  return runtime;
}
// Host adapter only; ONNX kernels execute in upstream Wasm/WebGPU.
export class BrowserOnnxBackend {
  constructor({load = loadRuntime, gpu = globalThis.navigator?.gpu} = {}) { this.load = load; this.gpu = gpu; this.sessions = new Set(); this.pending = 0; this.disposed = false; }
  isSupported(provider) { return !this.disposed && (provider === 'CPU' || provider === 'WebGPU' && !!this.gpu); }
  async createSession(model, provider, signal) {
    if (this.disposed) throw new OnnxFailure('Closed', '推論執行環境已關閉。');
    if (!['CPU', 'WebGPU'].includes(provider)) invalid('後端必須是 WebGPU 或 CPU。');
    if (!(model instanceof Uint8Array) || !model.length || model.length > MAX_MODEL) invalid('模型必須是非空的 ONNX 資料，最多二百五十六 MiB。');
    if (this.sessions.size + this.pending >= 8) invalid('最多只能同時建立八個推論工作階段。');
    if (!this.isSupported(provider)) throw new OnnxFailure('Unsupported', '此環境不支援 WebGPU。');
    cancelled(signal); this.pending++; let native;
    try {
      const ort = await this.load(); cancelled(signal);
      if (this.disposed) throw new OnnxFailure('Closed', '推論執行環境已關閉。');
      native = await ort.InferenceSession.create(model.slice(), {executionProviders: [provider === 'CPU' ? 'wasm' : 'webgpu']});
      cancelled(signal);
      if (this.disposed) throw new OnnxFailure('Closed', '推論執行環境已關閉。');
      const session = new BrowserOnnxSession(this, ort, native, provider); this.sessions.add(session); return session;
    } catch (error) {
      await native?.release(); cancelled(signal);
      if (error instanceof OnnxFailure) throw error;
      throw new OnnxFailure('Engine', 'ONNX 模型或推論後端初始化失敗。', error);
    } finally { this.pending--; }
  }
  async dispose() { if (this.disposed) return; this.disposed = true; await Promise.all([...this.sessions].map(s => s.dispose())); }
}
class BrowserOnnxSession {
  constructor(owner, ort, native, provider) {
    this.owner = owner; this.ort = ort; this.native = native; this.provider = provider; this.disposed = false; this.running = null;
    const metadata = values => Object.freeze(values.map(m => Object.freeze({name: m.name, type: m.isTensor && Object.hasOwn(TYPES, m.type) ? m.type : 'unsupported',
      shape: Object.freeze(m.isTensor ? m.shape.map(d => typeof d === 'number' ? d : -1) : [])})));
    this.inputs = metadata(native.inputMetadata); this.outputs = metadata(native.outputMetadata);
  }
  live() { if (this.disposed) throw new OnnxFailure('Closed', '推論工作階段已關閉。'); }
  async run(inputs, outputs, signal) {
    this.live(); cancelled(signal); validateRun(this.inputs, this.outputs, inputs, outputs);
    if (this.running) throw new OnnxFailure('InvalidArgument', '同一工作階段不可同時推論。');
    const operation = async () => {
      const feeds = Object.create(null), tensors = []; let results;
      try {
      for (const [name, t] of inputs) { const tensor = new this.ort.Tensor(t.type, t.data.slice(), t.shape); feeds[name] = tensor; tensors.push(tensor); }
      results = await this.native.run(feeds, outputs); cancelled(signal); this.live();
      const copied = new Map();
      for (const name of outputs) {
        const t = results[name];
        if (!t || !Object.hasOwn(TYPES, t.type)) throw new OnnxFailure('Unsupported', '此版本不支援模型輸出的張量型別。');
        const data = await t.getData();
        const shape = [...t.dims], count = shape.reduce((a, b) => a * b, 1);
        if (shape.length > 16 || shape.some(d => !Number.isInteger(d) || d < 0 || d > MAX_ELEMENTS) || count > MAX_ELEMENTS || count !== data.length) throw new OnnxFailure('Engine', '模型輸出尺寸超過上限或無效。');
        copied.set(name, {type: t.type, shape: Object.freeze(shape), data: new TYPES[t.type](data)});
      }
      cancelled(signal); return copied;
    } catch (error) { cancelled(signal); if (error instanceof OnnxFailure) throw error; throw new OnnxFailure('Engine', 'ONNX 推論失敗；請確認模型運算子與輸入資料。', error); }
      finally { for (const t of Object.values(results ?? {})) t.dispose(); for (const t of tensors) t.dispose(); }
    };
    this.running = Promise.resolve().then(operation);
    try { return await this.running; } finally { this.running = null; }
  }
  async dispose() {
    if (this.disposed) return this.releasing;
    this.disposed = true; this.owner.sessions.delete(this);
    this.releasing = (async () => { await this.running?.catch(() => {}); await this.native.release(); })();
    return this.releasing;
  }
}
export function registerOnnx(host, backend = new BrowserOnnxBackend()) {
  host.onnx = backend; const m = host.library('ONNX'); host.modules.set('ONNXRuntime', m); host.modules.set('推論引擎', m);
  const field = (module, english, chinese, value) => { host.set(module, english, value); host.set(module, chinese, value); };
  field(m, 'WebGPU', '網頁圖形加速', 'WebGPU'); field(m, 'CPU', '中央處理器', 'CPU');
  for (const [english, chinese, type] of [['Float32', '浮點32', 'float32'], ['Float64', '浮點64', 'float64'], ['Int32', '整數32', 'int32'], ['UInt8', '無號整數8', 'uint8'], ['Bool', '布林', 'bool']]) field(m, english, chinese, type);
  const tensors = new WeakMap();
  const tensorModule = (tensor, name = '') => {
    const t = host.newModule(); tensors.set(t.scope, tensor); field(t, 'Name', '名稱', name);
    host.bind(t, 'ONNX.Tensor', 'GetType', () => tensor.type);
    host.bind(t, 'ONNX.Tensor', 'GetShape', () => tensor.shape.map(n => num(n, 'int')));
    host.bind(t, 'ONNX.Tensor', 'GetData', () => Array.from(tensor.data, n => tensor.type === 'bool' ? !!n : num(n, tensor.type === 'uint8' || tensor.type === 'int32' ? 'int' : 'double')));
    return t;
  };
  host.bind(m, 'ONNX', 'IsSupported', provider => { if (!['CPU', 'WebGPU'].includes(provider)) invalid('後端必須是 WebGPU 或 CPU。'); return backend.isSupported(provider); });
  host.bind(m, 'ONNX', 'CreateTensor', (type, shape, data) => tensorModule(createTensor(type, shape, data)));
  host.bind(m, 'ONNX', 'CreateSession', async (path, provider) => {
    if (typeof path !== 'string') invalid('模型路徑必須是字串。');
    const session = await backend.createSession(host.asset(path), provider, host.signal), s = host.newModule();
    field(s, 'Backend', '後端', session.provider);
    const metadata = list => list.map(v => { const record = host.newModule(); field(record, 'Name', '名稱', v.name); field(record, 'Type', '型別', v.type); field(record, 'Shape', '形狀', v.shape.map(n => num(n, 'int'))); return record; });
    host.bind(s, 'ONNX.Session', 'GetInputs', () => { session.live(); return metadata(session.inputs); });
    host.bind(s, 'ONNX.Session', 'GetOutputs', () => { session.live(); return metadata(session.outputs); });
    host.bind(s, 'ONNX.Session', 'Dispose', () => session.dispose());
    host.bind(s, 'ONNX.Session', 'Run', async (names, values, outputs) => {
      if (!Array.isArray(names) || names.some(n => typeof n !== 'string') || !Array.isArray(values) || names.length !== values.length || new Set(names).size !== names.length) invalid('輸入名稱與張量數量必須相同，且名稱不得重複。');
      const feeds = new Map(names.map((name, i) => { const t = tensors.get(values[i]?.scope); if (!t) invalid('必須使用同一執行環境建立的張量。'); return [name, t]; }));
      const result = await session.run(feeds, outputs, host.signal); return outputs.map(name => tensorModule(result.get(name), name));
    }); return s;
  });
}
