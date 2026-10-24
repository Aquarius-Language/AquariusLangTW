import test from 'node:test';
import assert from 'node:assert/strict';
import {Processing,pixelValues,pixelBytes} from '../browser/processing.mjs';
import {Scope,module,wrap,unwrap} from '../browser/values.mjs';
import {readFileSync} from 'node:fs';
import {GeometryKernel} from '../../AquariusCore/hosting/geometry-core.mjs';
const geometry=await GeometryKernel.create(new WebAssembly.Module(readFileSync(new URL('../../AquariusCore/graphics/geometry.wasm',import.meta.url))));

test('direct pixel conversion preserves every byte including transparency and unsigned colors',()=>{
  const bytes=Uint8Array.from({length:4096},(_,i)=>(i*73+19)%256);
  assert.deepEqual(pixelBytes(pixelValues(bytes)),bytes);
  assert.equal(pixelValues(new Uint8Array([255,255,255,255]))[0].value,4294967295);
  assert.deepEqual(pixelBytes([{type:'double',value:0x80123456}]),new Uint8Array([0x12,0x34,0x56,0x80]));
});

test('offscreen endDraw retains batches until a texture consumer flushes them',()=>{
  const operations=[];
  const host={objects:new Map(),bundle:{graphics:{uniformBytes:1120}},newModule:()=>module(new Scope()),set:(m,n,v)=>m.scope.create(n,wrap(v)),
    bind(m,library,name,fn,raw=false){m.scope.create(name,(...args)=>fn(...(raw?args:args.map(unwrap))));},installProcessingInput(){}};
  const p=new Processing(host);
  p.geometry=geometry;p.device={createTarget:()=>({color:{},clear(){},draw(){operations.push('draw');},flush(){operations.push('flush');},dispose(){}})};
  p.screen.resize(32,32);p.shader={};
  const canvas=p.module.scope.get('createGraphics')(32,32),call=(name,...args)=>canvas.scope.get(name)(...args);
  call('beginDraw');call('line',1,1,10,10);call('endDraw');
  call('beginDraw');call('line',10,10,20,20);call('endDraw');
  assert.deepEqual(operations,['draw','draw']);
  p.screen.module.scope.get('image')(canvas,0,0);
  assert.deepEqual(operations,['draw','draw','flush','draw']);
});

test('backlog processing preserves all touch points and release ordering',async()=>{
  const names=[];
  const host={objects:new Map(),newModule:()=>module(new Scope()),set:(m,n,v)=>m.scope.create(n,wrap(v)),bind(m,l,n,f){m.scope.create(n,f);},installProcessingInput(){},runtime:{invoke:async fn=>fn()}};
  const p=new Processing(host);p.events.set('mouseDragged',()=>names.push('move'));p.events.set('mouseReleased',()=>names.push('up'));
  for(let i=0;i<60;i++)p.eventQueue.push({name:'mouseDragged',fields:{mouseX:i}});
  p.eventQueue.push({name:'mouseReleased',fields:{isMousePressed:false}});
  while(p.eventQueue.length)await p.beginFrame();assert.deepEqual(names,[...Array(60).fill('move'),'up']);
});

test('clip captures transformed canvas coordinates before applying framebuffer density',()=>{
  const clips=[];
  const host={objects:new Map(),bundle:{graphics:{uniformBytes:1120}},newModule:()=>module(new Scope()),set:(m,n,v)=>m.scope.create(n,wrap(v)),
    bind(m,library,name,fn,raw=false){m.scope.create(name,(...args)=>fn(...(raw?args:args.map(unwrap))));},installProcessingInput(){}};
  const p=new Processing(host);
  p.geometry=geometry;p.device={createTarget:()=>({color:{},clear(){},draw(...args){clips.push(args.at(-1));},flush(){},dispose(){}})};
  p.shader={};
  const canvas=p.module.scope.get('createGraphics')(240,160),call=(name,...args)=>canvas.scope.get(name)(...args);
  call('resize',240,160,480,320);call('beginDraw');call('noStroke');
  call('translate',10,5);call('scale',2.5);call('clip',40,20,40,24);
  call('resetMatrix');call('rect',0,0,240,160);
  assert.equal(clips.length,1);
  for(const [i,value] of [220,110,200,120].entries())assert.ok(Math.abs(clips[0][i]-value)<0.001,JSON.stringify(clips[0]));
  call('noClip');call('rect',220,60,10,20);assert.equal(clips.at(-1),undefined);
});
