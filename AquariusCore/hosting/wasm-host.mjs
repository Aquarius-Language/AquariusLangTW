import {Scope,num,numeric,nativeCall,nativeOwner,isPromiseLike,portableBuiltin,scopeMutations} from './values.mjs';
import {readManifest} from './wasm-manifest.mjs';
import {arrayBuiltin} from './array-library.mjs';
import {hashBuiltin} from './hash-library.mjs';
import {stringBuiltin} from './string-library.mjs';
import {ThreadGroup,ThreadPacket,threadBuiltin,threadModule} from './threads.mjs';
// Shared engine transport: Aquarius values, calls and control flow execute in Wasm.
// Mirrored guest objects are keyed by number: references are u32 addresses and tags are below 16.
const objectKey=(tag,reference)=>reference*16+tag;
export class WasmRuntime {
  get cancelled(){return this._cancelled??false;}
  set cancelled(value){this._cancelled=value;if(value)this.program?.session.threads.cancelPending();}
  constructor(host){this.host=host;this.cancelled=false;this.executions=new Set();this.nativeRoots=new Set();this.origins=new WeakMap();}
  async ready(){
    if(this.program)return this.program;
    this.initializing??=this.initialize().catch(error=>{this.initializing=null;throw error;});return this.initializing;
  }
  async initialize(){
    const module=this.host.bundle.compiledModule??await WebAssembly.compile(await(await fetch(this.host.bundle.wasm)).arrayBuffer());
    const metadata=readManifest(module);
    const program={module,metadata};program.session=new Session(this,program);await program.session.initialize();this.program=program;return program;
  }
  async execute(index,env=new Scope(),builtins=this.host.builtins,program=null){program??=await this.ready();const s=program.session,context=s.contexts.length;s.contexts.push(builtins);const scope=s.scope(env);return s.run(s.call('aqua_execute',program.metadata.programAddress,index,scope,context));}
  async invoke(fn,args=[]){
    if(typeof fn==='function'){const roots=[fn,...args];this.nativeRoots.add(roots);try{return await(fn[nativeCall]??fn)(...args);}finally{this.nativeRoots.delete(roots);}}
    const origin=this.origins.get(fn);if(fn?.type!=='closure'||!origin)throw new Error('Not a compiled function');if(args.length!==fn.parameters.length)throw new Error(`Function expects ${fn.parameters.length} arguments, got ${args.length}.`);
    const s=origin.session;const values=s.call('aqua_allocate',args.length*16);args.forEach((v,i)=>s.write(values+i*16,v));return s.run(s.call('aqua_invoke',origin.reference,values,args.length));
  }
  async dispose(){this.cancelled=true;if(this.program){await this.program.session.threads.dispose();this.program.session.detach();}}
  reachable(roots=[]){
    const seen=new Set(),pending=[],add=v=>{if(v&&(typeof v==='object'||typeof v==='function')&&!numeric(v)&&!seen.has(v)){seen.add(v);pending.push(v);}};
    for(const root of roots)add(root);for(const root of this.nativeRoots)add(root);for(const e of this.executions)for(const root of e.session.nativeReachable())add(root);
    while(pending.length){const v=pending.pop();if(typeof v==='function')add(v[nativeOwner]);else if(v.type==='module')add(v.scope);else if(v.type==='closure')add(v.env);else if(v instanceof Scope){add(v.outer);for(const value of v.store.values())add(value);}else if(v instanceof Map)for(const [k,value]of v){add(k);add(value);}else if(Array.isArray(v))for(const value of v)add(value);}return seen;
  }
}
class Session {
  constructor(runtime,program){
    this.runtime=runtime;this.program=program;this.contexts=[];this.natives=[null];this.nativeIds=new WeakMap();this.freeNativeIds=[];this.objects=new Map();this.scopes=new WeakMap();this.scopeLeases=[];this.projected=new Map();this.texts=new Map();this.leases=[];this.running=[];this.reading=new Set();this.writing=new Set();
    this.threads=new ThreadGroup(this);
  }
  async initialize(){
    this.instance=await WebAssembly.instantiate(this.program.module,{aquarius_v2:{service:(...a)=>this.service(...a)}});this.exports=this.instance.exports;this.call('aqua_initialize',this.program.metadata.heapStart);
    this.dirtyArrays=new Set();this.arrayOwners=new Map();this.mutableHashes=typeof this.exports.aqua_hash_expose==='function';
    this.deferredCollections=typeof this.exports.aqua_collection_due==='function'&&typeof this.exports.aqua_collection_generation==='function';
    // ABI 3 runtimes report which mirrored objects changed; ABI 2 artifacts refresh every mirror on read.
    this.borrowedArrays=typeof this.exports.aqua_array_items==='function';this.staleTracking=typeof this.exports.aqua_stale==='function';
    // Array and hash mirrors do not pin their guest objects; the guest reports dropped ones (see release()).
    this.weakMirrors=typeof this.exports.aqua_weak_mirrors==='function'&&this.call('aqua_weak_mirrors')===1;
    // The runtime's work-list heads and host era (RuntimeLayout.Pending*: lists at 0..12, era at 16); 0 without aqua_pending.
    this.pendingBlock=typeof this.exports.aqua_pending==='function'?this.call('aqua_pending'):0;
  }
  // Whether a runtime work list may be non-empty: read from guest memory instead of calling in to find out.
  pending(list){return !this.pendingBlock||this.int(this.pendingBlock+list)!==0;}
  // Before control returns to the guest: host bindings may have changed, so publish the era its member cache checks.
  publishHostEra(){if(this.pendingBlock)this.int(this.pendingBlock+16,scopeMutations.count>>>0||1);}
  call(name,...args){this.memoryChecked=false;return this.exports[name](...args);}
  // Memory grows only while guest code runs, so the buffer is re-read once after each call into the guest or service
  // entry, not on every access (the Android engine refreshes a grown buffer only when it is read).
  view(){if(!this.memoryChecked||this.memoryBuffer.byteLength===0){const buffer=this.exports.memory.buffer;if(buffer!==this.memoryBuffer){this.memoryBuffer=buffer;this.memoryView=new DataView(buffer);}this.memoryChecked=true;}return this.memoryView;}
  int(p,n){const v=this.view();if(n===undefined)return v.getUint32(p,true);v.setUint32(p,n,true);}
  raw(p,tag,reference=0,number=0){this.int(p,tag);this.int(p+4,reference);this.view().setFloat64(p+8,number,true);}
  text(value,intern=true){
    if(typeof value==='number'){const n=this.int(value),a=new Uint16Array(this.exports.memory.buffer,value+4,n);let result='';for(let i=0;i<n;i+=8192)result+=String.fromCharCode(...a.subarray(i,Math.min(n,i+8192)));return result;}
    if(this.texts.has(value))return this.texts.get(value);const p=this.call('aqua_text',value.length),a=new Uint16Array(this.exports.memory.buffer,p+4,value.length);for(let i=0;i<value.length;i++)a[i]=value.charCodeAt(i);
    if(intern){const cell=this.call('aqua_value');this.raw(cell,5,p);this.call('aqua_pin',cell);this.texts.set(value,p);}return p;
  }
  native(v){if(this.nativeIds.has(v))return this.nativeIds.get(v);const id=this.freeNativeIds.length?this.freeNativeIds.pop():this.natives.length;if(id>=65536)throw new Error('Native resource table capacity exceeded');this.nativeIds.set(v,id);this.natives[id]=v;return id;}
  lease(v,p,tag,reference){this.runtime.origins.set(v,{session:this,tag,reference});this.objects.set(objectKey(tag,reference),new WeakRef(v));if(!this.weakMirrors||(tag!==6&&tag!==7))this.leases.push({owner:new WeakRef(v),pin:this.call('aqua_pin',p)});if(tag===6)this.call('aqua_array_expose',reference);if(tag===7&&this.mutableHashes)this.call('aqua_hash_expose',reference);}
  // Forget mirrors of arrays and hashes the guest dropped. A retained mirror becomes a plain detached value: the guest
  // holds no reference that could observe it, and passing it back creates a new guest object.
  release(){
    if(!this.weakMirrors||!this.pending(8)&&!this.pending(12))return;let reference;
    while((reference=this.call('aqua_take_released'))!==0){
      for(const tag of [6,7]){const key=objectKey(tag,reference),mirror=this.objects.get(key)?.deref();if(mirror){const origin=this.runtime.origins.get(mirror);if(origin?.session===this&&origin.reference===reference)this.runtime.origins.delete(mirror);}this.objects.delete(key);}
      this.dirtyArrays.delete(reference);this.arrayOwners.delete(reference);
    }
  }
  collect(force=true){
    let released=false;
    for(const leases of [this.leases,this.scopeLeases])for(let i=leases.length-1;i>=0;i--)if(!leases[i].owner.deref()){this.call('aqua_unpin',leases[i].pin);leases.splice(i,1);released=true;}
    // Scheduler checkpoints bound latency; they need not trace all pixel buffers.
    // Older ABI 2 applications retain eager collection. A fresh trace is required
    // before consulting native liveness, including after the guest's own GC.
    if(this.deferredCollections&&!force&&!released&&!this.call('aqua_collection_due'))return;
    const generation=this.deferredCollections?this.call('aqua_collection_generation'):0;
    this.call('aqua_collect');this.release();
    if(this.deferredCollections&&this.call('aqua_collection_generation')===generation)throw new Error('Guest collection did not refresh native liveness');
    for(let i=1;i<this.natives.length;i++){const native=this.natives[i];if(native&&!this.call('aqua_native_live',i)){this.nativeIds.delete(native);this.natives[i]=null;this.freeNativeIds.push(i);}}
  }
  *nativeReachable(){this.collect();for(let i=1;i<this.natives.length;i++)if(this.call('aqua_native_live',i))yield this.natives[i];}
  // A host scope passed to execute becomes a live view of its guest scope, so neither side copies bindings per call.
  // dispose() turns it back into a plain scope holding the final bindings.
  scope(env){if(this.scopes.has(env))return this.scopes.get(env);const p=this.call('aqua_scope',env.outer?this.scope(env.outer):0);this.scopes.set(env,p);const root=this.call('aqua_value');this.raw(root,9,p);this.scopeLeases.push({owner:new WeakRef(env),pointer:p,pin:this.call('aqua_pin',root),host:true});for(const n of env.owned)this.set(p,n,env.store.get(n));this.projected.set(p,new WeakRef(env));this.attach(env,p);return p;}
  set(scope,n,v){const cell=this.call('aqua_value');this.write(cell,v);this.call('aqua_scope_set',scope,this.text(n),cell);}
  bindings(scope){const result=new Map(),count=this.call('aqua_scope_count',scope);for(let i=0;i<count;i++){const p=this.call('aqua_scope_binding',scope,i);result.set(this.text(this.int(p)),this.read(p+8));}return result;}
  project(scope){
    const cached=this.projected.get(scope)?.deref();if(cached)return cached;const outer=this.int(scope),env=new Scope(outer?this.project(outer):null);this.projected.set(scope,new WeakRef(env));this.scopes.set(env,scope);const root=this.call('aqua_value');this.raw(root,9,scope);this.scopeLeases.push({owner:new WeakRef(env),pointer:scope,pin:this.call('aqua_pin',root),host:false});return this.attach(env,scope);
  }
  // Backs a Scope with a guest scope: its owned bindings, lookups and assignments read and write guest memory.
  attach(env,scope){const session=this;
    class Bindings extends Map {
      get size(){return session.call('aqua_scope_count',scope);}
      get(n){return session.bindings(scope).get(n);}has(n){return session.bindings(scope).has(n);}
      set(n,v){session.set(scope,n,v);return this;}delete(n){return !!session.call('aqua_scope_delete',scope,session.text(n));}
      clear(){for(const n of this.keys())this.delete(n);}entries(){return session.bindings(scope).entries();}keys(){return session.bindings(scope).keys();}values(){return session.bindings(scope).values();}
      [Symbol.iterator](){return this.entries();}forEach(fn,thisArg){for(const [n,v] of this)fn.call(thisArg,v,n,this);}
    }
    const store=new Bindings();Object.defineProperty(env,'store',{value:store,configurable:true});Object.defineProperty(env,'owned',{get:()=>{const names=new Set(store.keys());names.delete=n=>store.delete(n);return names;},configurable:true});
    env.get=n=>{const p=this.call('aqua_scope_get',scope,this.text(n));if(!p)throw new Error(`Identifier not found: ${n}`);return this.read(p);};env.create=(n,v)=>this.set(scope,n,v);env.set=(n,v)=>{const cell=this.call('aqua_value');this.write(cell,v);if(!this.call('aqua_scope_assign',scope,this.text(n),cell))throw new Error(`Identifier not found: ${n}`);};return env;
  }
  // Host scopes become plain again with their final bindings once the session ends.
  detach(){for(const lease of this.scopeLeases){const env=lease.owner.deref();if(!lease.host||!env)continue;const store=this.bindings(lease.pointer);
    for(const name of ['get','create','set'])delete env[name];Object.defineProperty(env,'store',{value:store,writable:true,configurable:true});Object.defineProperty(env,'owned',{value:new Set(store.keys()),writable:true,configurable:true});}}
  arrayOwner(value,owner){const origin=this.runtime.origins.get(value);if(!Array.isArray(value)||origin?.session!==this)return;let owners=this.arrayOwners.get(origin.reference);if(!owners)this.arrayOwners.set(origin.reference,owners=[]);if(!owners.some(w=>w.deref()===owner))owners.push(new WeakRef(owner));}
  flushDirty(owner=null){let reference;if(this.mutableHashes){this.refreshingDirtyHashes=true;try{if(this.pending(4))while((reference=this.call('aqua_take_dirty_hash'))!==0){const cached=this.objects.get(objectKey(7,reference))?.deref();if(cached)this.refresh(7,reference,cached);}}finally{this.refreshingDirtyHashes=false;}}if(this.pending(0))while((reference=this.call('aqua_take_dirty_array'))!==0)this.dirtyArrays.add(reference);for(const item of this.dirtyArrays){const cached=this.objects.get(objectKey(6,item))?.deref();if(!cached){this.dirtyArrays.delete(item);this.arrayOwners.delete(item);continue;}if(owner&&!this.arrayOwners.get(item)?.some(w=>w.deref()===owner))continue;this.refresh(6,item,cached);this.dirtyArrays.delete(item);}}
  synchronizeOut(){this.flushDirty();}
  async run(pointer){
    const state={pointer,session:this,pending:null};let sliceStart=performance.now();this.runtime.executions.add(state);this.running.push(state);
    try{while(true){if(this.runtime.cancelled||this.runtime.host.signal?.aborted)throw new Error('Execution cancelled');const previous=this.dispatchState;this.dispatchState=state;let status;try{this.publishHostEra();status=this.call('aqua_run',pointer,32768);}finally{this.dispatchState=previous;this.release();}
      if(status===1){this.collect(false);if(performance.now()-sliceStart>=8){if(this.runtime.host.yieldExecution)await this.runtime.host.yieldExecution();else if(typeof window!=='undefined')await new Promise(r=>setTimeout(r,0));sliceStart=performance.now();}continue;}
      if(status===2){const pending=state.pending;try{const result=await pending.promise;if(this.runtime.cancelled||this.runtime.host.signal?.aborted)throw new Error('Execution cancelled');const cell=this.call('aqua_value');pending.args.forEach((v,i)=>{if(Array.isArray(v)||v instanceof Map)this.write(pending.arguments+i*16,v);});this.write(cell,result);this.call('aqua_resume',pointer,cell);}finally{this.runtime.nativeRoots.delete(pending.roots);state.pending=null;}continue;}
      const p=this.call('aqua_result',pointer);if(status===3)throw new Error(this.text(this.int(p+4)));return this.read(p);
    }}finally{this.synchronizeOut();this.call('aqua_release_execution',pointer);this.runtime.executions.delete(state);const i=this.running.indexOf(state);if(i>=0)this.running.splice(i,1);}
  }
  service(operation,context,subject,name,arguments_,count,output){
    try{return this.serviceCore(operation,context,subject,name,arguments_,count,output);}finally{this.publishHostEra();}
  }
  serviceCore(operation,context,subject,name,arguments_,count,output){
    this.memoryChecked=false;this.release();
    if(operation===4){const result=this.threads.call(subject,arguments_,count);if(isPromiseLike(result)){const roots=[];this.dispatchState.pending={promise:Promise.resolve(result),args:[],arguments:arguments_,roots,synchronize:false};return 1;}this.write(output,result);return 0;}
    if(operation===0){const n=this.text(name),builtins=this.contexts[context];if(n==='import'||n==='匯入'){const fallback=builtins.get(n);this.write(output,(name)=>{if(name==='Thread'||name==='執行緒')return threadModule;if(!fallback)throw new Error('Module not found: '+name);return (fallback[nativeCall]??fallback)(name);});return 0;}if(!builtins.has(n))return 1;this.write(output,builtins.get(n));return 0;}
    if(operation===2){const m=this.natives[subject];if(m?.type!=='module')return 1;this.flushDirty(m.scope);try{const value=m.scope.get(this.text(name));this.write(output,value);this.arrayOwner(value,m.scope);return 0;}catch{return 1;}}
    if(operation===3){const m=this.natives[subject];if(m?.type!=='module')return 1;this.raw(output,9,this.scope(m.scope));return 0;}
    if(operation!==1)throw new Error('Invalid native service request');const state=this.dispatchState,fn=this.natives[subject],args=new Array(count);for(let i=0;i<count;i++)args[i]=this.read(arguments_+i*16);const roots=[fn,...args];this.runtime.nativeRoots.add(roots);
    const synchronize=!fn?.[nativeOwner];
    try{if(synchronize)this.synchronizeOut();else this.flushDirty(fn[nativeOwner].scope);const result=typeof fn==='function'?(fn[nativeCall]??fn)(...args):this.runtime.invoke(fn,args);if(isPromiseLike(result)){state.pending={promise:Promise.resolve(result),args,arguments:arguments_,roots,synchronize};return 1;}
      // Only arrays and hashes can carry a callee's changes back; scalar argument slots are temporaries the guest drops.
      args.forEach((v,i)=>{if(Array.isArray(v)||v instanceof Map)this.write(arguments_+i*16,v);});this.write(output,result);if(!synchronize)this.arrayOwner(result,fn[nativeOwner].scope);this.runtime.nativeRoots.delete(roots);return 0;
    }catch(error){this.runtime.nativeRoots.delete(roots);throw error;}
  }
  write(p,v){
    if(v===threadModule){this.raw(p,9,this.call('aqua_thread_module'));return;}
    if(v instanceof ThreadPacket){const bytes=new Uint8Array(v.bytes),packet=this.call('aqua_allocate',bytes.length);new Uint8Array(this.exports.memory.buffer,packet,bytes.length).set(bytes);const cell=this.call('aqua_thread_decode',packet);if(!cell)throw new Error('Invalid thread result packet');new Uint8Array(this.exports.memory.buffer,p,16).set(new Uint8Array(this.exports.memory.buffer,cell,16));this.call('aqua_thread_free_packet',packet);return;}
    if(v===undefined){this.raw(p,0);return;}if(v===null){this.raw(p,11);return;}if(typeof v==='boolean'){this.raw(p,4,0,v?1:0);return;}if(numeric(v)){this.raw(p,{int:1,float:2,double:3}[v.type],0,v.value);return;}if(typeof v==='string'){this.raw(p,5,this.text(v,false));return;}
    if(typeof v==='function'&&v[portableBuiltin]){this.raw(p,14,v[portableBuiltin]);return;}const origin=this.runtime.origins.get(v);
    if(origin?.session===this){this.raw(p,origin.tag,origin.reference);if(this.writing.has(v))return;this.writing.add(v);try{if(Array.isArray(v)){if(this.call('aqua_array_count',origin.reference)!==v.length)this.call('aqua_array_resize',origin.reference,v.length);const items=this.call('aqua_array_values',origin.reference);this.writeArray(items,v);}else if(v instanceof Map){this.call('aqua_hash_resize',origin.reference,v.size);const items=this.call('aqua_hash_pairs',origin.reference);let i=0;for(const [key,value]of v.values()){this.write(items+i*32,key);this.write(items+i*32+16,value);i++;}}}finally{this.writing.delete(v);}return;}
    if(Array.isArray(v)){const reference=this.call('aqua_array',v.length);this.raw(p,6,reference);this.lease(v,p,6,reference);const items=this.call('aqua_array_values',reference);this.writeArray(items,v);return;}
    if(v instanceof Map){const reference=this.call('aqua_hash',v.size);this.raw(p,7,reference);this.lease(v,p,7,reference);const items=this.call('aqua_hash_pairs',reference);let i=0;for(const pair of v.values()){this.write(items+i*32,pair[0]);this.write(items+i*32+16,pair[1]);i++;}return;}
    if(v?.type==='module'&&this.scopes.has(v.scope)){const reference=this.scopes.get(v.scope);this.raw(p,9,reference);this.lease(v,p,9,reference);return;}this.raw(p,10,this.native(v));
  }
  read(p){
    if(this.int(p)===14)return this.portableBuiltin(this.int(p+4));
    const tag=this.int(p),reference=this.int(p+4),number=this.view().getFloat64(p+8,true);if(tag===0)return undefined;if(tag>=1&&tag<=3)return num(number,['','int','float','double'][tag]);if(tag===4)return number!==0;if(tag===5)return this.text(reference);if(tag===10)return this.natives[reference];if(tag===11)return null;if(tag===12)return {type:'break'};if(tag===13)throw new Error(this.text(reference));
    const key=objectKey(tag,reference),cached=this.objects.get(key)?.deref();if(cached){if(!this.refreshingDirtyHashes&&this.stale(tag,reference))this.refresh(tag,reference,cached);return cached;}
    let result;if(tag===6){result=new Array(this.call('aqua_array_count',reference));this.objects.set(key,new WeakRef(result));this.lease(result,p,tag,reference);this.reading.add(key);try{this.readArray(reference,result);}finally{this.reading.delete(key);}return result;}
    if(tag===7){result=new Map();this.objects.set(key,new WeakRef(result));this.lease(result,p,tag,reference);this.refresh(tag,reference,result);return result;}
    if(tag===8){const function_=this.int(reference+4),scope=this.int(reference+8),context=this.int(reference+12);result={type:'closure',function:function_,parameters:this.parameters(function_),env:this.project(scope),builtins:this.contexts[context],program:this.program};}
    else if(tag===9)result={type:'module',scope:this.project(reference)};else throw new Error(`Invalid Wasm value tag: ${tag}`);this.objects.set(key,new WeakRef(result));this.lease(result,p,tag,reference);return result;
  }
  // Whether a mirrored array or hash may differ from the guest object: changed in the guest since its dirty list was taken,
  // or taken but not yet refreshed (owner-filtered flushes leave arrays pending).
  stale(tag,reference){
    if(!this.staleTracking)return true;
    if(tag===6)return this.dirtyArrays.delete(reference)||this.call('aqua_stale',6,reference)!==0;
    if(tag===7)return !this.mutableHashes||this.call('aqua_stale',7,reference)!==0;
    return false;
  }
  // A function's parameter names, from its definition in the program data segment.
  parameters(function_){
    this.parameterNames??=new Map();let names=this.parameterNames.get(function_);
    if(!names){const m=this.program.metadata,definition=this.int(m.programAddress+4)+function_*m.functionLayout,table=this.int(definition+16);
      names=Array.from({length:this.int(definition+4)},(_,i)=>this.text(this.int(table+i*4)));this.parameterNames.set(function_,names);}
    return names;
  }
  // Portable builtin tokens by id: the first context's registration wins, then the portable libraries. A context's
  // index is rebuilt when its registrations change size or a hit is no longer registered under its name.
  portableBuiltin(id){
    this.portableIndex??=[];
    for(let c=0;c<this.contexts.length;c++){const context=this.contexts[c];
      for(let pass=0;pass<2;pass++){let index=this.portableIndex[c];
        if(pass===1||!index||index.size!==context.size){index={size:context.size,ids:new Map()};for(const [name,fn] of context)if(fn?.[portableBuiltin]&&!index.ids.has(fn[portableBuiltin]))index.ids.set(fn[portableBuiltin],[name,fn]);this.portableIndex[c]=index;}
        const entry=index.ids.get(id);if(!entry)break;if(context.get(entry[0])===entry[1])return entry[1];}}
    const fn=arrayBuiltin(id)??hashBuiltin(id)??stringBuiltin(id)??threadBuiltin(id);if(fn)return fn;throw new Error('Unknown portable builtin');
  }
  refresh(tag,reference,result){const key=objectKey(tag,reference);if(this.reading.has(key))return;this.reading.add(key);try{
    if(Array.isArray(result))this.readArray(reference,result);
    else if(result instanceof Map){const items=this.call('aqua_hash_pairs',reference),count=this.call('aqua_hash_count',reference);result.clear();for(let i=0;i<count;i++){const k=this.read(items+i*32),v=this.read(items+i*32+16);result.set(numeric(k)?`${k.type}:${k.value}`:`${typeof k}:${k}`,[k,v]);}}
  }finally{this.reading.delete(key);}}
  writeArray(items,values){
    let view=this.view();
    for(let i=0;i<values.length;i++){
      const value=values[i],p=items+i*16,tag=value?.type==='int'?1:value?.type==='float'?2:value?.type==='double'?3:0;
      if(tag){view.setUint32(p,tag,true);view.setUint32(p+4,0,true);view.setFloat64(p+8,value.value,true);}
      else {this.write(p,value);if(view.buffer!==this.exports.memory.buffer)view=this.view();}
    }
  }
  readArray(reference,result){
    result.length=this.call('aqua_array_count',reference);const items=this.call(this.borrowedArrays?'aqua_array_items':'aqua_array_values',reference);let view=this.view();
    for(let i=0;i<result.length;i++){
      const p=items+i*16,tag=view.getUint32(p,true);
      if(tag>=1&&tag<=3){const type=tag===1?'int':tag===2?'float':'double',value=view.getFloat64(p+8,true),old=result[i];if(old?.type!==type||!Object.is(old.value,value))result[i]=num(value,type);}
      else {result[i]=this.read(p);if(view.buffer!==this.exports.memory.buffer)view=this.view();}
    }
  }
}
