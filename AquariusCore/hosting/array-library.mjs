// Transport tokens only; algorithms and validation live in core Wasm.
import {portableBuiltin} from './values.mjs';
const names=[['Make','建立'],['Len','長度'],['Cap','容量'],['Grow','擴充'],
  ['Append','追加'],['AppendArray','追加陣列'],['Copy','複製'],['Clone','克隆'],
  ['Slice','切片'],['Fill','填入'],['Delete','刪除'],['Reverse','反轉'],['Index','尋找'],['Contains','包含']];
export function arrayBuiltin(id) {
  if(id<6||id>19)return undefined;
  const fn=()=>{throw new Error('Array functions require compiled Wasm execution');};
  fn[portableBuiltin]=id;return fn;
}
export function registerArrayLibrary(host) {
  const m=host.library('Array');host.modules.set('陣列',m);
  names.forEach(([english,chinese],i)=>{const fn=arrayBuiltin(i+6);m.scope.create(english,fn);m.scope.create(chinese,fn);});
}
