import test from 'node:test';
import assert from 'node:assert/strict';
import {BrowserOnnxBackend, createTensor, validateRun, registerOnnx, openSession, planSessions, parseSelection} from '../browser/onnx.mjs';
import {Scope, module, wrap, num} from '../browser/values.mjs';
import {compile} from './wasm-fixture.mjs';
import {WasmRuntime} from '../browser/wasm.mjs';

function fixture() {
  const state = {released: 0, disposed: 0, creates: 0, runs: 0, options: null};
  const native = {inputMetadata: [{name:'X', isTensor:true, type:'float32', shape:['batch']}], outputMetadata:[{name:'Y', isTensor:true, type:'float32', shape:['batch']}],
    async run(feeds) { state.runs++; return {Y: {type:'float32', dims:feeds.X.dims, async getData(){return feeds.X.data.map(n=>n*2);}, dispose(){state.disposed++;}}}; },
    async release(){state.released++;}};
  const ort = {InferenceSession:{async create(bytes, options){state.creates++;state.options=options;return native;}}, Tensor:class{constructor(type,data,dims){Object.assign(this,{type,data,dims});}dispose(){state.disposed++;}}};
  const backend = new BrowserOnnxBackend({load:async()=>ort,gpu:{async requestAdapter(){return {info:{vendor:'test',architecture:'unit'}};}},ml:null});
  return {state,native,backend};
}
test('tensor types, scalar/empty shapes, bounds and snapshots',()=>{
  const data=[1,2],t=createTensor('float32',[2],data);data[0]=3;assert.equal(t.data[0],1);
  assert.equal(createTensor('float32',[],[2]).data.length,1);assert.equal(createTensor('float32',[0],[]).data.length,0);
  for(const [type,shape,data] of [['float32',[-1],[1]],['float32',[2],[1]],['int32',[1],[1.5]],['uint8',[1],[256]],['bool',[1],[1]],['float32',[1],[1e100]],['int64',[1],[1]]])assert.throws(()=>createTensor(type,shape,data),e=>e.kind==='InvalidArgument');
});
test('provider is explicit, metadata and outputs copied; resources disposed',async()=>{
  const {state,backend}=fixture(),session=await openSession(backend,new Uint8Array([1]),'WebGPU');
  assert.deepEqual(state.options.executionProviders,[{name:'webgpu'},'wasm']);assert.equal(state.options.graphOptimizationLevel,'all');
  assert.equal(session.provider,'WebGPU');assert.equal(session.device,'GPU');assert.deepEqual([...session.providers],['WebGPU','CPU']);assert.equal(session.inputs[0].shape[0],-1);
  const feeds=new Map([['X',createTensor('float32',[2],[2,3])]]),result=await session.run(feeds,['Y']);
  assert.deepEqual([...result.get('Y').data],[4,6]);assert.equal(state.disposed,2);
  await assert.rejects(session.run(feeds,['unknown']),e=>e.kind==='InvalidArgument');assert.equal(state.runs,1);
  await session.dispose();await session.dispose();assert.equal(state.released,1);await assert.rejects(session.run(feeds,['Y']),e=>e.kind==='Closed');
  await backend.dispose();const unsupported=new BrowserOnnxBackend({gpu:null,ml:null});await assert.rejects(openSession(unsupported,new Uint8Array([1]),'WebGPU'),e=>e.kind==='Unsupported');
});
test('browser devices rank WebGPU, WebNN and Wasm; preferences fall back, explicit lists do not',async()=>{
  const {state,backend}=fixture();backend.ml={async createContext({deviceType}){if(deviceType==='npu')throw new Error('no npu');return {};}};
  const devices=await backend.devices();assert.deepEqual(devices.map(d=>d.id),['WebGPU','WebNN/GPU','WebNN/NPU','CPU']);
  assert.deepEqual(devices.map(d=>d.available),[true,true,false,true]);assert.match(devices[2].reason,/no npu/);
  assert.deepEqual(planSessions(devices,parseSelection('自動')).map(p=>p[0].provider),['WebGPU','WebNN','CPU']);
  assert.deepEqual(planSessions(devices,parseSelection('圖形處理器')).map(p=>p[0].provider),['WebGPU','WebNN']);
  assert.throws(()=>planSessions(devices,parseSelection('NPU')),e=>e.kind==='Unsupported');
  assert.throws(()=>planSessions(devices,parseSelection('CUDA')),e=>e.kind==='Unsupported'&&/此平台未提供 CUDA/.test(e.message));
  const create=backend.load;let attempts=[];backend.load=async()=>{const ort=await create();return {...ort,InferenceSession:{create:async(bytes,options)=>{attempts.push(options.executionProviders[0]);if(options.executionProviders[0]?.name==='webgpu')throw new Error('GPU device lost');return ort.InferenceSession.create(bytes,options);}}};};
  const auto=await openSession(backend,new Uint8Array([1]),'Auto');assert.equal(auto.provider,'WebNN');assert.deepEqual(state.options.executionProviders,[{name:'webnn',deviceType:'gpu',powerPreference:'high-performance'},'wasm']);
  attempts=[];await assert.rejects(openSession(backend,new Uint8Array([1]),['WebGPU','CPU']),e=>e.kind==='Engine'&&/GPU device lost/.test(e.message));assert.equal(attempts.length,1);
  const fallback=new BrowserOnnxBackend({load:backend.load,gpu:{requestAdapter:async()=>({info:{isFallbackAdapter:true}})},ml:null});
  const listed=await fallback.devices();assert.equal(listed[0].available,true);assert.equal(listed[0].automatic,false);
  assert.equal((await openSession(fallback,new Uint8Array([1]))).provider,'CPU');await backend.dispose();await fallback.dispose();
});
test('cancelled or closed pending creation releases the native session',async()=>{
  for(const cancel of [true,false]){
    const {state,native,backend}=fixture();let resolve;backend.load=async()=>({InferenceSession:{create:()=>new Promise(r=>{resolve=r;})}});
    const controller=new AbortController(),pending=openSession(backend,new Uint8Array([1]),'CPU',controller.signal);
    await new Promise(r=>setImmediate(r));if(cancel)controller.abort();else await backend.dispose();resolve(native);
    await assert.rejects(pending,e=>e.kind===(cancel?'Cancelled':'Closed'));assert.equal(state.released,1);assert.equal(backend.sessions.size,0);
  }
});
test('dispose waits for output download and cancelled inference cleans tensors',async()=>{
  const {state,native,backend}=fixture();let finish,releaseDownload;
  native.run=()=>new Promise(r=>{finish=()=>r({Y:{type:'float32',dims:[1],getData:()=>new Promise(r=>{releaseDownload=r;}),dispose(){state.disposed++;}}});});
  const session=await openSession(backend,new Uint8Array([1]),'CPU'),feeds=new Map([['X',createTensor('float32',[1],[2])]]);
  const pending=session.run(feeds,['Y']);await new Promise(r=>setImmediate(r));await assert.rejects(session.run(feeds,['Y']),e=>e.kind==='InvalidArgument');finish();await new Promise(r=>setImmediate(r));
  const disposal=session.dispose();assert.equal(state.released,0);releaseDownload(new Float32Array([4]));await pending;await disposal;assert.equal(state.released,1);assert.equal(state.disposed,2);
  const next=fixture(),c=new AbortController(),s=await openSession(next.backend,new Uint8Array([1]),'CPU');
  next.native.run=async()=>{c.abort();return {Y:{type:'float32',dims:[1],dispose(){next.state.disposed++;}}};};
  await assert.rejects(s.run(feeds,['Y'],c.signal),e=>e.kind==='Cancelled');assert.equal(next.state.disposed,2);await next.backend.dispose();
});
test('binding executes both languages in actual Aquarius Wasm',async()=>{
  const source=`變數 引擎 = 匯入("推論引擎"); 變數 工作 = 引擎.建立工作階段("模型.onnx", 引擎.中央處理器);
變數 張量 = 引擎.CreateTensor(引擎.浮點32, [2], [2, 3]); 變數 結果 = 工作.推論(["X"], [張量], ["Y"]);
變數 輸出 = 結果[0]; 變數 資訊 = 工作.取得輸入資訊(); 變數 輸入 = 資訊[0];
變數 英文資料 = 輸出.GetData(); 變數 中文資料 = 輸出.取得資料();
變數 自動 = 引擎.建立工作階段("模型.onnx"); 變數 清單 = 引擎.取得後端清單(); 變數 首選 = 清單[0];
變數 成功 = 英文資料[0] == 4 && 中文資料[1] == 6 && 輸入.名稱 == "X" && 工作.後端 == 引擎.CPU && 自動.後端 == 引擎.網頁圖形加速 && 自動.裝置 == 引擎.圖形處理器 && 首選.可用 && 引擎.是否支援(引擎.自動);
工作.釋放(); 工作.Dispose(); 自動.釋放(); 成功;`;
  const bundle=compile({'main.aqua':source}),catalog=[['ONNX','IsSupported','是否支援',1],['ONNX','GetProviders','取得後端清單',0],['ONNX','CreateSession','建立工作階段',2],['ONNX','CreateTensor','建立張量',3],['ONNX.Session','GetInputs','取得輸入資訊',0],['ONNX.Session','GetOutputs','取得輸出資訊',0],['ONNX.Session','Run','推論',3],['ONNX.Session','Dispose','釋放',0],['ONNX.Tensor','GetData','取得資料',0],['ONNX.Tensor','GetShape','取得形狀',0],['ONNX.Tensor','GetType','取得型別',0]];
  const host={bundle,signal:new AbortController().signal,modules:new Map(),newModule:()=>module(new Scope()),set(m,n,v){m.scope.create(n,wrap(v));},library(n){const m=this.newModule();this.modules.set(n,m);return m;},
    bind(m,lib,name,fn){const call=async(...args)=>wrap(await fn(...args.map(v=>v?.type==='int'||v?.type==='double'?v.value:Array.isArray(v)?v.map(x=>x?.value??x):v)));m.scope.create(name,call);m.scope.create(catalog.find(c=>c[0]===lib&&c[1]===name)[2],call);},asset:()=>new Uint8Array([1])};
  const {backend,state}=fixture();registerOnnx(host,backend);const imports=async name=>host.modules.get(name);
  const vm=new WasmRuntime({...host,builtins:new Map([['匯入',imports]])});await vm.ready();assert.equal(await vm.execute(bundle.modules['main.aqua']),true);assert.equal(state.released,2);await backend.dispose();
});
