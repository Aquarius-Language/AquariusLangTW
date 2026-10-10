import {Scope,num,numeric,nativeCall,nativeOwner,isPromiseLike,portableBuiltin} from './values.mjs';
import {validateManifest} from './wasm-manifest.mjs';
// Browser transport only: Aquarius values, calls and control flow execute in Wasm.
export class WasmRuntime {
  constructor(host){this.host=host;this.cancelled=false;this.executions=new Set();this.nativeRoots=new Set();this.origins=new WeakMap();}
  async ready(){
    if(this.program)return this.program;
    const module=this.host.bundle.compiledModule??await WebAssembly.compile(await(await fetch(this.host.bundle.wasm)).arrayBuffer());
    const sections=WebAssembly.Module.customSections(module,'aquarius.application');if(sections.length!==1)throw new Error('Missing or duplicate Aquarius metadata');
    const metadata=validateManifest(module,JSON.parse(new TextDecoder().decode(sections[0])));
    this.program={module,metadata};this.program.session=new Session(this,this.program);return this.program;
  }
  async execute(index,env=new Scope(),builtins=this.host.builtins,program=null){program??=await this.ready();const s=program.session,context=s.contexts.length;s.contexts.push(builtins);const scope=s.scope(env);if(!s.running.length)s.synchronizeIn();return s.run(s.call('aqua_execute',program.metadata.programAddress,index,scope,context));}
  async invoke(fn,args=[]){
    if(typeof fn==='function'){const roots=[fn,...args];this.nativeRoots.add(roots);try{return await(fn[nativeCall]??fn)(...args);}finally{this.nativeRoots.delete(roots);}}
    const origin=this.origins.get(fn);if(fn?.type!=='closure'||!origin)throw new Error('Not a compiled function');if(args.length!==fn.parameters.length)throw new Error(`Function expects ${fn.parameters.length} arguments, got ${args.length}.`);
    const s=origin.session;if(!s.running.length)s.synchronizeIn();const values=s.call('aqua_allocate',args.length*16);args.forEach((v,i)=>s.write(values+i*16,v));return s.run(s.call('aqua_invoke',origin.reference,values,args.length));
  }
  reachable(roots=[]){
    const seen=new Set(),pending=[],add=v=>{if(v&&(typeof v==='object'||typeof v==='function')&&!numeric(v)&&!seen.has(v)){seen.add(v);pending.push(v);}};
    for(const root of roots)add(root);for(const root of this.nativeRoots)add(root);for(const e of this.executions)for(const root of e.session.nativeReachable())add(root);
    while(pending.length){const v=pending.pop();if(typeof v==='function')add(v[nativeOwner]);else if(v.type==='module')add(v.scope);else if(v.type==='closure')add(v.env);else if(v instanceof Scope){add(v.outer);for(const value of v.store.values())add(value);}else if(v instanceof Map)for(const [k,value]of v){add(k);add(value);}else if(Array.isArray(v))for(const value of v)add(value);}return seen;
  }
}
class Session {
  constructor(runtime,program){
    this.runtime=runtime;this.program=program;this.contexts=[];this.natives=[null];this.nativeIds=new WeakMap();this.freeNativeIds=[];this.objects=new Map();this.scopes=new WeakMap();this.scopeLeases=[];this.projected=new Map();this.texts=new Map();this.leases=[];this.running=[];this.reading=new Set();this.writing=new Set();
    this.instance=new WebAssembly.Instance(program.module,{aquarius_v2:{service:(...a)=>this.service(...a)}});this.exports=this.instance.exports;this.call('aqua_initialize',program.metadata.heapStart);
    this.dirtyArrays=new Set();this.arrayOwners=new Map();
  }
  call(name,...args){return this.exports[name](...args);}
  view(){return new DataView(this.exports.memory.buffer);}
  int(p,n){const v=this.view();if(n===undefined)return v.getUint32(p,true);v.setUint32(p,n,true);}
  raw(p,tag,reference=0,number=0){this.int(p,tag);this.int(p+4,reference);this.view().setFloat64(p+8,number,true);}
  text(value,intern=true){
    if(typeof value==='number'){const n=this.int(value),a=new Uint16Array(this.exports.memory.buffer,value+4,n);let result='';for(let i=0;i<n;i+=8192)result+=String.fromCharCode(...a.subarray(i,Math.min(n,i+8192)));return result;}
    if(this.texts.has(value))return this.texts.get(value);const p=this.call('aqua_text',value.length),a=new Uint16Array(this.exports.memory.buffer,p+4,value.length);for(let i=0;i<value.length;i++)a[i]=value.charCodeAt(i);
    if(intern){const cell=this.call('aqua_value');this.raw(cell,5,p);this.call('aqua_pin',cell);this.texts.set(value,p);}return p;
  }
  native(v){if(this.nativeIds.has(v))return this.nativeIds.get(v);const id=this.freeNativeIds.length?this.freeNativeIds.pop():this.natives.length;if(id>=65536)throw new Error('Native resource table capacity exceeded');this.nativeIds.set(v,id);this.natives[id]=v;return id;}
  lease(v,p,tag,reference){this.runtime.origins.set(v,{session:this,tag,reference});this.objects.set(`${tag}:${reference}`,new WeakRef(v));this.leases.push({owner:new WeakRef(v),pin:this.call('aqua_pin',p)});if(tag===6)this.call('aqua_array_expose',reference);}
  collect(){for(const leases of [this.leases,this.scopeLeases])for(let i=leases.length-1;i>=0;i--)if(!leases[i].owner.deref()){this.call('aqua_unpin',leases[i].pin);leases.splice(i,1);}this.call('aqua_collect');for(let i=1;i<this.natives.length;i++){const native=this.natives[i];if(native&&!this.call('aqua_native_live',i)){this.nativeIds.delete(native);this.natives[i]=null;this.freeNativeIds.push(i);}}}
  *nativeReachable(){this.collect();for(let i=1;i<this.natives.length;i++)if(this.call('aqua_native_live',i))yield this.natives[i];}
  scope(env){if(this.scopes.has(env))return this.scopes.get(env);const p=this.call('aqua_scope',env.outer?this.scope(env.outer):0);this.scopes.set(env,p);const root=this.call('aqua_value');this.raw(root,9,p);this.scopeLeases.push({owner:new WeakRef(env),pointer:p,pin:this.call('aqua_pin',root),mirror:true});for(const n of env.owned)this.set(p,n,env.store.get(n));return p;}
  set(scope,n,v){const cell=this.call('aqua_value');this.write(cell,v);this.call('aqua_scope_set',scope,this.text(n),cell);}
  bindings(scope){const result=new Map(),count=this.call('aqua_scope_count',scope);for(let i=0;i<count;i++){const p=this.call('aqua_scope_binding',scope,i);result.set(this.text(this.int(p)),this.read(p+8));}return result;}
  project(scope){
    const cached=this.projected.get(scope)?.deref();if(cached)return cached;const outer=this.int(scope),env=new Scope(outer?this.project(outer):null);this.projected.set(scope,new WeakRef(env));this.scopes.set(env,scope);const root=this.call('aqua_value');this.raw(root,9,scope);this.scopeLeases.push({owner:new WeakRef(env),pointer:scope,pin:this.call('aqua_pin',root),mirror:false});const session=this;
    class Bindings extends Map {
      get size(){return session.call('aqua_scope_count',scope);}
      get(n){return session.bindings(scope).get(n);}has(n){return session.bindings(scope).has(n);}
      set(n,v){session.set(scope,n,v);return this;}delete(n){return !!session.call('aqua_scope_delete',scope,session.text(n));}
      clear(){for(const n of this.keys())this.delete(n);}entries(){return session.bindings(scope).entries();}keys(){return session.bindings(scope).keys();}values(){return session.bindings(scope).values();}
      [Symbol.iterator](){return this.entries();}forEach(fn,thisArg){for(const [n,v] of this)fn.call(thisArg,v,n,this);}
    }
    const store=new Bindings();Object.defineProperty(env,'store',{value:store});Object.defineProperty(env,'owned',{get:()=>{const names=new Set(store.keys());names.delete=n=>store.delete(n);return names;}});
    env.get=n=>{const p=this.call('aqua_scope_get',scope,this.text(n));if(!p)throw new Error(`Identifier not found: ${n}`);return this.read(p);};env.create=(n,v)=>this.set(scope,n,v);env.set=(n,v)=>{const cell=this.call('aqua_value');this.write(cell,v);if(!this.call('aqua_scope_assign',scope,this.text(n),cell))throw new Error(`Identifier not found: ${n}`);};return env;
  }
  synchronizeIn(){for(const lease of this.scopeLeases){const env=lease.owner.deref();if(lease.mirror&&env)for(const n of env.owned)this.set(lease.pointer,n,env.store.get(n));}}
  arrayOwner(value,owner){const origin=this.runtime.origins.get(value);if(!Array.isArray(value)||origin?.session!==this)return;let owners=this.arrayOwners.get(origin.reference);if(!owners)this.arrayOwners.set(origin.reference,owners=[]);if(!owners.some(w=>w.deref()===owner))owners.push(new WeakRef(owner));}
  flushDirty(owner=null){let reference;while((reference=this.call('aqua_take_dirty_array'))!==0)this.dirtyArrays.add(reference);for(const item of this.dirtyArrays){const cached=this.objects.get(`6:${item}`)?.deref();if(!cached){this.dirtyArrays.delete(item);this.arrayOwners.delete(item);continue;}if(owner&&!this.arrayOwners.get(item)?.some(w=>w.deref()===owner))continue;this.refresh(6,item,cached);this.dirtyArrays.delete(item);}}
  synchronizeOut(){this.flushDirty();for(const lease of this.scopeLeases){const env=lease.owner.deref();if(lease.mirror&&env)for(const [n,v]of this.bindings(lease.pointer))env.create(n,v);}}
  async run(pointer){
    const state={pointer,session:this,pending:null};this.runtime.executions.add(state);this.running.push(state);
    try{while(true){if(this.runtime.cancelled||this.runtime.host.signal?.aborted)throw new Error('Execution cancelled');const previous=this.dispatchState;this.dispatchState=state;let status;try{status=this.call('aqua_run',pointer,4096);}finally{this.dispatchState=previous;}
      if(status===1){this.collect();if(typeof window!=='undefined')await new Promise(r=>setTimeout(r,0));continue;}
      if(status===2){const pending=state.pending;try{const result=await pending.promise;if(this.runtime.cancelled||this.runtime.host.signal?.aborted)throw new Error('Execution cancelled');const cell=this.call('aqua_value');if(pending.synchronize)this.synchronizeIn();pending.args.forEach((v,i)=>this.write(pending.arguments+i*16,v));this.write(cell,result);this.call('aqua_resume',pointer,cell);}finally{this.runtime.nativeRoots.delete(pending.roots);state.pending=null;}continue;}
      const p=this.call('aqua_result',pointer);if(status===3)throw new Error(this.text(this.int(p+4)));return this.read(p);
    }}finally{this.synchronizeOut();this.call('aqua_release_execution',pointer);this.runtime.executions.delete(state);const i=this.running.indexOf(state);if(i>=0)this.running.splice(i,1);}
  }
  service(operation,context,subject,name,arguments_,count,output){
    if(operation===0){const n=this.text(name),builtins=this.contexts[context];if(!builtins.has(n))return 1;this.write(output,builtins.get(n));return 0;}
    if(operation===2){const m=this.natives[subject];if(m?.type!=='module')return 1;this.flushDirty(m.scope);try{const value=m.scope.get(this.text(name));this.write(output,value);this.arrayOwner(value,m.scope);return 0;}catch{return 1;}}
    if(operation===3){const m=this.natives[subject];if(m?.type!=='module')return 1;this.raw(output,9,this.scope(m.scope));return 0;}
    if(operation!==1)throw new Error('Invalid native service request');const state=this.dispatchState,fn=this.natives[subject],args=Array.from({length:count},(_,i)=>this.read(arguments_+i*16)),roots=[fn,...args];this.runtime.nativeRoots.add(roots);
    const synchronize=!fn?.[nativeOwner];
    try{if(synchronize)this.synchronizeOut();else this.flushDirty(fn[nativeOwner].scope);const result=typeof fn==='function'?(fn[nativeCall]??fn)(...args):this.runtime.invoke(fn,args);if(isPromiseLike(result)){state.pending={promise:Promise.resolve(result),args,arguments:arguments_,roots,synchronize};return 1;}
      if(synchronize)this.synchronizeIn();args.forEach((v,i)=>this.write(arguments_+i*16,v));this.write(output,result);if(!synchronize)this.arrayOwner(result,fn[nativeOwner].scope);this.runtime.nativeRoots.delete(roots);return 0;
    }catch(error){this.runtime.nativeRoots.delete(roots);throw error;}
  }
  write(p,v){
    if(v===undefined){this.raw(p,0);return;}if(v===null){this.raw(p,11);return;}if(typeof v==='boolean'){this.raw(p,4,0,v?1:0);return;}if(numeric(v)){this.raw(p,{int:1,float:2,double:3}[v.type],0,v.value);return;}if(typeof v==='string'){this.raw(p,5,this.text(v,false));return;}
    if(typeof v==='function'&&v[portableBuiltin]){this.raw(p,14,v[portableBuiltin]);return;}const origin=this.runtime.origins.get(v);
    if(origin?.session===this){this.raw(p,origin.tag,origin.reference);if(this.writing.has(v))return;this.writing.add(v);try{if(Array.isArray(v)){if(this.call('aqua_array_count',origin.reference)!==v.length)this.call('aqua_array_resize',origin.reference,v.length);const items=this.call('aqua_array_values',origin.reference);v.forEach((x,i)=>this.write(items+i*16,x));}else if(v instanceof Map){this.call('aqua_hash_resize',origin.reference,v.size);const items=this.call('aqua_hash_pairs',origin.reference);let i=0;for(const [key,value]of v.values()){this.write(items+i*32,key);this.write(items+i*32+16,value);i++;}}}finally{this.writing.delete(v);}return;}
    if(Array.isArray(v)){const reference=this.call('aqua_array',v.length);this.raw(p,6,reference);this.lease(v,p,6,reference);const items=this.call('aqua_array_values',reference);v.forEach((x,i)=>this.write(items+i*16,x));return;}
    if(v instanceof Map){const reference=this.call('aqua_hash',v.size);this.raw(p,7,reference);this.lease(v,p,7,reference);const items=this.call('aqua_hash_pairs',reference);let i=0;for(const pair of v.values()){this.write(items+i*32,pair[0]);this.write(items+i*32+16,pair[1]);i++;}return;}
    if(v?.type==='module'&&this.scopes.has(v.scope)){const reference=this.scopes.get(v.scope);this.raw(p,9,reference);this.lease(v,p,9,reference);return;}this.raw(p,10,this.native(v));
  }
  read(p){
    if(this.int(p)===14){const id=this.int(p+4);for(const context of this.contexts)for(const fn of context.values())if(fn?.[portableBuiltin]===id)return fn;throw new Error('Unknown portable builtin');}
    const tag=this.int(p),reference=this.int(p+4),number=this.view().getFloat64(p+8,true);if(tag===0)return undefined;if(tag>=1&&tag<=3)return num(number,['','int','float','double'][tag]);if(tag===4)return number!==0;if(tag===5)return this.text(reference);if(tag===10)return this.natives[reference];if(tag===11)return null;if(tag===12)return {type:'break'};if(tag===13)throw new Error(this.text(reference));
    const key=`${tag}:${reference}`,cached=this.objects.get(key)?.deref();if(cached){this.refresh(tag,reference,cached);return cached;}
    let result;if(tag===6){result=new Array(this.call('aqua_array_count',reference));this.objects.set(key,new WeakRef(result));this.lease(result,p,tag,reference);this.reading.add(key);const items=this.call('aqua_array_values',reference);try{for(let i=0;i<result.length;i++)result[i]=this.read(items+i*16);}finally{this.reading.delete(key);}return result;}
    if(tag===7){result=new Map();this.objects.set(key,new WeakRef(result));this.lease(result,p,tag,reference);this.refresh(tag,reference,result);return result;}
    if(tag===8){const function_=this.int(reference+4),scope=this.int(reference+8),context=this.int(reference+12);result={type:'closure',function:function_,parameters:this.program.metadata.functions[function_].parameters??[],env:this.project(scope),builtins:this.contexts[context],program:this.program};}
    else if(tag===9)result={type:'module',scope:this.project(reference)};else throw new Error(`Invalid Wasm value tag: ${tag}`);this.objects.set(key,new WeakRef(result));this.lease(result,p,tag,reference);return result;
  }
  refresh(tag,reference,result){const key=`${tag}:${reference}`;if(this.reading.has(key))return;this.reading.add(key);try{
    if(Array.isArray(result)){result.length=this.call('aqua_array_count',reference);const items=this.call('aqua_array_values',reference);for(let i=0;i<result.length;i++)result[i]=this.read(items+i*16);}
    else if(result instanceof Map){const items=this.call('aqua_hash_pairs',reference),count=this.call('aqua_hash_count',reference);result.clear();for(let i=0;i<count;i++){const k=this.read(items+i*32),v=this.read(items+i*32+16);result.set(numeric(k)?`${k.type}:${k.value}`:`${typeof k}:${k}`,[k,v]);}}
  }finally{this.reading.delete(key);}}
}
