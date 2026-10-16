import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import fs from 'node:fs';
const source=fs.readFileSync(new URL('../template/app/src/main/assets/aquarius/wasmtime.js',import.meta.url),'utf8');
function runtime(){
  let id=0;const threads=new Map(),timers=[],calls=[];
  const api={compile(){return 7;},threadStart(module,snapshot,closure,args,count){calls.push({module,snapshot,closure,args,count});threads.set(++id,{done:false,cancelled:false});return id;},threadPoll(id){const t=threads.get(id);return t.done?t.result:undefined;},threadCancel(id){threads.get(id).cancelled=true;},threadClose(id){threads.delete(id);}};
  const context=vm.createContext({__wasmtime:api,TextDecoder,Uint8Array,ArrayBuffer,setTimeout:fn=>{timers.push(fn);return fn;},clearTimeout:fn=>{const i=timers.indexOf(fn);if(i>=0)timers.splice(i,1);}});vm.runInContext(source,context);
  return {context,threads,calls,pump(){timers.shift()?.();},run:s=>vm.runInContext(s,context)};
}
test('Android worker passes the compiled module and exact snapshot to native adapter',()=>{
  const r=runtime();r.run('globalThis.received=null;const module=new WebAssembly.Module(new Uint8Array([0]));globalThis.worker=WebAssembly.threadWorker({module,snapshot:new Uint8Array([1,2,3]).buffer,closure:42,arguments:80,count:2},value=>{received=value;});');
  assert.deepEqual({...r.calls[0],snapshot:[...r.calls[0].snapshot]},{module:7,snapshot:[1,2,3],closure:42,args:80,count:2});
  r.pump();assert.equal(r.run('received'),null);r.threads.get(1).done=true;r.threads.get(1).result={packet:new ArrayBuffer(4)};r.pump();assert.ok(r.run('received.packet') instanceof ArrayBuffer);
  r.run('worker.terminate();worker.terminate();');assert.equal(r.threads.size,0);
});
test('Android cancellation and termination stop polling and release native worker',()=>{
  const r=runtime();r.run('const module=new WebAssembly.Module(new Uint8Array([0]));globalThis.worker=WebAssembly.threadWorker({module,snapshot:new ArrayBuffer(0),closure:1,arguments:2,count:0},()=>{throw Error("late callback");});worker.postMessage({cancel:true});');
  assert.equal(r.threads.get(1).cancelled,true);r.run('worker.terminate();worker.postMessage({cancel:true});');r.pump();assert.equal(r.threads.size,0);
});
test('Android worker errors propagate as completion messages',()=>{
  const r=runtime();r.run('const module=new WebAssembly.Module(new Uint8Array([0]));globalThis.failure=null;globalThis.worker=WebAssembly.threadWorker({module,snapshot:new ArrayBuffer(0),closure:1,arguments:2,count:0},message=>{failure=message.error;});');
  Object.assign(r.threads.get(1),{done:true,result:{error:'worker failed'}});r.pump();assert.equal(r.run('failure'),'worker failed');r.run('worker.terminate();');
});
