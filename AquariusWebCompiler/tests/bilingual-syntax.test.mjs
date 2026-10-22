import test from 'node:test';
import assert from 'node:assert/strict';
import {runtime, compile} from './wasm-fixture.mjs';
import {inspect} from '../browser/values.mjs';
import {WasmRuntime} from '../browser/wasm.mjs';
import {CapabilityHost} from '../../AquariusCore/hosting/capability-host.mjs';

const programs = [
  ['Traditional Chinese', `
    變數 階乘 = 函式(n) { 如果 (n < 2) { 回傳 1; } 回傳 n * 階乘(n - 1); };
    變數 分類 = 函式(n) { 如果 (n < 0) { 回傳 -1; } 否則如果 (n == 0) { 回傳 0; } 否則 { 回傳 1; } };
    變數 總和 = 0;
    迴圈 (變數 i = 0; i < 10; i++) { 如果 (i == 3) { 中斷; } 總和 += i; };
    [階乘(6), 分類(-1), 分類(0), 分類(9), 總和, 真 == !假];`],
  ['English', `
    var factorial = function(n) { if (n < 2) { return 1; } return n * factorial(n - 1); };
    var classify = function(n) { if (n < 0) { return -1; } else if (n == 0) { return 0; } else { return 1; } };
    var sum = 0;
    for (var i = 0; i < 10; i++) { if (i == 3) { break; } sum += i; };
    [factorial(6), classify(-1), classify(0), classify(9), sum, true == !false];`],
  ['mixed', `
    var 階乘 = 函式(n) { if (n < 2) { 回傳 1; } return n * 階乘(n - 1); };
    變數 classify = function(n) { 如果 (n < 0) { return -1; } else 如果 (n == 0) { 回傳 0; } 否則 { return 1; } };
    var 總和 = 0;
    迴圈 (var i = 0; i < 10; i++) { if (i == 3) { 中斷; } 總和 += i; };
    [階乘(6), classify(-1), classify(0), classify(9), 總和, true == !假];`]
];

for (const [language, source] of programs) test(`${language} syntax executes in the browser Wasm engine`, async () => {
  const {vm, index} = await runtime(source);
  assert.equal(inspect(await vm.execute(index)), '[720, -1, 0, 1, 3, 真]');
});

test('mixed function syntax preserves shared aliases, captures and English elif branches', async () => {
  const {vm, index} = await runtime(`
    var 建立, make = 函式(n) { return function(step) { n += step; 回傳 n; }; };
    變數 count = make(40);
    var branch = function(n) { if (false) { 99; } elif (n == 0) { 0; } 否則如果 (n == 1) { 1; } else { 2; } };
    [建立 == make, count(1), count(2), branch(0), branch(1), branch(5)];`);
  assert.equal(inspect(await vm.execute(index)), '[真, 41, 43, 0, 1, 2]');
});

test('English and Chinese modules call each other in one source-free Wasm', async () => {
  const bundle = compile({
    'main.aqua': 'var en=import("english.aqua");變數 中=匯入("中文.aqua");[en.add(20,22),中.加法(1,2)];',
    'english.aqua': 'var add = function(a,b) { return a+b; };',
    '中文.aqua': '變數 加法 = 函式(甲,乙) { 回傳 甲+乙; };'
  });
  const host = Object.assign(Object.create(CapabilityHost.prototype), {
    bundle, builtins: new Map(), modules: new Map(), cache: new Map(), importing: new Set()
  });
  host.runtime = new WasmRuntime(host);
  try { assert.equal(inspect(await host.execute('main.aqua')), '[42, 3]'); }
  finally { await host.runtime.dispose(); }
});
