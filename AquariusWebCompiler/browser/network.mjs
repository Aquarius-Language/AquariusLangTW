import {maxNetworkBody,networkFailure,registerNetwork} from './network-registration.mjs';

function deadline(ms,signal){const controller=new AbortController(),cancel=()=>controller.abort(signal.reason);signal?.throwIfAborted();signal?.addEventListener('abort',cancel,{once:true});const timer=setTimeout(()=>controller.abort(new Error('Timeout / 網路操作逾時。')),ms);return {signal:controller.signal,abort:()=>controller.abort(),dispose(){clearTimeout(timer);signal?.removeEventListener('abort',cancel);}};}
export class BrowserNetworkBackend {
  constructor({fetch=globalThis.fetch,WebSocket=globalThis.WebSocket}={}){this.fetch=fetch.bind(globalThis);this.WebSocket=WebSocket;this.features=new Set(['http','cookies',...(WebSocket?['websocket']:[])]);this.resources=new Set();this.pending=new Set();this.closed=false;}
  live(){if(this.closed)throw new Error('Closed / 網路執行環境已關閉。');}
  parseURL(value){const u=new URL(value);return {scheme:u.protocol.slice(0,-1),host:u.hostname,path:u.pathname,query:u.search,fragment:u.hash,port:Number(u.port||(u.protocol==='https:'||u.protocol==='wss:'?443:u.protocol==='http:'||u.protocol==='ws:'?80:-1))};}
  createClient(){const backend=this;let closed=false;return {async send(method,url,options,signal){backend.live();if(closed)throw new Error('Closed / 用戶端已關閉。');const uri=new URL(url,globalThis.location?.href);if(!['http:','https:'].includes(uri.protocol))throw new Error('InvalidArgument / 需要 HTTP(S) 網址。');const d=deadline(options.timeout,signal);backend.pending.add(d);try{
      const response=await backend.fetch(uri.href,{method,headers:options.headers,body:options.body,credentials:options.credentials,redirect:options.followRedirects?'follow':'manual',signal:d.signal});
      if(response.type==='opaqueredirect')throw new Error('Unsupported / 瀏覽器無法讀取手動重新導向回應。');
      if(Number(response.headers.get('content-length'))>options.maxBodyBytes)throw new Error('LimitExceeded / 回應本文過大。');
      const chunks=[];let length=0;const reader=response.body?.getReader();if(reader)try{for(;;){const {value,done}=await reader.read();if(done)break;length+=value.length;if(length>options.maxBodyBytes)throw new Error('LimitExceeded / 回應本文過大。');chunks.push(value);}}finally{await reader.cancel();reader.releaseLock();}
      const body=new Uint8Array(length);let at=0;for(const chunk of chunks){body.set(chunk,at);at+=chunk.length;}
      return {status:response.status,url:response.url,headers:Object.fromEntries(response.headers),body,error:response.status>=400?{kind:'HTTP',message:'HTTP '+response.status,retryable:[408,429].includes(response.status)||response.status>=500}:null};
    }catch(e){return {status:0,url,headers:{},body:new Uint8Array(),error:networkFailure(d.signal.aborted?(d.signal.reason??e):e)};}finally{backend.pending.delete(d);d.dispose();}},dispose(){closed=true;}};}
  async connectWebSocket(url,options,signal){this.live();const uri=new URL(url);if(!['ws:','wss:'].includes(uri.protocol))throw new Error('InvalidArgument / 需要 ws:// 或 wss://。');if(Object.keys(options.headers).length)throw new Error('Unsupported / 瀏覽器無法設定 WebSocket 標頭。');const socket=new this.WebSocket(url);socket.binaryType='arraybuffer';const d=deadline(options.timeout,signal);this.pending.add(d);
    try{await new Promise((resolve,reject)=>{const open=()=>{clean();resolve();},error=()=>{clean();reject(new Error('Network / WebSocket 連線失敗。'));},abort=()=>{clean();socket.close();reject(d.signal.reason);},clean=()=>{socket.removeEventListener('open',open);socket.removeEventListener('error',error);d.signal.removeEventListener('abort',abort);};socket.addEventListener('open',open);socket.addEventListener('error',error);d.signal.addEventListener('abort',abort,{once:true});});}
    catch(e){socket.close();throw e;}finally{this.pending.delete(d);d.dispose();}
    return new BrowserSocket(socket,options,signal);
  }
  dispose(){if(this.closed)return;this.closed=true;for(const d of this.pending){d.abort();d.dispose();}this.pending.clear();for(const r of this.resources)r.dispose();this.resources.clear();}
}
class BrowserSocket {
  constructor(socket,options,signal){this.socket=socket;this.options=options;this.queue=[];this.queuedBytes=0;this.waiter=null;this.closed=false;this.error=null;this.signal=signal;
    this.message=e=>{const isText=typeof e.data==='string',bytes=isText?new TextEncoder().encode(e.data):new Uint8Array(e.data);if(bytes.length>options.maxBodyBytes||this.queuedBytes+bytes.length>maxNetworkBody||this.queue.length>=256){this.error=new Error('LimitExceeded / WebSocket 佇列超過上限。');this.dispose();return;}this.queue.push({bytes,isText});this.queuedBytes+=bytes.length;this.wake();};
    this.end=()=>{this.closed=true;this.wake();};this.fail=()=>{this.error=new Error('Network / WebSocket 連線失敗。');this.end();};this.abort=()=>this.dispose();socket.addEventListener('message',this.message);socket.addEventListener('close',this.end);socket.addEventListener('error',this.fail);signal?.addEventListener('abort',this.abort,{once:true});
  }
  wake(){this.waiter?.();}
  send(bytes,text){if(this.closed||this.socket.readyState!==1)throw new Error('Closed / WebSocket 已關閉。');if(bytes.length>this.options.maxBodyBytes||this.socket.bufferedAmount+bytes.length>maxNetworkBody)throw new Error('LimitExceeded / 傳送佇列超過上限。');this.socket.send(text?new TextDecoder().decode(bytes):bytes);}
  async receive(signal){signal?.throwIfAborted();if(this.waiter)throw new Error('InvalidArgument / 不允許同時接收。');if(!this.queue.length&&!this.closed){const d=deadline(this.options.timeout,signal);try{await new Promise((resolve,reject)=>{const abort=()=>{this.waiter=null;reject(d.signal.reason);};d.signal.addEventListener('abort',abort,{once:true});this.waiter=()=>{d.signal.removeEventListener('abort',abort);this.waiter=null;resolve();};});}finally{d.dispose();}}
    if(this.error)throw this.error;const value=this.queue.shift();if(value){this.queuedBytes-=value.bytes.length;return value;}return {bytes:new Uint8Array(),isText:false,closed:true};
  }
  close(code=1000,reason=''){this.socket.close(code,reason);this.closed=true;this.wake();}
  dispose(){this.socket.removeEventListener('message',this.message);this.socket.removeEventListener('close',this.end);this.socket.removeEventListener('error',this.fail);this.signal?.removeEventListener('abort',this.abort);this.close();}
}
export function registerBrowserNetwork(host){registerNetwork(host,new BrowserNetworkBackend());}
