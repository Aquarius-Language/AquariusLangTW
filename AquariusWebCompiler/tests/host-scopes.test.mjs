import test from 'node:test';
import assert from 'node:assert/strict';
import {runtime} from './wasm-fixture.mjs';
import {Scope,num,inspect} from '../browser/values.mjs';

test('a host scope is a live view of its guest scope during and after execution, and plain after dispose',async()=>{
  const env=new Scope();env.create('x',num(1,'int'));
  const peek=()=>env.get('x'),poke=()=>{env.create('y',num(7,'int'));return null;};
  const {vm,index}=await runtime('x=5;變數 seen=peek();poke();[seen,y];',new Map([['peek',peek],['poke',poke]]));
  assert.equal(inspect(await vm.execute(index,env)),'[5, 7]');
  assert.equal(inspect(env.get('x')),'5');assert.deepEqual([...env.owned].sort(),['seen','x','y']);
  await vm.dispose();
  assert.ok(env.store instanceof Map);assert.equal(Object.getPrototypeOf(env.store),Map.prototype);
  assert.equal(inspect(env.get('y')),'7');env.create('z',num(1,'int'));assert.equal(inspect(env.get('z')),'1');
});

test('global native calls do not copy every global binding',async()=>{
  const declarations=Array.from({length:100},(_,i)=>`變數 g${i}=${i};`).join('');
  const {vm,index}=await runtime(`${declarations}迴圈(變數 i=0;i<1000;i++){tick();}g99;`,new Map([['tick',()=>null]]));
  await vm.ready();const session=vm.program.session,call=session.call.bind(session);let reads=0;
  session.call=(name,...args)=>{if(name==='aqua_scope_binding')reads++;return call(name,...args);};
  assert.equal(inspect(await vm.execute(index,new Scope())),'99');
  assert.ok(reads<100,`${reads} binding reads for 1000 native calls`);
});
