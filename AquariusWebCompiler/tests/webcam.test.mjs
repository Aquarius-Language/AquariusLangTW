import test from 'node:test';
import assert from 'node:assert/strict';
import {BrowserWebcamBackend, validateOptions, WebcamFailure} from '../browser/webcam.mjs';

const options = {width: 2, height: 2, fps: 30};
function fixture() {
  let requests = 0, stops = 0, frame = 0;
  const track = new EventTarget(); track.readyState = 'live'; track.stop = () => { stops++; track.readyState = 'ended'; };
  const stream = {getTracks: () => [track], getVideoTracks: () => [track]};
  const video = new EventTarget(); Object.assign(video, {readyState: 2, play: async () => {}, pause() {}});
  const context = {fillRect() {},drawImage() {}, getImageData() { return {data: Uint8ClampedArray.from([++frame,2,3,255,4,5,6,255,7,8,9,255,10,11,12,255])}; }};
  const backend = new BrowserWebcamBackend({secure: true, now: () => frame * 10,
    document: {createElement: type => type === 'video' ? video : {getContext: () => context}},
    mediaDevices: {getUserMedia: async constraints => {requests++; assert.equal(constraints.audio, false); return stream; },
      enumerateDevices: async () => [{kind: 'audioinput', deviceId: '麥克風'}, {kind: 'videoinput', deviceId: '攝影機', label: ''}]}});
  return {backend, stream, video, context, track, requests: () => requests, stops: () => stops};
}
test('能力查詢及裝置列舉不會啟用攝影機或麥克風', async () => {
  const f = fixture(); assert.equal(f.backend.isSupported, true);
  assert.deepEqual(await f.backend.enumerate(), [{id: '攝影機', name: '攝影機 1'}]); assert.equal(f.requests(), 0); f.backend.dispose();
  assert.equal(new BrowserWebcamBackend({secure: false}).isSupported, false);
});
test('尺寸、影格率及逾時參數在請求權限前驗證', async () => {
  const f = fixture();
  for (const width of [0, -1, 1.5, NaN, Infinity, 1921, '640']) {
    assert.throws(() => validateOptions({...options, width}), e => e.kind === 'InvalidArgument');
    await assert.rejects(f.backend.open('', {...options, width}), e => e.kind === 'InvalidArgument');
  }
  for (const fps of [0, 60.5, 61]) assert.throws(() => validateOptions({...options, fps}));
  assert.throws(() => validateOptions({...options, height: 1081}));
  assert.throws(() => validateOptions({...options, timeout: 0}));
  assert.equal(f.requests(), 0); f.backend.dispose();
});
test('影格快照、序號、時間戳記及重複釋放', async () => {
  const f = fixture(), session = await f.backend.open('', options);
  const a = await session.capture(), b = await session.capture();
  assert.equal(a.pixels.length, 16); assert.equal(a.sequence, 1); assert.equal(b.sequence, 2); assert.equal(a.pixels[0], 1);
  assert.ok(b.timestamp >= a.timestamp); assert.equal(session.isOpen, true);
  session.dispose(); session.dispose(); assert.equal(f.stops(), 1); assert.equal(f.backend.sessions.size, 0);
  await assert.rejects(session.capture(), e => e.kind === 'Closed'); f.backend.dispose();
});
test('權限拒絕及裝置不存在的錯誤使用繁體中文', async () => {
  const f = fixture();
  for (const [name, kind] of [['NotAllowedError', 'PermissionDenied'], ['NotFoundError', 'NotFound'], ['NotReadableError', 'Unavailable']]) {
    f.backend.mediaDevices.getUserMedia = async () => { throw new DOMException('原生錯誤', name); };
    await assert.rejects(f.backend.open('', options), e => e instanceof WebcamFailure && e.kind === kind && !e.message.includes('原生錯誤'));
    assert.equal(f.backend.sessions.size, 0);
  }
  f.backend.dispose();
});
test('權限請求取消後才取得的影像軌道仍會停止', async () => {
  const f = fixture(); let finish; f.backend.mediaDevices.getUserMedia = () => new Promise(resolve => {finish = resolve;});
  const cancel = new AbortController(), opening = f.backend.open('', options, cancel.signal); cancel.abort();
  await assert.rejects(opening, e => e.kind === 'Cancelled'); finish(f.stream); await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.stops(), 1); assert.equal(f.backend.sessions.size, 0); f.backend.dispose();
});
test('權限請求逾時後的資源以及宿主結束時的待處理請求', async () => {
  const f = fixture(); let finish; f.backend.mediaDevices.getUserMedia = () => new Promise(resolve => {finish = resolve;});
  await assert.rejects(f.backend.open('', {...options, timeout: 5}), e => e.kind === 'Timeout');
  finish(f.stream); await new Promise(resolve => setImmediate(resolve)); assert.equal(f.stops(), 1);
  const pending = f.backend.open('', options); f.backend.dispose(); await assert.rejects(pending, e => e.kind === 'Cancelled');
  finish(f.stream); await new Promise(resolve => setImmediate(resolve)); assert.equal(f.backend.sessions.size, 0);
});
test('影格等待逾時、裝置拔除及擷取取消會關閉裝置', async () => {
  const f = fixture(), cancel = new AbortController(), session = await f.backend.open('', {...options, timeout: 5}, cancel.signal);
  f.video.requestVideoFrameCallback = () => 1; f.video.cancelVideoFrameCallback = () => {};
  await assert.rejects(session.capture(), e => e.kind === 'Timeout'); assert.equal(session.isOpen, false); assert.equal(f.stops(), 1);
  const g = fixture(), source = new AbortController(), s = await g.backend.open('', options, source.signal);
  source.abort(); assert.equal(s.isOpen, false); await assert.rejects(s.capture(), e => e.kind === 'Cancelled'); assert.equal(g.stops(), 1);
  const h = fixture(), removed = await h.backend.open('', options); h.track.readyState = 'ended'; h.track.dispatchEvent(new Event('ended'));
  assert.equal(removed.isOpen, false); assert.equal(h.backend.sessions.size, 0); f.backend.dispose(); g.backend.dispose(); h.backend.dispose();
});
test('等待中的權限請求也計入工作階段上限', async () => {
  const f = fixture(); f.backend.mediaDevices.getUserMedia = () => new Promise(() => {});
  const pending = Array.from({length: 4}, () => f.backend.open('', options));
  await assert.rejects(f.backend.open('', options), e => e.kind === 'Unavailable'); f.backend.dispose();
  await Promise.all(pending.map(p => assert.rejects(p, e => e.kind === 'Cancelled'))); assert.equal(f.backend.sessions.size, 0);
});
test('同時擷取及無效像素不會留下活動工作階段', async () => {
  const f = fixture(), session = await f.backend.open('', options); let finish;
  f.video.requestVideoFrameCallback = callback => { finish = callback; return 1; }; f.video.cancelVideoFrameCallback = () => {};
  const capture = session.capture(); await assert.rejects(session.capture(), e => e.kind === 'Unavailable'); finish(); await capture;
  f.video.requestVideoFrameCallback = null; f.context.getImageData = () => ({data: new Uint8Array(16)});
  await assert.rejects(session.capture(), e => e.kind === 'Unavailable'); assert.equal(session.isOpen, false); assert.equal(f.stops(), 1); f.backend.dispose();
});
test('新影格可立即擷取，重複影格才等待，並保持攝影機比例',async()=>{
  const f=fixture(),session=await f.backend.open('',options);let waits=0,draw;
  f.video.currentTime=1;f.video.videoWidth=4;f.video.videoHeight=2;
  f.video.requestVideoFrameCallback=callback=>{waits++;queueMicrotask(()=>{f.video.currentTime+=.033;callback();});return 1;};
  f.video.cancelVideoFrameCallback=()=>{};f.context.drawImage=(...args)=>{draw=args;};
  await session.capture();assert.equal(waits,0);assert.deepEqual(draw.slice(1),[0,.5,2,1]);
  await session.capture();assert.equal(waits,1);f.video.currentTime+=.1;
  await session.capture();assert.equal(waits,1);session.dispose();
});
