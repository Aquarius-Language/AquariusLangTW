import test from 'node:test';
import assert from 'node:assert/strict';
import {BrowserWgpuDevice} from '../browser/wgpu.mjs';

function fixture(){
  globalThis.GPUBufferUsage={STORAGE:1,COPY_SRC:2,COPY_DST:4,MAP_READ:8};globalThis.GPUMapMode={READ:1};
  const events=[],device=Object.create(BrowserWgpuDevice.prototype);
  Object.assign(device,{resources:new Set(),disposed:false,failure:null,flush(){this.live();},async compile(source){events.push(['compile',source]);return {};},gpu:{
    createBuffer({size}){return {data:new Uint8Array(size),destroy(){events.push(['destroy',size]);},async mapAsync(){},getMappedRange(){return this.data.buffer;}};},
    queue:{writeBuffer(buffer,offset,data){buffer.data.set(new Uint8Array(data.buffer,data.byteOffset,data.byteLength),offset);events.push(['write',offset,data.byteLength]);},submit(){events.push(['submit']);}},
    async createComputePipelineAsync(){events.push(['pipeline']);return {getBindGroupLayout(){return {};}};},createBindGroup(){return {};},
    createCommandEncoder(){return {copyBufferToBuffer(src,offset,dest,to,size){dest.data.set(src.data.subarray(offset,offset+size),to);events.push(['copy',offset,size]);},beginComputePass(){return {setPipeline(){},setBindGroup(){},dispatchWorkgroups(n){events.push(['dispatch',n]);},end(){}};},finish(){return {};}};}
  }});
  return {device,events};
}
test('browser range transfers leave the resident input data untouched',async()=>{
  const {device,events}=fixture(),buffer=device.createBuffer([1,2,3,4]);buffer.writeRange(1,[9,8]);assert.deepEqual(await buffer.readRange(1,2),[9,8]);
  assert.deepEqual(await buffer.read(),[1,9,8,4]);assert.ok(events.some(e=>e[0]==='write'&&e[1]===4&&e[2]===8));assert.ok(events.some(e=>e[0]==='copy'&&e[1]===4&&e[2]===8));
  for(const [offset,count] of [[-1,1],[0,0],[3,2],[.5,1],[0,NaN]])await assert.rejects(buffer.readRange(offset,count));
  assert.throws(()=>buffer.writeRange(0,[Infinity]));buffer.dispose();await assert.rejects(buffer.readRange(0,1));
});
test('repeated rules reuse a pipeline and the per-device cache stays bounded',async()=>{
  const {device,events}=fixture(),buffer=device.createBuffer([1]);await device.dispatch('first',buffer,1);await device.dispatch('first',buffer,1);
  assert.equal(events.filter(e=>e[0]==='pipeline').length,1);for(let i=0;i<40;i++)await device.dispatch('rule'+i,buffer,1);assert.equal(device.computePipelines.size,32);
  await assert.rejects(device.dispatch('first',buffer,0));await assert.rejects(device.dispatch('first',buffer,65536));
  const other=fixture().device;await assert.rejects(other.dispatch('first',buffer,1));
});
