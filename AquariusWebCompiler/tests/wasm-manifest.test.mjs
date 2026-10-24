import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {compile} from './wasm-fixture.mjs';
import {readManifest,validateManifest,validAssetEncoding} from '../browser/wasm-manifest.mjs';

// ---- ABI 3: binary metadata ----------------------------------------------------------------------------------------
const bundle=compile({'main.aqua':'函式(x){x;};'}),module=bundle.compiledModule,bytes=bundle.bytes;
const leb=n=>{const out=[];do{let b=n&127;n>>>=7;if(n)b|=128;out.push(b);}while(n);return out;};
const text=s=>{const b=[...new TextEncoder().encode(s)];return [...leb(b.length),...b];};
const encode=m=>[0,65,81,77,m.abi??3,...[m.valueAbi??2,m.programAddress??4194304,m.heapStart,m.functionLayout??40,m.functionCount].flatMap(leb),
  ...leb(Object.keys(m.modules).length),...Object.entries(m.modules).flatMap(([name,index])=>[...text(name),...leb(index)]),...leb(m.entryIndex??0),
  ...leb(m.platforms?m.platforms.length+1:0),...(m.platforms??[]).flatMap(text),m.storage??0,...leb(m.assets.length),...m.assets.flatMap(([name,length])=>[...text(name),...leb(length)])];
// Rebuild the module with replacement Aquarius custom sections.
function rebuild(metadata,assets=null){
  const out=[...bytes.subarray(0,8)];let p=8;
  const u=()=>{let n=0,shift=0,b;do{b=bytes[p++];n|=(b&127)<<shift;shift+=7;}while(b&128);return n>>>0;};
  while(p<bytes.length){const start=p,id=bytes[p++],length=u(),end=p+length;
    if(id===0){const n=u(),name=new TextDecoder().decode(bytes.subarray(p,p+n));if(name==='aquarius.application'||name==='aquarius.assets'){p=end;continue;}}
    out.push(...bytes.subarray(start,end));p=end;}
  const custom=(name,payload)=>{const body=[...text(name),...payload];out.push(0,...leb(body.length),...body);};
  custom('aquarius.application',metadata);if(assets)custom('aquarius.assets',assets);
  return new WebAssembly.Module(new Uint8Array(out));
}
const original=readManifest(module);
const base={heapStart:original.heapStart,functionCount:original.functionCount,modules:original.modules,assets:[]};
test('compiler writes binary ABI 3 metadata that the browser reads',()=>{
  assert.deepEqual([...new Uint8Array(WebAssembly.Module.customSections(module,'aquarius.application')[0]).subarray(0,5)],[0,65,81,77,3]);
  assert.equal(original.abiVersion,3);assert.equal(original.entry,'main.aqua');assert.equal(original.functionLayout,40);
  assert.deepEqual(readManifest(rebuild(encode(base))),original);
});
test('embedded assets are a raw section and external assets keep only their names',()=>{
  const m=readManifest(rebuild(encode({...base,assets:[['a.bin',3],['b.txt',1]]}),[1,2,3,4]));
  assert.deepEqual(m.assetNames,['a.bin','b.txt']);assert.equal(m.externalAssets,false);
  assert.equal(readManifest(rebuild(encode({...base,storage:1,assets:[['a.bin',3]]}))).externalAssets,true);
});
for(const [name,metadata,assets]of [
  ['retired ABI byte',encode({...base,abi:2})],['trailing bytes',[...encode(base),0]],['truncated metadata',encode(base).slice(0,-1)],
  ['value ABI',encode({...base,valueAbi:1})],['memory base',encode({...base,programAddress:0})],['heap overlap',encode({...base,heapStart:16})],
  ['function layout',encode({...base,functionLayout:24})],['missing entry',encode({...base,entryIndex:9})],
  ['module reference',encode({...base,modules:{'main.aqua':base.functionCount}})],['unsafe module path',encode({...base,modules:{'../main.aqua':0}})],
  ['case collision',encode({...base,modules:{'main.aqua':0,'MAIN.aqua':0}})],['asset named like a module',encode({...base,assets:[['x.aqua',1]]}),[1]],
  ['asset length mismatch',encode({...base,assets:[['a.bin',3]]}),[1,2]],['missing asset section',encode({...base,assets:[['a.bin',1]]})],
  ['external assets with bytes',encode({...base,storage:1,assets:[['a.bin',1]]}),[1]],['asset storage',encode({...base,storage:2})],
  ['empty platforms',encode({...base,platforms:[]})],['missing function export',encode({...base,functionCount:base.functionCount+1})],
])test(`binary manifest rejects ${name}`,()=>assert.throws(()=>readManifest(rebuild(metadata,assets))));

// ---- ABI 2: JSON metadata (artifact built by the ABI 2 compiler; sources beside it) -----------------------------------------------------------
const legacyModule=new WebAssembly.Module(fs.readFileSync(new URL('../../AquariusTests/compiler/fixtures/abi2-application.wasm',import.meta.url)));
const legacy=JSON.parse(new TextDecoder().decode(WebAssembly.Module.customSections(legacyModule,'aquarius.application')[0]));
test('ABI 2 artifacts still load',()=>{
  const m=readManifest(legacyModule);assert.equal(m.abiVersion,2);assert.equal(m.functionCount,legacy.functions.length);assert.equal(m.entry,legacy.entry);
  assert.equal(validateManifest(legacyModule,structuredClone(legacy)).valueAbi,2);
});
test('model-sized base64 assets validate without recursive regexp stack growth',()=>{
  const metadata=structuredClone(legacy);metadata.assets['pose.onnx']='AAAA'.repeat(2*1024*1024)+'AA==';
  assert.equal(validateManifest(legacyModule,metadata),metadata);
  for(const valid of ['', 'AA==','AAA=','AAAA','AA\n=='])assert.equal(validAssetEncoding(valid),true);
  for(const invalid of ['A','AAAA=','A===','AA=A','AA!A','====',null])assert.equal(validAssetEncoding(invalid),false);
});
const withPool=legacy.functions.findIndex(f=>f.pool.length>0),withNumeric=legacy.functions.findIndex(f=>f.numericTable>0);
for(const [name,damage]of [
  ['retired ABI',m=>m.abiVersion=1],['value ABI',m=>m.valueAbi=1],['memory base',m=>m.programAddress=0],
  ['heap overlap',m=>m.heapStart=16],['heap mismatch',m=>m.heapStart++],['missing entry',m=>m.entry='missing.aqua'],
  ['invalid function layout',m=>m.functionLayout=28],['invalid numeric table',m=>m.functions[withNumeric].numericTable=-1],
  ['invalid recursive name',m=>m.functions[withNumeric].numericRecursiveName=42],['legacy layout with numeric entry',m=>m.functionLayout=24],
  ['invalid frame',m=>m.functions[0].stackCapacity=0],['invalid binding cache',m=>m.functions[0].cacheBindings='yes'],['invalid reference',m=>m.functions[withPool].pool[0]={type:'function',function:999,parameters:[]}],
  ['invalid constant',m=>m.functions[withPool].pool[0].type='unknown'],['unsafe asset',m=>m.assets['../file']=''],
  ['bad encoding',m=>m.assets['safe.txt']='!'],['case collision',m=>m.modules[m.entry.toUpperCase()]=0]
])test(`legacy manifest rejects ${name}`,()=>{const metadata=structuredClone(legacy);damage(metadata);assert.throws(()=>validateManifest(legacyModule,metadata));});
