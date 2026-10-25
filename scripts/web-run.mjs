// Runs an application .wasm on the web runtime in Chromium (WebGPU enabled) and prints its output: a website is staged
// from web-glue, a runtime web build and the application, served on localhost and driven by Playwright.
//   node scripts/web-run.mjs <runtime web install dir> <app.wasm> [--libraries a,b] [--frames N] [--screenshot file.png]
//                            [--timeout seconds] [--keep directory] [--headed]
// Exit code: the application's, or 3 when the page fails to run it.
import { createServer } from 'node:http';
import { cp, mkdir, mkdtemp, readFile, readdir, rm, writeFile, copyFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, extname, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const argv = process.argv.slice(2);
const option = (name, fallback) => { const i = argv.indexOf(name); if (i < 0) return fallback; const v = argv[i + 1]; argv.splice(i, 2); return v; };
const flag = (name) => { const i = argv.indexOf(name); if (i < 0) return false; argv.splice(i, 1); return true; };
const libraries = (option('--libraries', '') || '').split(',').filter(Boolean);
const frames = option('--frames', '');
const screenshot = option('--screenshot', '');
const timeout = Number(option('--timeout', '60')) * 1000;
const keep = option('--keep', '');
const headed = flag('--headed');
const [runtimeDirectory, program] = argv;
if (!runtimeDirectory || !program) { console.error('usage: node scripts/web-run.mjs <runtime web dir> <app.wasm> [options]'); process.exit(2); }

const site = keep ? resolve(keep) : await mkdtemp(join(tmpdir(), 'aquarius-web-'));
await mkdir(join(site, 'runtime', 'libraries'), { recursive: true });
await cp(join(repository, 'web-glue'), join(site, 'glue'), { recursive: true });
await copyFile(join(repository, 'web-glue', 'page', 'index.html'), join(site, 'index.html'));
await copyFile(join(repository, 'web-glue', 'page', 'app.mjs'), join(site, 'app.mjs'));
for (const file of await readdir(runtimeDirectory)) {
    if (file.endsWith('.wasm') || file.endsWith('.so')) await copyFile(join(runtimeDirectory, file), join(site, 'runtime', file));
}
const libraryDirectory = join(runtimeDirectory, 'libraries');
if (existsSync(libraryDirectory)) await cp(libraryDirectory, join(site, 'runtime', 'libraries'), { recursive: true });
await copyFile(program, join(site, 'program.wasm'));
await writeFile(join(site, 'aquarius.json'), JSON.stringify({ program: 'program.wasm', libraries, assets: {} }));

const types = { '.html': 'text/html', '.mjs': 'text/javascript', '.js': 'text/javascript', '.wasm': 'application/wasm', '.so': 'application/wasm', '.json': 'application/json' };
const server = createServer(async (request, response) => {
    const pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    const path = join(site, pathname.endsWith('/') ? pathname + 'index.html' : pathname);
    try {
        const body = await readFile(path);
        response.writeHead(200, { 'content-type': types[extname(path)] ?? 'application/octet-stream', 'cache-control': 'no-store' });
        response.end(body);
    } catch { response.writeHead(404); response.end(); }
});
await new Promise((ok) => server.listen(0, '127.0.0.1', ok));
const url = `http://127.0.0.1:${server.address().port}/${frames ? `?frames=${frames}` : ''}`;

const require = createRequire(pathToFileURL(join(repository, 'AquariusWebCompiler', 'package.json')));
const { chromium } = require('playwright');
const launch = { headless: !headed, args: ['--enable-unsafe-webgpu', '--enable-features=Vulkan', '--ignore-gpu-blocklist'] };
let browser;
try { browser = await chromium.launch({ ...launch, channel: 'chrome' }); } catch { browser = await chromium.launch(launch); }
let code = 3;
try {
    const page = await browser.newPage();
    page.on('console', (message) => { if (message.type() === 'error') console.error('[page]', message.text()); });
    page.on('pageerror', (error) => console.error('[page]', error.stack ?? error.message));
    await page.goto(url);
    await page.waitForFunction(() => ['finished', 'failed'].includes(window.aquarius?.state?.status), null, { timeout });
    const state = await page.evaluate(() => window.aquarius.state);
    for (const line of state.lines) console.log(line);
    if (screenshot) await page.screenshot({ path: screenshot });
    code = state.status === 'finished' ? state.exitCode : 3;
} catch (error) {
    console.error(error.message);
} finally {
    await browser.close();
    server.close();
    if (!keep) await rm(site, { recursive: true, force: true });
}
process.exit(code);
