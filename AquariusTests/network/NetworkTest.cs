using System.Net;
using System.Net.Sockets;
using System.Text;
using AquariusLang.Compiler;
using AquariusLang.Desktop;
using AquariusLang.Desktop.Network;
using AquariusLang.Network;
using AquariusLang.Object;
using AquariusLang.runtime;
using Xunit;

namespace AquariusTests.Network;

public class NetworkTest {
    private static IObject Call(ModuleObj m,string name,params IObject[] args)=>Assert.IsType<BuiltinObj>(m._Environment.Get(name,out _)).Fn(args);
    private static string Text(ModuleObj m,string name)=>Assert.IsType<StringObj>(m._Environment.Get(name,out _)).Value;
    private static StringObj S(string s)=>new(s);
    private static BuiltinObj Handler(Func<IObject[],IObject> fn)=>new(a=>fn(a));
    [Fact] public void CoreImportsWithoutPlatformAndAliasesShareIdentity(){
        using var runtime=new NetworkRuntime(new NetworkBackend());Assert.True(runtime.TryImport("網路",out var m));Assert.True(runtime.TryImport("Network",out var same));Assert.Same(m,same);
        Assert.Same(m._Environment.Get("取得",out _),m._Environment.Get("Get",out _));Assert.False(Assert.IsType<BooleanObj>(Call(m,"支援",S("tcp"))).Value);
        var r=Assert.IsType<ModuleObj>(Call(m,"取得",S("http://localhost/")));Assert.Equal(0,Assert.IsType<IntegerObj>(r._Environment.Get("狀態碼",out _)).Value);
        Assert.Equal("Unsupported",Text(Assert.IsType<ModuleObj>(r._Environment.Get("錯誤",out _)),"種類"));
        Assert.All(NetworkCatalog.Functions,f=>Assert.Equal(2,f.Names.Count));Assert.DoesNotContain(typeof(NetworkRuntime).Assembly.GetReferencedAssemblies(),a=>a.Name!.Contains("AspNetCore"));
    }
    [Fact] public void ChineseWasmUsesBilingualDefaultNetworkLibrary(){
        using var builtins=new DesktopBuiltins();var result=new CompiledRuntime(builtins).Execute(new LoweringCompiler().Compile("變數 網路=匯入(\"網路\");變數 網址=網路.解析網址(\"https://example.com:8443/路徑?a=b\");網路.支援(\"網頁伺服器\") && 網路.Get==網路.取得 && 網址.連接埠==8443 && 網路.編碼查詢({\"訊息\":\"星泉 空白\"})==\"%E8%A8%8A%E6%81%AF=%E6%98%9F%E6%B3%89%20%E7%A9%BA%E7%99%BD\";"));
        Assert.True(Assert.IsType<BooleanObj>(result).Value);
    }
    [Fact] public async Task TcpBinaryRoundTripEofAndClosedResource(){
        using var b=new DesktopNetworkBackend();using var listener=b.ListenTcp("127.0.0.1",0,new());int port=IPEndPoint.Parse(listener.LocalAddress).Port;
        Assert.Null(await listener.Accept(0));var accept=listener.Accept(1000).AsTask();using var client=await b.ConnectTcp("127.0.0.1",port,new());using var server=await accept;Assert.NotNull(server);
        await client.Write(new byte[]{0,255,1,42});Assert.Equal(new byte[]{0,255,1,42},await server!.Read(4));await server.Write(Encoding.UTF8.GetBytes("星泉"));Assert.Equal("星泉",Encoding.UTF8.GetString(await client.Read(64)));
        client.ShutdownWrite();Assert.Empty(await server.Read(4));client.Dispose();await Assert.ThrowsAsync<ObjectDisposedException>(()=>client.Read(1).AsTask());
    }
    [Fact] public async Task UdpPreservesPacketBoundariesAndRejectsOversize(){
        using var b=new DesktopNetworkBackend();using var a=b.OpenUdp("127.0.0.1",0,new());using var c=b.OpenUdp("127.0.0.1",0,new());int port=IPEndPoint.Parse(c.LocalAddress).Port;
        await a.Send("127.0.0.1",port,new byte[]{1,0,255});var packet=await c.Receive(16);Assert.Equal(new byte[]{1,0,255},packet.Bytes);Assert.Equal("127.0.0.1",packet.Address);
        await Assert.ThrowsAsync<ArgumentException>(()=>a.Send("127.0.0.1",port,new byte[65508]).AsTask());
        await a.Send("127.0.0.1",port,new byte[10]);await Assert.ThrowsAsync<IOException>(()=>c.Receive(2).AsTask());
    }
    [Fact] public async Task HttpCookiesRedirectsErrorsTimeoutAndLimits(){
        using var b=new DesktopNetworkBackend();using var server=b.ListenHttp("http://127.0.0.1:0",new());using var client=b.CreateClient(new());
        async Task<NetworkResponse> Request(string path,Action<INetworkRequest> handler,NetworkOptions? options=null){var send=client.Send("GET",server.Address+path,options??new()).AsTask();using var req=await server.Accept(5000);Assert.NotNull(req);handler(req!);req!.Dispose();return await send;}
        var set=await Request("/set",r=>{r.Header("Set-Cookie","token=abc; Path=/; HttpOnly");r.Header("Content-Type","text/plain; charset=utf-8");r.Write(Encoding.UTF8.GetBytes("你好")).AsTask().GetAwaiter().GetResult();});Assert.True(set.Success);Assert.Equal("你好",Encoding.UTF8.GetString(set.Body));
        await Request("/cookie",r=>Assert.Equal("abc",r.Cookies["token"]));client.ClearCookies();await Request("/clear",r=>Assert.Empty(r.Cookies));
        var error=await Request("/bad",r=>r.Status(422));Assert.False(error.Success);Assert.Equal(422,error.Status);Assert.Equal("HTTP",error.Error!.Kind);
        var limit=await Request("/large",r=>r.Write(new byte[8]).AsTask().GetAwaiter().GetResult(),new(){MaxBodyBytes=2});Assert.Equal(0,limit.Status);Assert.NotNull(limit.Error);
        var timeout=await client.Send("GET",server.Address+"/slow",new(){TimeoutMilliseconds=20});Assert.Equal("Timeout",timeout.Error!.Kind);
        // Drain the timed-out request before checking a manual redirect.
        using(var abandoned=await server.Accept(100)){}
        var redirect=await Request("/redirect",r=>{r.Status(302);r.Header("Location","/target");},new(){FollowRedirects=false});Assert.Equal(302,redirect.Status);
        var invalid=await client.Send("GET","file:///secret",new());Assert.Equal("InvalidArgument",invalid.Error!.Kind);
    }
    [Fact] public async Task RoutesSessionsMiddlewareFailuresCorsAndStaticFiles(){
        using var runtime=new NetworkRuntime(new DesktopNetworkBackend());runtime.Invoke=(fn,args)=>Assert.IsType<BuiltinObj>(fn).Fn(args);runtime.TryImport("網路",out var net);
        var server=Assert.IsType<ModuleObj>(Call(net,"監聽",S("http://127.0.0.1:0")));string address=Text(server,"位址");
        Call(server,"使用中介",Handler(a=>Call((ModuleObj)a[1],"設定標頭",S("X-Test"),S("middleware"))));
        Call(server,"處理",S("GET"),S("/hello/{名稱}"),Handler(a=>Call((ModuleObj)a[1],"文字",Call((ModuleObj)a[0],"路由參數",S("名稱")))));
        Call(server,"處理",S("GET"),S("/session"),Handler(a=>{var session=(ModuleObj)Call((ModuleObj)a[0],"工作階段");var old=Call(session,"取得",S("計數"));int count=old is IntegerObj n?n.Value+1:1;Call(session,"設定",S("計數"),new IntegerObj(count));return Call((ModuleObj)a[1],"文字",S(count.ToString()));}));
        Call(server,"處理",S("GET"),S("/fail"),Handler(_=>new ErrorObj("private failure")));
        using var client=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer()});
        async Task<HttpResponseMessage> Request(string path,string? origin=null,HttpMethod? method=null){var request=new HttpRequestMessage(method??HttpMethod.Get,address+path);if(origin!=null)request.Headers.Add("Origin",origin);var pending=client.SendAsync(request);Assert.True(Assert.IsType<BooleanObj>(Call(server,"處理下一筆",new IntegerObj(5000))).Value);return await pending;}
        using var hello=await Request("/hello/星泉");Assert.Equal("星泉",await hello.Content.ReadAsStringAsync());Assert.Equal("middleware",hello.Headers.GetValues("X-Test").Single());
        using var one=await Request("/session");Assert.Equal("1",await one.Content.ReadAsStringAsync());Assert.Contains("HttpOnly",one.Headers.GetValues("Set-Cookie").Single());
        using var two=await Request("/session");Assert.Equal("2",await two.Content.ReadAsStringAsync());
        using var failed=await Request("/fail");Assert.Equal(HttpStatusCode.InternalServerError,failed.StatusCode);Assert.DoesNotContain("private",await failed.Content.ReadAsStringAsync());
        using var again=await Request("/hello/仍然正常");Assert.Equal(HttpStatusCode.OK,again.StatusCode);
        using var denied=await Request("/session","https://untrusted.example");Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        Call(server,"跨來源",new ArrayObj(new IObject[]{S("https://trusted.example")}));using var cors=await Request("/session","https://trusted.example");Assert.Equal("https://trusted.example",cors.Headers.GetValues("Access-Control-Allow-Origin").Single());
        using var wrongMethod=await Request("/session",method:HttpMethod.Post);Assert.Equal(HttpStatusCode.MethodNotAllowed,wrongMethod.StatusCode);
        string root=Path.Combine(Path.GetTempPath(),"aqua-network-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{File.WriteAllText(Path.Combine(root,"index.html"),"<p>星泉</p>");Call(server,"靜態檔案",S("/"),S(root));using var file=await Request("/");Assert.Contains("星泉",await file.Content.ReadAsStringAsync());Assert.Equal("text/html",file.Content.Headers.ContentType!.MediaType);}finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task StaticTraversalAndHeaderInjectionAreRejected(){
        using var b=new DesktopNetworkBackend();Assert.Null(await b.ReadPublicFile(Path.GetTempPath(),"../secret"));Assert.Null(await b.ReadPublicFile(Path.GetTempPath(),"%2e%2e/secret"));Assert.Null(await b.ReadPublicFile(Path.GetTempPath(),"C:\\secret"));
        Assert.Throws<ArgumentException>(()=>NetworkOptions.ValidateHeader("X-Test","a\r\nInjected: true"));Assert.Throws<ArgumentException>(()=>NetworkOptions.ValidateHeader("Bad:Name","x"));
        Assert.Throws<ArgumentException>(()=>new NetworkOptions{TimeoutMilliseconds=0}.Validate());
    }
    [Fact] public async Task WebSocketTextBinaryAndCloseRoundTrip(){
        using var b=new DesktopNetworkBackend();using var server=b.ListenHttp("http://127.0.0.1:0",new());var connect=b.ConnectWebSocket(server.Address.Replace("http:","ws:")+"/socket",new()).AsTask();using var request=await server.Accept(5000);Assert.NotNull(request);using var remote=await request!.UpgradeWebSocket();using var client=await connect;
        await client.Send(Encoding.UTF8.GetBytes("你好"),true);var text=await remote.Receive();Assert.True(text.IsText);Assert.Equal("你好",Encoding.UTF8.GetString(text.Bytes));await remote.Send(new byte[]{0,255},false);Assert.Equal(new byte[]{0,255},(await client.Receive()).Bytes);await client.Close(1000,"");Assert.True((await remote.Receive()).Closed);
    }
    [Fact] public async Task HttpsRequiresCertificateAndTlsRejectsUntrustedPeers(){
        using var b=new DesktopNetworkBackend();Assert.Throws<ArgumentException>(()=>b.ListenHttp("https://127.0.0.1:0",new()));
        using var rsa=System.Security.Cryptography.RSA.Create(2048);var csr=new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost",rsa,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pkcs1);using var cert=csr.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));string file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".pfx");File.WriteAllBytes(file,cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
        try{using var server=b.ListenHttp("https://127.0.0.1:0",new(){Certificate=file});using var client=b.CreateClient(new());var result=await client.Send("GET",server.Address,new(){TimeoutMilliseconds=2000});Assert.False(result.Success);Assert.NotNull(result.Error);await Assert.ThrowsAnyAsync<Exception>(()=>b.ConnectTcp("127.0.0.1",new Uri(server.Address).Port,new(){TimeoutMilliseconds=2000},true).AsTask());}finally{File.Delete(file);}
    }
    [Fact] public async Task PendingRequestCanBePolledCancelledAndClosed(){
        using var runtime=new NetworkRuntime(new DesktopNetworkBackend());runtime.TryImport("Network",out var net);
        using var b=new DesktopNetworkBackend();using var server=b.ListenHttp("http://127.0.0.1:0",new());
        var pending=Assert.IsType<ModuleObj>(Call(net,"開始請求",S("GET"),S(server.Address+"/slow")));
        Assert.IsType<NullObj>(Call(pending,"輪詢"));using var request=await server.Accept(5000);Assert.NotNull(request);Call(pending,"取消");
        var response=Assert.IsType<ModuleObj>(Call(pending,"等待"));var error=Assert.IsType<ModuleObj>(response._Environment.Get("錯誤",out _));Assert.Equal("Cancelled",Text(error,"種類"));
        Assert.True(Assert.IsType<BooleanObj>(Call(pending,"已完成")).Value);Call(pending,"關閉");Call(pending,"關閉");
    }
}
