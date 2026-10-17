import test from 'node:test';
import assert from 'node:assert/strict';
import {runtime} from './wasm-fixture.mjs';
import {num,inspect} from '../browser/values.mjs';

test('host memory views are reused and refreshed after guest growth',async()=>{
  const {vm}=await runtime('42;');
  const session=vm.program.session,first=session.view();
  assert.strictEqual(session.view(),first);
  session.exports.memory.grow(1);
  const next=session.view();
  assert.notStrictEqual(next,first);
  assert.strictEqual(session.view(),next);
  const address=next.byteLength-16;
  session.raw(address,1,0,42);
  assert.equal(inspect(session.read(address)),'42');
});

test('closed scalar helpers and recursion execute the portable typed ABI',async()=>{
  const {vm,index}=await runtime('變數 fib=函式(n){如果(n<2){回傳 n;}回傳 fib(n-1)+fib(n-2);};fib(20);');
  assert.equal(inspect(await vm.execute(index)),'6765');
  assert.ok(vm.program.session.call('aqua_numeric_fast_calls')>0);
});

test('native integer registers preserve checked arithmetic on another Wasm engine',async()=>{
  const {vm,index}=await runtime('變數 add=函式(a,b){a+b;};變數 mul=函式(a,b){a*b;};變數 div=函式(a,b){a/b;};變數 choose=函式(a){如果(a<0&&a>=-7){回傳 -a;}回傳 a/2;};[add(2147483647,1),mul(65537,65537),div(-2147483647-1,-1),div(1,0),div(7,-2),choose(-7)];');
  assert.equal(inspect(await vm.execute(index)),'[-2147483648, -2147483648, -2147483648, -2147483648, -3, 7]');
  assert.ok(vm.program.session.call('aqua_numeric_fast_calls')>=6);
});

test('portable scalar guards retain promotion, overflow and dynamic fallback',async()=>{
  const {vm,index}=await runtime('變數 f=函式(a,b){a+b;};[f(2147483647,1),f(1.0f,0.1d),f("繁","體"),f(真,1)];');
  // The final invalid addition must retain the ordinary language error.
  await assert.rejects(vm.execute(index),/Type mismatch/);
  assert.ok(vm.program.session.call('aqua_numeric_fallback_calls')>0);
});

test('recursive binding replacement is observed by previously retained closures',async()=>{
  const {vm,index}=await runtime('變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};變數 old=f;f=函式(n){100;};old(3);');
  assert.equal(inspect(await vm.execute(index)),'101');
});

test('portable depth guard preserves stackless recursion',async()=>{
  const {vm,index}=await runtime('變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};f(20000);');
  assert.equal(inspect(await vm.execute(index)),'20000');
  // The depth bound deoptimizes native activations into generic frames instead of restarting.
  assert.equal(vm.program.session.call('aqua_numeric_depth_fallbacks'),0);
  assert.ok(vm.program.session.call('aqua_numeric_deopts')>0);
});

test('fuel exhaustion yields before pure scalar work resumes on the generic path',async()=>{
  const {vm,index}=await runtime('變數 fib=函式(n){如果(n<2){回傳 n;}回傳 fib(n-1)+fib(n-2);};fib(18);');
  const session=vm.program.session,call=session.call.bind(session);let slices=0;
  session.call=(name,...args)=>{if(name==='aqua_run'){args[1]=32;slices++;}return call(name,...args);};
  assert.equal(inspect(await vm.execute(index)),'2584');
  // Exhausted fuel deoptimizes into the generic body, which then yields at its own checkpoint.
  assert.ok(slices>10);assert.ok(session.call('aqua_numeric_deopts')>0);
});

test('capability effects are excluded from restartable numeric execution',async()=>{
  let count=0;
  const {vm,index}=await runtime('變數 f=函式(n){effect()+n;};f(2);',new Map([['effect',()=>num(++count,'int')]]));
  assert.equal(inspect(await vm.execute(index)),'3');assert.equal(count,1);
});

test('captured inner-scope shadows defeat ancestor slot guards',async()=>{
  const {vm,index}=await runtime('變數 x=1;變數 total=0;迴圈(變數 i=0;i<2;i++){edit(函式(){i;});total+=x;};[total,x];',
    new Map([['edit',fn=>{fn.env.create('x',num(99,'int'));return num(0,'int');}]]));
  assert.equal(inspect(await vm.execute(index)),'[198, 1]');
});
