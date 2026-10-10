import test from 'node:test';
import assert from 'node:assert/strict';
import {compile} from './wasm-fixture.mjs';
import {validateManifest} from '../browser/wasm-manifest.mjs';

const module=compile({'main.aqua':'函式(x){x;};'}).compiledModule;
const original=JSON.parse(new TextDecoder().decode(WebAssembly.Module.customSections(module,'aquarius.application')[0]));
test('compiler metadata satisfies the shared browser ABI contract',()=>assert.equal(validateManifest(module,structuredClone(original)).valueAbi,2));
for(const [name,damage]of [
  ['retired ABI',m=>m.abiVersion=1],['value ABI',m=>m.valueAbi=1],['memory base',m=>m.programAddress=0],
  ['heap overlap',m=>m.heapStart=16],['heap mismatch',m=>m.heapStart++],['missing entry',m=>m.entry='missing.aqua'],
  ['invalid frame',m=>m.functions[0].stackCapacity=0],['invalid binding cache',m=>m.functions[0].cacheBindings='yes'],['invalid reference',m=>m.functions[0].pool[0].function=999],
  ['invalid constant',m=>m.functions[0].pool[0].type='unknown'],['unsafe asset',m=>m.assets['../file']=''],
  ['bad encoding',m=>m.assets['safe.txt']='!'],['case collision',m=>m.modules['MAIN.aqua']=0]
])test(`manifest rejects ${name}`,()=>{const metadata=structuredClone(original);damage(metadata);assert.throws(()=>validateManifest(module,metadata));});
