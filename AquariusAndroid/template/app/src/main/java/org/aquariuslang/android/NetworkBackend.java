package org.aquariuslang.android;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicLong;
import javax.net.ssl.*;
import okhttp3.*;
import okio.ByteString;
import org.json.*;

/** Bounded asynchronous I/O jobs. No network work runs on Android's UI or V8 actor thread. */
public final class NetworkBackend implements AutoCloseable {
  private static final int MAX = 16 * 1024 * 1024;
  private final AtomicLong next = new AtomicLong();
  private final ConcurrentHashMap<Long, AutoCloseable> resources = new ConcurrentHashMap<>();
  private final ConcurrentHashMap<Long, Future<Object>> jobs = new ConcurrentHashMap<>();
  private final ThreadPoolExecutor workers = new ThreadPoolExecutor(4, 4, 30, TimeUnit.SECONDS, new ArrayBlockingQueue<>(64));
  private volatile boolean closed;

  public String command(String operation, String request) throws Exception {
    JSONObject a = new JSONObject(request);
    if (operation.equals("close")) { close(); return "{}"; }
    if (closed) throw new IOException("Closed / 網路執行環境已關閉。");
    long id = a.optLong("id");
    if (operation.equals("start")) {
      if (jobs.size() >= 64) throw new IOException("LimitExceeded / 待處理工作已滿。");
      long token = next.incrementAndGet(); jobs.put(token, workers.submit(() -> execute(a.getString("op"), a))); return object("id", token).toString();
    }
    Future<Object> future = jobs.get(id);
    if (future == null) throw new IOException("Closed / 工作不存在。");
    if (operation.equals("cancel")) { jobs.remove(id); future.cancel(true); return "{}"; }
    if (!operation.equals("poll")) throw new IllegalArgumentException("InvalidArgument / 未知網路操作。");
    if (!future.isDone()) return "{\"done\":false}";
    jobs.remove(id);
    try { return object("done", true, "value", future.get()).toString(); }
    catch (ExecutionException e) {
      Throwable cause = e.getCause(); String kind = cause instanceof SocketTimeoutException ? "Timeout" : cause instanceof SSLException ? "TLS" : cause instanceof IllegalArgumentException ? "InvalidArgument" : "Network";
      return object("done", true, "error", object("kind", kind, "message", cause.getMessage() == null ? kind : cause.getMessage())).toString();
    }
  }
  private synchronized long own(AutoCloseable value) throws Exception {
    if (closed || Thread.currentThread().isInterrupted() || resources.size() >= 1024) { value.close(); throw new IOException("LimitExceeded / 網路資源已滿或已關閉。"); }
    long id = next.incrementAndGet(); resources.put(id, value); return id;
  }
  private <T> T resource(JSONObject a, Class<T> type) throws Exception {
    Object value = resources.get(a.getLong("resource")); if (!type.isInstance(value)) throw new IOException("Closed / 網路資源已關閉。"); return type.cast(value);
  }
  private static JSONObject object(Object... values) { return NativeBridge.object(values); }
  private static byte[] bytes(JSONObject a) { return NativeBridge.unbase64(a.optString("bytes")); }
  private static JSONObject data(byte[] bytes) { return object("bytes", NativeBridge.base64(bytes)); }
  private static int timeout(JSONObject a) throws Exception { int n=a.optInt("timeout",10000);if(n<1||n>300000)throw new IllegalArgumentException("InvalidArgument / 逾時無效。");return n; }
  private static int size(JSONObject a,int fallback) { int n=a.optInt("size",fallback);if(n<1||n>MAX)throw new IllegalArgumentException("InvalidArgument / 大小無效。");return n; }
  private static InetSocketAddress address(JSONObject a) throws Exception { int port=a.getInt("port");if(port<0||port>65535)throw new IllegalArgumentException("InvalidArgument / 連接埠無效。");return new InetSocketAddress(a.getString("host"),port); }
  private Object execute(String op,JSONObject a) throws Exception {
    switch(op) {
      case "parseURL": { URI u=new URI(a.getString("url"));if(u.getScheme()==null||u.getHost()==null)throw new IllegalArgumentException("InvalidArgument / 網址無效。");return object("scheme",u.getScheme(),"host",u.getHost(),"path",u.getRawPath().isEmpty()?"/":u.getRawPath(),"query",u.getRawQuery()==null?"":"?"+u.getRawQuery(),"fragment",u.getRawFragment()==null?"":"#"+u.getRawFragment(),"port",u.getPort()<0?(u.getScheme().equals("https")||u.getScheme().equals("wss")?443:80):u.getPort()); }
      case "createClient": return object("id",own(new Client()));
      case "http": return http(resource(a,Client.class),a);
      case "clearCookies": resource(a,Client.class).jar.cookies.clear();return JSONObject.NULL;
      case "resolve": { JSONArray result=new JSONArray();for(InetAddress ip:InetAddress.getAllByName(a.getString("host")))result.put(ip.getHostAddress());return result; }
      case "connectTCP": {
        Socket raw=new Socket();try {raw.connect(address(a),timeout(a));raw.setSoTimeout(timeout(a));Socket socket=raw;
          if(a.optBoolean("tls")){SSLSocket secure=(SSLSocket)((SSLSocketFactory)SSLSocketFactory.getDefault()).createSocket(raw,a.getString("host"),a.getInt("port"),true);SSLParameters parameters=secure.getSSLParameters();parameters.setEndpointIdentificationAlgorithm("HTTPS");secure.setSSLParameters(parameters);secure.startHandshake();socket=secure;}
          Socket connected=socket;long id=own(connected);return object("id",id,"localAddress",connected.getLocalSocketAddress().toString(),"remoteAddress",connected.getRemoteSocketAddress().toString());
        }catch(Exception e){raw.close();throw e;}
      }
      case "read": { Socket s=resource(a,Socket.class);byte[] buffer=new byte[size(a,65536)];int count=s.getInputStream().read(buffer);return data(count<0?new byte[0]:Arrays.copyOf(buffer,count)); }
      case "write": { byte[] b=bytes(a);if(b.length>MAX)throw new IllegalArgumentException("LimitExceeded");Socket s=resource(a,Socket.class);s.getOutputStream().write(b);s.getOutputStream().flush();return JSONObject.NULL; }
      case "shutdownWrite": resource(a,Socket.class).shutdownOutput();return JSONObject.NULL;
      case "openUDP": { MulticastSocket socket=new MulticastSocket(address(a));socket.setSoTimeout(timeout(a));long id=own(socket);return object("id",id,"localAddress",socket.getLocalSocketAddress().toString()); }
      case "udpSend": { byte[] b=bytes(a);if(b.length>65507)throw new IllegalArgumentException("LimitExceeded / 資料報過大。");resource(a,MulticastSocket.class).send(new DatagramPacket(b,b.length,address(a)));return JSONObject.NULL; }
      case "udpReceive": { byte[] b=new byte[65536];DatagramPacket p=new DatagramPacket(b,b.length);resource(a,MulticastSocket.class).receive(p);if(p.getLength()>size(a,65507))throw new IOException("LimitExceeded / 資料報超過上限。");return object("bytes",NativeBridge.base64(Arrays.copyOf(b,p.getLength())),"address",p.getAddress().getHostAddress(),"port",p.getPort()); }
      case "joinMulticast": resource(a,MulticastSocket.class).joinGroup(InetAddress.getByName(a.getString("address")));return JSONObject.NULL;
      case "leaveMulticast": resource(a,MulticastSocket.class).leaveGroup(InetAddress.getByName(a.getString("address")));return JSONObject.NULL;
      case "setBroadcast": resource(a,MulticastSocket.class).setBroadcast(a.getBoolean("enabled"));return JSONObject.NULL;
      case "websocket": { SocketMessages messages=new SocketMessages(a);try{if(!messages.ready.await(timeout(a),TimeUnit.MILLISECONDS))throw new SocketTimeoutException("Timeout / WebSocket 連線逾時。");if(messages.failure!=null)throw new IOException(messages.failure);long id=own(messages);return object("id",id);}catch(Exception e){messages.close();throw e;} }
      case "wsSend": { SocketMessages s=resource(a,SocketMessages.class);byte[] b=bytes(a);if(b.length>s.maximum)throw new IOException("LimitExceeded");boolean sent=a.getBoolean("text")?s.socket.send(new String(b,StandardCharsets.UTF_8)):s.socket.send(ByteString.of(b));if(!sent)throw new IOException("Closed / WebSocket 已關閉。");return JSONObject.NULL; }
      case "wsReceive": { SocketMessages s=resource(a,SocketMessages.class);JSONObject value=s.messages.poll(s.timeout,TimeUnit.MILLISECONDS);if(value==null)throw new SocketTimeoutException("Timeout / WebSocket 接收逾時。");s.queuedBytes.addAndGet(-value.optInt("length"));if(s.failure!=null)throw new IOException(s.failure);return value; }
      case "wsClose": { SocketMessages s=resource(a,SocketMessages.class);s.socket.close(a.optInt("status",1000),a.optString("reason"));return JSONObject.NULL; }
      case "dispose": { AutoCloseable r=resources.remove(a.getLong("resource"));if(r!=null)r.close();return JSONObject.NULL; }
      default: throw new IllegalArgumentException("Unsupported / 未知網路能力："+op);
    }
  }
  private JSONObject http(Client owner,JSONObject a) throws Exception {
    int limit=a.optInt("maxBodyBytes",MAX);if(limit<1||limit>MAX)throw new IllegalArgumentException("InvalidArgument / 本文上限無效。");
    OkHttpClient client=owner.client.newBuilder().callTimeout(timeout(a),TimeUnit.MILLISECONDS).connectTimeout(timeout(a),TimeUnit.MILLISECONDS).readTimeout(timeout(a),TimeUnit.MILLISECONDS).followRedirects(false).followSslRedirects(false).cookieJar(a.optString("credentials").equals("omit")?CookieJar.NO_COOKIES:owner.jar).build();
    String method=a.getString("method");byte[] body=a.has("bytes")?bytes(a):null;if(body!=null&&body.length>limit)throw new IllegalArgumentException("LimitExceeded");
    JSONObject headers=a.optJSONObject("headers");Request.Builder builder=new Request.Builder().url(a.getString("url"));if(headers!=null)for(Iterator<String> keys=headers.keys();keys.hasNext();){String name=keys.next();builder.header(name,headers.getString(name));}
    RequestBody content=body==null?null:RequestBody.create(null,body);if(content==null&&Arrays.asList("POST","PUT","PATCH","PROPPATCH","REPORT").contains(method))content=RequestBody.create(null,new byte[0]);
    Request request=builder.method(method,content).build();long end=System.nanoTime()+TimeUnit.MILLISECONDS.toNanos(timeout(a));
    for(int redirects=0;;redirects++) {
      if(Thread.interrupted())throw new InterruptedIOException("Cancelled / 已取消。");
      Call call=client.newCall(request);call.timeout().timeout(Math.max(1,end-System.nanoTime()),TimeUnit.NANOSECONDS);
      try(Response response=call.execute()) {
        String location=response.header("Location");boolean redirect=Arrays.asList(301,302,303,307,308).contains(response.code())&&location!=null&&a.optBoolean("followRedirects",true)&&a.optInt("maxRedirects",10)>0;
        if(redirect){if(redirects>=a.optInt("maxRedirects",10))throw new IOException("RedirectLimit / 重新導向超過上限。");HttpUrl target=request.url().resolve(location);if(target==null)throw new IOException("Invalid redirect URL");Request.Builder next=request.newBuilder().url(target);if(!target.host().equals(request.url().host())||target.port()!=request.url().port()||!target.scheme().equals(request.url().scheme()))next.removeHeader("Authorization").removeHeader("Cookie");
          if(response.code()==303&&!request.method().equals("HEAD")||(response.code()==301||response.code()==302)&&request.method().equals("POST"))next.method("GET",null).removeHeader("Content-Type").removeHeader("Content-Length");request=next.build();continue;}
        ResponseBody payload=response.body();if(payload!=null&&payload.contentLength()>limit)throw new IOException("LimitExceeded / 回應本文過大。");byte[] bytes=payload==null?new byte[0]:NativeBridge.readBounded(payload.byteStream(),limit);JSONObject resultHeaders=new JSONObject();for(String name:response.headers().names())resultHeaders.put(name,String.join(", ",response.headers(name)));
        return object("status",response.code(),"url",response.request().url().toString(),"headers",resultHeaders,"bytes",NativeBridge.base64(bytes));
      }
    }
  }
  private static final class Jar implements CookieJar {
    final Map<String,Cookie> cookies=new ConcurrentHashMap<>();
    public void saveFromResponse(HttpUrl url,List<Cookie> received){for(Cookie c:received){if(cookies.size()>=512)cookies.clear();cookies.put(c.name()+"\n"+c.domain()+"\n"+c.path(),c);}}
    public List<Cookie> loadForRequest(HttpUrl url){cookies.values().removeIf(c->c.expiresAt()<System.currentTimeMillis());List<Cookie> result=new ArrayList<>();for(Cookie c:cookies.values())if(c.matches(url))result.add(c);return result;}
  }
  private static final class Client implements AutoCloseable {
    final Jar jar=new Jar();final OkHttpClient client=new OkHttpClient.Builder().cookieJar(jar).build();
    public void close(){client.dispatcher().cancelAll();client.connectionPool().evictAll();client.dispatcher().executorService().shutdown();jar.cookies.clear();}
  }
  private static final class SocketMessages extends WebSocketListener implements AutoCloseable {
    final BlockingQueue<JSONObject> messages=new ArrayBlockingQueue<>(256);final CountDownLatch ready=new CountDownLatch(1);final java.util.concurrent.atomic.AtomicInteger queuedBytes=new java.util.concurrent.atomic.AtomicInteger();
    final OkHttpClient client;final WebSocket socket;final int timeout,maximum;volatile String failure;
    SocketMessages(JSONObject a) throws Exception {timeout=timeout(a);maximum=a.optInt("maxBodyBytes",MAX);client=new OkHttpClient.Builder().connectTimeout(timeout,TimeUnit.MILLISECONDS).pingInterval(20,TimeUnit.SECONDS).build();Request.Builder b=new Request.Builder().url(a.getString("url"));JSONObject headers=a.optJSONObject("headers");if(headers!=null)for(Iterator<String> keys=headers.keys();keys.hasNext();){String name=keys.next();b.header(name,headers.getString(name));}socket=client.newWebSocket(b.build(),this);}
    public void onOpen(WebSocket s,Response r){ready.countDown();}
    private void add(byte[] bytes,boolean text){int length=queuedBytes.addAndGet(bytes.length);if(bytes.length>maximum||length>MAX||!messages.offer(object("bytes",NativeBridge.base64(bytes),"isText",text,"length",bytes.length))){failure="LimitExceeded / WebSocket 佇列已滿。";socket.cancel();messages.clear();messages.offer(object("closed",true));}}
    public void onMessage(WebSocket s,String text){add(text.getBytes(StandardCharsets.UTF_8),true);}
    public void onMessage(WebSocket s,ByteString bytes){add(bytes.toByteArray(),false);}
    public void onClosing(WebSocket s,int code,String reason){s.close(code,reason);messages.offer(object("closed",true));}
    public void onFailure(WebSocket s,Throwable e,Response r){failure="Network / WebSocket 連線失敗。";ready.countDown();messages.offer(object("closed",true));}
    public void close(){socket.cancel();client.dispatcher().cancelAll();client.dispatcher().executorService().shutdown();client.connectionPool().evictAll();}
  }
  public synchronized void close(){if(closed)return;closed=true;for(Future<?> f:jobs.values())f.cancel(true);jobs.clear();workers.shutdownNow();for(AutoCloseable r:resources.values())try{r.close();}catch(Exception ignored){}resources.clear();}
}
