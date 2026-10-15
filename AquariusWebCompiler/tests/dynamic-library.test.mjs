import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {createWasmLibrary,createJavaScriptLibrary,validateSymbol,MAX_BYTES} from '../../AquariusCore/hosting/dynamic-library-core.mjs';
import {CapabilityHost} from '../../AquariusCore/hosting/capability-host.mjs';
// Deployment copies the shared core beside the Android adapter. Resolve that
// same import explicitly when testing the source template before packaging.
const androidAdapter = readFileSync(new URL('../../AquariusAndroid/template/app/src/main/assets/aquarius/android-dynamic-library.mjs',import.meta.url),'utf8')
  .replace("'./dynamic-library-core.mjs'",JSON.stringify(new URL('../../AquariusCore/hosting/dynamic-library-core.mjs',import.meta.url).href));
const {registerAndroidDynamicLibrary} = await import('data:text/javascript;base64,'+Buffer.from(androidAdapter).toString('base64'));
const fixture = readFileSync(new URL('../../AquariusTests/linking/fixtures/showcase.wasm', import.meta.url));

for (const extension of ['mjs','js']) test(`Android ${extension} modules await native evaluation and retain independent factory state`, async () => {
  const module = {}, file = `asset-${'a'.repeat(64)}.${extension}`;
  let imported = 0, created = 0, released = 0, ready;
  const host = {
    modules:new Map(), library:()=>module, newModule:()=>({}),
    set:(target,name,value)=>{target[name]=value;},
    bind:(target,group,name,fn)=>{target[name]=fn;}, modelAsset:()=>({asset:file})
  };
  const bridge = {importLibrary(name) {
    assert.equal(name,file); imported++;
    globalThis.__aquaImportedLibrary = {
      ready:new Promise(resolve=>{ready=resolve;}),
      namespace:{aqua_library_abi:()=>1,aqua_create_library:()=>{
        created++; let calls=0;
        return {counter:()=>new Uint8Array([++calls]),aqua_dispose(){released++;}};
      }}
    };
  }};
  registerAndroidDynamicLibrary(host,bridge);
  const loading=module.Load('/bottles/example/library.'+extension);
  await Promise.resolve(); assert.equal(created,0); assert.equal(imported,1);
  assert.equal(Object.hasOwn(globalThis,'__aquaImportedLibrary'),false);
  ready(); const first=await loading, second=await module.Load('/bottles/example/library.'+extension);
  assert.equal(imported,1); assert.equal(created,2);
  assert.equal((await first.Invoke('counter',[]))[0].value,1);
  assert.equal((await first.Invoke('counter',[]))[0].value,2);
  assert.equal((await second.Invoke('counter',[]))[0].value,1);
  await first.Dispose(); assert.equal(released,1);
  await host.dynamicLibraries.dispose(); assert.equal(released,2);
});

test('Android refuses unpackaged module paths before native import', async () => {
  const module={},host={modules:new Map(),library:()=>module,newModule:()=>({}),set:(o,k,v)=>{o[k]=v;},bind:(o,g,k,v)=>{o[k]=v;},modelAsset:()=>({asset:'../library.mjs'})};
  registerAndroidDynamicLibrary(host,{importLibrary(){assert.fail('unexpected native import');}});
  await assert.rejects(module.Load('library.mjs'),/Invalid packaged module/);
});

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
  for (const name of ['webcam','camera','pose','onnx','audio','application','processing','graphics']) host[name] = {dispose(){released.push(name);}};
  await assert.rejects(CapabilityHost.prototype.dispose.call(host), /plugin cleanup failed/);
  assert.equal(host.runtime.cancelled,true);assert.equal(released.length,8);
});
