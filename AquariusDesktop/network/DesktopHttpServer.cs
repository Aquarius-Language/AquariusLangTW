using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using AquariusLang.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AquariusLang.Desktop.Network;

/// <summary>Kestrel handles HTTP framing/TLS; a bounded queue isolates script execution from I/O threads.</summary>
internal sealed class DesktopHttpServer : INetworkServer {
    private readonly IWebHost host;
    private readonly Channel<Request> requests=Channel.CreateBounded<Request>(new BoundedChannelOptions(128){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly NetworkOptions options;
    private bool closed;
    private X509Certificate2? certificate;
    public string Address {get;}
    internal DesktopHttpServer(string address,NetworkOptions options){
        this.options=options;var uri=new Uri(address,UriKind.Absolute);if(uri.Scheme is not("http" or "https")||uri.AbsolutePath!="/"||uri.Query!=""||uri.UserInfo!="")throw new ArgumentException("Expected HTTP(S) listener origin / 需要 HTTP(S) 監聽來源。");
        if(uri.Scheme=="https"){if(options.Certificate==null)throw new ArgumentException("HTTPS requires PFX certificate / HTTPS 需要 PFX 憑證。");certificate=new X509Certificate2(options.Certificate,options.CertificatePassword);}
        try{
            host=new WebHostBuilder().UseKestrel(k=>{k.AddServerHeader=false;k.Limits.MaxRequestBodySize=options.MaxBodyBytes;k.Limits.MaxConcurrentConnections=256;k.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(15);k.Limits.KeepAliveTimeout=TimeSpan.FromSeconds(30);
                if(certificate!=null)k.ConfigureHttpsDefaults(t=>t.ServerCertificate=certificate);
            }).UseUrls(address).ConfigureLogging(l=>l.ClearProviders()).ConfigureServices(s=>s.AddResponseCompression()).Configure(app=>{
                app.UseResponseCompression();app.UseWebSockets(new WebSocketOptions{KeepAliveInterval=TimeSpan.FromSeconds(30)});app.Run(Enqueue);
            }).Build();host.Start();Address=host.ServerFeatures.Get<IServerAddressesFeature>()!.Addresses.First();
        }catch{certificate?.Dispose();throw;}
    }
    private async Task Enqueue(HttpContext context){
        try{
            using var body=new MemoryStream();var buffer=new byte[16384];int count;
            while((count=await context.Request.Body.ReadAsync(buffer,context.RequestAborted))>0){if(body.Length+count>options.MaxBodyBytes){context.Response.StatusCode=413;return;}body.Write(buffer,0,count);}
            using var request=new Request(context,body.ToArray(),options);
            if(!requests.Writer.TryWrite(request)){context.Response.StatusCode=503;return;}
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);timeout.CancelAfter(options.TimeoutMilliseconds);
            try{await request.Completion.Task.WaitAsync(timeout.Token);}
            catch(OperationCanceledException){context.Abort();}
        }catch(BadHttpRequestException e){if(!context.Response.HasStarted)context.Response.StatusCode=e.StatusCode;}
        catch(OperationCanceledException){context.Abort();}
    }
    public async ValueTask<INetworkRequest?> Accept(int timeoutMilliseconds,CancellationToken cancellation=default){
        ObjectDisposedException.ThrowIf(closed,this);if(timeoutMilliseconds==0)return Take();using var t=DesktopNetworkBackend.Timeout(timeoutMilliseconds,cancellation);
        try{while(await requests.Reader.WaitToReadAsync(t.Token)){var r=Take();if(r!=null)return r;}return null;}catch(OperationCanceledException) when(!cancellation.IsCancellationRequested){return null;}
    }
    private Request? Take(){while(requests.Reader.TryRead(out var r)){if(!r.IsClosed)return r;}return null;}
    public void Dispose(){if(closed)return;closed=true;requests.Writer.TryComplete();while(requests.Reader.TryRead(out var r))r.Dispose();using var t=new CancellationTokenSource(2000);try{host.StopAsync(t.Token).GetAwaiter().GetResult();}finally{host.Dispose();certificate?.Dispose();}}
    private sealed class Request : INetworkRequest {
        private readonly HttpContext context;private readonly NetworkOptions options;private bool closed;
        public readonly TaskCompletionSource Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Request(HttpContext context,byte[] body,NetworkOptions options){this.context=context;this.options=options;Body=body;Headers=context.Request.Headers.ToDictionary(h=>h.Key,h=>h.Value.ToString(),StringComparer.OrdinalIgnoreCase);Query=context.Request.Query.ToDictionary(h=>h.Key,h=>h.Value.ToString());Cookies=context.Request.Cookies.ToDictionary(p=>p.Key,p=>Uri.UnescapeDataString(p.Value));}
        public bool IsClosed=>closed||context.RequestAborted.IsCancellationRequested;
        private void Live(){ObjectDisposedException.ThrowIf(IsClosed,this);}
        public string Method=>context.Request.Method;
        public string Path=>context.Request.Path.Value??"/";
        public string Url=>context.Request.Scheme+"://"+context.Request.Host+context.Request.PathBase+context.Request.Path+context.Request.QueryString;
        public string RemoteAddress=>context.Connection.RemoteIpAddress?.ToString()??"";
        public bool IsSecure=>context.Request.IsHttps;
        public IReadOnlyDictionary<string,string> Headers{get;}
        public IReadOnlyDictionary<string,string> Query{get;}
        public IReadOnlyDictionary<string,string> Cookies{get;}
        public byte[] Body{get;}
        public bool HasStarted=>context.Response.HasStarted;
        public void Status(int status){Live();context.Response.StatusCode=status;}
        public void Header(string name,string value,bool append=false){Live();NetworkOptions.ValidateHeader(name,value);if(append)context.Response.Headers.Append(name,value);else context.Response.Headers[name]=value;}
        public async ValueTask Write(byte[] bytes,CancellationToken cancellation=default){Live();using var t=CancellationTokenSource.CreateLinkedTokenSource(cancellation,context.RequestAborted);if(Method!="HEAD"&&context.Response.StatusCode is not(204 or 304))await context.Response.Body.WriteAsync(bytes,t.Token);await context.Response.Body.FlushAsync(t.Token);}
        public async ValueTask<INetworkWebSocket> UpgradeWebSocket(CancellationToken cancellation=default){Live();if(!context.WebSockets.IsWebSocketRequest)throw new ArgumentException("Expected WebSocket upgrade / 需要 WebSocket 升級請求。");var ws=await context.WebSockets.AcceptWebSocketAsync();return new DesktopNetworkBackend.WebSocketAdapter(ws,options);}
        public void Dispose(){if(closed)return;closed=true;Completion.TrySetResult();}
    }
}
