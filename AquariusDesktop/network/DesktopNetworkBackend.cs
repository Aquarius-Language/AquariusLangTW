using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using AquariusLang.Network;
using AquariusLang.runtime;

namespace AquariusLang.Desktop.Network;

public sealed class DesktopNetworkBackend : INetworkBackend {
    public LibraryCapabilities Capabilities { get; } = new(OperatingSystem.IsWindows()?"windows":OperatingSystem.IsMacOS()?"macos":"linux",1,NetworkCatalog.Features,NetworkCatalog.Features.Select(f=>f.Name));
    public INetworkClient CreateClient(NetworkOptions options){options.Validate();return new HttpClientAdapter(options);}
    public async ValueTask<string[]> Resolve(string host,CancellationToken cancellation=default) {
        using var timeout=Timeout(10000,cancellation);return (await Dns.GetHostAddressesAsync(host,timeout.Token)).Select(a=>a.ToString()).Distinct().ToArray();
    }
    internal static CancellationTokenSource Timeout(int ms,CancellationToken cancellation){var t=CancellationTokenSource.CreateLinkedTokenSource(cancellation);t.CancelAfter(ms);return t;}
    public async ValueTask<INetworkStream> ConnectTcp(string host,int port,NetworkOptions options,bool tls=false,CancellationToken cancellation=default){
        options.Validate();NetworkOptions.Port(port);var client=new TcpClient();
        try{using var timeout=Timeout(options.TimeoutMilliseconds,cancellation);await client.ConnectAsync(host,port,timeout.Token);Stream stream=client.GetStream();
            if(tls){var secure=new SslStream(stream,false);stream=secure;await secure.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost=host},timeout.Token);}
            return new StreamAdapter(client,stream,options.TimeoutMilliseconds);
        }catch{client.Dispose();throw;}
    }
    public INetworkListener ListenTcp(string address,int port,NetworkOptions options){options.Validate();NetworkOptions.Port(port,true);return new ListenerAdapter(IPAddress.Parse(address),port,options.TimeoutMilliseconds);}
    public INetworkDatagram OpenUdp(string address,int port,NetworkOptions options){options.Validate();NetworkOptions.Port(port,true);return new DatagramAdapter(IPAddress.Parse(address),port,options.TimeoutMilliseconds);}
    public async ValueTask<INetworkWebSocket> ConnectWebSocket(string url,NetworkOptions options,CancellationToken cancellation=default){
        options.Validate();var uri=new Uri(url);if(uri.Scheme is not("ws" or "wss"))throw new ArgumentException("Expected ws:// or wss:// / 需要 WebSocket 網址。");var socket=new ClientWebSocket();
        try{foreach(var h in options.Headers)socket.Options.SetRequestHeader(h.Key,h.Value);using var timeout=Timeout(options.TimeoutMilliseconds,cancellation);await socket.ConnectAsync(uri,timeout.Token);return new WebSocketAdapter(socket,options);}catch{socket.Dispose();throw;}
    }
    public INetworkServer ListenHttp(string address,NetworkOptions options){options.Validate();return new DesktopHttpServer(address,options);}
    public async ValueTask<NetworkFile?> ReadPublicFile(string root,string path,CancellationToken cancellation=default) {
        root=Path.GetFullPath(root);path=Uri.UnescapeDataString(path);if(path.IndexOfAny(new[]{'\0','\\',':'})>=0||path.Split('/').Any(p=>p is "." or ".."))return null;
        string target=Path.GetFullPath(Path.Combine(root,path.Replace('/',Path.DirectorySeparatorChar)));string prefix=Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar;
        if(!target.StartsWith(prefix,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)&&target!=root)return null;
        if(Directory.Exists(target))target=Path.Combine(target,"index.html");
        // Deny all links in the root and its ancestry as well as inside the public tree.
        for(string? p=target;p!=null;p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)return null;
        if(!File.Exists(target))return null;if(new FileInfo(target).Length>NetworkOptions.MaximumBodyBytes)throw new IOException("LimitExceeded / 靜態檔案過大。");
        var bytes=await File.ReadAllBytesAsync(target,cancellation);if(bytes.Length>NetworkOptions.MaximumBodyBytes)throw new IOException("LimitExceeded / 靜態檔案過大。");
        string type=Path.GetExtension(target).ToLowerInvariant() switch {".html"=>"text/html; charset=utf-8",".css"=>"text/css; charset=utf-8",".js" or ".mjs"=>"text/javascript; charset=utf-8",".json"=>"application/json",".wasm"=>"application/wasm",".png"=>"image/png",".jpg" or ".jpeg"=>"image/jpeg",".svg"=>"image/svg+xml",".ico"=>"image/x-icon",".woff2"=>"font/woff2",".txt"=>"text/plain; charset=utf-8",_=>"application/octet-stream"};
        return new(bytes,type,"\""+Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()+"\"");
    }
    public void Dispose(){}

    private sealed class HttpClientAdapter(NetworkOptions defaults) : INetworkClient {
        private readonly CookieContainer cookies=new();
        private readonly Dictionary<(bool,int,bool),HttpClient> clients=new();
        private bool closed;
        public async ValueTask<NetworkResponse> Send(string method,string url,NetworkOptions options,CancellationToken cancellation=default) {
            try {
                ObjectDisposedException.ThrowIf(closed,this);options.Validate();var uri=new Uri(url,UriKind.Absolute);if(uri.Scheme is not("http" or "https"))throw new ArgumentException("Expected HTTP(S) URL / 需要 HTTP(S) 網址。");
                var key=(options.FollowRedirects,options.MaxRedirects,options.Credentials!="omit");
                if(!clients.TryGetValue(key,out var client)){client=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=key.Item1&&key.Item2>0,MaxAutomaticRedirections=Math.Max(1,key.Item2),UseCookies=key.Item3,CookieContainer=cookies,AutomaticDecompression=DecompressionMethods.All,PooledConnectionLifetime=TimeSpan.FromMinutes(5),ConnectTimeout=TimeSpan.FromMilliseconds(options.TimeoutMilliseconds)}){Timeout=System.Threading.Timeout.InfiniteTimeSpan};clients.Add(key,client);}
                using var request=new HttpRequestMessage(new HttpMethod(method),uri);
                if(options.Body!=null)request.Content=new ByteArrayContent(options.Body);
                foreach(var h in defaults.Headers.Concat(options.Headers).GroupBy(h=>h.Key,StringComparer.OrdinalIgnoreCase).Select(g=>g.Last())){if(!request.Headers.TryAddWithoutValidation(h.Key,h.Value)){request.Content??=new ByteArrayContent(Array.Empty<byte>());request.Content.Headers.Remove(h.Key);request.Content.Headers.TryAddWithoutValidation(h.Key,h.Value);}}
                using var timeout=Timeout(options.TimeoutMilliseconds,cancellation);using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                if(response.Content.Headers.ContentLength>options.MaxBodyBytes)throw new IOException("LimitExceeded / 回應本文過大。");
                await using var input=await response.Content.ReadAsStreamAsync(timeout.Token);using var body=new MemoryStream();var buffer=new byte[16384];int n;
                while((n=await input.ReadAsync(buffer,timeout.Token))>0){if(body.Length+n>options.MaxBodyBytes)throw new IOException("LimitExceeded / 回應本文過大。");body.Write(buffer,0,n);}
                int status=(int)response.StatusCode;return new(status,response.RequestMessage?.RequestUri?.ToString()??url,response.Headers.Concat(response.Content.Headers).ToDictionary(h=>h.Key,h=>string.Join(", ",h.Value),StringComparer.OrdinalIgnoreCase),body.ToArray(),status>=400?new("HTTP",$"HTTP {status}",status is 408 or 429 or >=500):null);
            }catch(Exception e) when(e is HttpRequestException or IOException or OperationCanceledException or ArgumentException or InvalidOperationException){return new(0,url,new Dictionary<string,string>(),Array.Empty<byte>(),e is OperationCanceledException && cancellation.IsCancellationRequested?new("Cancelled","Request cancelled / 請求已取消。"):NetworkRuntime.Failure(e));}
        }
        public void ClearCookies(){ObjectDisposedException.ThrowIf(closed,this);foreach(Cookie cookie in cookies.GetAllCookies())cookie.Expired=true;}
        public void Dispose(){if(closed)return;closed=true;foreach(var c in clients.Values)c.Dispose();clients.Clear();}
    }
    private sealed class StreamAdapter(TcpClient client,Stream stream,int timeout) : INetworkStream {
        public string LocalAddress {get;}=client.Client.LocalEndPoint!.ToString()!;
        public string RemoteAddress {get;}=client.Client.RemoteEndPoint!.ToString()!;
        private bool closed;
        public async ValueTask<byte[]> Read(int maximumBytes,CancellationToken cancellation=default){ObjectDisposedException.ThrowIf(closed,this);if(maximumBytes<1||maximumBytes>NetworkOptions.MaximumBodyBytes)throw new ArgumentException("Invalid read size");using var t=Timeout(timeout,cancellation);var bytes=new byte[maximumBytes];int n=await stream.ReadAsync(bytes,t.Token);return bytes[..n];}
        public async ValueTask Write(byte[] bytes,CancellationToken cancellation=default){ObjectDisposedException.ThrowIf(closed,this);if(bytes.Length>NetworkOptions.MaximumBodyBytes)throw new ArgumentException("Write too large");using var t=Timeout(timeout,cancellation);await stream.WriteAsync(bytes,t.Token);await stream.FlushAsync(t.Token);}
        public void ShutdownWrite(){ObjectDisposedException.ThrowIf(closed,this);client.Client.Shutdown(SocketShutdown.Send);}
        public void Dispose(){if(closed)return;closed=true;stream.Dispose();client.Dispose();}
    }
    private sealed class ListenerAdapter : INetworkListener {
        private readonly TcpListener listener;private readonly int timeout;private bool closed;
        public ListenerAdapter(IPAddress address,int port,int timeout){this.timeout=timeout;listener=new(address,port);listener.Start(128);LocalAddress=listener.LocalEndpoint.ToString()!;}
        public string LocalAddress {get;}
        public async ValueTask<INetworkStream?> Accept(int timeoutMilliseconds,CancellationToken cancellation=default){ObjectDisposedException.ThrowIf(closed,this);if(timeoutMilliseconds==0&&!listener.Pending())return null;using var t=Timeout(Math.Max(1,timeoutMilliseconds),cancellation);try{var c=await listener.AcceptTcpClientAsync(t.Token);return new StreamAdapter(c,c.GetStream(),timeout);}catch(OperationCanceledException) when(!cancellation.IsCancellationRequested){return null;}}
        public void Dispose(){if(closed)return;closed=true;listener.Stop();}
    }
    private sealed class DatagramAdapter : INetworkDatagram {
        private readonly UdpClient socket;private readonly int timeout;private bool closed;
        public DatagramAdapter(IPAddress address,int port,int timeout){this.timeout=timeout;socket=new(new IPEndPoint(address,port));LocalAddress=socket.Client.LocalEndPoint!.ToString()!;}
        public string LocalAddress {get;}
        public async ValueTask Send(string host,int port,byte[] bytes,CancellationToken cancellation=default){ObjectDisposedException.ThrowIf(closed,this);NetworkOptions.Port(port);if(bytes.Length>65507)throw new ArgumentException("Datagram too large / 資料報過大。");using var t=Timeout(timeout,cancellation);await socket.SendAsync(bytes,host,port,t.Token);}
        public async ValueTask<NetworkPacket> Receive(int maximumBytes,CancellationToken cancellation=default){ObjectDisposedException.ThrowIf(closed,this);using var t=Timeout(timeout,cancellation);var p=await socket.ReceiveAsync(t.Token);if(p.Buffer.Length>maximumBytes)throw new IOException("LimitExceeded / 資料報超過讀取上限。");return new(p.Buffer,p.RemoteEndPoint.Address.ToString(),p.RemoteEndPoint.Port);}
        public void JoinMulticast(string address)=>socket.JoinMulticastGroup(IPAddress.Parse(address));
        public void LeaveMulticast(string address)=>socket.DropMulticastGroup(IPAddress.Parse(address));
        public void SetBroadcast(bool enabled)=>socket.EnableBroadcast=enabled;
        public void Dispose(){if(closed)return;closed=true;socket.Dispose();}
    }
    internal sealed class WebSocketAdapter(WebSocket socket,NetworkOptions options) : INetworkWebSocket {
        private bool closed;
        public async ValueTask Send(byte[] bytes,bool text,CancellationToken cancellation=default){ObjectDisposedException.ThrowIf(closed,this);if(bytes.Length>options.MaxBodyBytes)throw new ArgumentException("Message too large");using var t=Timeout(options.TimeoutMilliseconds,cancellation);await socket.SendAsync(bytes,text?WebSocketMessageType.Text:WebSocketMessageType.Binary,true,t.Token);}
        public async ValueTask<NetworkMessage> Receive(CancellationToken cancellation=default){ObjectDisposedException.ThrowIf(closed,this);using var t=Timeout(options.TimeoutMilliseconds,cancellation);using var body=new MemoryStream();var buffer=new byte[16384];WebSocketReceiveResult r;
            do{r=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),t.Token);if(r.MessageType==WebSocketMessageType.Close)return new(Array.Empty<byte>(),false,true);if(body.Length+r.Count>options.MaxBodyBytes){Dispose();throw new IOException("LimitExceeded / WebSocket 訊息過大。");}body.Write(buffer,0,r.Count);}while(!r.EndOfMessage);return new(body.ToArray(),r.MessageType==WebSocketMessageType.Text);
        }
        public async ValueTask Close(int status,string reason,CancellationToken cancellation=default){if(closed)return;using var t=Timeout(options.TimeoutMilliseconds,cancellation);if(socket.State is WebSocketState.Open or WebSocketState.CloseReceived)await socket.CloseOutputAsync((WebSocketCloseStatus)status,reason,t.Token);}
        public void Dispose(){if(closed)return;closed=true;socket.Dispose();}
    }
}
