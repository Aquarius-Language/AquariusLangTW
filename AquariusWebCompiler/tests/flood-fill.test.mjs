import test from 'node:test';
import assert from 'node:assert/strict';
import {runtime} from './wasm-fixture.mjs';
import {Scope,num,inspect,portableBuiltin} from '../browser/values.mjs';
const fill=()=>{throw new Error('Fill algorithm crossed the host boundary');};fill[portableBuiltin]=5;
const builtins=new Map([['floodFill',fill],['區域填色',fill]]);

test('portable fill aliases preserve exact unsigned ARGB and four-connected boundaries',async()=>{
  const {vm,index}=await runtime('變數 a=[0,1,0,0,1,0,1,0,0];變數 n=區域填色(a,3,3,0,0,4294967295.0d);[n,a,floodFill(a,3,3,0,0,4294967295.0d)];',builtins);
  assert.equal(inspect(await vm.execute(index)),'[2, [4294967295, 1, 0, 4294967295, 1, 0, 1, 0, 0], 0]');
});

test('fill retains aliases and detaches shared push storage',async()=>{
  const push=()=>{throw Error('Host push called');};push[portableBuiltin]=4;
  const {vm,index}=await runtime('變數 a=[0,0,0,0];變數 alias=a;變數 snapshot=push(a,9);floodFill(a,2,2,1,1,7);[alias,snapshot];',new Map([...builtins,['push',push]]));
  assert.equal(inspect(await vm.execute(index)),'[[7, 7, 7, 7], [0, 0, 0, 0, 9]]');
});

test('scanline fill agrees with a reference traversal over holes and branching runs',async()=>{
  const {vm,index}=await runtime('floodFill(pixels,31,23,x,y,99);',builtins);
  let seed=12345;const random=()=>{seed=(Math.imul(seed,1664525)+1013904223)>>>0;return seed;};
  for(let sample=0;sample<40;sample++){
    const source=Array.from({length:31*23},()=>random()%5===0?1:0),x=random()%31,y=random()%23,expected=[...source],old=expected[y*31+x],queue=[y*31+x];let count=0;
    expected[queue[0]]=99;
    while(queue.length){const i=queue.pop();count++;for(const j of [i%31?i-1:-1,i%31<30?i+1:-1,i-31,i+31])if(j>=0&&j<expected.length&&expected[j]===old){expected[j]=99;queue.push(j);}}
    const pixels=source.map(v=>num(v,'int')),env=new Scope();env.create('pixels',pixels);env.create('x',num(x,'int'));env.create('y',num(y,'int'));
    assert.equal((await vm.execute(index,env)).value,count);assert.deepEqual(pixels.map(v=>v.value),expected);
  }
});

for(const args of ['a,2,3,0,0,7','a,2,2,-1,0,7','a,2,2,2,0,7','a,2.0d,2,0,0,7','a,2,2,0,0,"bad"','[0,0,"bad",0],2,2,0,0,7'])test('invalid fill is rejected before mutation: '+args,async()=>{
  const env=new Scope(),a=[0,0,0,0].map(v=>num(v,'int'));env.create('a',a);
  const {vm,index}=await runtime(`floodFill(${args});`,builtins);await assert.rejects(()=>vm.execute(index,env),/floodFill/);assert.deepEqual(a.map(v=>v.value),[0,0,0,0]);
});

test('large fill uses no host loop or full-size work array and updates retained pixels',async()=>{
  const pixels=Array.from({length:1024*1024},()=>num(0,'int')),env=new Scope();env.create('pixels',pixels);
  const {vm,index}=await runtime('floodFill(pixels,1024,1024,1023,1023,42);',builtins);
  assert.equal((await vm.execute(index,env)).value,pixels.length);assert.ok(pixels.every(v=>v.value===42));
});
