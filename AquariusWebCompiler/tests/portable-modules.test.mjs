// Shared JSON cases for the portable Hash / String modules and the % operator.
import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {runtime} from './wasm-fixture.mjs';
import {inspect,portableBuiltin} from '../browser/values.mjs';
import {CapabilityHost} from '../../AquariusCore/hosting/capability-host.mjs';
import {registerHashLibrary} from '../../AquariusCore/hosting/hash-library.mjs';
import {registerStringLibrary} from '../../AquariusCore/hosting/string-library.mjs';
const load=name=>JSON.parse(readFileSync(new URL(`../../tests/${name}.json`,import.meta.url),'utf8'));
const libraries=[
  ['Hash','雜湊',load('hash-library'),[['Entries','項目列表'],['Values','值列表'],['Delete','刪除鍵'],['Clone','克隆'],['Clear','清空'],['Merge','合併'],['Keys','鍵列表'],['Has','包含鍵'],['Get','取得'],['Len','長度']],/Hash|hash/],
  ['String','字串',load('string-library'),[['StartsWith','開頭是'],['ParseInt','解析整數'],['FromCode','從字元碼'],['EndsWith','結尾是'],['Contains','包含'],['Replace','取代'],['Repeat','重複'],['Split','分割'],['Slice','切片'],['Upper','轉大寫'],['Lower','轉小寫'],['Trim','修剪'],['Join','合併'],['Find','尋找'],['Code','字元碼'],['From','轉文字']],/String/]
];
for(const [english,chinese,cases,names,error]of libraries){
  const translate=source=>{for(const [en,zh]of names)source=source.replaceAll(`${english}.${en}(`,`${chinese}.${zh}(`);return source.replaceAll(`${english}==${chinese}`,`${chinese}==${english}`);};
  for(const [name,source,expected]of cases.valid)for(const [language,code]of [['English',source],['繁體中文',translate(source)]])test(`${english}: ${name} / ${language}`,async()=>{
    const {vm,index}=await runtime(code);assert.equal(inspect(await vm.execute(index)),expected);
  });
  for(const source of cases.invalid)test(`${english} rejects: ${source}`,async()=>{
    const {vm,index}=await runtime(source);await assert.rejects(()=>vm.execute(index),error);
  });
}
const modulo=load('modulo-operator');
for(const [name,source,expected]of modulo.valid)test(`%: ${name}`,async()=>{const {vm,index}=await runtime(source);assert.equal(inspect(await vm.execute(index)),expected);});
for(const source of modulo.invalid)test(`% rejects: ${source}`,async()=>{const {vm,index}=await runtime(source);await assert.rejects(()=>vm.execute(index));});
test('Hash and String modules import identically on web and Android hosts',async()=>{
  for(const platform of ['web','android']){
    const host=Object.create(CapabilityHost.prototype);host.modules=new Map();host.adapters={platform};registerHashLibrary(host);registerStringLibrary(host);
    const load=host.globalImport('main.aqua');
    assert.equal(await load('Hash'),await load('雜湊'));assert.equal((await load('雜湊')).scope.get('鍵列表')[portableBuiltin],20);
    assert.equal(await load('String'),await load('字串'));assert.equal((await load('字串')).scope.get('轉文字')[portableBuiltin],30);
    const {vm,index}=await runtime('變數 文=匯入("字串");變數 表=匯入("雜湊");文.轉文字(表.長度({"a":1}));',new Map([['匯入',load]]));
    assert.equal(inspect(await vm.execute(index)),'1');
  }
});
test('portable tokens survive host transport',async()=>{
  const {vm,index}=await runtime('變數 keys=identity(Hash.Keys);變數 text=identity(String.From);[keys({"a":1}),text(5)];',new Map([['identity',v=>v]]));
  assert.equal(inspect(await vm.execute(index)),'[[a], 5]');
});
