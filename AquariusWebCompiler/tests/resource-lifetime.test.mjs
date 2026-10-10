import test from 'node:test';
import assert from 'node:assert/strict';
import {runtime} from './wasm-fixture.mjs';
import {Scope,module,nativeOwner} from '../browser/values.mjs';
import {Processing} from '../browser/processing.mjs';

test('safe-point tracing retains suspended native arguments and callback locals',async()=>{
  const resource=module(new Scope()),env=new Scope();env.create('resource',resource);
  let vm,entered,release;
  const waiting=new Promise(r=>entered=r),gate=new Promise(r=>release=r);
  const hold=async(value,callback)=>{entered();await gate;return vm.invoke(callback,[value]);};
  const check=value=>{assert.ok(vm.reachable().has(value));return true;};
  ({vm}=await runtime('hold(resource,函式(local){check(local);});',new Map([['hold',hold],['check',check]])));
  const execution=vm.execute(vm.host.bundle.modules['main.aqua'],env);
  await waiting;env.set('resource',null);
  assert.ok(vm.reachable().has(resource));release();await execution;
  assert.equal(vm.reachable().has(resource),false);
});
test('extracted native methods retain their owner through cyclic containers',async()=>{
  const resource=module(new Scope()),fn=()=>true;fn[nativeOwner]=resource;
  const cycle=[];cycle.push(cycle,new Map([['method',fn]]));
  const {vm}=await runtime('真;');
  assert.ok(vm.reachable([cycle]).has(resource));
});
test('failed executions release their roots',async()=>{
  const {vm,index}=await runtime('fail();',new Map([['fail',async()=>{throw Error('denied');}]]));
  await assert.rejects(()=>vm.execute(index),/denied/);
  assert.equal(vm.executions.size,0);assert.equal(vm.nativeRoots.size,0);
});
test('registered event error handler recovers drawing and propagates cancellation',async()=>{
  const processing=Object.create(Processing.prototype),controller=new AbortController();
  const canvas={drawing:true,vertices:[],target:{flush(){}}},fields={},handled=[];
  processing.canvases=new Set([canvas]);
  processing.events=new Map([['mousePressed',()=>{throw Error('permission denied');}],['error',()=>handled.push(fields.errorEvent)]]);
  processing.host={signal:controller.signal,runtime:{invoke:async fn=>fn()},set:(_,name,value)=>fields[name]=value};
  await processing.dispatchEvent('mousePressed');
  assert.deepEqual(handled,['mousePressed']);assert.equal(fields.errorMessage,'permission denied');assert.equal(canvas.drawing,false);
  controller.abort();await assert.rejects(()=>processing.dispatchEvent('mousePressed'),/permission denied/);
  assert.equal(handled.length,1);
});
