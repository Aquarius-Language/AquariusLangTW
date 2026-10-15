using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AquariusLang.Object;
using AquariusLang.runtime;
using static AquariusLang.Application.ApplicationValues;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.Network;

/// <summary>Shared language registration, validation and resource ownership for host networking.</summary>
public sealed partial class NetworkRuntime : IDisposable {
    private readonly INetworkBackend backend;
    private readonly HashSet<IDisposable> resources = new();
    private readonly ModuleObj module = NewModule();
    private readonly INetworkClient client;
    private bool disposed;
    public Func<IObject, IObject[], IObject>? Invoke { get; set; }
    public Func<string, string> ResourcePath { get; set; } = p => p;
    public NetworkRuntime(INetworkBackend backend) {
        this.backend = backend; client = backend.CreateClient(new()); resources.Add(client);
        Bind(module,"Network","Supports",a => new BooleanObj(backend.Capabilities.Supports(Text(a[0]))));
        Bind(module,"Network","Capabilities",_ => Hash(backend.Capabilities.Snapshot().ToDictionary(p=>p.Key,p=>(IObject)new BooleanObj(p.Value))));
        Field(module,"Platform","平台",new StringObj(backend.Capabilities.Platform));
        Field(module,"ContractVersion","契約版本",new IntegerObj(backend.Capabilities.ContractVersion));
        Bind(module,"Network","CreateClient",a => { var options=Options(a,0); return Client(backend.CreateClient(options),options); });
        RegisterRequests(module,"Network",client);
        Bind(module,"Network","Resolve",a => { Require("dns"); return new ArrayObj(Await(backend.Resolve(Text(a[0]))).Select(s=>(IObject)new StringObj(s)).ToArray()); });
        foreach (bool tls in new[] {false,true}) Bind(module,"Network",tls?"ConnectTLS":"ConnectTCP",a => {
            Require(tls?"tls":"tcp"); int port=Integer(a[1]); NetworkOptions.Port(port); return Stream(Await(backend.ConnectTcp(Text(a[0]),port,Options(a,2),tls)));
        });
        Bind(module,"Network","ListenTCP",a => { Require("tcp-listen"); int port=Integer(a[1]); NetworkOptions.Port(port,true); return Listener(backend.ListenTcp(Text(a[0]),port,Options(a,2))); });
        Bind(module,"Network","OpenUDP",a => { Require("udp"); int port=Integer(a[1]); NetworkOptions.Port(port,true); return Datagram(backend.OpenUdp(Text(a[0]),port,Options(a,2))); });
        Bind(module,"Network","ConnectWebSocket",a => { Require("websocket"); return WebSocket(Await(backend.ConnectWebSocket(Text(a[0]),Options(a,1)))); });
        Bind(module,"Network","Listen",a => { Require("http-server"); return Server(backend.ListenHttp(Text(a[0]),Options(a,1))); });
        Bind(module,"Network","EncodeQuery",a => new StringObj(string.Join("&",Dictionary(a[0]).Select(p => Uri.EscapeDataString(p.Key)+"="+Uri.EscapeDataString(Text(p.Value))))));
        Bind(module,"Network","ParseURL",a => {
            var uri = new Uri(Text(a[0]),UriKind.Absolute); var m=NewModule();
            foreach(var f in new[]{("Scheme","協定",uri.Scheme),("Host","主機",uri.Host),("Path","路徑",uri.AbsolutePath),("Query","查詢字串",uri.Query),("Fragment","片段",uri.Fragment)}) Field(m,f.Item1,f.Item2,new StringObj(f.Item3));
            Field(m,"Port","連接埠",new IntegerObj(uri.Port)); return m;
        });
        Bind(module,"Network","Try",a => {
            try { var value=InvokeCallback(a[0]); return value is ErrorObj e ? Outcome(null,new("OperationFailed",e.Message)) : Outcome(value,null); }
            catch(Exception e) when (Recoverable(e)) { return Outcome(null,Failure(e)); }
        });
    }
    public bool TryImport(string name,out ModuleObj result) { result=module; return name is "Network" or "網路"; }
    private void Require(string feature) { if(!backend.Capabilities.Supports(feature)) throw new NotSupportedException($"Unsupported / 不支援：{backend.Capabilities.Platform} / {feature}"); }
    private IObject InvokeCallback(IObject fn, params IObject[] args) => Invoke?.Invoke(fn,args) ?? throw new NotSupportedException("Host callbacks unavailable / 主機不支援回呼。");
    private ModuleObj Client(INetworkClient c,NetworkOptions options) { Own(c); var m=NewModule(); RegisterRequests(m,"Network.Client",c,options); Bind(m,"Network.Client","ClearCookies",_=> { Require("cookie-control"); c.ClearCookies();return RepeatedPrimitives.NULL; }); Bind(m,"Network.Client","Close",_=>Close(c)); return m; }
    private void RegisterRequests(ModuleObj m,string library,INetworkClient c,NetworkOptions? defaults=null) {
        Bind(m,library,"Request",a => Response(Await(c.Send(Text(a[0]),Text(a[1]),Options(a,2,defaults)))));
        Bind(m,library,"Get",a => Response(Await(c.Send("GET",Text(a[0]),Options(a,1,defaults)))));
        Bind(m,library,"Post",a => Response(Await(c.Send("POST",Text(a[0]),Options(a,2,defaults) with { Body=Payload(a[1]) }))));
        Bind(m,library,"StartRequest",a => Operation(c,Text(a[0]),Text(a[1]),Options(a,2,defaults)));
    }
    private sealed class PendingRequest : IDisposable {
        internal readonly CancellationTokenSource Cancellation=new();
        internal Task<NetworkResponse> Task=null!;
        public void Dispose(){Cancellation.Cancel();Cancellation.Dispose();}
    }
    private ModuleObj Operation(INetworkClient client,string method,string url,NetworkOptions options) {
        var operation=new PendingRequest();Own(operation);
        try{operation.Task=client.Send(method,url,options,operation.Cancellation.Token).AsTask();}catch{Close(operation);throw;}
        var m=NewModule();NetworkResponse? result=null;
        IObject Result(){result??=operation.Task.GetAwaiter().GetResult();Close(operation);return Response(result);}
        Bind(m,"Network.Operation","IsDone",_=>new BooleanObj(operation.Task.IsCompleted));
        Bind(m,"Network.Operation","Cancel",_=>{if(resources.Contains(operation))operation.Cancellation.Cancel();return RepeatedPrimitives.NULL;});
        Bind(m,"Network.Operation","Poll",_=>operation.Task.IsCompleted?Result():RepeatedPrimitives.NULL);
        Bind(m,"Network.Operation","Wait",_=>Result());Bind(m,"Network.Operation","Close",_=>Close(operation));return m;
    }
    private ModuleObj Response(NetworkResponse r) {
        var m=NewModule(); Field(m,"Success","成功",new BooleanObj(r.Success)); Field(m,"Status","狀態碼",new IntegerObj(r.Status));
        Field(m,"Url","網址",new StringObj(r.Url)); Field(m,"Body","本文",new StringObj(Encoding.UTF8.GetString(r.Body)));
        Field(m,"Headers","標頭",StringHash(r.Headers)); Field(m,"Error","錯誤",r.Error==null?RepeatedPrimitives.NULL:Error(r.Error));
        Bind(m,"Network.Response","JSON",_=>Json(r.Body)); Bind(m,"Network.Response","Bytes",_=>Binary(r.Body));
        Bind(m,"Network.Response","Header",a=>new StringObj(Lookup(r.Headers,Text(a[0])))); return m;
    }
    private ModuleObj Stream(INetworkStream s) {
        Own(s); var m=NewModule(); Field(m,"LocalAddress","本機位址",new StringObj(s.LocalAddress)); Field(m,"RemoteAddress","遠端位址",new StringObj(s.RemoteAddress));
        Bind(m,"Network.Stream","Read",a=>Binary(Await(s.Read(Size(a[0])))));
        Bind(m,"Network.Stream","ReadText",a=>new StringObj(Encoding.UTF8.GetString(Await(s.Read(Size(a[0]))))));
        Bind(m,"Network.Stream","Write",a=>{Await(s.Write(Bytes(a[0])));return RepeatedPrimitives.NULL;});
        Bind(m,"Network.Stream","WriteText",a=>{Await(s.Write(Encoding.UTF8.GetBytes(Text(a[0]))));return RepeatedPrimitives.NULL;});
        Bind(m,"Network.Stream","ShutdownWrite",_=>{s.ShutdownWrite();return RepeatedPrimitives.NULL;}); Bind(m,"Network.Stream","Close",_=>Close(s)); return m;
    }
    private ModuleObj Listener(INetworkListener l) {
        Own(l); var m=NewModule(); Field(m,"LocalAddress","本機位址",new StringObj(l.LocalAddress));
        Bind(m,"Network.Listener","Accept",a=>{var s=Await(l.Accept(a.Length==0?10000:Wait(a[0])));return s==null?RepeatedPrimitives.NULL:Stream(s);}); Bind(m,"Network.Listener","Close",_=>Close(l)); return m;
    }
    private ModuleObj Datagram(INetworkDatagram d) {
        Own(d); var m=NewModule(); Field(m,"LocalAddress","本機位址",new StringObj(d.LocalAddress));
        Bind(m,"Network.Datagram","Send",a=>{int port=Integer(a[1]);NetworkOptions.Port(port);Await(d.Send(Text(a[0]),port,Payload(a[2])));return RepeatedPrimitives.NULL;});
        Bind(m,"Network.Datagram","Receive",a=>{
            var p=Await(d.Receive(a.Length==0?65507:Size(a[0],65535)));var r=NewModule();
            Field(r,"Data","資料",Binary(p.Bytes)); Field(r,"Text","文字",new StringObj(Encoding.UTF8.GetString(p.Bytes))); Field(r,"Address","位址",new StringObj(p.Address));Field(r,"Port","連接埠",new IntegerObj(p.Port));return r;
        });
        Bind(m,"Network.Datagram","JoinMulticast",a=>{d.JoinMulticast(Text(a[0]));return RepeatedPrimitives.NULL;});
        Bind(m,"Network.Datagram","LeaveMulticast",a=>{d.LeaveMulticast(Text(a[0]));return RepeatedPrimitives.NULL;});
        Bind(m,"Network.Datagram","SetBroadcast",a=>{d.SetBroadcast(Boolean(a[0]));return RepeatedPrimitives.NULL;});Bind(m,"Network.Datagram","Close",_=>Close(d));return m;
    }
    private ModuleObj WebSocket(INetworkWebSocket s) {
        Own(s);var m=NewModule();
        Bind(m,"Network.WebSocket","SendText",a=>{Await(s.Send(Encoding.UTF8.GetBytes(Text(a[0])),true));return RepeatedPrimitives.NULL;});
        Bind(m,"Network.WebSocket","SendBytes",a=>{Await(s.Send(Bytes(a[0]),false));return RepeatedPrimitives.NULL;});
        Bind(m,"Network.WebSocket","Receive",_=>{var message=Await(s.Receive());var r=NewModule();Field(r,"Text","文字",new StringObj(Encoding.UTF8.GetString(message.Bytes)));Field(r,"Data","資料",Binary(message.Bytes));Field(r,"IsText","是文字",new BooleanObj(message.IsText));Field(r,"Closed","已關閉",new BooleanObj(message.Closed));return r;});
        Bind(m,"Network.WebSocket","Close",a=>{Await(s.Close(a.Length>0?Integer(a[0]):1000,a.Length>1?Text(a[1]):""));return Close(s);});return m;
    }
    private void Bind(ModuleObj m,string library,string name,Func<IObject[],IObject> action) {
        var f=LibraryCatalog.Find(library,name) ?? throw new InvalidOperationException(library+"."+name);
        var fn=new BuiltinObj(a=>{
            try {ObjectDisposedException.ThrowIf(disposed,this);if(a.Length<f.MinimumArguments||a.Length>f.MaximumArguments)throw new ArgumentException($"Expected / 參數數量：{f.MinimumArguments}..{f.MaximumArguments}");return action(a);}
            catch(Exception e) when(Recoverable(e)){var error=Failure(e);return new ErrorObj($"{library}.{name}: {error.Kind}: {error.Message}");}
        }) {RetainedEnvironment=m._Environment};
        FunctionRegistration.Define(m._Environment,fn,f.TraditionalChineseName,f.EnglishName);
    }
    private void Own(IDisposable resource) { if(resources.Count>=1024){resource.Dispose();throw new IOException("LimitExceeded / 資源上限 1024。");}resources.Add(resource); }
    private IObject Close(IDisposable resource) { if(resources.Remove(resource))resource.Dispose();return RepeatedPrimitives.NULL; }
    private static ModuleObj NewModule()=>new(AquaEnvironment.NewEnvironment());
    private static void Field(ModuleObj m,string en,string zh,IObject value){m._Environment.Create(en,value);if(en!=zh)m._Environment.Create(zh,value);}
    private static HashObj Hash(IReadOnlyDictionary<string,IObject> fields)=>new(fields.ToDictionary(p=>new StringObj(p.Key).HashKey(),p=>new HashPair(new StringObj(p.Key),p.Value)));
    private static HashObj StringHash(IReadOnlyDictionary<string,string> fields)=>Hash(fields.ToDictionary(p=>p.Key,p=>(IObject)new StringObj(p.Value)));
    private static IReadOnlyDictionary<string,IObject> Dictionary(IObject value)=>value is HashObj h?h.Pairs.Values.ToDictionary(p=>Text(p.Key),p=>p.Value):throw new ArgumentException("Expected options hash / 請傳入雜湊表。");
    private static string Lookup(IReadOnlyDictionary<string,string> values,string key)=>values.FirstOrDefault(p=>p.Key.Equals(key,StringComparison.OrdinalIgnoreCase)).Value??"";
    private static IObject Json(byte[] bytes){using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=64});return FromJson(doc.RootElement);}
    private static byte[] Payload(IObject value)=>value is StringObj s?Encoding.UTF8.GetBytes(s.Value):value is ArrayObj?Bytes(value):JsonSerializer.SerializeToUtf8Bytes(JsonValue(value));
    private static int Size(IObject value,int max=NetworkOptions.MaximumBodyBytes){int n=Integer(value);if(n<1||n>max)throw new ArgumentException("Invalid read size / 讀取大小無效。");return n;}
    private static int Wait(IObject value){int n=Integer(value);if(n<0||n>300000)throw new ArgumentException("Invalid timeout / 逾時須介於 0..300000。");return n;}
    private static T Await<T>(ValueTask<T> value)=>value.AsTask().GetAwaiter().GetResult();
    private static void Await(ValueTask value)=>value.AsTask().GetAwaiter().GetResult();
    private static NetworkOptions Options(IObject[] a,int at,NetworkOptions? defaults=null) {
        defaults??=new();if(a.Length<=at)return defaults;var d=Dictionary(a[at]);
        IObject? Get(string en,string zh)=>d.TryGetValue(en,out var v)?v:d.GetValueOrDefault(zh);
        var headers=new Dictionary<string,string>(defaults.Headers,StringComparer.OrdinalIgnoreCase);
        if(Get("headers","標頭") is {} h)foreach(var p in Dictionary(h))headers[p.Key]=Text(p.Value);
        byte[]? body=Get("body","本文") is {} b?Payload(b):null;
        if(Get("json","JSON") is {} j){body=JsonSerializer.SerializeToUtf8Bytes(JsonValue(j));headers["Content-Type"]="application/json; charset=utf-8";}
        if(Get("form","表單") is {} f){body=Encoding.UTF8.GetBytes(string.Join("&",Dictionary(f).Select(p=>Uri.EscapeDataString(p.Key)+"="+Uri.EscapeDataString(Text(p.Value)))));headers["Content-Type"]="application/x-www-form-urlencoded";}
        var o=new NetworkOptions{TimeoutMilliseconds=Get("timeout","逾時") is {} t?Integer(t):defaults.TimeoutMilliseconds,MaxBodyBytes=Get("maxBodyBytes","本文上限") is {} m?Integer(m):defaults.MaxBodyBytes,
            MaxRedirects=Get("maxRedirects","重新導向上限") is {} r?Integer(r):defaults.MaxRedirects,FollowRedirects=Get("followRedirects","跟隨重新導向") is {} x?Boolean(x):defaults.FollowRedirects,
            Credentials=Get("credentials","認證") is {} c?Text(c):defaults.Credentials,Headers=headers,Body=body,
            Certificate=Get("certificate","憑證") is {} cert?Text(cert):null,CertificatePassword=Get("certificatePassword","憑證密碼") is {} password?Text(password):null};o.Validate();return o;
    }
    private static ModuleObj Error(NetworkError error){var m=NewModule();Field(m,"Kind","種類",new StringObj(error.Kind));Field(m,"Message","訊息",new StringObj(error.Message));Field(m,"Retryable","可重試",new BooleanObj(error.Retryable));return m;}
    private static ModuleObj Outcome(IObject? value,NetworkError? error){var m=NewModule();Field(m,"Success","成功",new BooleanObj(error==null));Field(m,"Value","值",value??RepeatedPrimitives.NULL);Field(m,"Error","錯誤",error==null?RepeatedPrimitives.NULL:Error(error));return m;}
    private static bool Recoverable(Exception e)=>e is ArgumentException or IOException or InvalidOperationException or NotSupportedException or OperationCanceledException or JsonException or OverflowException or System.Net.Sockets.SocketException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or System.Security.Authentication.AuthenticationException or UnauthorizedAccessException;
    public static NetworkError Failure(Exception e)=>e switch {
        IOException when e.Message.Contains("LimitExceeded",StringComparison.OrdinalIgnoreCase)=>new("LimitExceeded",e.Message),
        System.Net.Http.HttpRequestException{HttpRequestError:System.Net.Http.HttpRequestError.SecureConnectionError}=>new("TLS","TLS validation failed / TLS 驗證失敗。"),
        System.Net.Http.HttpRequestException{HttpRequestError:System.Net.Http.HttpRequestError.NameResolutionError}=>new("DNS","Host resolution failed / 主機解析失敗。",true),
        OperationCanceledException=>new("Timeout","Operation timed out or cancelled / 操作逾時或已取消。",true),ObjectDisposedException=>new("Closed","Resource closed / 資源已關閉。"),
        NotSupportedException=>new("Unsupported",e.Message),System.Security.Authentication.AuthenticationException=>new("TLS","TLS validation failed / TLS 驗證失敗。"),
        ArgumentException or JsonException or OverflowException=>new("InvalidArgument",e.Message),
        _=>new("Network",e.Message,true)
    };
    public void Dispose(){if(disposed)return;disposed=true;foreach(var r in resources.Reverse())try{r.Dispose();}catch(IOException){}resources.Clear();backend.Dispose();}
}
