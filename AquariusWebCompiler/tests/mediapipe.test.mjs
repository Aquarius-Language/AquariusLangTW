import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {BrowserPoseTracker} from '../browser/mediapipe.mjs';
const wasm=await readFile(new URL('../../AquariusCore/mediapipe/pose.wasm',import.meta.url));
test('portrait camera frames are accepted within the shared pixel budget',async()=>{
  const f=fixture(),t=await f.create();
  const result=await t.analyze({width:1080,height:1920,pixels:new Uint8Array(1080*1920*4)},0);
  assert.equal(result.hasPose,false);
  await assert.rejects(t.analyze({width:1920,height:1920,pixels:new Uint8Array(1920*1920*4)},1));
  await t.dispose();
});
function fixture(){
  let releases=0,runs=0;
  const info=(name,shape)=>({name,type:'float32',shape});
  const detector={inputs:[info('input',[1,3,224,224])],outputs:[info('boxes',[1,2254,12]),info('scores',[1,2254,1])],
    async run(){runs++;return new Map([['boxes',{data:new Float32Array(2254*12)}],['scores',{data:new Float32Array(2254).fill(-100)}]]);},async dispose(){releases++;}};
  const landmark={inputs:[info('input',[1,256,256,3])],outputs:[info('points',[1,195]),info('flag',[1,1]),info('heatmap',[1,64,64,39])],async dispose(){releases++;}};
  let created=0;const backend={plans:[],async devices(){return [{id:'CPU',provider:'CPU',kind:'CPU',name:'cpu',available:true,automatic:true}];},async createSession(model,plan){this.plans.push(plan.map(d=>d.provider));return ++created===1?detector:landmark;}};
  return {backend,detector,landmark,releases:()=>releases,runs:()=>runs,create:()=>BrowserPoseTracker.create(backend,new Uint8Array([1]),new Uint8Array([2]),'CPU',undefined,wasm)};
}
test('blank frames have no stale skeleton, validate dimensions, reset and close',async()=>{
  const f=fixture(),t=await f.create(),image={width:640,height:480,pixels:new Uint8Array(640*480*4)};
  const first=await t.analyze(image,0);assert.equal(first.hasPose,false);assert.deepEqual(first.landmarks,[]);assert.equal(first.detectionRan,true);
  const second=await t.analyze(image,33);assert.equal(second.detectionRan,false);assert.equal(f.runs(),1);
  t.reset();await t.analyze(image,34);assert.equal(f.runs(),2);
  await assert.rejects(t.analyze({...image,width:1921},40));await t.dispose();await t.dispose();assert.equal(f.releases(),2);
  await assert.rejects(t.analyze(image,50));assert.throws(()=>t.reset());
});
test('both pose stages share the provider chain resolved for the detector',async()=>{
  const f=fixture(),t=await f.create();assert.deepEqual(f.backend.plans,[['CPU'],['CPU']]);assert.equal(t.detector.provider,'CPU');await t.dispose();
});
test('metadata initialization failure releases both owned sessions',async()=>{
  const f=fixture();f.landmark.outputs=[];await assert.rejects(f.create(),/輸出介面/);assert.equal(f.releases(),2);
});
test('overlap is rejected and disposal waits for inference before releasing sessions',async()=>{
  const f=fixture(),t=await f.create(),image={width:640,height:480,pixels:new Uint8Array(640*480*4)};let finish;
  const run=f.detector.run.bind(f.detector);f.detector.run=()=>new Promise(r=>{finish=async()=>r(await run());});
  const pending=t.analyze(image,0);await assert.rejects(t.analyze(image,33),/同時/);assert.throws(()=>t.reset());
  const dispose=t.dispose();assert.equal(f.releases(),0);await finish();await assert.rejects(pending,/已關閉/);await dispose;assert.equal(f.releases(),2);
});
