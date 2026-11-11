// Runs a JavaScript consumer of a native library's web output in Chromium and prints what it prints: the output folder
// (aqua build --target web --kind shared: <name>.wasm, runtime/, glue/) is staged with the consumer module beside it,
// served on localhost and driven by Playwright. The consumer exports `main(print)`.
//   node scripts/web-library-run.mjs <output>/web <consumer.mjs> [--timeout seconds]
// Exit code: 0 when main() resolves, 3 when it throws or the page fails.
import { createServer } from 'node:http';
import { cp, mkdtemp, readFile, rm, writeFile, copyFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, extname, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const argv = process.argv.slice(2);
const option = (name, fallback) => { const i = argv.indexOf(name); if (i < 0) return fallback; const v = argv[i + 1]; argv.splice(i, 2); return v; };
const timeout = Number(option('--timeout', '60')) * 1000;
const [output, consumer] = argv;
if (!output || !consumer) { console.error('usage: node scripts/web-library-run.mjs <output>/web <consumer.mjs>'); process.exit(2); }

const site = await mkdtemp(join(tmpdir(), 'aquarius-web-library-'));
await cp(output, site, { recursive: true });
await copyFile(consumer, join(site, 'consumer.mjs'));
await writeFile(join(site, 'index.html'), `<!doctype html><meta charset="utf-8"><script type="module">
window.result = { lines: [], status: 'running' };
import('./consumer.mjs').then((m) => m.main((line) => window.result.lines.push(line)))
    .then(() => { window.result.status = 'finished'; }, (error) => { window.result.lines.push('error: ' + (error.stack ?? error)); window.result.status = 'failed'; });
</script>`);

const types = { '.html': 'text/html', '.mjs': 'text/javascript', '.js': 'text/javascript', '.wasm': 'application/wasm', '.so': 'application/wasm', '.json': 'application/json' };
const server = createServer(async (request, response) => {
    const path = join(site, decodeURIComponent(new URL(request.url, 'http://localhost').pathname));
    try {
        const body = await readFile(path.endsWith('/') || path === site ? join(site, 'index.html') : path);
        response.writeHead(200, { 'content-type': types[extname(path)] ?? 'text/html', 'cache-control': 'no-store' });
        response.end(body);
    } catch { response.writeHead(404); response.end(); }
});
await new Promise((ok) => server.listen(0, '127.0.0.1', ok));

const require = createRequire(pathToFileURL(join(repository, 'scripts', 'package.json'))); // npm install in scripts/
const { chromium } = require('playwright');
let browser;
try { browser = await chromium.launch({ channel: 'chrome' }); } catch { browser = await chromium.launch(); }
let code = 3;
try {
    const page = await browser.newPage();
    page.on('pageerror', (error) => console.error('[page]', error.stack ?? error.message));
    await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
    await page.waitForFunction(() => window.result?.status !== 'running', null, { timeout });
    const result = await page.evaluate(() => window.result);
    for (const line of result.lines) console.log(line);
    code = result.status === 'finished' ? 0 : 3;
} catch (error) {
    console.error(error.message);
} finally {
    await browser.close();
    server.close();
    await rm(site, { recursive: true, force: true });
}
process.exit(code);
