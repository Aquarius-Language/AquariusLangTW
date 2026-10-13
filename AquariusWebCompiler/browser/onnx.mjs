import {num} from './values.mjs';
import {OnnxFailure,createTensor,validateRun} from './onnx-registration.mjs';
export {OnnxFailure,createTensor,validateRun} from './onnx-registration.mjs';
const invalid=message=>{throw new OnnxFailure('InvalidArgument',message);};
const cancelled=signal=>{if(signal?.aborted)throw new OnnxFailure('Cancelled','推論操作已取消。');};
const MAX_MODEL=256*1024*1024,MAX_ELEMENTS=16*1024*1024;const TYPES={float32:Float32Array,float64:Float64Array,int32:Int32Array,uint8:Uint8Array,bool:Uint8Array};
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

import {registerOnnx as registerPortableOnnx} from './onnx-registration.mjs';
export function registerOnnx(host,backend=new BrowserOnnxBackend()){return registerPortableOnnx(host,backend);}
