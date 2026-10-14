import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {runtime} from './wasm-fixture.mjs';
import {Scope,num,inspect,portableBuiltin} from '../browser/values.mjs';
import {CapabilityHost} from '../../AquariusCore/hosting/capability-host.mjs';
import {registerArrayLibrary} from '../../AquariusCore/hosting/array-library.mjs';
const cases=JSON.parse(readFileSync(new URL('../../tests/array-library.json',import.meta.url),'utf8'));
const names=[['AppendArray','追加陣列'],['Contains','包含'],['Reverse','反轉'],['Delete','刪除'],['Clone','克隆'],['Slice','切片'],['Append','追加'],['Index','尋找'],['Make','建立'],['Grow','擴充'],['Copy','複製'],['Fill','填入'],['Len','長度'],['Cap','容量']];
const chinese=source=>{for(const [en,zh]of names)source=source.replaceAll('Array.'+en,'陣列.'+zh).replaceAll('['+en+']','['+zh+']');return source.replaceAll('Array.(','陣列.(').replaceAll('變數 Array=','變數 陣列=').replaceAll(',Array];',',陣列];');};
const push=()=>{throw Error('Array algorithm crossed host boundary');};push[portableBuiltin]=4;
const builtins=new Map([['push',push]]);
for(const [name,source,expected]of cases.valid)for(const [language,code]of [['English',source],['繁體中文',chinese(source)]])test(`${name} / ${language}`,async()=>{
  const {vm,index}=await runtime(code,builtins);assert.equal(inspect(await vm.execute(index)),expected);
});
for(const source of cases.invalid)for(const code of [source,chinese(source)])test('reject before mutation: '+code,async()=>{
  const a=[num(1,'int'),num(2,'int')],scope=new Scope();scope.create('a',a);
  const {vm,index}=await runtime(code,builtins);await assert.rejects(()=>vm.execute(index,scope),/Array/);assert.equal(inspect(a),'[1, 2]');
});
test('array functions and modules survive transport',async()=>{
  const {vm,index}=await runtime('變數 make=identity(Array.Make);make(2,8);',new Map([['identity',v=>v]]));
  assert.equal(inspect(await vm.execute(index)),'[8, 8]');
  const projected=await runtime('Array;');const module=await projected.vm.execute(projected.index);
  assert.equal(module.scope.get('Make')[portableBuiltin],6);
});
test('host retained arrays receive bulk edits and length changes',async()=>{
  const a=[num(1,'int'),num(2,'int')],scope=new Scope();scope.create('a',a);
  const {vm,index}=await runtime('Array.Append(a,3);Array.Delete(a,0,1);Array.Copy(a,[9]);check();',new Map([['check',()=>inspect(a)]]));
  assert.equal(inspect(await vm.execute(index,scope)),'[9, 3]');assert.equal(a.length,2);
});
test('snapshot values survive allocation pressure and collection',async()=>{
  const {vm,index}=await runtime('變數 child=[7];變數 a=Array.Make(100,child);變數 b=push(a,9);迴圈(變數 i=0;i<100000;i++){變數 garbage=[i,i,i,i];}Array.Delete(a,0,99);Array.Append(a,8);child[0]=42;[a[0][0],a[1],b[0][0],b[100],Array.Len(b)];',builtins);
  assert.equal(inspect(await vm.execute(index)),'[42, 8, 42, 9, 101]');
});
test('imports expose identical portable tokens on web and Android hosts',async()=>{
  for(const platform of ['web','android']){
    const host=Object.create(CapabilityHost.prototype);host.modules=new Map();host.adapters={platform};registerArrayLibrary(host);
    const english=await host.globalImport('main.aqua')('Array'),zh=await host.globalImport('main.aqua')('陣列');
    assert.equal(english,zh);assert.equal(english.scope.get('Make')[portableBuiltin],6);assert.equal(zh.scope.get('建立')[portableBuiltin],6);
    const {vm,index}=await runtime('變數 arrays=匯入("陣列");arrays.建立(2,7);',new Map([['匯入',host.globalImport('main.aqua')]]));
    assert.equal(inspect(await vm.execute(index)),'[7, 7]');
    const aliases=await runtime('匯入("Array")==匯入("陣列");',new Map([['匯入',host.globalImport('main.aqua')]]));
    assert.equal(inspect(await aliases.vm.execute(aliases.index)),'真');
  }
});
