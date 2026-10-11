import {num} from './values.mjs';

const descriptions = {
  Unsupported: ['不支援', '此環境不支援攝影機；請使用 HTTPS 或本機網址，並確認瀏覽器媒體功能已啟用。'],
  PermissionDenied: ['權限遭拒', '攝影機權限遭拒；請在瀏覽器或系統設定中允許使用攝影機。'],
  NotFound: ['找不到裝置', '找不到指定的攝影機；請重新列出裝置。'],
  Unavailable: ['裝置無法使用', '攝影機無法使用；請確認裝置連線、影格率或是否被其他程式占用。'],
  Timeout: ['擷取逾時', '攝影機操作逾時，工作階段已關閉。'],
  Cancelled: ['已取消', '攝影機操作已取消。'],
  Closed: ['已關閉', '攝影機工作階段已關閉。'],
  InvalidArgument: ['參數無效', '請提供有效的裝置識別碼、尺寸及影格率。'],
};
export class WebcamFailure extends Error {
  constructor(kind, detail) {
    const [name, message] = descriptions[kind];
    super(`攝影機：${name}：${detail ?? message}`); this.kind = kind;
  }
}
function failure(error) {
  if (error instanceof WebcamFailure) return error;
  return new WebcamFailure(({NotAllowedError: 'PermissionDenied', SecurityError: 'PermissionDenied', NotFoundError: 'NotFound',
    OverconstrainedError: 'Unavailable', NotReadableError: 'Unavailable', AbortError: 'Cancelled'})[error?.name] ?? 'Unavailable');
}
export function validateOptions({width, height, fps, timeout = 10000}) {
  if (!Number.isInteger(width) || width < 1 || width > 1920 || !Number.isInteger(height) || height < 1 || height > 1080)
    throw new WebcamFailure('InvalidArgument', '寬度必須介於 1 至 1920，高度必須介於 1 至 1080，且必須是整數。');
  if (!Number.isInteger(fps) || fps < 1 || fps > 60)
    throw new WebcamFailure('InvalidArgument', '影格率必須是介於 1 至 60 的整數。');
  if (!Number.isInteger(timeout) || timeout < 1 || timeout > 60000) throw new WebcamFailure('InvalidArgument', '逾時時間必須介於 1 至 60000 毫秒。');
  return {width, height, fps, timeout};
}
const stop = stream => { for (const track of stream?.getTracks() ?? []) track.stop(); };

// 權限請求無法由瀏覽器取消。逾時或取消後仍觀察原請求，並立即停止稍後取得的軌道。
function bounded(promise, signal, timeout, late = () => {}) {
  return new Promise((resolve, reject) => {
    let done = false;
    const cleanup = () => { clearTimeout(timer); signal?.removeEventListener('abort', abort); };
    const finish = (fn, value) => { if (done) return; done = true; cleanup(); fn(value); };
    const abort = () => finish(reject, new WebcamFailure('Cancelled'));
    const timer = setTimeout(() => finish(reject, new WebcamFailure('Timeout')), timeout);
    signal?.addEventListener('abort', abort, {once: true});
    if (signal?.aborted) abort();
    Promise.resolve(promise).then(value => { if (done) late(value); else finish(resolve, value); }, error => finish(reject, failure(error)));
  });
}

/// 攝影機宿主配接器；只在開啟時請求權限，不取得麥克風或錄製媒體。
export class BrowserWebcamBackend {
  constructor({mediaDevices = globalThis.navigator?.mediaDevices, document = globalThis.document,
    secure = globalThis.isSecureContext, now = () => performance.now()} = {}) {
    this.mediaDevices = mediaDevices; this.document = document; this.secure = secure; this.now = now;
    this.sessions = new Set(); this.disposed = false;
    this.closed = new AbortController();
  }
  get isSupported() { return !!(this.secure && this.mediaDevices?.getUserMedia && this.mediaDevices?.enumerateDevices && this.document); }
  live() { if (this.disposed) throw new WebcamFailure('Closed'); if (!this.isSupported) throw new WebcamFailure('Unsupported'); }
  async enumerate(signal) {
    this.live(); if (signal?.aborted) throw new WebcamFailure('Cancelled');
    signal = signal ? AbortSignal.any([signal, this.closed.signal]) : this.closed.signal;
    const devices = await bounded(this.mediaDevices.enumerateDevices(), signal, 10000);
    if (this.disposed) throw new WebcamFailure('Closed');
    return devices.filter(d => d.kind === 'videoinput').map((d, i) => ({id: d.deviceId, name: d.label || `攝影機 ${i + 1}`}));
  }
  async open(deviceId, options, signal) {
    options = validateOptions(options);
    if (typeof deviceId !== 'string') throw new WebcamFailure('InvalidArgument');
    this.live(); if (signal?.aborted) throw new WebcamFailure('Cancelled');
    signal = signal ? AbortSignal.any([signal, this.closed.signal]) : this.closed.signal;
    if (this.sessions.size >= 4) throw new WebcamFailure('Unavailable', '最多只能同時開啟四個攝影機工作階段。');
    // 先占有名額，避免多個尚未完成的權限請求突破工作階段上限。
    const slot = {}; this.sessions.add(slot);
    let stream, session;
    try {
      stream = await bounded(this.mediaDevices.getUserMedia({audio: false, video: {
        ...(deviceId ? {deviceId: {exact: deviceId}} : {}), width: {ideal: options.width}, height: {ideal: options.height}, frameRate: {ideal: options.fps}
      }}), signal, options.timeout, stop);
      if (this.disposed) throw new WebcamFailure('Closed');
      if (signal.aborted) throw new WebcamFailure('Cancelled');
      session = new BrowserWebcamSession(this, stream, options, signal);
      this.sessions.delete(slot); this.sessions.add(session);
      await session.ready(); return session;
    } catch (error) { this.sessions.delete(slot); this.sessions.delete(session); session?.dispose(); stop(stream); throw failure(error); }
  }
  closeSessions() { for (const session of [...this.sessions]) session.dispose?.(); }
  dispose() { if (this.disposed) return; this.disposed = true; this.closed.abort(); this.closeSessions(); this.sessions.clear(); }
}

