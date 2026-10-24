import test from 'node:test';
import assert from 'node:assert/strict';
import {runtime} from './wasm-fixture.mjs';
import {Scope,num,inspect,module,nativeCall,plainArguments} from '../browser/values.mjs';

// The guest caches members of host modules per host era; any host binding change must be seen by the next read.
test('host module members are re-read after the host changes them',async()=>{
  const lib=new Scope();let n=0;
  lib.create('value',num(0,'int'));
  lib.create('bump',()=>{n++;lib.set('value',num(n,'int'));return null;});
  lib.create('f',()=>num(1,'int'));
  lib.create('swap',()=>{lib.set('f',()=>num(2,'int'));return null;});
  const env=new Scope();env.create('m',module(lib));
  const {vm,index}=await runtime('變數 total=0;迴圈(變數 i=0;i<5;i++){total+=m.value;m.bump();}變數 a=m.f();m.swap();[total,a,m.f(),m.value];',new Map());
  assert.equal(inspect(await vm.execute(index,env)),'[10, 1, 2, 5]');
});

test('repeated host member calls resolve the member once and do not probe empty runtime lists',async()=>{
  const lib=new Scope();lib.create('tick',()=>null);
  const env=new Scope();env.create('m',module(lib));
  const {vm,index}=await runtime('迴圈(變數 i=0;i<1000;i++){m.tick();}1;',new Map());
  await vm.ready();const session=vm.program.session,call=session.call.bind(session),core=session.serviceCore.bind(session);
  let lookups=0,probes=0;
  session.serviceCore=(operation,...rest)=>{if(operation===2)lookups++;return core(operation,...rest);};
  session.call=(name,...args)=>{if(/^aqua_take_/.test(name))probes++;return call(name,...args);};
  assert.equal(inspect(await vm.execute(index,env)),'1');
  assert.ok(lookups<=2,`${lookups} host member lookups for 1000 calls`);
  assert.ok(probes<20,`${probes} runtime list probes for 1000 calls`);
});

test('bindings that take plain values receive guest numbers and arrays without boxing',async()=>{
  const seen=[];const entry=(...a)=>{throw new Error('boxed entry used');};
  entry[plainArguments]=a=>{seen.push(a);return num(a[0]+a[1].length,'int');};
  const fn=async()=>{};fn[nativeCall]=entry;
  const lib=new Scope();lib.create('f',fn);const env=new Scope();env.create('m',module(lib));
  const {vm,index}=await runtime('m.f(2.5d,[1,[2,3]],"x",真);',new Map());
  assert.equal(inspect(await vm.execute(index,env)),'4');
  assert.deepEqual(seen,[[2.5,[1,[2,3]],'x',true]]);
});