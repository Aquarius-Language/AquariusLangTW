import test from 'node:test';
import assert from 'node:assert/strict';
import {runtime} from './wasm-fixture.mjs';
import {inspect,num} from '../browser/values.mjs';

test('guarded binding slots preserve branches, duplicate parameters and loop captures',async()=>{
  const {vm,index}=await runtime('變數 f=函式(a,a,b){如果(b<0){變數 skipped=9;}變數 x=a+b;回傳 x;};變數 out=[0,0,0];迴圈(變數 i=0;i<3;i++){變數 x=i;out[i]=函式(){x++;x;};};[f(1,2,40),out[0](),out[1](),out[2]()];');
  assert.equal(inspect(await vm.execute(index)),'[42, 1, 2, 3]');
});

test('numeric update fast paths preserve integer conversion and prefix/postfix values',async()=>{
  const {vm,index}=await runtime('變數 n=2147483647;變數 first=n++;變數 x=1;x+=1.75d;變數 q=7;q/=(-2);[first,n,x,q,++x,x++];');
  assert.equal(inspect(await vm.execute(index)),'[2147483647, -2147483648, 2, -3, 3, 3]');
});

test('array read fast paths preserve snapshots, aliases, null bounds and hash/string fallback',async()=>{
  const {vm,index}=await runtime('變數 a=[4,5];變數 b=a;變數 h={"值":42};a[0]=7;[b[0],a[-1],a[2],h["值"],"繁體"[1]];');
  assert.equal(inspect(await vm.execute(index)),'[7, null, null, 42, 體]');
});

test('asynchronous RHS and callback reentry retain compound-assignment behavior',async()=>{
  let vm;const reenter=async callback=>{await Promise.resolve();await vm.invoke(callback,[]);return num(2,'int');};
  const r=await runtime('變數 x=1;x+=reenter(函式(){x=10;});x;',new Map([['reenter',reenter]]));vm=r.vm;
  assert.equal(inspect(await vm.execute(r.index)),'12');
});

test('optimized scalar loops still yield and observe browser cancellation',async()=>{
  const {vm,index}=await runtime('變數 x=0;迴圈(變數 i=0;i<100000000;i++){x+=i;}x;');
  const previous=globalThis.window;globalThis.window={};
  try {
    const running=vm.execute(index);setTimeout(()=>{vm.cancelled=true;},0);
    await assert.rejects(running,/cancel/i);
  } finally {globalThis.window=previous;}
});
