import test from 'node:test';
import assert from 'node:assert/strict';
import {prepareBundle} from '../browser/startup.mjs';
import {CapabilityHost} from '../../AquariusCore/hosting/capability-host.mjs';
const wasm=new Uint8Array([0,97,115,109,1,0,0,0]);
const file=i=>'asset-'+String(i).padStart(64,'0')+'.bin';
test('binary assets load once with bounded concurrency while Wasm compiles',async()=>{
  let active=0,peak=0;const requests=[];
  const bundle={wasm:'program.wasm',assetEncoding:'files',assets:Object.fromEntries(Array.from({length:9},(_,i)=>['data/'+i,file(i)]))};
  await prepareBundle(bundle,async name=>{requests.push(name);if(name==='program.wasm')return new Response(wasm,{headers:{'Content-Type':'application/wasm'}});
    active++;peak=Math.max(peak,active);await new Promise(r=>setTimeout(r,2));active--;return new Response(new Uint8Array([42,255]));});
  assert.equal(peak,4);assert.equal(requests.length,10);assert.ok(bundle.compiledModule instanceof WebAssembly.Module);
  const host={bundle,resolve:p=>p};const first=CapabilityHost.prototype.asset.call(host,'data/0');first[0]=0;
  assert.deepEqual(CapabilityHost.prototype.asset.call(host,'DATA/0'),new Uint8Array([42,255]));
});
test('old inline bundles and servers without Wasm MIME type remain supported',async()=>{
  const bundle={wasm:'program.wasm',assets:{'x.bin':'Kg=='}};
  await prepareBundle(bundle,async name=>{assert.equal(name,'program.wasm');return new Response(wasm);});
  assert.equal(CapabilityHost.prototype.asset.call({bundle,resolve:p=>p},'x.bin')[0],42);
});
test('missing or unsafe external assets fail before execution',async()=>{
  for(const asset of ['../outside.bin','https://example.test/x','asset-a.bin'])await assert.rejects(()=>prepareBundle({wasm:'program.wasm',assetEncoding:'files',assets:{x:asset}},async()=>new Response(wasm)),/filename/);
  await assert.rejects(()=>prepareBundle({wasm:'program.wasm',assetEncoding:'files',assets:{x:file(0)}},async name=>new Response(name==='program.wasm'?wasm:'missing',{status:name==='program.wasm'?200:404})),/404/);
});
