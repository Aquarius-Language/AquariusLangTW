import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {BrowserHost} from '../browser/host.mjs';
import {Scope,nativeCall} from '../browser/values.mjs';

// The website's program.json catalog is LibraryCatalog.Functions; these rows come from the generated part of it.
const generated=fs.readFileSync(new URL('../../AquariusCore/runtime/LibraryCatalog.Generated.cs',import.meta.url),'utf8');
const catalog=[...generated.matchAll(/new\("([^"]+)", "([^"]+)", "([^"]+)", (\d+), (\d+),/g)].map(m=>({library:m[1],english:m[2],chinese:m[3],min:+m[4],max:+m[5]}));
function host(){const h=Object.create(BrowserHost.prototype);h.bundle={catalog};h.signal=new AbortController().signal;return h;}

for(const [library,english,chinese]of [
  ['Processing','loadMesh','載入網格'],['Processing.Canvas','loadMesh','載入網格'],['Processing','loadTexture','載入紋理'],
  ['Processing.Shader','setTexture','設定紋理'],['Processing.Shape','setNormal','設定法線'],
  ['WGPU.Buffer','ReadRange','讀取範圍'],['WGPU.Buffer','WriteRange','寫入範圍'],
])test(`browser binds ${library}.${english} under both names`,async()=>{
  const entry=catalog.find(f=>f.library===library&&f.english===english);assert.equal(entry?.chinese,chinese);
  const module={scope:new Scope()},calls=[];
  host().bind(module,library,english,(...args)=>{calls.push(args.length);return null;});
  const a=module.scope.get(english),b=module.scope.get(chinese);assert.equal(a,b);
  await b(...Array.from({length:entry.min},()=>0));assert.deepEqual(calls,[entry.min]);
  assert.throws(()=>b[nativeCall](...Array.from({length:entry.max+1},()=>0)),/arguments/);
});
