import test from 'node:test';
import assert from 'node:assert/strict';
import {resolvePath, lookupPath} from '../browser/package-paths.mjs';
import {BrowserHost} from '../browser/host.mjs';
import {Scope, num} from '../browser/values.mjs';
import {WasmRuntime} from '../browser/wasm.mjs';
import {compile} from './wasm-fixture.mjs';

for (const [name,current,expected] of [
  ['../main.aqua','lib/module.aqua','main.aqua'], ['/lib/工具.aqua','main.aqua','lib/工具.aqua'],
  ['..\\Main.aqua','lib/module.aqua','Main.aqua'], ['./a//b.txt','','a/b.txt'],
  ['../../main.aqua','lib/sub/module.aqua','main.aqua']
]) test(`package path ${name} from ${current}`,()=>assert.equal(resolvePath(name,current),expected));
for (const name of ['../x','a/../../x','C:/x','a:stream','a?.txt','a\x00.txt','NUL.txt','COM1','trailing.','trailing '])
  test(`reject unsafe package path ${JSON.stringify(name)}`,()=>assert.throws(()=>resolvePath(name)));
test('module lookup accepts either compiled module names and preserves canonical case',()=>{
  const files={'lib/工具.aqua':{},'Main.aqua':{}};
  assert.equal(lookupPath(files,'LIB/工具.AQUA',{module:true}),'lib/工具.aqua');
  assert.equal(lookupPath(files,'main.AQUA',{module:true}),'Main.aqua');
  assert.equal(lookupPath({'main.aqua':{}},'MAIN.aqua',{module:true}),'main.aqua');
});
test('missing modules and non-script imports fail explicitly',()=>{
  assert.throws(()=>lookupPath({},'missing.aqua',{module:true}),/not found/);
  assert.throws(()=>lookupPath({},'asset.txt',{module:true}),/require/);
});
test('case matching does not collapse Unicode expansions or non-ASCII letters into ASCII names',()=>{
  assert.equal(lookupPath({'straße.aqua':{},'strasse.aqua':{}},'STRASSE.aqua',{module:true}),'strasse.aqua');
  assert.throws(()=>lookupPath({'I.aqua':{}},'ı.aqua',{module:true}),/not found/);
  assert.throws(()=>lookupPath({'S.aqua':{}},'ſ.aqua',{module:true}),/not found/);
});
test('non-ASCII simple case pairs retain case-insensitive matching',()=>{
  assert.equal(lookupPath({'école.aqua':{}},'ÉCOLE.aqua',{module:true}),'école.aqua');
});
test('asset lookup supports empty content, Unicode and case',()=>{
  assert.equal(lookupPath({'Assets/空.txt':''},'assets/空.TXT'),'Assets/空.txt');
  assert.throws(()=>lookupPath({},'missing.txt'),/not found/);
});

// Exercise the production host's module/asset methods without constructing GPU or DOM services.
function host(modules,assets={}) {
  const h=Object.create(BrowserHost.prototype);
  Object.assign(h,{bundle:{modules,assets},modules:new Map(),cache:new Map(),importing:new Set(),builtins:new Map(),signal:new AbortController().signal});
  h.bundle={...(Object.keys(modules).length?compile(Object.fromEntries(Object.keys(modules).map(n=>[n,"變數 value=40;"])),Object.keys(modules)[0]):{}),assets};h.runtime=new WasmRuntime(h);return h;
}
const valueProgram='變數 value=40;';
test('repeated imports have independent module state like desktop',async()=>{
  const h=host({'module.aqua':valueProgram}),a=await h.globalImport('main.aqua')('module.aqua'),b=await h.globalImport('main.aqua')('MODULE.aqua');
  a.scope.set('value',num(42,'int'));assert.equal(b.scope.get('value').value,40);assert.notEqual(a,b);
});
test('nested imports resolve relative to their defining module',async()=>{
  const h=host({'lib/module.aqua':valueProgram});
  assert.equal((await h.globalImport('lib/main.aqua')('./MODULE.aqua')).scope.get('value').value,40);
});
test('circular imports fail and clean up state after errors',async()=>{
  const h=host({'main.aqua':valueProgram});h.importing.add('main.aqua');
  await assert.rejects(()=>h.globalImport('other.aqua')('MAIN.aqua'),/Circular/);
  h.importing.clear();h.runtime.execute=async()=>{throw new Error('runtime failure');};
  await assert.rejects(()=>h.globalImport('other.aqua')('main.aqua'),/runtime failure/);
  assert.equal(h.importing.size,0);
});
test('root entry participates in cycle detection and cleans up on failure',async()=>{
  const h=host({'main.aqua':valueProgram});
  h.runtime.execute=async()=>h.globalImport('main.aqua')('main.aqua');
  await assert.rejects(()=>h.execute('MAIN.aqua'),/Circular/);assert.equal(h.importing.size,0);
});
test('assets retain binary data and empty files and reject escapes',()=>{
  const h=host({}, {'assets/空.txt':'','bytes.bin':Buffer.from([0,255,1]).toString('base64')});
  assert.deepEqual([...h.asset('/ASSETS/空.txt')],[]);assert.deepEqual([...h.asset('bytes.bin')],[0,255,1]);
  assert.throws(()=>h.asset('../outside'),/escapes/);
});
