// Minimal worker host. No DOM, UI capabilities, native handles or shared Wasm memory.
let cancelled=false,wake=null,started=false;
export async function runThread({module,snapshot,closure,arguments:args,count},isCancelled=()=>cancelled,sleep=ms=>new Promise(resolve=>{
  const finish=()=>{clearTimeout(timer);wake=null;resolve(!isCancelled());},timer=setTimeout(finish,ms);wake=finish;if(isCancelled())finish();
})){
  let e;
  const view=()=>new DataView(e.memory.buffer),text=p=>{const n=view().getUint32(p,true),data=new Uint16Array(e.memory.buffer,p+4,n);let s='';for(let i=0;i<n;i+=8192)s+=String.fromCharCode(...data.subarray(i,i+8192));return s;};
  let sleeping=null;
  const service=(op,context,subject,name,a,n,out)=>{
    const raw=(tag,ref=0,value=0)=>{const v=view();v.setUint32(out,tag,true);v.setUint32(out+4,ref,true);v.setFloat64(out+8,value,true);};
    if(op===0){const key=text(name),id={len:1,'長度':1,last:2,'最後一個':2,rest:3,'其餘':3,push:4,'加入':4,floodFill:5,'區域填色':5}[key];if(id){raw(14,id);return 0;}throw new Error('Worker cannot access host capability: '+key);}
    if(op===4&&subject===107){raw(4,0,isCancelled()?1:0);return 0;}
    if(op===4&&subject===106){sleeping=sleep(view().getFloat64(a+8,true));return 1;}
    throw new Error('Worker cannot access host handles or start/wait for other threads.');
  };
  e=(await WebAssembly.instantiate(module,{aquarius_v2:{service}})).exports;
  e.aqua_thread_reserve(snapshot.byteLength);new Uint8Array(e.memory.buffer,0,snapshot.byteLength).set(new Uint8Array(snapshot));
  const execution=e.aqua_thread_enter(closure,args,count);
  while(true){
    if(isCancelled())throw new Error('Thread: Execution cancelled.');
    const status=e.aqua_run(execution,32768);
    if(status===1){await new Promise(resolve=>setTimeout(resolve,0));continue;}
    if(status===2){const result=await sleeping,cell=e.aqua_value(),v=view();v.setUint32(cell,4,true);v.setUint32(cell+4,0,true);v.setFloat64(cell+8,result?1:0,true);e.aqua_resume(execution,cell);continue;}
    if(status!==3&&status!==4)throw new Error('Invalid worker execution status.');
    const packet=e.aqua_thread_encode(e.aqua_result(execution));
    if(!packet)throw new Error('Thread results must be portable data (no functions or host handles; depth <= 128, graph <= 4096, packet <= 16 MiB).');
    return e.memory.buffer.slice(packet,packet+4+view().getUint32(packet,true));
  }
}
// Android calls the same guest ABI through its native Wasmtime worker adapter.
if(typeof WebAssembly!=='undefined'){
  let send,listen,close;
  if(typeof globalThis.postMessage==='function'&&typeof document==='undefined'){
    send=(message,transfer)=>globalThis.postMessage(message,transfer);listen=fn=>{globalThis.onmessage=event=>fn(event.data);};close=()=>globalThis.close();
  }else if(typeof process!=='undefined'&&process.versions?.node){
    const {parentPort}=await import('node:worker_threads');if(parentPort){send=(message,transfer)=>parentPort.postMessage(message,transfer);listen=fn=>parentPort.on('message',fn);close=()=>parentPort.close();}
  }
  if(listen)listen(async message=>{
    if(message.cancel){cancelled=true;wake?.();return;}
    if(started)return;started=true;
    try{const packet=await runThread(message);send({packet},[packet]);}catch(error){send({error:error.message});}finally{close();}
  });
}
