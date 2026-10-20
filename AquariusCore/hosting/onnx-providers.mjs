// ONNX contract version 2: provider catalog and selection policy shared by every JavaScript-capable host
// (browser, Android). Mirrors AquariusCore/onnx/OnnxProviders.cs and OnnxExecution.cs exactly, so a script
// selects providers identically on every platform. Hosts only implement:
//   devices(): Promise<Device[]>   ranked best-first, unavailable entries kept with a reason
//   createSession(model, plan, signal): Promise<Session>   strict: never substitutes another provider
// where Device = {id, provider, kind:'CPU'|'GPU'|'NPU', name, available, automatic, reason}.

export const CONTRACT_VERSION = 2;
export const KINDS = Object.freeze(['CPU', 'GPU', 'NPU']);
export const PROVIDERS = Object.freeze([
  ['CPU', 'CPU', '中央處理器', ['CPU']],
  ['CUDA', 'CUDA', '輝達CUDA', ['GPU']],
  ['TensorRT', 'TensorRT', '輝達TensorRT', ['GPU']],
  ['CoreML', 'CoreML', '蘋果CoreML', ['GPU', 'NPU']],
  ['WebGPU', 'WebGPU', '網頁圖形加速', ['GPU']],
  ['WebNN', 'WebNN', '網頁神經網路', ['GPU', 'NPU']],
  ['NNAPI', 'NNAPI', '安卓神經網路', ['NPU']],
  ['XNNPACK', 'XNNPACK', '行動處理器加速', ['CPU']],
  ['OpenVINO', 'OpenVINO', '英特爾OpenVINO', ['GPU', 'NPU', 'CPU']],
  ['QNN', 'QNN', '高通QNN', ['GPU', 'NPU']]
].map(([id, english, chinese, kinds]) => Object.freeze({id, english, chinese, kinds: Object.freeze(kinds)})));
export const PREFERENCES = Object.freeze([['Auto', '自動'], ['GPU', '圖形處理器'], ['NPU', '神經處理器']]
  .map(([english, chinese]) => Object.freeze({id: english, english, chinese})));

export class OnnxFailure extends Error {
  constructor(kind, message, cause) { super(`推論引擎：${message}`, {cause}); this.kind = kind; }
}
const fail = (kind, message) => { throw new OnnxFailure(kind, message); };

export function parseProvider(text) {
  if (typeof text !== 'string') fail('InvalidArgument', '推論後端必須是字串或字串陣列。');
  const match = PROVIDERS.find(p => p.id.toLowerCase() === text.toLowerCase() || p.chinese === text);
  return match?.id ?? fail('InvalidArgument', `未知的推論後端「${text}」。可用：自動、圖形處理器、神經處理器或 ${PROVIDERS.map(p => p.id).join('、')}。`);
}
/** Selection = {preference:'Auto'|'GPU'|'NPU'} or {providers:[...]} (strict, ordered like ORT executionProviders). */
export function parseSelection(value = 'Auto') {
  if (value && typeof value === 'object' && !Array.isArray(value) && Array.isArray(value.providers)) return value;
  if (typeof value === 'string') {
    const preference = PREFERENCES.find(p => p.english.toLowerCase() === value.toLowerCase() || p.chinese === value);
    if (preference) return Object.freeze({preference: preference.id, providers: Object.freeze([])});
    value = [value];
  }
  if (!Array.isArray(value) || !value.length) fail('InvalidArgument', '推論後端清單不得為空。');
  const providers = value.map(parseProvider);
  if (new Set(providers).size !== providers.length) fail('InvalidArgument', '推論後端清單不得重複。');
  return Object.freeze({preference: null, providers: Object.freeze(providers)});
}

/** The engine-visible chain: requested providers, companions, then the implicit CPU fallback. */
export function withCpuFallback(plan, companions = []) {
  return Object.freeze([...new Set([...plan.map(d => d.provider), ...companions, 'CPU'])]);
}

/** Ranked candidate plans (arrays of devices) for a selection. Same rules as OnnxPlanner.Plan in C#. */
export function planSessions(devices, selection) {
  if (!selection.preference) {
    const chosen = selection.providers.map(provider => {
      const matches = devices.filter(d => d.provider === provider);
      if (!matches.length) fail('Unsupported', `此平台未提供 ${provider} 推論後端。`);
      return matches.find(d => d.available) ?? fail('Unsupported', `推論後端 ${provider} 目前無法使用：${matches[0].reason || '未提供原因'}`);
    });
    return [Object.freeze(chosen)];
  }
  const seen = new Set(), candidates = [];
  for (const d of devices) {
    if (!d.available || !d.automatic || seen.has(d.provider)) continue;
    if (selection.preference !== 'Auto' && d.kind !== selection.preference) continue;
    seen.add(d.provider); candidates.push(d);
  }
  if (selection.preference === 'Auto') {
    const index = candidates.findIndex(d => d.provider === 'CPU');
    const cpu = index >= 0 ? candidates.splice(index, 1)[0] : devices.find(d => d.provider === 'CPU' && d.available);
    if (cpu) candidates.push(cpu);
  }
  if (!candidates.length) fail('Unsupported', selection.preference === 'GPU' ? '找不到可用的圖形處理器（GPU）推論後端。'
    : selection.preference === 'NPU' ? '找不到可用的神經處理器（NPU）推論後端。' : '此平台沒有可用的推論後端。');
  return candidates.map(d => Object.freeze([d]));
}

const retryable = error => !(error instanceof OnnxFailure) || error.kind === 'Engine' || error.kind === 'Unsupported';
/**
 * Core selection policy (OnnxSessionFactory in C#). Explicit lists are strict; preferences try ranked candidates and
 * move on only when a provider fails to initialize. Returned sessions carry provider/device/providers/plan.
 */
export async function openSession(backend, model, value, signal, observe) {
  const selection = parseSelection(value);
  const plans = planSessions(await backend.devices(), selection), failures = [];
  for (const plan of plans) {
    if (signal?.aborted) throw new OnnxFailure('Cancelled', '推論操作已取消。');
    try {
      const session = await backend.createSession(model, plan, signal);
      session.plan = plan; session.provider = plan[0].provider; session.device = plan[0].kind;
      session.providers ??= withCpuFallback(plan);
      observe?.({plan, failure: null}); return session;
    } catch (error) {
      if (!selection.preference || !retryable(error) || signal?.aborted) throw error;
      observe?.({plan, failure: error}); failures.push([plan, error]);
    }
  }
  if (failures.length === 1) throw failures[0][1];
  throw new OnnxFailure('Engine', '所有候選推論後端都無法初始化：' + failures.map(([p, e]) => `${p[0].provider}（${e.message}）`).join('；'), failures.at(-1)[1]);
}

export async function isSupported(backend, value) {
  try { return planSessions(await backend.devices(), parseSelection(value)).length > 0; }
  catch (error) { if (error instanceof OnnxFailure && error.kind === 'Unsupported') return false; throw error; }
}

/** Builds a device record; hosts use it so every platform reports the same shape. */
export function device(id, provider, kind, name, available, {automatic = true, reason = null} = {}) {
  return Object.freeze({id, provider, kind, name, available: !!available, automatic: !!available && automatic, reason: available ? reason : (reason || '未提供原因')});
}
