import {num,numeric,wrap} from './values.mjs';

export const networkFeatures={http:'超文字傳輸',cookies:'Cookie','cookie-control':'Cookie管理',websocket:'網頁通訊端',tcp:'傳輸控制協定',tls:'傳輸層安全',udp:'使用者資料報',dns:'網域名稱解析','tcp-listen':'監聽傳輸控制','http-server':'網頁伺服器'};
export const maxNetworkBody=16*1024*1024;
export function networkPlain(v,depth=0){
  if(depth>64)throw new Error('InvalidArgument / 資料過深。');
  if(numeric(v))return v.value;if(Array.isArray(v))return v.map(x=>networkPlain(x,depth+1));
  if(v instanceof Map)return Object.fromEntries([...v.values()].map(([k,x])=>[k,networkPlain(x,depth+1)]));return v;
}
export function networkHash(v){return new Map(Object.entries(v).map(([k,x])=>["string:"+k,[k,x&&typeof x==='object'&&!numeric(x)&&!Array.isArray(x)&&!(x instanceof Map)?networkHash(x):wrap(x)]]));}
export function networkJson(v){if(v===null)return null;if(Array.isArray(v))return v.map(networkJson);if(typeof v==='object')return networkHash(Object.fromEntries(Object.entries(v).map(([k,x])=>[k,networkJson(x)])));return typeof v==='number'?num(v,Number.isInteger(v)&&v>=-2147483648&&v<=2147483647?'int':'double'):v;}
export function networkBytes(value){value=networkPlain(value);if(typeof value==='string')return new TextEncoder().encode(value);if(Array.isArray(value)&&value.every(x=>Number.isInteger(x)&&x>=0&&x<=255)&&value.length<=maxNetworkBody)return Uint8Array.from(value);throw new Error('InvalidArgument / 需要文字或位元組陣列。');}
export function networkOptions(value={},defaults={}){
  value=networkPlain(value);if(!value||Array.isArray(value)||typeof value!=='object')throw new Error('InvalidArgument / 選項必須是雜湊表。');
  const get=(en,zh,fallback)=>value[en]??value[zh]??defaults[en]??fallback;
  const o={timeout:get('timeout','逾時',10000),maxBodyBytes:get('maxBodyBytes','本文上限',maxNetworkBody),maxRedirects:get('maxRedirects','重新導向上限',10),followRedirects:get('followRedirects','跟隨重新導向',true),credentials:get('credentials','認證','same-origin'),headers:{...(defaults.headers??{}),...get('headers','標頭',{})}};
  if(!Number.isInteger(o.timeout)||o.timeout<1||o.timeout>300000||!Number.isInteger(o.maxBodyBytes)||o.maxBodyBytes<1||o.maxBodyBytes>maxNetworkBody||!Number.isInteger(o.maxRedirects)||o.maxRedirects<0||o.maxRedirects>20||typeof o.followRedirects!=='boolean'||!['omit','same-origin','include'].includes(o.credentials))throw new Error('InvalidArgument / 網路選項無效。');
  for(const [k,v]of Object.entries(o.headers))if(!/^[!#$%&'*+.^_`|~0-9A-Za-z-]+$/.test(k)||typeof v!=='string'||/[\r\n]/.test(v))throw new Error('InvalidArgument / HTTP 標頭無效。');
  const json=get('json','JSON',undefined),form=get('form','表單',undefined),body=get('body','本文',undefined);
  if(body!==undefined)o.body=networkBytes(body);
  if(json!==undefined){o.body=new TextEncoder().encode(JSON.stringify(json));o.headers['Content-Type']='application/json; charset=utf-8';}
  if(form!==undefined){o.body=new TextEncoder().encode(encodeQuery(form));o.headers['Content-Type']='application/x-www-form-urlencoded';}
  if(o.body?.length>o.maxBodyBytes)throw new Error('LimitExceeded / 請求本文過大。');return o;
}
export function encodeQuery(values){values=networkPlain(values);if(!values||Array.isArray(values)||typeof values!=='object')throw new Error('InvalidArgument / 查詢需要雜湊表。');return Object.entries(values).map(([k,v])=>{if(typeof v!=='string')throw new Error('InvalidArgument / 查詢值必須是文字。');return encodeURIComponent(k)+'='+encodeURIComponent(v);}).join('&');}
export function networkFailure(e){return {kind:e.name==='AbortError'?'Cancelled':/Timeout|逾時/.test(e.message)?'Timeout':/Closed|已關閉/.test(e.message)?'Closed':/TLS/.test(e.message)?'TLS':/Unsupported|不支援/.test(e.message)?'Unsupported':/InvalidArgument/.test(e.message)?'InvalidArgument':/LimitExceeded/.test(e.message)?'LimitExceeded':'Network',message:e.message??String(e),retryable:!(/InvalidArgument|Unsupported|LimitExceeded|Closed|TLS/.test(e.message))};}

export function registerNetwork(host,backend){
  host.network=backend;const m=host.library('Network');host.modules.set('網路',m);
  const field=(m,en,zh,v)=>{host.set(m,en,v);host.set(m,zh,v);};
  const bind=(m,lib,n,fn)=>host.bind(m,lib,n,fn,true);
  const record=fields=>{const m=host.newModule();for(const [en,zh,v]of fields)field(m,en,zh,v);return m;};
  const supports=n=>backend.features.has(n)||Object.entries(networkFeatures).some(([en,zh])=>zh===n&&backend.features.has(en));
  const requireFeature=n=>{if(!supports(n))throw new Error(`Unsupported / 不支援：${host.adapters.platform} / ${n}`);};
  const track=resource=>{if(backend.resources.size>=1024){resource.dispose();throw new Error('LimitExceeded / 資源上限 1024。');}backend.resources.add(resource);return resource;};
  const close=r=>{if(backend.resources.delete(r))r.dispose();return null;};
  field(m,'Platform','平台',host.adapters.platform);field(m,'ContractVersion','契約版本',num(1,'int'));
  bind(m,'Network','Supports',n=>supports(n));bind(m,'Network','Capabilities',()=>networkHash(Object.fromEntries(Object.entries(networkFeatures).flatMap(([en,zh])=>[[en,supports(en)],[zh,supports(en)]]))));
  const error=e=>record([['Kind','種類',e.kind],['Message','訊息',e.message],['Retryable','可重試',!!e.retryable]]);
  bind(m,'Network','Try',async fn=>{try{return record([['Success','成功',true],['Value','值',await host.runtime.invoke(fn)],['Error','錯誤',null]]);}catch(e){return record([['Success','成功',false],['Value','值',null],['Error','錯誤',error(networkFailure(e))]]);}});
  const response=r=>{const bytes=r.body??new Uint8Array(),m=record([['Success','成功',!r.error&&r.status>=200&&r.status<300],['Status','狀態碼',num(r.status,'int')],['Url','網址',r.url],['Body','本文',new TextDecoder().decode(bytes)],['Headers','標頭',networkHash(r.headers??{})],['Error','錯誤',r.error?error(r.error):null]]);
    bind(m,'Network.Response','JSON',()=>networkJson(JSON.parse(new TextDecoder().decode(bytes))));bind(m,'Network.Response','Bytes',()=>Array.from(bytes,n=>num(n,'int')));bind(m,'Network.Response','Header',name=>Object.entries(r.headers??{}).find(([k])=>k.toLowerCase()===name.toLowerCase())?.[1]??'');return m;};
  const requests=(m,lib,client,defaults={})=>{
    bind(m,lib,'StartRequest',(method,url,value)=>{
      const options=networkOptions(value,defaults),controller=new AbortController(),signal=controller.signal;
      let done=false,result;const cancel=()=>controller.abort(host.signal.reason);host.signal.addEventListener('abort',cancel,{once:true});
      const task=Promise.resolve().then(()=>client.send(method,url,options,signal)).catch(e=>({status:0,url,headers:{},error:networkFailure(e)})).then(r=>{result=r;done=true;host.signal.removeEventListener('abort',cancel);return r;});
      const operation=track({dispose(){controller.abort();host.signal.removeEventListener('abort',cancel);}}),m=host.newModule();
      const finish=()=>{close(operation);return response(result);};
      bind(m,'Network.Operation','IsDone',()=>done);bind(m,'Network.Operation','Cancel',()=>{controller.abort();return null;});bind(m,'Network.Operation','Poll',()=>done?finish():null);
      bind(m,'Network.Operation','Wait',async()=>{await task;return finish();});bind(m,'Network.Operation','Close',()=>close(operation));return m;
    });
    const send=async(method,url,value,body)=>{try{if(typeof method!=='string'||typeof url!=='string')throw new Error('InvalidArgument / 方法及網址必須是文字。');const o=networkOptions(value,defaults);if(body!==undefined)o.body=typeof networkPlain(body)==='object'&&!Array.isArray(body)?new TextEncoder().encode(JSON.stringify(networkPlain(body))):networkBytes(body);return response(await client.send(method,url,o,host.signal));}catch(e){return response({status:0,url,headers:{},error:networkFailure(e)});}};
    bind(m,lib,'Request',(method,url,o)=>send(method,url,o));bind(m,lib,'Get',(url,o)=>send('GET',url,o));bind(m,lib,'Post',(url,body,o)=>send('POST',url,o,body));
  };
  const defaultClient=track(backend.createClient({}));requests(m,'Network',defaultClient);
  bind(m,'Network','CreateClient',value=>{const options=networkOptions(value),c=track(backend.createClient(options)),m=host.newModule();requests(m,'Network.Client',c,options);bind(m,'Network.Client','Close',()=>close(c));bind(m,'Network.Client','ClearCookies',()=>{requireFeature('cookie-control');return c.clearCookies();});return m;});
  bind(m,'Network','EncodeQuery',value=>encodeQuery(value));bind(m,'Network','ParseURL',value=>{const u=backend.parseURL(value);return record([['Scheme','協定',u.scheme],['Host','主機',u.host],['Path','路徑',u.path],['Query','查詢字串',u.query],['Fragment','片段',u.fragment],['Port','連接埠',num(u.port,'int')]]);});
  bind(m,'Network','Resolve',async name=>{requireFeature('dns');return await backend.resolve(name,host.signal);});
  const port=(n,zero=false)=>{n=networkPlain(n);if(!Number.isInteger(n)||n<(zero?0:1)||n>65535)throw new Error('InvalidArgument / 連接埠無效。');return n;};
  const size=(n,max=maxNetworkBody)=>{n=networkPlain(n);if(!Number.isInteger(n)||n<1||n>max)throw new Error('InvalidArgument / 讀取大小無效。');return n;};
  const stream=s=>{track(s);const m=record([['LocalAddress','本機位址',s.localAddress],['RemoteAddress','遠端位址',s.remoteAddress]]);
    bind(m,'Network.Stream','Read',async n=>Array.from(await s.read(size(n),host.signal),n=>num(n,'int')));bind(m,'Network.Stream','ReadText',async n=>new TextDecoder().decode(await s.read(size(n),host.signal)));
    bind(m,'Network.Stream','Write',v=>s.write(networkBytes(v),host.signal));bind(m,'Network.Stream','WriteText',v=>s.write(networkBytes(v),host.signal));bind(m,'Network.Stream','ShutdownWrite',()=>s.shutdownWrite());bind(m,'Network.Stream','Close',()=>close(s));return m;};
  for(const [name,tls]of [['ConnectTCP',false],['ConnectTLS',true]])bind(m,'Network',name,async(hostname,p,value)=>{requireFeature(tls?'tls':'tcp');return stream(await backend.connectTCP(hostname,port(p),networkOptions(value),tls,host.signal));});
  bind(m,'Network','OpenUDP',async(address,p,value)=>{requireFeature('udp');const d=track(await backend.openUDP(address,port(p,true),networkOptions(value))),m=record([['LocalAddress','本機位址',d.localAddress]]);
    bind(m,'Network.Datagram','Send',(address,p,bytes)=>d.send(address,port(p),networkBytes(bytes),host.signal));bind(m,'Network.Datagram','Receive',async n=>{const r=await d.receive(n===undefined?65507:size(n,65535),host.signal);return record([['Data','資料',Array.from(r.bytes,n=>num(n,'int'))],['Text','文字',new TextDecoder().decode(r.bytes)],['Address','位址',r.address],['Port','連接埠',num(r.port,'int')]]);});
    for(const [n,op]of [['JoinMulticast','joinMulticast'],['LeaveMulticast','leaveMulticast'],['SetBroadcast','setBroadcast']])bind(m,'Network.Datagram',n,v=>d[op](v));bind(m,'Network.Datagram','Close',()=>close(d));return m;});
  bind(m,'Network','ConnectWebSocket',async(url,value)=>{requireFeature('websocket');const s=track(await backend.connectWebSocket(url,networkOptions(value),host.signal)),m=host.newModule();
    bind(m,'Network.WebSocket','SendText',v=>s.send(networkBytes(v),true,host.signal));bind(m,'Network.WebSocket','SendBytes',v=>s.send(networkBytes(v),false,host.signal));
    bind(m,'Network.WebSocket','Receive',async()=>{const r=await s.receive(host.signal);return record([['Data','資料',Array.from(r.bytes,n=>num(n,'int'))],['Text','文字',new TextDecoder().decode(r.bytes)],['IsText','是文字',r.isText],['Closed','已關閉',!!r.closed]]);});
    bind(m,'Network.WebSocket','Close',async(code,reason='')=>{await s.close(code===undefined?1000:networkPlain(code),reason);return close(s);});return m;});
  for(const [name,feature]of [['ListenTCP','tcp-listen'],['Listen','http-server']])bind(m,'Network',name,()=>{requireFeature(feature);throw new Error('Unsupported / 此主機不提供伺服器。');});
}
