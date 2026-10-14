import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {runtime} from './wasm-fixture.mjs';
import {Scope,num,inspect} from '../browser/values.mjs';
import {WasmRuntime} from '../browser/wasm.mjs';

test('scheduler slices avoid full traces without allocation pressure', async () => {
  const {vm,index}=await runtime('probe();變數 n=0;迴圈(變數 i=0;i<100000;i++){n++;}probe()+n;',new Map([['probe',()=>num(42,'int')]]));
  const s=vm.program.session,call=s.call.bind(s);let runs=0,collections=0;
  s.call=(name,...args)=>{if(name==='aqua_run'){runs++;args[1]=128;}if(name==='aqua_collect')collections++;return call(name,...args);};
  assert.equal(inspect(await vm.execute(index)),'100042');assert.ok(runs>10);assert.equal(collections,0);
  s.collect();assert.equal(collections,1);
});

test('released leases trigger tracing and live native capabilities survive', async () => {
  const {vm,index}=await runtime('probe();變數 n=0;迴圈(變數 i=0;i<20000;i++){n++;}probe();',new Map([['probe',()=>num(42,'int')]]));
  const s=vm.program.session,cell=s.call('aqua_value');s.raw(cell,1,0,99);
  s.leases.push({owner:{deref:()=>undefined},pin:s.call('aqua_pin',cell)});
  const before=s.call('aqua_collection_generation');assert.equal(inspect(await vm.execute(index)),'42');
  assert.notEqual(s.call('aqua_collection_generation'),before);
});

test('stale collection results cannot release native handles', async () => {
  const {vm}=await runtime('42;'),s=vm.program.session,call=s.call.bind(s);
  s.call=(name,...args)=>name==='aqua_collect'?0:call(name,...args);
  assert.throws(()=>s.collect(),/did not refresh native liveness/);
});

test('old ABI 2 programs retain eager scheduling collection', async () => {
  const compiledModule=new WebAssembly.Module(fs.readFileSync(new URL('../../AquariusTests/compiler/fixtures/legacy-performance-v2.wasm',import.meta.url)));
  const metadata=JSON.parse(new TextDecoder().decode(WebAssembly.Module.customSections(compiledModule,'aquarius.application')[0]));
  const vm=new WasmRuntime({bundle:{version:2,compiledModule,modules:metadata.modules,assets:{}},builtins:new Map([['probe',()=>num(42,'int')]])});
  await vm.ready();const s=vm.program.session,call=s.call.bind(s);let collections=0;
  s.call=(name,...args)=>{if(name==='aqua_run')args[1]=16;if(name==='aqua_collect')collections++;return call(name,...args);};
  assert.equal(s.deferredCollections,false);assert.equal(inspect(await vm.execute(0)),'499542');assert.ok(collections>0);
});

test('numeric bulk transfer preserves types, nonfinite values and scalar snapshots', async () => {
  const values=[num(-2147483648,'int'),num(0.1,'float'),num(NaN),num(Infinity),num(-0),...Array.from({length:4096},(_,i)=>num(i,'int'))];
  const env=new Scope();env.create('values',values);const saved=values[5];
  const {vm,index}=await runtime('values[5]=99;values;',new Map());const result=await vm.execute(index,env);
  assert.equal(result,values);assert.equal(saved.value,0);assert.equal(values[5].value,99);
  assert.equal(values[0].type,'int');assert.equal(values[1].type,'float');assert.equal(values[1].value,Math.fround(0.1));
  assert.ok(Number.isNaN(values[2].value));assert.equal(values[3].value,Infinity);assert.ok(Object.is(values[4].value,-0));
});

test('mixed and cyclic arrays remain safe when nested transport grows memory', async () => {
  const nested=Array.from({length:1100000},()=>num(7,'int')),values=[nested,num(42,'int')];values.push(values);
  const env=new Scope();env.create('values',values);
  const {vm,index}=await runtime('values[0][1099999]=9;[values[0][1099999],values[1],values[2]==values];');
  assert.equal(inspect(await vm.execute(index,env)),'[9, 42, 真]');assert.equal(values[0].at(-1).value,9);assert.equal(values[2],values);
});
