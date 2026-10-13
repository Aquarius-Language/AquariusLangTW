// Native V8 services. There is no DOM, browser window, or embedded browser.
const alphabet='ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
globalThis.decode64=text=>{const out=new Uint8Array(text.length*3/4-(text.endsWith('==')?2:text.endsWith('=')?1:0));let value=0,bits=0,index=0;for(const char of text){const n=alphabet.indexOf(char);if(n<0)continue;value=(value<<6)|n;bits+=6;if(bits>=8){bits-=8;out[index++]=(value>>bits)&255;}}return out;};
globalThis.encode64=data=>{data=ArrayBuffer.isView(data)?new Uint8Array(data.buffer,data.byteOffset,data.byteLength):new Uint8Array(data);const chunks=[];for(let i=0;i<data.length;i+=3){const a=data[i],b=data[i+1],c=data[i+2];chunks.push(alphabet[a>>2]+alphabet[((a&3)<<4)|((b??0)>>4)]+(i+1<data.length?alphabet[((b&15)<<2)|((c??0)>>6)]:'=')+(i+2<data.length?alphabet[c&63]:'='));}return chunks.join('');};
globalThis.atob=text=>{const bytes=decode64(text);let out='';for(let i=0;i<bytes.length;i+=8192)out+=String.fromCharCode(...bytes.subarray(i,i+8192));return out;};
globalThis.btoa=text=>encode64(Uint8Array.from(text,c=>c.charCodeAt(0)));
globalThis.TextEncoder=class {encode(text=''){return decode64(native.encode(String(text)));}encodeInto(text,out){let read=0,written=0;for(const character of String(text)){const bytes=this.encode(character);if(written+bytes.length>out.length)break;out.set(bytes,written);written+=bytes.length;read+=character.length;}return {read,written};}};
globalThis.TextDecoder=class {constructor(encoding='utf-8',options={}){this.encoding=encoding;this.options=options;}decode(data=new Uint8Array()){return native.decodeText(encode64(data),this.encoding,!!this.options.fatal,!!this.options.ignoreBOM);}};
globalThis.performance={now:()=>native.now()};
globalThis.crypto={
  randomUUID:()=>native.uuid(),
  getRandomValues(array){
    const integerTypes=[Int8Array,Uint8Array,Uint8ClampedArray,Int16Array,Uint16Array,Int32Array,Uint32Array,BigInt64Array,BigUint64Array];
    if(!integerTypes.some(type=>array instanceof type))throw new DOMException('Expected an integer typed array','TypeMismatchError');
    if(array.byteLength>65536)throw new DOMException('Random byte request exceeds 65536 bytes','QuotaExceededError');
    new Uint8Array(array.buffer,array.byteOffset,array.byteLength).set(decode64(native.randomBytes(array.byteLength)));
    return array;
  }
};
globalThis.console={log:(...a)=>native.log(a.join(' ')),warn:(...a)=>native.log(a.join(' ')),error:(...a)=>native.log(a.join(' '))};
globalThis.DOMException=class extends Error {constructor(message,name='Error'){super(message);this.name=name;}};
globalThis.structuredClone=value=>{
  const seen=new Map(),clone=value=>{
    if(typeof value==='function'||typeof value==='symbol')throw new DOMException('Value cannot be cloned','DataCloneError');
    if(value===null||typeof value!=='object')return value;if(seen.has(value))return seen.get(value);
    let result;
    if(value instanceof ArrayBuffer)result=value.slice(0);
    else if(ArrayBuffer.isView(value)){const buffer=clone(value.buffer);result=value instanceof DataView?new DataView(buffer,value.byteOffset,value.byteLength):new value.constructor(buffer,value.byteOffset,value.length);}
    else if(value instanceof Date)result=new Date(value.getTime());
    else if(value instanceof RegExp)result=new RegExp(value.source,value.flags);
    else if(value instanceof Map){result=new Map();seen.set(value,result);for(const [key,item]of value)result.set(clone(key),clone(item));}
    else if(value instanceof Set){result=new Set();seen.set(value,result);for(const item of value)result.add(clone(item));}
    else {if(value instanceof WeakMap||value instanceof WeakSet||value instanceof Promise)throw new DOMException('Value cannot be cloned','DataCloneError');result=Array.isArray(value)?[]:{};seen.set(value,result);for(const key of Object.keys(value))Object.defineProperty(result,key,{value:clone(value[key]),enumerable:true,writable:true,configurable:true});}
    seen.set(value,result);return result;
  };return clone(value);
};
globalThis.EventTarget=class {constructor(){this.listeners=new Map();}addEventListener(name,fn,options={}){let items=this.listeners.get(name);if(!items)this.listeners.set(name,items=new Map());items.set(fn,options);}removeEventListener(name,fn){this.listeners.get(name)?.delete(fn);}dispatchEvent(event){for(const [fn,options]of [...this.listeners.get(event.type)??[]]){fn(event);if(options.once)this.removeEventListener(event.type,fn);}}};
globalThis.AbortSignal=class extends EventTarget {constructor(){super();this.aborted=false;}throwIfAborted(){if(this.aborted)throw this.reason;}static any(signals){const c=new AbortController();for(const s of signals){if(s.aborted){c.abort(s.reason);break;}s.addEventListener('abort',()=>c.abort(s.reason),{once:true});}return c.signal;}};
globalThis.AbortController=class {constructor(){this.signal=new AbortSignal();}abort(reason=new DOMException('Cancelled','AbortError')){if(this.signal.aborted)return;this.signal.aborted=true;this.signal.reason=reason;this.signal.dispatchEvent({type:'abort'});}};
let timerId=0;const timers=new Map();
globalThis.setTimeout=(fn,ms=0,...args)=>{const id=++timerId;timers.set(id,{time:performance.now()+ms,fn:()=>fn(...args)});return id;};
globalThis.clearTimeout=id=>timers.delete(id);
globalThis.pumpTimers=()=>{const now=performance.now();for(const [id,t]of [...timers])if(t.time<=now){timers.delete(id);t.fn();}};
globalThis.queueMicrotask=fn=>Promise.resolve().then(fn);
globalThis.URL=class {constructor(path,base=''){this.href=path.includes('://')?path:base.slice(0,base.lastIndexOf('/')+1)+path.replace(/^\.\//,'');}toString(){return this.href;}};
globalThis.fetch=async url=>{const path=String(url).split('/').at(-1);const data=decode64(native.readAsset(path));return {ok:true,status:200,arrayBuffer:async()=>data.buffer,text:async()=>new TextDecoder().decode(data),json:async()=>JSON.parse(new TextDecoder().decode(data))};};
WebAssembly.instantiateStreaming=async(response,imports)=>WebAssembly.instantiate(await(await response).arrayBuffer(),imports);
globalThis.Blob=class {constructor(parts=[],options={}){this.type=options.type??'';const arrays=parts.map(p=>typeof p==='string'?new TextEncoder().encode(p):p instanceof Blob?p.bytes:ArrayBuffer.isView(p)?new Uint8Array(p.buffer,p.byteOffset,p.byteLength):new Uint8Array(p));this.size=arrays.reduce((n,a)=>n+a.length,0);this.bytes=new Uint8Array(this.size);let offset=0;for(const a of arrays){this.bytes.set(a,offset);offset+=a.length;}}async arrayBuffer(){return this.bytes.slice().buffer;}async text(){return new TextDecoder().decode(this.bytes);}slice(start=0,end=this.size){return new Blob([this.bytes.slice(start,end)]);}};
