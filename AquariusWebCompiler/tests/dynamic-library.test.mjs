import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {createWasmLibrary,createJavaScriptLibrary,validateSymbol,MAX_BYTES} from '../../AquariusCore/hosting/dynamic-library-core.mjs';
import {CapabilityHost} from '../../AquariusCore/hosting/capability-host.mjs';
const fixture = readFileSync(new URL('../../AquariusTests/linking/fixtures/showcase.wasm', import.meta.url));

test('real Wasm byte ABI, UTF-8, independent state, maximum buffers and release', async () => {
  const first = await createWasmLibrary(fixture), second = await createWasmLibrary(fixture);
  const input = new TextEncoder().encode('繁體中文 & English\0🌊');
  assert.deepEqual(first.invoke('echo', input), input);
  assert.deepEqual(first.invoke('echo', new Uint8Array()), new Uint8Array());
  assert.deepEqual([...first.invoke('sum', new Uint8Array([10,20,12]))], [42]);
  assert.deepEqual([...first.invoke('counter', input)], [1]); assert.deepEqual([...first.invoke('counter', input)], [2]);
  assert.deepEqual([...second.invoke('counter', input)], [1]);
  assert.equal(first.hasSymbol('missing'), false);
  for (const symbol of ['failure','oversized','missing']) assert.throws(() => first.invoke(symbol, input));
  const maximum = new Uint8Array(MAX_BYTES); maximum[maximum.length - 1] = 255;
  for (let i = 0; i < 3; i++) assert.deepEqual(first.invoke('echo', maximum), maximum);
  assert.throws(() => first.invoke('echo', new Uint8Array(MAX_BYTES + 1)));
  first.dispose(); first.dispose(); assert.throws(() => first.hasSymbol('echo'), /closed/); second.dispose();
});
test('JavaScript factory, copied ownership, asynchronous calls and cleanup', async () => {
  let released = 0;
  const namespace = {aqua_library_abi:()=>1,aqua_create_library:()=>({echo:async input=>input,aqua_dispose(){released++;}})};
  const first = createJavaScriptLibrary(namespace), second = createJavaScriptLibrary(namespace);
  const input = new Uint8Array([0,255]); const output = await first.invoke('echo', input); output[0] = 42; assert.equal(input[0],0);
  assert.equal(first.hasSymbol('toString'),false); await assert.rejects(first.invoke('missing',input));
  await first.dispose(); await first.dispose(); assert.equal(released,1); await assert.rejects(first.invoke('echo',input), /closed/);
  await second.dispose(); assert.equal(released,2);
  assert.throws(() => createJavaScriptLibrary({...namespace,aqua_library_abi:()=>99}), /ABI/);
  for (const name of ['', 'aqua_library_abi', '../echo', 'e\0cho', 'a'.repeat(129)]) assert.throws(() => validateSymbol(name));
});
test('JavaScript disposal waits for calls already in progress', async () => {
  let finish, released = false;
  const library = createJavaScriptLibrary({aqua_library_abi:()=>1,aqua_create_library:()=>({echo:()=>new Promise(resolve=>{finish=resolve;}),aqua_dispose(){released=true;}})});
  const call = library.invoke('echo',new Uint8Array([42])); await Promise.resolve();
  const dispose = library.dispose(); assert.equal(released,false); finish(new Uint8Array([42]));
  assert.deepEqual([...await call],[42]); await dispose; assert.equal(released,true);
});
test('a failing plugin cleanup still releases application and platform resources', async () => {
  const released = [], host = {runtime:{},dynamicLibraries:{dispose(){throw new Error('plugin cleanup failed');}}};
  for (const name of ['webcam','camera','pose','onnx','audio','application','processing','physics','graphics']) host[name] = {dispose(){released.push(name);}};
  await assert.rejects(CapabilityHost.prototype.dispose.call(host), /plugin cleanup failed/);
  assert.equal(host.runtime.cancelled,true);assert.equal(released.length,9);
});
