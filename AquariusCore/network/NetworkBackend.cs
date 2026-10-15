using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AquariusLang.runtime;

namespace AquariusLang.Network;

/// <summary>Opt-in adapter base. Future hosts implement only their supported features; imports remain stable.</summary>
public class NetworkBackend : INetworkBackend {
    public LibraryCapabilities Capabilities { get; }
    public NetworkBackend(string platform="core",IEnumerable<string>? supported=null)=>Capabilities=new(platform,1,NetworkCatalog.Features,supported??Array.Empty<string>());
    protected static NotSupportedException Unsupported()=>new("Unsupported / 此平台未提供這項網路能力。");
    public virtual INetworkClient CreateClient(NetworkOptions options)=>new UnavailableClient();
    public virtual ValueTask<string[]> Resolve(string host,CancellationToken cancellation=default)=>ValueTask.FromException<string[]>(Unsupported());
    public virtual ValueTask<INetworkStream> ConnectTcp(string host,int port,NetworkOptions options,bool tls=false,CancellationToken cancellation=default)=>ValueTask.FromException<INetworkStream>(Unsupported());
    public virtual INetworkListener ListenTcp(string address,int port,NetworkOptions options)=>throw Unsupported();
    public virtual INetworkDatagram OpenUdp(string address,int port,NetworkOptions options)=>throw Unsupported();
    public virtual ValueTask<INetworkWebSocket> ConnectWebSocket(string url,NetworkOptions options,CancellationToken cancellation=default)=>ValueTask.FromException<INetworkWebSocket>(Unsupported());
    public virtual INetworkServer ListenHttp(string address,NetworkOptions options)=>throw Unsupported();
    public virtual ValueTask<NetworkFile?> ReadPublicFile(string root,string path,CancellationToken cancellation=default)=>ValueTask.FromException<NetworkFile?>(Unsupported());
    public virtual void Dispose(){}
    private sealed class UnavailableClient : INetworkClient {
        public ValueTask<NetworkResponse> Send(string method,string url,NetworkOptions options,CancellationToken cancellation=default)=>new(new NetworkResponse(0,url,new Dictionary<string,string>(),Array.Empty<byte>(),new("Unsupported","No HTTP adapter / 未提供 HTTP 配接器。")));
        public void ClearCookies()=>throw Unsupported();
        public void Dispose(){}
    }
}
