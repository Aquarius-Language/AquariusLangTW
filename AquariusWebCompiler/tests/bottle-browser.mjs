// Run after exporting examples/bottle_showcase, passing the web output directory.
import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
const website = path.resolve(process.argv[2] ?? '.web-build/bottle-showcase');
const mime = {'.html':'text/html', '.mjs':'text/javascript', '.js':'text/javascript', '.json':'application/json', '.wasm':'application/wasm'};
const server = createServer(async (request, response) => {
  try {
    const relative = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    const file = path.resolve(website, '.' + relative + (relative.endsWith('/') ? 'index.html' : ''));
    if (!file.startsWith(website + path.sep)) throw new Error('Outside website');
    response.setHeader('Content-Type', mime[path.extname(file)] ?? 'application/octet-stream');
    response.end(await readFile(file));
  } catch { response.statusCode = 404; response.end('Not found'); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
let browser;
try {
  browser = await chromium.launch({channel: process.env.AQUARIUS_BROWSER ?? 'chrome', headless: true});
  const page = await browser.newPage(); const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(`http://127.0.0.1:${server.address().port}/?autorun=0`);
  await page.waitForFunction(() => window.aquarius?.state === 'ready');
  const result = await page.evaluate(() => window.aquarius.run());
  assert.match(result.output, /20 \+ 22 = 42/);
  assert.match(result.output, /cached third-party asset/);
  assert.match(result.output, /BOTTLE_DYNAMIC_LINK_OK/);
  assert.match(result.output, /BOTTLE_SHOWCASE_OK/);
  assert.deepEqual(errors, []);
  console.log(result.output);
} finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }
