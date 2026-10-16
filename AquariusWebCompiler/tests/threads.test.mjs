import test from 'node:test';
import assert from 'node:assert/strict';
import {runtime} from './wasm-fixture.mjs';
import {inspect} from '../browser/values.mjs';
import {ThreadGroup} from '../../AquariusCore/hosting/threads.mjs';
async function run(source){const {vm,index}=await runtime(source);try{return await vm.execute(index);}finally{await vm.dispose();}}
test('real workers receive functions, arguments, captured snapshots and bilingual aliases',async()=>{
  assert.equal(inspect(await run(`變數 factor=2;變數 data=[1];變數 transform=函式(x){回傳 x*factor;};
    變數 h=Thread.Start(函式(f,a){a[0]=9;回傳 f(21);},[transform,data]);factor=10;
    Thread.Wait(h);變數 answer=Thread.Result(h);Thread.Close(h);[answer,factor,data[0],Thread==執行緒];`)),'[42, 10, 1, 真]');
});
test('繁體中文 import and extracted functions work',async()=>{
  assert.equal(inspect(await run(`變數 t=匯入("執行緒");變數 e=import("Thread");變數 啟動=t.啟動;
    變數 h=啟動(函式(){回傳 42;});t.等待(h);變數 r=e.Result(h);[r,t.關閉(h),e.Close(h)];`)),'[42, 真, 真]');
});
test('poll and timeout do not consume handle; cancellation wakes sleep; close is repeatable',async()=>{
  assert.equal(inspect(await run(`變數 h=執行緒.啟動(函式(){執行緒.休眠(60000);回傳 1;});
    變數 poll=執行緒.等待(h,0);變數 timed=執行緒.等待(h,1);變數 cancel=執行緒.取消(h);
    [poll,timed,cancel,執行緒.關閉(h,5000),執行緒.關閉(h)];`)),'[假, 假, 真, 真, 真]');
});
test('pure infinite loop can be safely closed',async()=>{
  assert.equal(await run('變數 h=Thread.Start(函式(){迴圈(變數 i=0;真;i++){};});Thread.Close(h,5000);'),true);
});
test('results preserve cycles, shared references, Unicode and numeric types',async()=>{
  assert.equal(inspect(await run(`變數 h=Thread.Start(函式(){變數 a=[0];a[0]=a;變數 m={"自己":0};m["自己"]=m;
    回傳 [a,m,1.25f,"繁體中文",{1:9}];});變數 r=Thread.Result(h);
    變數 x=[r[0]==r[0][0],r[1]==r[1]["自己"],r[2],r[3],r[4][1]];Thread.Close(h);x;`)),'[真, 真, 1.25, 繁體中文, 9]');
});
test('each Result returns a fresh graph and multiple workers run independently',async()=>{
  assert.equal(inspect(await run(`變數 a=Thread.Start(函式(){回傳 [42];});變數 b=Thread.Start(函式(){回傳 99;});
    變數 first=Thread.Result(a);first[0]=1;變數 second=Thread.Result(a);變數 value=Thread.Result(b);
    Thread.Close(a);Thread.Close(b);[first[0],second[0],value];`)),'[1, 42, 99]');
});
for(const [source,message] of [
  ['Thread.Start(1);',/function/],['Thread.Start(函式(x){回傳 x;});',/argument count/],
  ['Thread.Wait(1);',/handle/],['Thread.Sleep(-1);',/timeout/],['Thread.Sleep(1.5d);',/timeout/],
  ['Thread.IsDone();',/argument count/],['變數 h=Thread.Start(函式(){});Thread.Wait(h,-2);',/timeout/],
  ['變數 h=Thread.Start(函式(){});Thread.Close(h);Thread.Result(h);',/closed/],
  ['變數 h=Thread.Start(函式(){回傳 unknown;});Thread.Result(h);',/capability/],
  ['變數 h=Thread.Start(函式(){回傳 函式(){};});Thread.Result(h);',/portable data/],
  ['變數 h=Thread.Start(函式(){回傳 1+真;});Thread.Result(h);',/INTEGER/],
  ['變數 h=Thread.Start(函式(){回傳 Thread.Start(函式(){});});Thread.Result(h);',/other threads/],
  ['變數 h=Thread.Start(函式(){Thread.Wait(1);});Thread.Result(h);',/other threads/],
])test('invalid calls and worker failures: '+source,async()=>{await assert.rejects(run(source),message);});
test('disposal joins outstanding workers and releases handles',async()=>{
  const {vm,index}=await runtime('變數 h=Thread.Start(函式(){迴圈(變數 i=0;真;i++){};});1;');
  assert.equal(inspect(await vm.execute(index)),'1');const group=vm.program.session.threads;assert.equal(group.handles.size,1);
  await vm.dispose();assert.equal(group.handles.size,0);await vm.dispose();
});
test('browser event loop remains responsive during worker computation',async()=>{
  let ticks=0;const timer=setInterval(()=>ticks++,1);
  try{assert.equal(inspect(await run('變數 h=Thread.Start(函式(){Thread.Sleep(100);回傳 42;});變數 r=Thread.Result(h);Thread.Close(h);r;')),'42');assert.ok(ticks>1);}finally{clearInterval(timer);}
});
test('host abort cancels a pending result wait and joins its worker',async()=>{
  const controller=new AbortController(),{vm,index}=await runtime('變數 h=Thread.Start(函式(){Thread.Sleep(60000);回傳 1;});Thread.Result(h);',new Map(),{signal:controller.signal});
  const execution=vm.execute(index);setTimeout(()=>controller.abort(),50);
  try{await assert.rejects(execution,/cancelled/);}finally{await vm.dispose();}
  assert.equal(vm.program.session.threads.handles.size,0);
});
test('runtime cancellation interrupts main-thread sleep',async()=>{
  const {vm,index}=await runtime('Thread.Sleep(60000);');const execution=vm.execute(index);setTimeout(()=>{vm.cancelled=true;},10);
  try{await assert.rejects(execution,/cancelled/);}finally{await vm.dispose();}
});
test('timed close retains ownership until a later join succeeds',async()=>{
  let finish,stops=0,cancels=0;
  const group=new ThreadGroup({runtime:{host:{threadWorker(request,receive){finish=receive;return {postMessage(){cancels++;},terminate(){stops++;}};}}}});
  const h=await group.start({});assert.equal(await group.close(h,0),false);assert.equal(group.handles.has(h),true);assert.equal(h.closed,false);
  finish({packet:new ArrayBuffer(4)});await h.completion;assert.equal(await group.close(h),true);assert.equal(await group.close(h),true);
  assert.equal(cancels,1);assert.equal(stops,1);assert.equal(group.handles.size,0);
});
test('workers cannot invoke captured host capabilities',async()=>{
  let calls=0;const {vm,index}=await runtime('變數 callback=host;變數 h=Thread.Start(函式(){回傳 callback();});Thread.Result(h);',new Map([['host',()=>{calls++;}]]));
  try{await assert.rejects(vm.execute(index),/host handles/);assert.equal(calls,0);}finally{await vm.dispose();}
});
