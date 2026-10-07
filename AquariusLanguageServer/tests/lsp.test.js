'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

const server = process.env.AQUARIUS_SERVER || path.resolve(__dirname, '../bin/Release/net8.0/AquariusLanguageServer.dll');
const uri = 'file:///test/example.aqua';

class Session {
  constructor(t) {
    this.process = spawn(process.env.AQUARIUS_DOTNET || 'dotnet', [server], { windowsHide: true });
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
  const root = path.resolve(__dirname, '../../AquariusDesktopInterpretedREPL/examples');
  for (const file of fs.readdirSync(root, { recursive: true }).filter(name => name.endsWith('.aqua'))) {
    const fullPath = path.join(root, file);
    const diagnostics = await s.open(fs.readFileSync(fullPath, 'utf8'), pathToFileURL(fullPath).href);
    assert.deepEqual(diagnostics, [], file);
  }
});
