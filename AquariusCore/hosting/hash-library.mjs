// Transport tokens only; algorithms and validation live in core Wasm (runtime/hashes.h).
import {portableBuiltin} from './values.mjs';
const names=[['Keys','鍵列表'],['Values','值列表'],['Has','包含鍵'],['Get','取得'],['Delete','刪除鍵'],
  ['Len','長度'],['Clone','克隆'],['Clear','清空'],['Merge','合併'],['Entries','項目列表']];
export function hashBuiltin(id) {
  if(id<20||id>29)return undefined;
  const fn=()=>{throw new Error('Hash functions require compiled Wasm execution');};
  fn[portableBuiltin]=id;return fn;
}
export function registerHashLibrary(host) {
  const m=host.library('Hash');host.modules.set('雜湊',m);
  names.forEach(([english,chinese],i)=>{const fn=hashBuiltin(i+20);m.scope.create(english,fn);m.scope.create(chinese,fn);});
}
