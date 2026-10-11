import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';

const root = path.resolve(process.env.AQUARIUS_WEBCAM_WEB ?? path.join(path.dirname(fileURLToPath(import.meta.url)), '../../.web-build/webcam/web'));
const server = createServer(async (request, response) => {
  try {
    const name = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    const file = path.resolve(root, '.' + name + (name.endsWith('/') ? 'index.html' : ''));
    if (!file.startsWith(root + path.sep)) throw new Error('要求的路徑超出測試網站。');
    response.setHeader('Content-Type', ({'.html': 'text/html', '.mjs': 'text/javascript', '.json': 'application/json', '.wasm': 'application/wasm'})[path.extname(file)] ?? 'application/octet-stream');
    response.end(await readFile(file));
  } catch { response.statusCode = 404; response.end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
let browser;
try {
  browser = await chromium.launch({channel: process.env.AQUARIUS_BROWSER ?? 'chrome', headless: true,
    args: ['--enable-unsafe-webgpu', '--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream']});
  const page = await browser.newPage(), errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto(`http://127.0.0.1:${server.address().port}/?autorun=0`); await page.waitForFunction(() => window.aquarius);
  const smoke = await page.evaluate(() => window.aquarius.run('冒煙測試.aqua'));
  assert.match(smoke.output, /攝影機冒煙測試通過/);
  const frames = await page.evaluate(() => window.aquarius.run('影格驗證.aqua'));
  assert.match(frames.output, /攝影機影格驗證全部通過/);
  assert.equal(await page.evaluate(() => window.aquarius.host.webcam.sessions.size), 0);
  const check = await page.evaluate(async () => {
    const {BrowserWebcamBackend} = await import('./webcam.mjs'), backend = new BrowserWebcamBackend(), source = new AbortController();
    try {
      const session = await backend.open('', {width: 8, height: 8, fps: 30}, source.signal), tracks = session.stream.getTracks();
      const before = await session.capture(); const pending = session.capture(); source.abort();
      let cancelled; try { await pending; } catch (error) { cancelled = error.kind; }
      let missing; try { await backend.open('不存在的攝影機識別碼', {width: 8, height: 8, fps: 30}); } catch (error) { missing = error.kind; }
      return {length: before.pixels.length, cancelled, missing, stopped: tracks.every(track => track.readyState === 'ended'), remaining: backend.sessions.size};
    } finally { backend.dispose(); }
  });
  assert.deepEqual(check, {length: 256, cancelled: 'Cancelled', missing: 'Unavailable', stopped: true, remaining: 0});
  await page.evaluate(() => window.aquarius.run('main.aqua', 2));
  assert.equal(await page.evaluate(() => window.aquarius.host.webcam.sessions.size), 0);
  await page.evaluate(() => { window.displayTask = window.aquarius.run('main.aqua').catch(() => {}); });
  await page.waitForFunction(() => window.aquarius.host?.processing.frameCount >= 2);
  const tracks = await page.evaluate(async () => {
    const host = window.aquarius.host, session = [...host.webcam.sessions][0], tracks = session.stream.getTracks();
    await window.aquarius.stop(); return tracks.map(track => track.readyState);
  });
  assert.ok(tracks.length > 0); assert.ok(tracks.every(state => state === 'ended')); assert.deepEqual(errors, []);
  console.log('攝影機瀏覽器冒煙測試通過：封裝腳本、虛擬裝置、影格格式、取消、未知裝置、繪圖展示及軌道清理。');
} finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }
