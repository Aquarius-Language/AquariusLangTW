'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { spawn, spawnSync } = require('node:child_process');
const { once } = require('node:events');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const os = require('node:os');

const server = process.env.AQUARIUS_SERVER || path.resolve(__dirname, '../bin/Release/net8.0/AquariusLanguageServer.dll');
const uri = 'file:///test/example.aqua';

test('Thread and 執行緒 provide bilingual completion, import aliases and shadowing', async t => {
  const s = new Session(t); await s.initialize();
  for (const [source,line,column] of [['Thread.',0,7],['執行緒.',0,4],['變數 t=匯入("執行緒");\nt.',1,2]]) {
    await s.open(source);const items=await s.feature('completion',line,column);
    for (const name of ['Start','啟動','Wait','等待','Result','結果','Cancel','取消','Close','關閉','Sleep','休眠','IsDone','是否完成','IsCancellationRequested','是否要求取消']) assert.ok(items.some(item=>item.label===name),name);
  }
  await s.open('變數 Thread=1;\nThread.');
  assert.equal((await s.feature('completion',1,7)).length,0);
});

test('network results expose bilingual fields, nested errors and optional platform capabilities', async t => {
  const s = new Session(t); await s.initialize();
  await s.open('變數 網路=匯入("網路");變數 回應=網路.取得("https://example.com");\n回應.');
  let items=await s.feature('completion',1,3);
  for(const name of ['Success','成功','Status','狀態碼','Error','錯誤','JSON','解析JSON'])assert.ok(items.some(x=>x.label===name),name);
  await s.open('變數 網路=匯入("網路");變數 回應=網路.Get("https://example.com");\n回應.錯誤.');
  items=await s.feature('completion',1,6);for(const name of ['Kind','種類','Message','訊息','Retryable','可重試'])assert.ok(items.some(x=>x.label===name),name);
  await s.open('變數 網路=匯入("網路");變數 網址=網路.解析網址("https://example.com");\n網址.');
  items=await s.feature('completion',1,3);for(const name of ['Host','主機','Port','連接埠'])assert.ok(items.some(x=>x.label===name),name);
  await s.open('變數 網路=匯入("網路");\n網路.Listen("http://127.0.0.1:0");');
  assert.match((await s.feature('hover',1,5)).contents.value,/http-server/);
});

test('built-in array modules offer both languages and respect lexical shadowing', async t => {
  const s = new Session(t); await s.initialize();
  await s.open('陣列.建立(2,0);\nArray.');
  const items = await s.feature('completion', 1, 6);
  for (const name of ['Make','建立','AppendArray','追加陣列','Copy','複製'])
    assert.ok(items.some(item => item.label === name), name);
  assert.match((await s.feature('hover', 0, 4)).contents.value, /Make/);
  await s.open('變數 陣列=99;\n陣列.');
  assert.deepEqual(await s.feature('completion', 1, 3), []);
  await s.open('變數 arrays=匯入("陣列");\narrays.');
  assert.ok((await s.feature('completion', 1, 7)).some(item => item.label === 'Make'));
});

class Session {
  constructor(t, environment = {}) {
    this.process = spawn(process.env.AQUARIUS_DOTNET || 'dotnet', [server], { windowsHide: true, env: { ...process.env, ...environment } });
    this.buffer = Buffer.alloc(0);
    this.messages = [];
    this.nextId = 0;
    this.stderr = '';
    this.process.stderr.on('data', data => { this.stderr += data; });
    this.process.stdout.on('data', data => {
      this.buffer = Buffer.concat([this.buffer, data]);
      while (true) {
        const split = this.buffer.indexOf('\r\n\r\n');
        if (split < 0) break;
        const match = /Content-Length: (\d+)/i.exec(this.buffer.subarray(0, split).toString('ascii'));
        assert.ok(match, 'stdout contains only LSP messages');
        const length = Number(match[1]);
        if (this.buffer.length < split + 4 + length) break;
        this.messages.push(JSON.parse(this.buffer.subarray(split + 4, split + 4 + length).toString('utf8')));
        this.buffer = this.buffer.subarray(split + 4 + length);
      }
    });
    t.after(() => this.process.kill());
  }
  send(method, params, id) {
    const body = Buffer.from(JSON.stringify({ jsonrpc: '2.0', method, params, ...(id === undefined ? {} : { id }) }));
    this.process.stdin.write(Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`), body]));
  }
  async take(predicate) {
    const deadline = Date.now() + 7000;
    while (Date.now() < deadline) {
      const index = this.messages.findIndex(predicate);
      if (index >= 0) return this.messages.splice(index, 1)[0];
      if (this.process.exitCode !== null) throw new Error(`Server exited: ${this.process.exitCode} ${this.stderr}`);
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    throw new Error(`Timed out waiting for server: ${this.stderr}`);
  }
  async request(method, params = {}) {
    const id = ++this.nextId;
    this.send(method, params, id);
    const response = await this.take(message => message.id === id);
    assert.equal(response.error, undefined, JSON.stringify(response.error));
    return response.result;
  }
  async initialize() {
    const result = await this.request('initialize', { processId: process.pid, rootUri: null, capabilities: {} });
    this.send('initialized', {});
    return result;
  }
  async open(text, documentUri = uri) {
    this.send('textDocument/didOpen', { textDocument: { uri: documentUri, languageId: 'aquarius', version: 1, text } });
    return this.diagnostics(1, documentUri);
  }
  async diagnostics(version, documentUri = uri) {
    return (await this.take(m => m.method === 'textDocument/publishDiagnostics' && m.params.uri === documentUri && m.params.version === version)).params.diagnostics;
  }
  feature(method, line, character, documentUri = uri) {
    return this.request(`textDocument/${method}`, { textDocument: { uri: documentUri }, position: { line, character } });
  }
}

test('LSP lifecycle, unsupported requests, malformed JSON, and fragmented UTF-8 frames', async t => {
  const s = new Session(t);
  s.send('textDocument/hover', {}, 'before-init');
  assert.equal((await s.take(m => m.id === 'before-init')).error.code, -32002);
  const body = Buffer.from('{broken json');
  s.process.stdin.write(`Content-Length: ${body.length}\r\n\r\n`);
  s.process.stdin.write(body);
  assert.equal((await s.take(m => m.id === null)).error.code, -32700);
  const capabilities = (await s.initialize()).capabilities;
  assert.equal(capabilities.positionEncoding, 'utf-16');
  assert.equal(capabilities.textDocumentSync.change, 2);
  s.send('unsupported/request', {}, 'unknown');
  assert.equal((await s.take(m => m.id === 'unknown')).error.code, -32601);
  const text = '變數 中文 = "😀";';
  const open = Buffer.from(JSON.stringify({ jsonrpc: '2.0', method: 'textDocument/didOpen', params: { textDocument: { uri, languageId: 'aquarius', version: 1, text } } }));
  const frame = Buffer.concat([Buffer.from(`content-length: ${open.length}\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n`), open]);
  for (let i = 0; i < frame.length; i += 3) s.process.stdin.write(frame.subarray(i, i + 3));
  assert.deepEqual(await s.diagnostics(1), []);
  assert.equal(await s.request('shutdown'), null);
  const exited = once(s.process, 'exit');
  s.send('exit');
  assert.equal((await exited)[0], 0);
});

test('Chinese completion, hover, definition, and outline use real source ranges', async t => {
  const s = new Session(t);
  await s.initialize();
  assert.deepEqual(await s.open('變數 加法 = 函式(甲, 乙) { 回傳 甲 + 乙; };\r\n印出(加法(1, 2));'), []);
  const completion = await s.feature('completion', 1, 0);
  assert.ok(completion.some(item => item.label === '加法' && item.kind === 3));
  assert.ok(completion.some(item => item.label === '印出'));
  const hover = await s.feature('hover', 1, 3);
  assert.match(hover.contents.value, /加法\(甲, 乙\)/);
  const definition = await s.feature('definition', 1, 3);
  assert.equal(definition.uri, uri);
  assert.deepEqual(definition.range, { start: { line: 0, character: 3 }, end: { line: 0, character: 5 } });
  const symbols = await s.request('textDocument/documentSymbol', { textDocument: { uri } });
  assert.ok(symbols.some(symbol => symbol.name === '加法' && symbol.kind === 12));
});

test('core time aliases provide completion and explain milliseconds in hover', async t => {
  const s = new Session(t);
  await s.initialize();
  assert.deepEqual(await s.open('目前時間();\ncurrentTime();'), []);
  const completion = await s.feature('completion', 0, 0);
  for (const name of ['目前時間', 'currentTime']) assert.ok(completion.some(item => item.label === name));
  for (const line of [0, 1]) {
    const hover = await s.feature('hover', line, 0);
    assert.match(hover.contents.value, /Monotonic.*double milliseconds/);
  }
});

test('functions and loop scopes resolve shadowed identifiers and hide locals outside their scope', async t => {
  const s = new Session(t);
  await s.initialize();
  const text = '變數 名稱 = 1;\n變數 測試 = 函式(名稱) {\n  變數 本地 = 名稱;\n  回傳 名稱;\n};\n印出(名稱);\n迴圈 (變數 索引 = 0; 索引 < 2; 索引++) { 印出(索引); }\n印出(名稱);';
  assert.deepEqual(await s.open(text), []);
  assert.equal((await s.feature('definition', 3, 5)).range.start.line, 1);
  assert.equal((await s.feature('definition', 5, 3)).range.start.line, 0);
  assert.ok((await s.feature('completion', 3, 2)).some(item => item.label === '本地'));
  const outside = await s.feature('completion', 7, 0);
  assert.ok(!outside.some(item => item.label === '本地' || item.label === '索引'));
});

test('incremental edits, multiple changes, full replacement, and stale versions', async t => {
  const s = new Session(t);
  await s.initialize();
  assert.deepEqual(await s.open('變數 名稱 = 1;\n印出(名稱);'), []);
  s.send('textDocument/didChange', { textDocument: { uri, version: 2 }, contentChanges: [
    { range: { start: { line: 0, character: 8 }, end: { line: 0, character: 9 } }, text: '' }
  ] });
  const errors = await s.diagnostics(2);
  assert.ok(errors.length > 0);
  assert.deepEqual(errors[0].range.start, { line: 0, character: 8 });
  s.send('textDocument/didChange', { textDocument: { uri, version: 3 }, contentChanges: [
    { range: { start: { line: 0, character: 8 }, end: { line: 0, character: 8 } }, text: '2' },
    { range: { start: { line: 0, character: 8 }, end: { line: 0, character: 9 } }, text: '3' }
  ] });
  assert.deepEqual(await s.diagnostics(3), []);
  s.send('textDocument/didChange', { textDocument: { uri, version: 4 }, contentChanges: [{ text: '變數 更新 = 4;' }] });
  assert.deepEqual(await s.diagnostics(4), []);
  s.send('textDocument/didChange', { textDocument: { uri, version: 2 }, contentChanges: [{ text: '變數 舊版 = 0;' }] });
  const symbols = await s.request('textDocument/documentSymbol', { textDocument: { uri } });
  assert.deepEqual(symbols.map(symbol => symbol.name), ['更新']);
});

test('UTF-16 offsets survive astral characters, combining marks, and CRLF', async t => {
  const s = new Session(t);
  await s.initialize();
  const identifier = '𠀀名稱';
  const text = `變數 ${identifier} = "😀"; 印出(${identifier});\r\n變數 cafe\u0301 = 1;\r\n印出(cafe\u0301);`;
  assert.deepEqual(await s.open(text), []);
  const definition = await s.feature('definition', 0, text.indexOf(identifier, 8));
  assert.deepEqual(definition.range, { start: { line: 0, character: 3 }, end: { line: 0, character: 7 } });
  assert.equal((await s.feature('definition', 2, 4)).range.end.character, 8);
});

test('comments and strings suppress completion; module members do not resolve to locals', async t => {
  const s = new Session(t);
  await s.initialize();
  assert.deepEqual(await s.open('變數 名稱 = 1;\n# 名稱\n"名稱";\n變數 模組 = 匯入("other.aqua");\n模組.名稱();\n## 名稱 # still comment ##印出(名稱);'), []);
  assert.deepEqual(await s.feature('completion', 1, 4), []);
  assert.deepEqual(await s.feature('completion', 2, 2), []);
  assert.equal(await s.feature('definition', 4, 3), null);
  assert.equal(await s.feature('hover', 4, 3), null);
  assert.deepEqual(await s.feature('completion', 4, 3), []);
});

test('incomplete input reports errors and never hangs or crashes the server', async t => {
  const s = new Session(t);
  await s.initialize();
  for (const text of ['## unterminated', '"unterminated', '變數 函 = 函式(甲) { 回傳 甲;', '變數 函 = 函式(1) {};', '變數 數字 = 1.5;', '變數 = ;', '迴圈 (變數 i = 0; i < 2; i++) 印出(i);', '('.repeat(400) + '1' + ')'.repeat(400)]) {
    assert.ok((await s.open(text)).length > 0, text);
  }
  assert.deepEqual(await s.open('# line\n'.repeat(10000) + '## block ##變數 正常 = 1;'), []);
  assert.deepEqual(await s.open('變數 正常 = 1;'), []);
});

test('closing a document clears errors and invalid feature positions return errors', async t => {
  const s = new Session(t);
  await s.initialize();
  await s.open('變數 值 = ;');
  s.send('textDocument/hover', { textDocument: { uri }, position: { line: 99, character: 0 } }, 'bad-position');
  assert.equal((await s.take(m => m.id === 'bad-position')).error.code, -32602);
  s.send('textDocument/didClose', { textDocument: { uri } });
  assert.deepEqual((await s.take(m => m.method === 'textDocument/publishDiagnostics')).params.diagnostics, []);
});

test('all repository examples parse through the LSP server without executing them', async t => {
  const s = new Session(t);
  await s.initialize();
  const root = path.resolve(__dirname, '../../AquariusDesktop/examples');
  for (const file of fs.readdirSync(root, { recursive: true }).filter(name => name.endsWith('.aqua'))) {
    const fullPath = path.join(root, file);
    const diagnostics = await s.open(fs.readFileSync(fullPath, 'utf8'), pathToFileURL(fullPath).href);
    assert.deepEqual(diagnostics, [], file);
  }
});

test('bilingual declarations complete, hover, resolve definitions, and expose both symbols', async t => {
  const s = new Session(t); await s.initialize();
  const text = '變數 加法, add = 函式(甲,乙){甲+乙;};\n加法(1,2);\nadd(3,4);\n';
  assert.deepEqual(await s.open(text), []);
  for (const [name, line] of [['加法', 1], ['add', 2]]) {
    const hover = await s.feature('hover', line, 1);
    assert.match(hover.contents.value, /加法 \/ add\(甲, 乙\)/);
    const definition = await s.feature('definition', line, 1);
    assert.equal(definition.range.start.character, text.indexOf(name));
  }
  const items = await s.feature('completion', 3, 0);
  for (const name of ['加法', 'add', 'len', '長度', 'print', '印出', 'import', '匯入'])
    assert.ok(items.some(item => item.label === name), name);
  const symbols = await s.request('textDocument/documentSymbol', { textDocument: { uri } });
  assert.ok(symbols.some(symbol => symbol.name === '加法' && symbol.kind === 12));
  assert.ok(symbols.some(symbol => symbol.name === 'add' && symbol.kind === 12));
});

test('bilingual aliases respect shadowing and malformed declarations produce diagnostics', async t => {
  const s = new Session(t); await s.initialize();
  assert.deepEqual(await s.open('變數 中文, english=函式(){1;};\n變數 測試=函式(){變數 中文, english=函式(){2;};\nenglish();};\nenglish();'), []);
  assert.equal((await s.feature('definition', 2, 1)).range.start.line, 1);
  assert.equal((await s.feature('definition', 3, 1)).range.start.line, 0);
  for (const text of ['變數 中文,english=1;', '變數 相同,相同=函式(){};', '變數 中文,english,third=函式(){};', '變數 中文,=函式(){};'])
    assert.ok((await s.open(text)).length > 0, text);
});

test('all native library catalog functions offer both member names without loading native libraries', async t => {
  const s = new Session(t); await s.initialize();
  const catalog = fs.readFileSync(path.resolve(__dirname, '../../AquariusCore/runtime/LibraryCatalog.Generated.cs'), 'utf8');
  const groups = new Map();
  for (const [, library, english, chinese] of catalog.matchAll(/new\("([^"]+)", "([^"]+)", "([^"]+)"/g)) {
    if (library.includes('.')) continue;
    if (!groups.has(library)) groups.set(library, []);
    groups.get(library).push([english, chinese]);
  }
  assert.equal(groups.size, 8);
  assert.equal(groups.has('Jolt'), false);
  for (const [library, names] of groups) {
    const text = `變數 lib=import("${library}");\nlib.${names[0][0]};`;
    assert.deepEqual(await s.open(text), [], library);
    const items = await s.feature('completion', 1, 4);
    for (const [english, chinese] of names) {
      for (const name of [english, chinese]) assert.ok(items.some(item => item.label === name && item.kind === 3), `${library}.${name}`);
    }
    const hover = await s.feature('hover', 1, 4);
    assert.ok(hover.contents.value.includes(names[0][0]) && hover.contents.value.includes(names[0][1]));
    assert.equal(await s.feature('definition', 1, 4), null);
  }
});

test('Chinese prefixes and incomplete member calls complete native aliases and respect receiver shadowing', async t => {
  const s = new Session(t); await s.initialize();
  await s.open('變數 m=匯入("GLM");\nm.正規化([1,0,0]);\nm.');
  const filtered = await s.feature('completion', 1, 4);
  assert.deepEqual(filtered.map(item => item.label), ['正規化']);
  assert.match((await s.feature('hover', 1, 3)).contents.value, /Normalize/);
  assert.ok((await s.feature('completion', 2, 2)).some(item => item.label === '正規化'));
  assert.deepEqual(await s.open('變數 m=匯入("GLM");\n變數 f=函式(m){m.未知;};'), []);
  assert.deepEqual(await s.feature('completion', 1, 14), []);
  assert.equal(await s.feature('hover', 1, 14), null);
  assert.deepEqual(await s.open('變數 import=函式(路徑){42;};\n變數 m=import("GLM");\nm.未知;'), []);
  assert.deepEqual(await s.feature('completion', 2, 3), []);
});

test('native returned objects and chained calls infer bilingual method names', async t => {
  const s = new Session(t); await s.initialize();
  for (const [setup, english, chinese] of [
    ['變數 j=匯入("DynamicLibrary");變數 obj=j.載入("plugin.mjs");', 'Call', '呼叫'],
    ['變數 g=匯入("wgpu");變數 obj=g.建立裝置();', 'CreateBuffer', '建立緩衝區'],
    ['變數 g=匯入("WGPU");變數 obj=g.建立裝置().建立緩衝區([1]);', 'Read', '讀取'],
    ['變數 g=匯入("WGPU");變數 d=g.CreateDevice();變數 obj=d.CreateShader("","");', 'SetInt', '設定整數'],
    ['變數 g=匯入("WGPU");變數 d=g.CreateDevice();變數 obj=d.建立繪圖目標(2,2);', 'ReadPixels', '讀取像素'],
    ['變數 p=匯入("Processing");變數 obj=p.建立向量(3,4);', 'normalize', '正規化'],
    ['變數 p=匯入("Processing");變數 obj=p.createVector(3,4).copy();', 'mag', '長度'],
    ['變數 p=匯入("Processing");變數 obj=p.建立圖片(2,2);', 'loadPixels', '載入像素'],
    ['變數 p=匯入("Processing");變數 obj=p.建立圖形();', 'addChild', '加入子圖形'],
    ['變數 p=匯入("Processing");變數 obj=p.建立畫布(2,2);', 'beginDraw', '開始繪圖'],
    ['變數 p=匯入("Processing");變數 obj=p.建立著色器("","");', 'setInt', '設定整數']
  ]) {
    assert.deepEqual(await s.open(`${setup}\nobj.${chinese};`), []);
    const items = await s.feature('completion', 1, 4);
    for (const name of [english, chinese]) assert.ok(items.some(item => item.label === name), `${setup} ${name}`);
    const hover = await s.feature('hover', 1, 4);
    assert.ok(hover.contents.value.includes(english) && hover.contents.value.includes(chinese));
  }
});

test('攝影機提供繁體中文補全且不重複列出單一名稱', async t => {
  const s = new Session(t); await s.initialize();
  assert.deepEqual(await s.open('變數 裝置=匯入("攝影機");\n裝置.是否支援();'), []);
  const items = await s.feature('completion', 1, 3);
  assert.deepEqual(items.map(item => item.label).sort(), ['列出裝置', '是否支援', '開啟'].sort());
  assert.ok(items.every(item => item.detail.includes('參數數量')));
  assert.deepEqual(await s.open('變數 裝置=匯入("攝影機");變數 相機=裝置.開啟("",640,480,30);\n相機.擷取影格();'), []);
  assert.ok((await s.feature('completion', 1, 3)).some(item => item.label === '擷取影格'));
});

test('script-library aliases resolve from disk and unsaved buffers without executing the library', async t => {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'aquarius-bilingual-lsp-'));
  t.after(() => fs.rmSync(folder, { recursive: true, force: true }));
  const libraryFile = path.join(folder, '工具.aqua');
  const libraryUri = pathToFileURL(libraryFile).href;
  const mainUri = pathToFileURL(path.join(folder, 'main.aqua')).href;
  const library = '變數 加法,add=函式(甲,乙){甲+乙;};\n變數 中文限定=函式(){1;};\n變數 englishOnly=函式(){2;};\n執行檔案("MUST_NOT_EXECUTE",[]);';
  fs.writeFileSync(libraryFile, library);
  const s = new Session(t); await s.initialize();
  assert.deepEqual(await s.open('變數 lib=import("工具.aqua");\nlib.加法(1,2);\nlib.add(3,4);', mainUri), []);
  let items = await s.feature('completion', 1, 4, mainUri);
  for (const name of ['加法', 'add', '中文限定', 'englishOnly']) assert.ok(items.some(item => item.label === name), name);
  for (const [name, line] of [['加法', 1], ['add', 2]]) {
    assert.match((await s.feature('hover', line, 4, mainUri)).contents.value, /加法 \/ add/);
    const definition = await s.feature('definition', line, 4, mainUri);
    assert.equal(definition.uri, libraryUri); assert.equal(definition.range.start.character, library.indexOf(name));
  }
  for (const directory of ['目前工作目錄', 'currWorkingDir']) {
    assert.deepEqual(await s.open(`變數 路徑=${directory}+"/工具.aqua";變數 lib=匯入(路徑);\nlib.加法;`, mainUri), []);
    assert.ok((await s.feature('completion', 1, 4, mainUri)).some(item => item.label === 'add'));
  }
  assert.deepEqual(await s.open('變數 新函式,newFunction=函式(){42;};', libraryUri), []);
  items = await s.feature('completion', 1, 4, mainUri);
  assert.ok(items.some(item => item.label === '新函式') && items.some(item => item.label === 'newFunction'));
  assert.ok(!items.some(item => item.label === 'add'));
  s.send('textDocument/didChange', { textDocument: { uri: libraryUri, version: 2 }, contentChanges: [{ text: '變數 更新,updated=函式(){7;};' }] });
  assert.deepEqual(await s.diagnostics(2, libraryUri), []);
  items = await s.feature('completion', 1, 4, mainUri);
  assert.ok(items.some(item => item.label === '更新') && !items.some(item => item.label === 'newFunction'));
});

function bottleFixture(t) {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'aquarius-bottle-lsp-'));
  t.after(() => fs.rmSync(folder, { recursive: true, force: true }));
  const write = (name, text) => { const file = path.join(folder, name); fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, text); return file; };
  const manifest = '[project]\nname="demo"\ntype="executable"\nentry="main.aqua"\nsources=["main.aqua"]\nplatforms=["web"]\n[dependencies]\ngreeter={path="vendor/greeter",version=">= 1.0, < 2.0"}\n';
  const file = write('aqua.toml', manifest);
  write('vendor/greeter/aqua.toml', '[project]\nname="greeter"\ntype="library"\nversion="1.2.0"\nplatforms=["web"]\n');
  const library = write('vendor/greeter/main.aqua', '變數 問候,greet=函式(){回傳 42;};');
  const main = write('main.aqua', '變數 lib=import("/bottles/greeter/main.aqua");\nlib.greet();');
  return { folder, write, manifest, manifestUri: pathToFileURL(file).href, mainUri: pathToFileURL(main).href, libraryUri: pathToFileURL(library).href, cache: path.join(folder, 'cache') };
}

test('TOML manifests diagnose names and versions and refresh source diagnostics on unsaved edits', async t => {
  const f = bottleFixture(t); const s = new Session(t, { AQUA_BOTTLE_HOME: f.cache }); await s.initialize();
  assert.deepEqual(await s.open(fs.readFileSync(path.join(f.folder, 'main.aqua'), 'utf8'), f.mainUri), []);
  const invalid = f.manifest.replace('name="demo"', 'name="bad name"');
  let errors = await s.open(invalid, f.manifestUri);
  assert.ok(errors.some(d => /whitespace/.test(d.message) && d.range.start.line === 1));
  errors = await s.diagnostics(1, f.mainUri); assert.ok(errors.some(d => /whitespace/.test(d.message)));
  const items = await s.feature('completion', 1, 0, f.manifestUri); assert.ok(items.some(i => i.label === 'platforms'));
  s.send('textDocument/didChange', { textDocument: { uri: f.manifestUri, version: 2 }, contentChanges: [{ text: f.manifest }] });
  assert.deepEqual(await s.diagnostics(2, f.manifestUri), []); assert.deepEqual(await s.diagnostics(1, f.mainUri), []);
  const libManifest = pathToFileURL(path.join(f.folder, 'vendor/greeter/aqua.toml')).href;
  errors = await s.open('[project]\nname="greeter"\ntype="library"\nplatforms=["web"]', libManifest);
  assert.ok(errors.some(d => /version/.test(d.message)));
  assert.equal(fs.existsSync(f.cache), false, 'editor inspection does not write to the global cache');
});

test('Bottle import completion, hover and definitions work from local libraries before installation', async t => {
  const f = bottleFixture(t); const s = new Session(t, { AQUA_BOTTLE_HOME: f.cache }); await s.initialize();
  assert.deepEqual(await s.open(fs.readFileSync(path.join(f.folder, 'main.aqua'), 'utf8'), f.mainUri), []);
  const items = await s.feature('completion', 1, 4, f.mainUri);
  for (const name of ['問候', 'greet']) assert.ok(items.some(item => item.label === name), name);
  assert.match((await s.feature('hover', 1, 5, f.mainUri)).contents.value, /greet/);
  assert.equal((await s.feature('definition', 1, 5, f.mainUri)).uri, f.libraryUri);
  assert.equal(fs.existsSync(path.join(f.folder, 'aqua.lock.json')), false);
});

test('locked cached libraries remain navigable after original source removal and file watchers refresh diagnostics', async t => {
  const f = bottleFixture(t);
  const cli = process.env.AQUARIUS_CLI || path.resolve(__dirname, '../../AquariusCli/bin/Debug/net8.0/aqua.dll');
  const restore = spawnSync(process.env.AQUARIUS_DOTNET || 'dotnet', [cli, 'bottle', 'restore', '--project', f.folder], { env: { ...process.env, AQUA_BOTTLE_HOME: f.cache }, encoding: 'utf8', windowsHide: true });
  assert.equal(restore.status, 0, restore.stderr);
  fs.rmSync(path.join(f.folder, 'vendor'), { recursive: true });
  const s = new Session(t, { AQUA_BOTTLE_HOME: f.cache }); await s.initialize();
  assert.deepEqual(await s.open(fs.readFileSync(path.join(f.folder, 'main.aqua'), 'utf8'), f.mainUri), []);
  const definition = await s.feature('definition', 1, 5, f.mainUri);
  assert.match(definition.uri, /packages\/greeter\/1.2.0\/content\/main.aqua$/);
  const corrupt = path.join(f.cache, 'packages/greeter/1.2.0/content/main.aqua'); fs.writeFileSync(corrupt, 'corrupt');
  s.send('workspace/didChangeWatchedFiles', { changes: [{ uri: pathToFileURL(corrupt).href, type: 2 }] });
  assert.ok((await s.diagnostics(1, f.mainUri)).some(d => /integrity/.test(d.message)));
});
