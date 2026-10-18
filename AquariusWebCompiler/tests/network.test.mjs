import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import {BrowserNetworkBackend} from '../browser/network.mjs';
import {networkOptions,networkPlain,networkJson,networkBytes,encodeQuery,networkFailure} from '../../AquariusCore/hosting/network-registration.mjs';

test('雙語選項、UTF8、JSON 巢狀雜湊與查詢編碼',()=>{
  const value=networkJson({標頭:{'X-Test':'星泉'},逾時:250,JSON:{訊息:'繁體中文',陣列:[1,true,null]}}),options=networkOptions(value);
  assert.equal(options.timeout,250);assert.equal(options.headers['X-Test'],'星泉');assert.deepEqual(JSON.parse(new TextDecoder().decode(options.body)),{訊息:'繁體中文',陣列:[1,true,null]});
  assert.deepEqual(networkPlain(networkJson({a:[{b:3}]})),{a:[{b:3}]});assert.equal(encodeQuery(networkJson({訊息:'星泉 空白'})),'%E8%A8%8A%E6%81%AF=%E6%98%9F%E6%B3%89%20%E7%A9%BA%E7%99%BD');assert.deepEqual([...networkBytes([0,255])],[0,255]);
});
test('無效參數、標頭注入、逾時及大小上限',()=>{
  for(const value of [{timeout:0},{timeout:Infinity},{timeout:1.5},{maxBodyBytes:0},{maxRedirects:21},{headers:{'X-Test':'\r\nInjected: yes'}},{headers:{'Bad:Name':'yes'}},{credentials:'bad'}])assert.throws(()=>networkOptions(value));
  assert.throws(()=>networkBytes([256]));assert.throws(()=>networkBytes([-1]));assert.equal(networkFailure(new Error('Unsupported / 不支援')).kind,'Unsupported');
});
test('真實 HTTP 的文字、二進位、POST、錯誤及重新導向',async()=>{
  const server=http.createServer(async(req,res)=>{if(req.url==='/redirect'){res.writeHead(302,{Location:'/text'});return res.end();}if(req.url==='/error'){res.writeHead(422);return res.end('錯誤');}if(req.url==='/binary')return res.end(Buffer.from([0,255,1]));if(req.url==='/echo'){let body='';for await(const data of req)body+=data;res.setHeader('Content-Type','application/json');return res.end(body);}res.end('星泉');});
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));const backend=new BrowserNetworkBackend(),client=backend.createClient(),url=`http://127.0.0.1:${server.address().port}`;
  try{const text=await client.send('GET',url+'/text',networkOptions(),new AbortController().signal);assert.equal(text.status,200);assert.equal(new TextDecoder().decode(text.body),'星泉');assert.deepEqual([...(await client.send('GET',url+'/binary',networkOptions())).body],[0,255,1]);
    const echo=await client.send('POST',url+'/echo',networkOptions({json:{訊息:'你好'}}));assert.deepEqual(JSON.parse(new TextDecoder().decode(echo.body)),{訊息:'你好'});assert.equal((await client.send('GET',url+'/error',networkOptions())).error.kind,'HTTP');assert.equal((await client.send('GET',url+'/redirect',networkOptions())).status,200);
  }finally{backend.dispose();await new Promise(resolve=>server.close(resolve));}
});
test('串流回應上限、取消及宿主清理',async()=>{
  const backend=new BrowserNetworkBackend({fetch:async(_url,init)=>new Response(new ReadableStream({start(controller){controller.enqueue(new Uint8Array(8));controller.close();}})),WebSocket:null}),client=backend.createClient();
  const large=await client.send('GET','http://localhost/',networkOptions({maxBodyBytes:2}));assert.equal(large.error.kind,'LimitExceeded');assert.equal(backend.features.has('tcp'),false);assert.equal(backend.features.has('websocket'),false);
  let aborted=false;backend.fetch=(_url,init)=>new Promise((_resolve,reject)=>init.signal.addEventListener('abort',()=>{aborted=true;reject(init.signal.reason);}));
  const pending=client.send('GET','http://localhost/',networkOptions({timeout:5}));assert.equal((await pending).error.kind,'Timeout');assert.equal(aborted,true);
  const request=client.send('GET','http://localhost/',networkOptions());backend.dispose();assert.equal((await request).error.kind,'Cancelled');assert.equal(backend.pending.size,0);
});
class FakeSocket extends EventTarget {
  constructor(){super();this.readyState=0;this.bufferedAmount=0;queueMicrotask(()=>{this.readyState=1;this.dispatchEvent(new Event('open'));});}
  send(value){const event=new Event('message');event.data=typeof value==='string'?value:value.slice().buffer;queueMicrotask(()=>this.dispatchEvent(event));}
  close(){this.readyState=3;this.dispatchEvent(new Event('close'));}
}
test('WebSocket 文字、二進位、關閉與接收逾時',async()=>{
  const backend=new BrowserNetworkBackend({WebSocket:FakeSocket}),options=networkOptions({timeout:20}),s=await backend.connectWebSocket('ws://localhost',options,new AbortController().signal);
  s.send(new TextEncoder().encode('星泉'),true);assert.equal(new TextDecoder().decode((await s.receive()).bytes),'星泉');s.send(new Uint8Array([0,255]),false);assert.deepEqual([...(await s.receive()).bytes],[0,255]);
  await assert.rejects(s.receive(),/Timeout/);s.dispose();assert.equal((await s.receive()).closed,true);backend.dispose();
});
test('WebSocket 佇列有訊息數與位元組上限',async()=>{
  const backend=new BrowserNetworkBackend({WebSocket:FakeSocket}),s=await backend.connectWebSocket('ws://localhost',networkOptions({maxBodyBytes:2}),new AbortController().signal);s.send(new Uint8Array([1,2]),false);await new Promise(resolve=>setImmediate(resolve));
  const event=new Event('message');event.data=new Uint8Array([1,2,3]).buffer;s.socket.dispatchEvent(event);await assert.rejects(s.receive(),/LimitExceeded/);backend.dispose();
});
test('串流 HTTP 逐段回報、閒置逾時並保留完整本文',async()=>{
  const encoder=new TextEncoder(),bytes=encoder.encode('data: 星泉\n\ndata: [DONE]\n'),parts=[bytes.slice(0,7),bytes.slice(7,9),bytes.slice(9)];
  const backend=new BrowserNetworkBackend({fetch:async()=>new Response(new ReadableStream({async start(controller){for(const part of parts){controller.enqueue(part);await new Promise(resolve=>setTimeout(resolve,15));}controller.close();}}),{status:200}),WebSocket:null}),client=backend.createClient();
  assert.equal(backend.features.has('http-stream'),true);const seen=[];let status=0;
  const result=await client.stream('POST','http://localhost/',networkOptions({timeout:40}),{started(code){status=code;},chunk(value){seen.push(value.length);}},new AbortController().signal);
  assert.equal(status,200);assert.deepEqual(seen,parts.map(p=>p.length));assert.equal(new TextDecoder().decode(result.body),'data: 星泉\n\ndata: [DONE]\n');
  backend.fetch=async()=>new Response(new ReadableStream({start(controller){controller.enqueue(encoder.encode('a'));}}));
  assert.equal((await client.stream('GET','http://localhost/',networkOptions({timeout:20}),{started(){},chunk(){}})).error.kind,'Timeout');backend.dispose();
});
