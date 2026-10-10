import {resolvePath} from './package-paths.mjs';

// Match the application contract checked by WasmProgram.Load on desktop.
export function validateManifest(module,m){
  const invalid=message=>{throw new Error(`Invalid Aquarius metadata: ${message}`);};
  const record=v=>v!==null&&typeof v==='object'&&!Array.isArray(v);
  if(!record(m)||m.abiVersion!==2||m.valueAbi!==2)invalid('unsupported ABI; recompile the application');
  if(m.programAddress!==4194304||!Number.isInteger(m.heapStart)||m.heapStart<4194312||m.heapStart>268435456)invalid('memory layout');
  if(!record(m.modules)||!record(m.assets)||!Array.isArray(m.functions)||!Object.keys(m.modules).length||Object.keys(m.modules).length+Object.keys(m.assets).length>10000||m.functions.length>100000)invalid('module count');
  const seen=new Set(),fold=value=>Array.from(value,c=>{const upper=c.toUpperCase();return [...upper].length===1&&(c.codePointAt(0)<128||upper.codePointAt(0)>=128)?upper:c;}).join('');
  const path=name=>{if(typeof name!=='string'||name.length>1024||resolvePath(name)!==name||seen.has(fold(name)))invalid('unsafe or duplicate path');seen.add(fold(name));};
  for(const [name,index]of Object.entries(m.modules)){path(name);if(!/\.aqua$/i.test(name)||!Number.isInteger(index)||index<0||index>=m.functions.length)invalid('module reference');}
  for(const [name,bytes]of Object.entries(m.assets)){path(name);if(/\.aqua$/i.test(name)||typeof bytes!=='string'||!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(bytes.replace(/[\r\n\t ]/g,'')))invalid('asset encoding');}
  if(typeof m.entry!=='string'||!Object.hasOwn(m.modules,m.entry))invalid('missing entry');
  const types=new Set(['name','int','float','double','string','bool','null','break','assignment','function','program']);
  let position=0;const strings=new Map(),reserve=(size,align=8)=>{position=Math.ceil(position/align)*align;position+=size;};
  const text=value=>{if(!strings.has(value)){reserve(4+value.length*2,4);strings.set(value,true);}};
  reserve(8);reserve(m.functions.length*24,4);
  for(const f of m.functions){
    if(record(f)&&f.cacheBindings!==undefined&&typeof f.cacheBindings!=='boolean')invalid('binding cache flag');
    if(!record(f)||!Array.isArray(f.pool)||!Array.isArray(f.parameters)||f.parameters.length>10000||f.parameters.some(p=>typeof p!=='string'||!p.trim())||!Number.isInteger(f.stackCapacity)||f.stackCapacity<16||f.stackCapacity>1000000)invalid('function frame');
    reserve(f.parameters.length*4,4);reserve(f.pool.length*16);for(const p of f.parameters)text(p);
    for(const c of f.pool){
      if(!record(c)||!types.has(c.type))invalid('constant type');
      if(['function','program'].includes(c.type)&&(!Number.isInteger(c.function)||c.function<0||c.function>=m.functions.length))invalid('function reference');
      if(c.type==='function'&&(!Array.isArray(c.parameters)||c.parameters.some(p=>typeof p!=='string'||!p.trim())))invalid('function parameters');
      if(['name','string','assignment'].includes(c.type)&&typeof c.text!=='string')invalid('constant text');
      if(c.type==='assignment'&&!['Add','Subtract','Multiply','Divide'].includes(c.operation))invalid('assignment operation');
      if(c.type==='int'&&(!Number.isInteger(c.number)||c.number<-2147483648||c.number>2147483647))invalid('integer constant');
      if(c.text!==null&&c.text!==undefined){if(typeof c.text!=='string')invalid('constant text');text(c.text);}
    }
  }
  if(4194304+position!==m.heapStart)invalid('metadata does not match memory layout');
  const imports=WebAssembly.Module.imports(module);if(imports.length!==1||imports[0].module!=='aquarius_v2'||imports[0].name!=='service'||imports[0].kind!=='function')invalid('capability imports');
  const exports=new Map(WebAssembly.Module.exports(module).map(e=>[e.name,e.kind]));if(exports.get('memory')!=='memory')invalid('missing memory');
  for(let i=0;i<m.functions.length;i++)if(exports.get(`aqua_f${i}`)!=='function')invalid('missing function export');
  return m;
}
