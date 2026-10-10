import test from 'node:test';
import assert from 'node:assert/strict';
import {runtime,compile} from './wasm-fixture.mjs';
import {Scope,num,inspect,nativeCall} from '../browser/values.mjs';
import {WasmRuntime} from '../browser/wasm.mjs';

test('compiler emits native Wasm exports and metadata without instructions',()=>{
  const bundle=compile({'main.aqua':'42;'}),exports=WebAssembly.Module.exports(bundle.compiledModule);
  assert.ok(exports.some(e=>e.name==='aqua_f0'));
  const text=new TextDecoder().decode(WebAssembly.Module.customSections(bundle.compiledModule,'aquarius.application')[0]);
  assert.equal(JSON.parse(text).abiVersion,1);assert.ok(!text.includes('"code"'));
});
test('lexical bindings shadow builtins even when undefined',async()=>{
  const {vm,index}=await runtime('shadow;',new Map([['shadow',num(99,'int')]]));
  for(const value of [undefined,null,false,num(0,'int')]){const root=new Scope();root.create('shadow',value);assert.equal(await vm.execute(index,new Scope(root)),value);}
});
test('closures retain captured scopes and deep recursion uses continuations',async()=>{
  const {vm,index}=await runtime('變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;}; f(20000);');
  assert.equal(inspect(await vm.execute(index)),'20000');
});
test('loop closures capture individual iterations and break restores scopes',async()=>{
  const {vm,index}=await runtime('變數 a=[0,0,0];迴圈(變數 i=0;i<3;i++){變數 n=i;a[i]=函式(){n;};}; [a[0](),a[1](),a[2]()];');
  assert.equal(inspect(await vm.execute(index)),'[0, 1, 2]');
});
for(const count of [0,1,2,3,4,5,6,16])test(`compiled host calls preserve argument order for arity ${count}`,async()=>{
  const fn=(...args)=>{assert.deepEqual(args.map(n=>n.value),Array.from({length:count},(_,i)=>i+1));return num(42,'int');};
  const {vm,index}=await runtime(`fn(${Array.from({length:count},(_,i)=>i+1).join(',')});`,new Map([['fn',fn]]));
  assert.equal(inspect(await vm.execute(index)),'42');
});
test('asynchronous calls and thenables resume compiled control flow',async()=>{
  for(const fn of [async()=>num(40,'int'),()=>({then:resolve=>resolve(num(40,'int'))})]){
    const {vm,index}=await runtime('fn()+2;',new Map([['fn',fn]]));assert.equal(inspect(await vm.execute(index)),'42');
  }
});
test('callback reentry owns its own Wasm instance and operand values',async()=>{
  let vm;const fn=(callback,n)=>vm.invoke(callback,[n]);
  const result=await runtime('變數 x=10;1+fn(函式(n){x++;n+x;},30);',new Map([['fn',fn]]));vm=result.vm;
  assert.equal(inspect(await vm.execute(result.index)),'42');
});
test('native failures stop before later side effects',async()=>{
  const failure=new Error('host failure'),env=new Scope();env.create('n',num(0,'int'));
  const {vm,index}=await runtime('fail();n++;',new Map([['fail',()=>Promise.reject(failure)]]));
  await assert.rejects(()=>vm.execute(index,env),e=>e===failure);assert.equal(env.get('n').value,0);
});
test('cancellation is checked after asynchronous host calls',async()=>{
  const controller=new AbortController(),fn=async()=>{controller.abort();return num(42,'int');};
  const {vm,index}=await runtime('fn();',new Map([['fn',fn]]),{signal:controller.signal});
  await assert.rejects(()=>vm.execute(index),/cancelled/);
});
test('native loop checkpoints keep browser cancellation responsive',async()=>{
  const {vm,index}=await runtime('變數 n=0;迴圈(變數 i=0;真;i++){n++;}');
  const old=globalThis.window;globalThis.window={};
  try {const result=vm.execute(index);vm.cancelled=true;await assert.rejects(()=>result,/cancelled/);}
  finally {if(old===undefined)delete globalThis.window;else globalThis.window=old;}
});
test('direct native entry is used without invoking its Promise wrapper',async()=>{
  const fn=async()=>{throw new Error('wrapper used');};fn[nativeCall]=()=>num(42,'int');
  const {vm,index}=await runtime('fn();',new Map([['fn',fn]]));assert.equal(inspect(await vm.execute(index)),'42');
});
test('unknown ABI and invalid Wasm are rejected',async()=>{
  const compiledModule=new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0]));
  await assert.rejects(()=>new WasmRuntime({bundle:{compiledModule}}).ready(),/metadata/);
  assert.throws(()=>new WebAssembly.Module(new Uint8Array([0,1,2,3])));
});
