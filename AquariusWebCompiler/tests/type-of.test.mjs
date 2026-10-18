import test from 'node:test';
import assert from 'node:assert/strict';
import {BrowserHost} from '../browser/host.mjs';
import {runtime} from './wasm-fixture.mjs';

test('型別／typeOf 在瀏覽器 Wasm 回傳與桌面相同的型別標記',async()=>{
  const host=Object.create(BrowserHost.prototype);
  host.bundle={catalog:[{library:'',english:'typeOf',chinese:'型別',min:1,max:1}]};
  host.signal=new AbortController().signal;host.registerGlobals();
  const builtins=host.builtins;assert.equal(builtins.get('typeOf'),builtins.get('型別'));
  const {vm,index}=await runtime('變數 函={}["無"];[型別(1),型別(1.5f),型別(2.5d),型別("文"),型別(真),型別({}["無"]),型別([1]),型別({"a":1}),型別(函式(){1;}),typeOf(typeOf)];',builtins);
  assert.deepEqual(await vm.execute(index),['integer','float','double','string','boolean','null','array','hash','function','function']);
});
