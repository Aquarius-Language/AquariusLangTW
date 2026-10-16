import {portableBuiltin} from './values.mjs';
export const threadModule=Object.freeze({threadModule:true});
export function threadBuiltin(id){if(id<100||id>107)return null;const fn=()=>{throw new Error('Thread requires compiled Wasm execution');};fn[portableBuiltin]=id;return fn;}
export class ThreadPacket {constructor(bytes){this.bytes=bytes;}}
export class ThreadGroup {
  constructor(session){this.session=session;this.handles=new Set();this.sleepers=new Set();this.disposed=false;this.abort=()=>this.cancelPending();session.runtime.host.signal?.addEventListener('abort',this.abort,{once:true});}
  cancelPending(){for(const h of this.handles)this.cancel(h);for(const wake of this.sleepers)wake(false);}
  async start(request){
    if(this.disposed)throw new Error('Thread runtime disposed');
    if(this.handles.size>=64)throw new Error('Close threads before starting more (limit: 64).');
    const h={owner:this,closed:false,done:false,error:null,packet:null,worker:null,cancelled:false};this.handles.add(h);
    let finish;h.completion=new Promise(resolve=>{finish=resolve;});
    const complete=async(message)=>{if(h.finishing)return;h.finishing=true;h.packet=message.packet;h.error=message.error;try{await h.worker?.terminate();}catch(error){h.error??=error.message;}h.done=true;finish();};
    try{
      if(this.session.runtime.host.threadWorker){h.worker=await this.session.runtime.host.threadWorker(request,message=>void complete(message));}
      else {
        let WorkerType=globalThis.Worker,node=false;
        if(!WorkerType&&typeof process!=='undefined'&&process.versions?.node){WorkerType=(await import('node:worker_threads')).Worker;node=true;}
        if(!WorkerType)throw new Error('Thread: Web Workers are unavailable in this host.');
        const worker=new WorkerType(new URL('./thread-worker.mjs',import.meta.url),{type:'module'});h.worker=worker;
        if(node){worker.on('message',message=>void complete(message));worker.on('error',error=>void complete({error:error.message}));worker.on('exit',code=>{if(!h.finishing)void complete({error:'Thread worker exited before returning a result ('+code+').'});});}
        else {worker.onmessage=event=>void complete(event.data);worker.onerror=event=>void complete({error:event.message??'Thread worker failed'});worker.onmessageerror=()=>void complete({error:'Invalid thread worker message'});}
        worker.postMessage(request,[request.snapshot]);
      }
      if(h.cancelled)h.worker.postMessage({cancel:true});
    }catch(error){await complete({error:error.message});}
    return h;
  }
  async wait(h,timeout=-1){
    if(h.done)return true;if(timeout===0)return false;
    if(timeout<0){await h.completion;return true;}
    let timer;try{return await Promise.race([h.completion.then(()=>true),new Promise(resolve=>{timer=setTimeout(()=>resolve(false),timeout);})]);}finally{clearTimeout(timer);}
  }
  cancel(h){if(h.done||h.cancelled||h.closed)return false;h.cancelled=true;h.worker?.postMessage({cancel:true});return true;}
  async close(h,timeout=-1){if(h.closed)return true;this.cancel(h);if(!await this.wait(h,timeout))return false;h.closed=true;h.worker=null;h.packet=null;this.handles.delete(h);return true;}
  async dispose(){this.disposed=true;this.session.runtime.host.signal?.removeEventListener('abort',this.abort);this.cancelPending();await Promise.all([...this.handles].map(h=>this.close(h)));}
  call(id,p,count){
    const s=this.session;
    if(id===100){const closure=s.int(p+4),array=count===2?s.int(p+20):0,args=array?s.call('aqua_array_values',array):0,n=array?s.call('aqua_array_count',array):0;
      const snapshot=s.exports.memory.buffer.slice(0,s.call('aqua_thread_snapshot_size'));
      return this.start({module:s.program.module,snapshot,closure,arguments:args,count:n});}
    if(id===106)return new Promise(resolve=>{let timer;const finish=value=>{clearTimeout(timer);this.sleepers.delete(finish);resolve(value);};this.sleepers.add(finish);timer=setTimeout(()=>finish(true),s.view().getFloat64(p+8,true));});
    if(id===107)return false;
    const h=s.read(p),timeout=count===2?s.view().getFloat64(p+24,true):-1;
    if(!h||h.owner!==this)throw new Error('Expected a thread handle from this runtime.');
    if(id===104)return this.close(h,timeout);
    if(h.closed)throw new Error('Thread is closed.');
    if(id===101)return this.wait(h,timeout);
    if(id===103)return this.cancel(h);
    if(id===105)return h.done;
    if(id===102)return this.wait(h).then(()=>{if(h.error)throw new Error(h.error);return new ThreadPacket(h.packet);});
    throw new Error('Unknown thread operation');
  }
}