export class BrowserWebcamSession {
  constructor(owner, stream, options, signal) {
    this.owner = owner; this.stream = stream; this.options = options; this.sequence = 0; this.started = owner.now();
    this.disposed = false; this.capturing = false; this.closed = new AbortController(); this.signal = signal;
    this.video = owner.document.createElement('video'); this.video.muted = true; this.video.playsInline = true; this.video.srcObject = stream;
    this.canvas = owner.document.createElement('canvas'); this.canvas.width = options.width; this.canvas.height = options.height;
    this.context = this.canvas.getContext('2d', {willReadFrequently: true});
    this.abort = () => this.dispose(); signal?.addEventListener('abort', this.abort, {once: true});
    this.ended = () => this.dispose(); for (const track of stream.getVideoTracks()) track.addEventListener('ended', this.ended, {once: true});
    if (signal?.aborted) this.dispose();
  }
  get isOpen() { return !this.disposed && this.stream.getVideoTracks().some(t => t.readyState === 'live'); }
  live() { if (this.signal?.aborted) throw new WebcamFailure('Cancelled'); if (!this.isOpen) throw new WebcamFailure('Closed'); }
  async ready() {
    try {
      this.live(); if (!this.context) throw new WebcamFailure('Unavailable');
      await bounded(this.video.play(), this.closed.signal, this.options.timeout);
      await this.waitFrame(); this.live();
    } catch (error) { this.dispose(); throw failure(error); }
  }
  async waitFrame() {
    let cleanup = () => {};
    const promise = new Promise(resolve => {
      if (this.video.requestVideoFrameCallback) {
        const id = this.video.requestVideoFrameCallback(resolve); cleanup = () => this.video.cancelVideoFrameCallback(id);
      } else if (this.video.readyState >= 2) resolve();
      else { this.video.addEventListener('loadeddata', resolve, {once: true}); cleanup = () => this.video.removeEventListener('loadeddata', resolve); }
    });
    try { await bounded(promise, this.closed.signal, this.options.timeout); } finally { cleanup(); }
  }
  async capture() {
    this.live(); if (this.capturing) throw new WebcamFailure('Unavailable', '同一工作階段不能同時擷取多張影格。');
    this.capturing = true;
    try {
      await this.waitFrame(); this.live();
      const {width, height} = this.options; this.context.drawImage(this.video, 0, 0, width, height);
      const pixels = new Uint8Array(this.context.getImageData(0, 0, width, height).data);
      if (pixels.length !== width * height * 4) throw new WebcamFailure('Unavailable');
      for (let i = 3; i < pixels.length; i += 4) if (pixels[i] !== 255) throw new WebcamFailure('Unavailable');
      return {width, height, pixels, alpha: 'Opaque', sequence: ++this.sequence, timestamp: this.owner.now() - this.started};
    } catch (error) { this.dispose(); throw failure(error); } finally { this.capturing = false; }
  }
  dispose() {
    if (this.disposed) return; this.disposed = true; this.closed.abort(); this.signal?.removeEventListener('abort', this.abort);
    for (const track of this.stream.getVideoTracks()) track.removeEventListener('ended', this.ended);
    stop(this.stream); this.video.pause(); this.video.srcObject = null; this.canvas.width = this.canvas.height = 0;
    this.owner.sessions.delete(this);
  }
}

export function registerWebcam(host) {
  const backend = host.webcam = new BrowserWebcamBackend();
  const module = host.library('攝影機');
  host.bind(module, '攝影機', '是否支援', () => backend.isSupported);
  host.bind(module, '攝影機', '列出裝置', async () => (await backend.enumerate(host.signal)).map(d => {
    const m = host.newModule(); host.set(m, '識別碼', d.id); host.set(m, '名稱', d.name); return m;
  }));
  host.bind(module, '攝影機', '開啟', async (id, width, height, fps) => {
    const session = await backend.open(id, {width, height, fps}, host.signal); const m = host.newModule();
    host.bind(m, '攝影機.工作階段', '是否開啟', () => session.isOpen);
    host.bind(m, '攝影機.工作階段', '釋放', () => session.dispose());
    host.bind(m, '攝影機.工作階段', '擷取影格', async () => {
      const frame = await session.capture(), f = host.newModule();
      for (const [name, value] of [['寬度', frame.width], ['高度', frame.height], ['序號', frame.sequence]]) host.set(f, name, num(value, name === '序號' ? 'double' : 'int'));
      host.set(f, '時間戳記', frame.timestamp); host.set(f, '影像', host.application.image(frame));
      host.bind(f, '攝影機.影格', '取得像素', () => Array.from(frame.pixels, n => num(n, 'int'))); return f;
    }); return m;
  });
}
