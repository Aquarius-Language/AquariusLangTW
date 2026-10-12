import test from 'node:test';
import assert from 'node:assert/strict';
import {BrowserHost} from '../browser/host.mjs';
import {nativeCall,num,Scope} from '../browser/values.mjs';
import {runtime} from './wasm-fixture.mjs';

function globals() {
  const host=Object.create(BrowserHost.prototype);
  host.bundle={catalog:[{library:'',english:'currentTime',chinese:'目前時間',min:0,max:0},{library:'',english:'currentDate',chinese:'目前日期',min:0,max:0}]};
  host.signal=new AbortController().signal;
  host.registerGlobals();
  return host.builtins;
}

test('compiled core clock aliases measure elapsed milliseconds in browser Wasm',async()=>{
  const builtins=globals();
  assert.equal(builtins.get('currentTime'),builtins.get('目前時間'));
  let workStart,workEnd;
  builtins.set('work',async()=>{
    workStart=performance.now();
    await new Promise(resolve=>setTimeout(resolve,20));
    workEnd=performance.now();
  });
  const {vm,index}=await runtime('變數 start=目前時間();work();變數 end=currentTime();[start,end,end-start,(end-start)/1000.0d];',builtins);
  const outerStart=performance.now(),values=await vm.execute(index),outerEnd=performance.now();
  assert.ok(values.every(v=>v.type==='double'&&Number.isFinite(v.value)&&v.value>=0));
  assert.equal(values[2].value,values[1].value-values[0].value);
  assert.equal(values[3].value,values[2].value/1000);
  assert.ok(values[2].value>=workEnd-workStart-0.001);
  assert.ok(values[2].value<=outerEnd-outerStart+0.001);
});

test('clock uses a synchronous monotonic entry and retains submillisecond precision',()=>{
  const saved=globalThis.performance;
  try {
    let reading=0;
    globalThis.performance={now:()=>[123.25,125.75][reading++]};
    const builtins=globals(),fn=builtins.get('currentTime');
    assert.deepEqual(fn[nativeCall](),num(123.25,'double'));
    assert.deepEqual(builtins.get('目前時間')[nativeCall](),num(125.75,'double'));
  } finally {globalThis.performance=saved;}
});

test('core clock rejects arguments and stops before later side effects',async()=>{
  for(const name of ['currentTime','目前時間']){
    const {vm,index}=await runtime(`變數 n=0;${name}(1);n++;`,globals()),scope=new Scope();
    await assert.rejects(()=>vm.execute(index,scope),/Expected 0\.\.0 arguments, got 1/);
    assert.equal(scope.get('n').value,0);
  }
});

test('clock readings retain their origin across executions and stored aliases',async()=>{
  const {vm,index}=await runtime('變數 read=目前時間;read();',globals());
  const first=await vm.execute(index),second=await vm.execute(index);
  assert.ok(second.value>=first.value);
});
test('本地日期在編譯後的瀏覽器星泉環境可用且拒絕額外參數',async()=>{
  const builtins=globals(),日期=()=>{const d=new Date();return d.getFullYear()+'-'+String(d.getMonth()+1).padStart(2,'0')+'-'+String(d.getDate()).padStart(2,'0');};
  assert.equal(builtins.get('currentDate'),builtins.get('目前日期'));
  const 起=日期(),{vm,index}=await runtime('目前日期();',builtins),值=await vm.execute(index),終=日期();assert.ok(值===起||值===終);
  const 壞=await runtime('currentDate(1);',builtins);await assert.rejects(壞.vm.execute(壞.index),/Expected 0/);
});
