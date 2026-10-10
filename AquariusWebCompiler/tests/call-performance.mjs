// Informational VM call benchmark; no machine-dependent pass/fail timing limit.
// Run: node AquariusWebCompiler/tests/call-performance.mjs
import assert from 'node:assert/strict';
import {performance} from 'node:perf_hooks';
import {VirtualMachine,Scope,num} from '../browser/vm.mjs';
import {BrowserHost} from '../browser/host.mjs';

const host={builtins:new Map(),signal:new AbortController().signal,bundle:{catalog:[]}};
const fn=BrowserHost.prototype.bind.call(host,{scope:new Scope()},'Benchmark','native',value=>value,true);
const program={version:1,pool:[{type:'name',value:'native'},{type:'int',value:42}],code:[
  ...Array.from({length:20000},()=>[['Load',0],['Constant',1],['Call',1],['Pop']]).flat(),
  ['Constant',1],
]};
const variants=[{name:'public Promise call',entry:(...args)=>fn(...args)},{name:'direct native call',entry:fn}];
const machines=variants.map(variant=>new VirtualMachine({...host,builtins:new Map([['native',variant.entry]])}));
for(let round=0;round<4;round++)for(const vm of machines)assert.equal((await vm.execute(program)).value,42);
const samples=variants.map(()=>[]);
for(let round=0;round<9;round++)for(const index of round%2?[1,0]:[0,1]){
  const start=performance.now(),result=await machines[index].execute(program);
  samples[index].push(performance.now()-start);assert.equal(result.value,42);
}
console.log(JSON.stringify({node:process.version,callsPerExecution:20000,results:variants.map((variant,index)=>({
  name:variant.name,medianMs:[...samples[index]].sort((a,b)=>a-b)[4],samplesMs:samples[index],
}))},null,2));
