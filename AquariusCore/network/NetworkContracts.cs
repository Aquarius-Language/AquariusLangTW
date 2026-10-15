using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AquariusLang.Network;

/// <summary>Host I/O boundary. Core owns the bilingual API; hosts own OS sockets and HTTP engines.</summary>
public interface INetworkBackend : IDisposable {
    runtime.LibraryCapabilities Capabilities { get; }
    INetworkClient CreateClient(NetworkOptions options);
    ValueTask<string[]> Resolve(string host, CancellationToken cancellation = default);
    ValueTask<INetworkStream> ConnectTcp(string host, int port, NetworkOptions options, bool tls = false, CancellationToken cancellation = default);
    INetworkListener ListenTcp(string address, int port, NetworkOptions options);
    INetworkDatagram OpenUdp(string address, int port, NetworkOptions options);
    ValueTask<INetworkWebSocket> ConnectWebSocket(string url, NetworkOptions options, CancellationToken cancellation = default);
    INetworkServer ListenHttp(string address, NetworkOptions options);
    ValueTask<NetworkFile?> ReadPublicFile(string root, string path, CancellationToken cancellation = default);
}
public sealed record NetworkFile(byte[] Bytes, string ContentType, string ETag);
public interface INetworkClient : IDisposable {
    ValueTask<NetworkResponse> Send(string method, string url, NetworkOptions options, CancellationToken cancellation = default);
    void ClearCookies();
}
public interface INetworkStream : IDisposable {
    string LocalAddress { get; }
    string RemoteAddress { get; }
    ValueTask<byte[]> Read(int maximumBytes, CancellationToken cancellation = default);
    ValueTask Write(byte[] bytes, CancellationToken cancellation = default);
    void ShutdownWrite();
}
public interface INetworkListener : IDisposable {
    string LocalAddress { get; }
    ValueTask<INetworkStream?> Accept(int timeoutMilliseconds, CancellationToken cancellation = default);
}
public sealed record NetworkPacket(byte[] Bytes, string Address, int Port);
public interface INetworkDatagram : IDisposable {
    string LocalAddress { get; }
    ValueTask Send(string host, int port, byte[] bytes, CancellationToken cancellation = default);
    ValueTask<NetworkPacket> Receive(int maximumBytes, CancellationToken cancellation = default);
    void JoinMulticast(string address);
    void LeaveMulticast(string address);
    void SetBroadcast(bool enabled);
}
public sealed record NetworkMessage(byte[] Bytes, bool IsText, bool Closed = false);
public interface INetworkWebSocket : IDisposable {
    ValueTask Send(byte[] bytes, bool text, CancellationToken cancellation = default);
    ValueTask<NetworkMessage> Receive(CancellationToken cancellation = default);
    ValueTask Close(int status, string reason, CancellationToken cancellation = default);
}
/// <summary>Requests are queued by adapters; script handlers execute on the owning Aquarius thread.</summary>
public interface INetworkServer : IDisposable {
    string Address { get; }
    ValueTask<INetworkRequest?> Accept(int timeoutMilliseconds, CancellationToken cancellation = default);
}
public interface INetworkRequest : IDisposable {
    string Method { get; }
    string Path { get; }
    string Url { get; }
    string RemoteAddress { get; }
    bool IsSecure { get; }
    IReadOnlyDictionary<string, string> Headers { get; }
    IReadOnlyDictionary<string, string> Query { get; }
    IReadOnlyDictionary<string, string> Cookies { get; }
    byte[] Body { get; }
    bool HasStarted { get; }
    void Status(int status);
    void Header(string name, string value, bool append = false);
    ValueTask Write(byte[] bytes, CancellationToken cancellation = default);
    ValueTask<INetworkWebSocket> UpgradeWebSocket(CancellationToken cancellation = default);
}
public sealed record NetworkError(string Kind, string Message, bool Retryable = false);
public sealed record NetworkResponse(int Status, string Url, IReadOnlyDictionary<string, string> Headers, byte[] Body, NetworkError? Error = null) {
    public bool Success => Error == null && Status is >= 200 and < 300;
}
public sealed record NetworkOptions {
    public const int MaximumBodyBytes = 16 * 1024 * 1024;
    public int TimeoutMilliseconds { get; init; } = 10000;
    public int MaxBodyBytes { get; init; } = MaximumBodyBytes;
    public int MaxRedirects { get; init; } = 10;
    public bool FollowRedirects { get; init; } = true;
    public string Credentials { get; init; } = "same-origin";
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public byte[]? Body { get; init; }
    public string? Certificate { get; init; }
    public string? CertificatePassword { get; init; }
    public void Validate() {
        if (TimeoutMilliseconds is < 1 or > 300000 || MaxBodyBytes is < 1 or > MaximumBodyBytes || MaxRedirects is < 0 or > 20)
            throw new ArgumentException("InvalidArgument / 參數無效：逾時 1..300000 ms、本文上限 1..16777216、重新導向 0..20。");
        if (Body?.Length > MaxBodyBytes) throw new ArgumentException("LimitExceeded / 超過上限：請求本文過大。");
        if (Credentials is not ("omit" or "same-origin" or "include")) throw new ArgumentException("Invalid credentials / 無效的認證模式。");
        foreach (var (name, value) in Headers) ValidateHeader(name, value);
    }
    public static void ValidateHeader(string name, string value) {
        if (string.IsNullOrEmpty(name) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) || value.Contains('\r') || value.Contains('\n'))
            throw new ArgumentException("Invalid HTTP header / 無效的 HTTP 標頭。");
    }
    public static void Port(int port, bool allowZero = false) {
        if (port < (allowZero ? 0 : 1) || port > 65535) throw new ArgumentException("Invalid port / 連接埠必須介於 1..65535。");
    }
}
