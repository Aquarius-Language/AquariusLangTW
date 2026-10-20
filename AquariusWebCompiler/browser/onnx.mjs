import {OnnxFailure, createTensor, validateRun, adoptTensor, device, TYPES, registerOnnx as registerPortableOnnx} from './onnx-registration.mjs';
export {OnnxFailure, createTensor, validateRun, openSession, isSupported, parseSelection, planSessions} from './onnx-registration.mjs';
const invalid = message => { throw new OnnxFailure('InvalidArgument', message); };
const cancelled = signal => { if (signal?.aborted) throw new OnnxFailure('Cancelled', '推論操作已取消。'); };
const MAX_MODEL = 256 * 1024 * 1024;
let runtime;
async function loadRuntime() {
  runtime ??= import('./ort.webgpu.min.mjs').then(ort => {
    // 0 = ORT's automatic thread count: multi-threaded SIMD kernels when the page is cross-origin isolated
    // (SharedArrayBuffer available), a single thread otherwise — no isolation requirement for deployments.
    ort.env.wasm.numThreads = globalThis.crossOriginIsolated ? 0 : 1;
    ort.env.wasm.wasmPaths = new URL('./', import.meta.url).href;
    ort.env.webgpu.powerPreference = 'high-performance';
    return ort;
  }).catch(error => { runtime = undefined; throw error; });
  return runtime;
}
const adapterName = (info = {}) => [info.vendor, info.architecture, info.description || info.device].filter(Boolean).join(' ') || 'WebGPU 介面卡';
const threads = () => globalThis.crossOriginIsolated ? Math.min(4, Math.ceil((globalThis.navigator?.hardwareConcurrency || 1) / 2)) : 1;

/**
 * Browser host adapter. Ranking: WebGPU (any GPU through the browser) → WebNN GPU → WebNN NPU → Wasm SIMD CPU.
 * Kernels execute in upstream ORT Wasm/WebGPU/WebNN; selection policy is the shared core in onnx-providers.mjs.
 */
export class BrowserOnnxBackend {
  constructor({load = loadRuntime, gpu = globalThis.navigator?.gpu, ml = globalThis.navigator?.ml} = {}) {
    this.load = load; this.gpu = gpu; this.ml = ml; this.sessions = new Set(); this.pending = 0; this.disposed = false;
  }
  devices() { return this.probed ??= this.probe().catch(error => { this.probed = undefined; throw error; }); }
  async probe() {
    const list = [];
    let adapter = null, reason = null;
    if (!this.gpu?.requestAdapter) reason = '此瀏覽器不支援 WebGPU（需要相容的瀏覽器、驅動程式及 HTTPS 或 localhost）。';
    else {
      try { adapter = await this.gpu.requestAdapter({powerPreference: 'high-performance'}); if (!adapter) reason = '瀏覽器沒有提供 WebGPU 介面卡。'; }
      catch (error) { reason = String(error?.message ?? error); }
    }
    const software = !!(adapter?.info?.isFallbackAdapter ?? adapter?.isFallbackAdapter);
    list.push(device('WebGPU', 'WebGPU', 'GPU', adapterName(adapter?.info), !!adapter,
      {automatic: !software, reason: software ? '軟體模擬的 WebGPU 介面卡，僅在明確指定時使用。' : reason}));
    for (const kind of ['GPU', 'NPU']) {
      let ok = false, why = '此瀏覽器不支援 WebNN。';
      if (this.ml?.createContext) {
        try { const context = await this.ml.createContext({deviceType: kind.toLowerCase(), powerPreference: 'high-performance'}); ok = !!context; context?.destroy?.(); }
        catch (error) { why = String(error?.message ?? error); }
      }
      list.push(device(`WebNN/${kind}`, 'WebNN', kind, `WebNN ${kind}`, ok, {reason: ok ? null : why}));
    }
    list.push(device('CPU', 'CPU', 'CPU', `WebAssembly SIMD（${threads()} 執行緒）`, true));
    return Object.freeze(list);
  }
  /** ORT web execution provider entry for one device, with tuned options. */
  static executionProvider(d) {
    switch (d.provider) {
      case 'WebGPU': return {name: 'webgpu'};
      case 'WebNN': return {name: 'webnn', deviceType: d.kind.toLowerCase(), powerPreference: 'high-performance'};
      case 'CPU': return 'wasm';
      default: throw new OnnxFailure('Unsupported', `瀏覽器不支援 ${d.provider} 推論後端。`);
    }
  }
  async createSession(model, plan, signal) {
    if (this.disposed) throw new OnnxFailure('Closed', '推論執行環境已關閉。');
    if (!Array.isArray(plan) || !plan.length) invalid('推論計畫至少需要一個後端。');
    if (!(model instanceof Uint8Array) || !model.length || model.length > MAX_MODEL) invalid('模型必須是非空的 ONNX 資料，最多二百五十六 MiB。');
    if (this.sessions.size + this.pending >= 8) invalid('最多只能同時建立八個推論工作階段。');
    const executionProviders = plan.map(BrowserOnnxBackend.executionProvider);
    if (!executionProviders.includes('wasm')) executionProviders.push('wasm');
    cancelled(signal); this.pending++; let native;
    try {
      const ort = await this.load(); cancelled(signal);
      if (this.disposed) throw new OnnxFailure('Closed', '推論執行環境已關閉。');
      native = await ort.InferenceSession.create(model.slice(), {executionProviders, graphOptimizationLevel: 'all'});
      cancelled(signal);
      if (this.disposed) throw new OnnxFailure('Closed', '推論執行環境已關閉。');
      const session = new BrowserOnnxSession(this, ort, native, plan); this.sessions.add(session); return session;
    } catch (error) {
      await native?.release(); cancelled(signal);
      if (error instanceof OnnxFailure) throw error;
      throw new OnnxFailure('Engine', `ONNX 模型或推論後端 ${plan[0].provider} 初始化失敗：${error?.message ?? error}`, error);
    } finally { this.pending--; }
  }
  async dispose() { if (this.disposed) return; this.disposed = true; await Promise.all([...this.sessions].map(s => s.dispose())); }
}
class BrowserOnnxSession {
  constructor(owner, ort, native, plan) {
    this.owner = owner; this.ort = ort; this.native = native; this.disposed = false; this.running = null;
    this.plan = plan; this.provider = plan[0].provider; this.device = plan[0].kind;
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
        // Inputs are immutable snapshots (or a pose Wasm view stable for this call); ORT copies them into its heap.
        for (const [name, t] of inputs) { const tensor = new this.ort.Tensor(t.type, t.data, t.shape); feeds[name] = tensor; tensors.push(tensor); }
        results = await this.native.run(feeds, outputs); cancelled(signal); this.live();
        const copied = new Map();
        for (const name of outputs) {
          const t = results[name];
          if (!t || !Object.hasOwn(TYPES, t.type)) throw new OnnxFailure('Unsupported', '此版本不支援模型輸出的張量型別。');
          // getData() downloads GPU outputs and returns a JS-owned array for CPU outputs; adopt it without another copy.
          copied.set(name, adoptTensor(t.type, t.dims, await t.getData()));
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
export function registerOnnx(host, backend = new BrowserOnnxBackend()) { return registerPortableOnnx(host, backend); }
