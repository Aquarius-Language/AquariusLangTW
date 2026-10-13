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
export function registerOnnx(host, backend) {
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
    const session = await backend.createSession(host.modelAsset?.(path) ?? host.asset(path), provider, host.signal), s = host.newModule();
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
