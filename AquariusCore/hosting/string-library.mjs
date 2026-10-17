// Transport tokens only; algorithms and validation live in core Wasm (runtime/texts.h).
import {portableBuiltin} from './values.mjs';
const names=[['From','轉文字'],['Slice','切片'],['Find','尋找'],['Contains','包含'],['StartsWith','開頭是'],
  ['EndsWith','結尾是'],['Split','分割'],['Join','合併'],['Replace','取代'],['Trim','修剪'],['Upper','轉大寫'],
  ['Lower','轉小寫'],['Repeat','重複'],['Code','字元碼'],['FromCode','從字元碼'],['ParseInt','解析整數']];
export function stringBuiltin(id) {
  if(id<30||id>45)return undefined;
  const fn=()=>{throw new Error('String functions require compiled Wasm execution');};
  fn[portableBuiltin]=id;return fn;
}
export function registerStringLibrary(host) {
  const m=host.library('String');host.modules.set('字串',m);
  names.forEach(([english,chinese],i)=>{const fn=stringBuiltin(i+30);m.scope.create(english,fn);m.scope.create(chinese,fn);});
}
