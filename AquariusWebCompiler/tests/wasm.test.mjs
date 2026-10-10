import test from 'node:test';
import assert from 'node:assert/strict';
import {runtime,compile} from './wasm-fixture.mjs';
import {Scope,module,num,inspect,nativeCall,nativeOwner,portableBuiltin} from '../browser/values.mjs';
import {WasmRuntime} from '../browser/wasm.mjs';

test('compiler emits native Wasm exports and metadata without instructions',()=>{
  const bundle=compile({'main.aqua':'42;'}),exports=WebAssembly.Module.exports(bundle.compiledModule);
  assert.ok(exports.some(e=>e.name==='aqua_f0'));
  assert.deepEqual(WebAssembly.Module.imports(bundle.compiledModule),[{module:'aquarius_v2',name:'service',kind:'function'}]);
  assert.ok(exports.some(e=>e.name==='memory'&&e.kind==='memory'));
  const text=new TextDecoder().decode(WebAssembly.Module.customSections(bundle.compiledModule,'aquarius.application')[0]);
  assert.equal(JSON.parse(text).abiVersion,2);assert.ok(!text.includes('"code"'));
});
test('portable builtin aliases and recursive computation never call host implementations',async()=>{
  const len=()=>{throw Error('host length called');},push=()=>{throw Error('host push called');};len[portableBuiltin]=1;push[portableBuiltin]=4;
  const {vm,index}=await runtime('變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};變數 size=len;變數 a=push([f(10000)],42);[a[0],a[1],size(a)];',new Map([['len',len],['push',push]]));
  assert.equal(inspect(await vm.execute(index)),'[10000, 42, 2]');
});
test('shared array storage preserves push snapshots, aliases and native writes',async()=>{
  const push=()=>{throw Error('host push used');};push[portableBuiltin]=4;
  const edit=a=>{a[0]=num(7,'int');};
  const {vm,index}=await runtime('變數 a=[1];變數 alias=a;變數 b=push(a,2);變數 c=push(a,3);a[0]=9;變數 d=push(b,4);b[0]=8;edit(c);[a,alias,b,c,d];',new Map([['push',push],['edit',edit]]));
  assert.equal(inspect(await vm.execute(index)),'[[9], [9], [8, 2], [7, 3], [1, 2, 4]]');
});
test('typed NaN hash keys and signed zero survive lookup',async()=>{
  const {vm,index}=await runtime('變數 d=0.0d/0.0d;變數 f=0.0f/0.0f;變數 h={d:3,f:4,-0.0d:7};[h[d],h[f],h[0.0d],d==d];');
  assert.equal(inspect(await vm.execute(index)),'[3, 4, 7, 假]');
});
test('guest edits reach a host retained array even when the next call has no arguments',async()=>{
  const pixels=[num(0,'int')],env=new Scope();env.create('pixels',pixels);
  const {vm,index}=await runtime('pixels[0]=42;check();',new Map([['check',()=>pixels[0]]]));assert.equal(inspect(await vm.execute(index,env)),'42');
});
test('cyclic arrays survive capability transport',async()=>{
  const cycle=[],env=new Scope();cycle.push(cycle);env.create('cycle',cycle);
  const {vm,index}=await runtime('check(cycle);',new Map([['check',value=>{assert.equal(value,cycle);assert.equal(value[0],value);return num(42,'int');}]]));
  assert.equal(inspect(await vm.execute(index,env)),'42');
});
test('pixel buffers transfer to their owner instead of every scalar capability',async()=>{
  const pixels=Array.from({length:1000},()=>num(0,'int')),image=module(new Scope()),math=module(new Scope()),env=new Scope();image.scope.create('pixels',pixels);
  const update=()=>pixels.at(-1),scalar=value=>value;update[nativeOwner]=image;scalar[nativeOwner]=math;image.scope.create('update',update);math.scope.create('scalar',scalar);env.create('image',image);env.create('math',math);
  const {vm,index}=await runtime('變數 pixels=image.pixels;迴圈(變數 i=0;i<1000;i++){pixels[i]=math.scalar(i);};image.update();');
  const session=vm.program.session,call=session.call.bind(session);let transfers=0;session.call=(name,...args)=>{if(name==='aqua_array_values')transfers++;return call(name,...args);};
  assert.equal(inspect(await vm.execute(index,env)),'999');assert.ok(transfers<=10,`${transfers} full buffer transfers`);
});
test('native array resize and hash mutation preserve guest aliases',async()=>{
  const edit=(array,hash)=>{array.push(num(42,'int'));hash.set('string:new',['new',num(99,'int')]);};
  const {vm,index}=await runtime('變數 a=[0];變數 alias=a;變數 h={"old":1};edit(a,h);[alias[1],h["new"]];',new Map([['edit',edit]]));
  assert.equal(inspect(await vm.execute(index)),'[42, 99]');
});
test('lexical bindings shadow builtins even when undefined',async()=>{
  const {vm,index}=await runtime('shadow;',new Map([['shadow',num(99,'int')]]));
  for(const value of [undefined,null,false,num(0,'int')]){const root=new Scope();root.create('shadow',value);assert.deepEqual(await vm.execute(index,new Scope(root)),value);}
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
test('callback reentry preserves independent Wasm execution frames and operand values',async()=>{
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
