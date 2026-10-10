import test from 'node:test';
import assert from 'node:assert/strict';
import {Scope,VirtualMachine,num,nativeCall} from '../browser/vm.mjs';
import {BrowserHost} from '../browser/host.mjs';

const callProgram=(count=0)=>({version:1,pool:[{type:'name',value:'fn'},...Array.from({length:count},(_,i)=>({type:'int',value:i+1}))],code:[['Load',0],...Array.from({length:count},(_,i)=>['Constant',i+1]),['Call',count]]});
const bindingHost=()=>({signal:new AbortController().signal,bundle:{catalog:[{library:'Test',english:'fn',chinese:'函式值',min:0,max:16}]}});

test('synchronous native calls finish in one turn without implicit microtask yields',async()=>{
  const events=[];
  const env=new Scope();env.create('fn',()=>{events.push('call');queueMicrotask(()=>events.push('microtask'));return num(42,'int');});
  const vm=new VirtualMachine({builtins:new Map()});
  const program=callProgram();program.code.push(['Pop'],['Load',0],['Call',0]);
  const result=vm.execute(program,env);
  assert.deepEqual(events,['call','call']);
  assert.equal((await result).value,42);
  assert.deepEqual(events,['call','call','microtask','microtask']);
});

for(const count of [0,1,2,3,4,5,6,16])test(`native argument order and stack results for arity ${count}`,async()=>{
  const fn=(...args)=>{assert.deepEqual(args.map(n=>n.value),Array.from({length:count},(_,i)=>i+1));return num(42,'int');};
  assert.equal((await new VirtualMachine({builtins:new Map([['fn',fn]])}).execute(callProgram(count))).value,42);
});

test('host bindings keep public Promise behavior and aliases while providing direct VM calls',async()=>{
  const host=bindingHost(),m={scope:new Scope()};
  const fn=BrowserHost.prototype.bind.call(host,m,'Test','fn',value=>value+1);
  assert.equal(m.scope.get('函式值'),fn);
  assert.equal(fn[nativeCall](num(41,'int')).value,42);
  assert.equal((await fn(num(41,'int'))).value,42);
  assert.ok(fn(num(1,'int')) instanceof Promise);
  assert.equal((await new VirtualMachine({builtins:new Map([['fn',fn]])}).execute(callProgram(1))).value,2);
});

test('raw host results retain identity for direct and public calls',async()=>{
  const host=bindingHost(),m={scope:new Scope()},value=[num(42,'int')];
  const fn=BrowserHost.prototype.bind.call(host,m,'Test','fn',arg=>arg,true);
  assert.equal(fn[nativeCall](value),value);assert.equal(await fn(value),value);
});

test('asynchronous host bindings and thenables suspend, wrap results, and propagate rejection',async()=>{
  const host=bindingHost(),m={scope:new Scope()};
  for(const factory of [()=>Promise.resolve(42),()=>({then:resolve=>resolve(42)})]){
    const fn=BrowserHost.prototype.bind.call(host,m,'Test','fn',factory);
    assert.equal((await new VirtualMachine({builtins:new Map([['fn',fn]])}).execute(callProgram())).value,42);
  }
  const failure=new Error('rejected');
  const fn=BrowserHost.prototype.bind.call(host,m,'Test','fn',()=>Promise.reject(failure));
  await assert.rejects(()=>new VirtualMachine({builtins:new Map([['fn',fn]])}).execute(callProgram()),error=>error===failure);
});

test('direct synchronous errors and arity validation still reject public and VM calls',async()=>{
  const host=bindingHost(),m={scope:new Scope()},failure=new Error('host failure');
  const fn=BrowserHost.prototype.bind.call(host,m,'Test','fn',()=>{throw failure;});
  await assert.rejects(()=>fn(),error=>error===failure);
  await assert.rejects(()=>new VirtualMachine({builtins:new Map([['fn',fn]])}).execute(callProgram()),error=>error===failure);
  await assert.rejects(()=>fn(...Array(17).fill(null)),/Expected 0..16/);
});

test('closure arguments are bound directly and nested calls preserve stack and capture state',async()=>{
  const root=new Scope();root.create('offset',num(10,'int'));
  const fn={type:'closure',env:root,builtins:new Map(),parameters:['a','b'],program:{version:1,pool:[{type:'name',value:'a'},{type:'name',value:'b'},{type:'name',value:'offset'}],code:[['Load',0],['Load',1],['Add'],['Load',2],['Add']]}};
  root.create('fn',fn);
  const vm=new VirtualMachine({builtins:new Map()});
  assert.equal((await vm.execute(callProgram(2),root)).value,13);
  assert.equal((await vm.invoke(fn,[num(20,'int'),num(12,'int')])).value,42);
  await assert.rejects(()=>vm.execute(callProgram(1),root),/expects 2 arguments, got 1/);
});

test('asynchronous native callback reentry keeps outer arguments and operand stack intact',async()=>{
  const host={builtins:new Map()},vm=new VirtualMachine(host);
  const fn=(...args)=>vm.invoke(args[0],[args[1]]);
  const env=new Scope();
  const closure={type:'closure',env,builtins:host.builtins,parameters:['n'],program:{version:1,pool:[{type:'name',value:'n'}],code:[['Load',0]]}};
  env.create('fn',fn);env.create('callback',closure);
  const program={version:1,pool:[{type:'name',value:'fn'},{type:'name',value:'callback'},{type:'int',value:42}],code:[['Load',0],['Load',1],['Constant',2],['Call',2]]};
  assert.equal((await vm.execute(program,env)).value,42);
});

test('cancellation remains observable after suspension and synchronous instruction budgets yield',async()=>{
  const controller=new AbortController();
  const host={signal:controller.signal,builtins:new Map([['fn',async()=>{controller.abort();return num(42,'int');}]])};
  const vm=new VirtualMachine(host);
  await assert.rejects(()=>vm.execute(callProgram()),/cancelled/);
  // The browser-only safepoint remains active even with no Promise-producing calls.
  const previous=globalThis.window;globalThis.window={};
  try{
    const looping=new VirtualMachine({builtins:new Map()});
    const result=looping.execute({version:1,pool:[],code:[['Jump',0]]});
    assert.equal(looping.instructions,20000);looping.cancelled=true;
    await assert.rejects(()=>result,/cancelled/);
  }finally{if(previous===undefined)delete globalThis.window;else globalThis.window=previous;}
});
