import test from 'node:test';
import assert from 'node:assert/strict';
import {runtime} from './wasm-fixture.mjs';
import {Scope,num,inspect,nativeOwner} from '../browser/values.mjs';
test('雜湊表寫入保留別名、數值型別及主機保留資料',async()=>{
  const 資料=new Map([['string:值',['值',num(1,'int')]]]),環境=new Scope(),保留=new Scope();
  環境.create('資料',資料); const 檢查=()=>資料.get('string:值')[1]; 檢查[nativeOwner]={scope:保留};
  const {vm,index}=await runtime('變數 別名=資料;資料["值"]=7;資料[1]=8;資料[1.0d]=9;資料[真]=10;[檢查(),別名["值"],資料[1],資料[1.0d],資料[真]];',new Map([['檢查',檢查]]));
  assert.equal(inspect(await vm.execute(index,環境)),'[7, 7, 8, 9, 10]');
});
test('無效雜湊鍵先拒絕且成長循環保留內容',async()=>{
  let 次數=0; const bad=await runtime('變數 表={};表[[]]=副作用();',new Map([['副作用',()=>{次數++;return num(1,'int');}]]));
  await assert.rejects(bad.vm.execute(bad.index),/Unusable as hash key/); assert.equal(次數,0);
  const good=await runtime('變數 表={};變數 別名=表;迴圈(變數 索=0;索<1000;索++){表[索]=索;}表["自己"]=表;[別名[999],表["自己"]==表];');
  assert.equal(inspect(await good.vm.execute(good.index)),'[999, 真]');
});
test('修改雜湊欄位保持子物件別名並避免重複走訪未變更的陣列',async()=>{
  const 子=new Map([['string:值',['值',num(1,'int')]]]),卡片=Array.from({length:1000},(_,i)=>num(i,'int'));
  const 資料=new Map([['string:卡片',['卡片',卡片]],['string:子',['子',子]]]),環境=new Scope(),數學=new Scope();環境.create('資料',資料);
  const scalar=v=>v;scalar[nativeOwner]={scope:數學};
  const {vm,index}=await runtime('迴圈(變數 i=0;i<500;i++){資料["計數"]=i;scalar(i);}變數 子=資料["子"];子["值"]=7;資料["新增"]=子;scalar(0);資料["計數"];',new Map([['scalar',scalar]]));
  const 機=vm.program.session,原=機.call.bind(機);let 傳輸=0;機.call=(名,...參)=>{if(名==='aqua_array_values')傳輸++;return 原(名,...參);};
  assert.equal(inspect(await vm.execute(index,環境)),'499');assert.equal(資料.get('string:子')[1],子);assert.equal(資料.get('string:新增')[1],子);assert.equal(inspect(子.get('string:值')[1]),'7');assert.ok(傳輸<=10,`${傳輸} 次完整陣列傳輸`);
});
