# Network / 網路

`匯入("Network")` and `匯入("網路")` load one module: HTTP(S) clients with streamed responses, WebSocket, raw TCP and
TLS streams, UDP datagrams, DNS, and an HTTP(S) server with routes, middleware, static files, CORS, cookies, sessions,
Server-Sent Events and WebSocket upgrades. Every function, class and field keeps the names, Traditional Chinese
aliases, argument counts, results and error messages of the previous implementation (`docs/networking.md`;
`AquariusCore/network/NetworkCatalog.cs`); `src/api.def` is the whole API in one table.

```text
變數 網路 = 匯入("網路");
變數 用戶端 = 網路.建立用戶端({"逾時":5000, "認證":"include"});
變數 回應 = 用戶端.請求("POST", "https://example.com/api", {"JSON":{"訊息":"你好"}});
如果 (回應.成功) { 印出(回應.解析JSON()); } 否則 { 印出(回應.狀態碼, 回應.錯誤.訊息); }
```

## Platforms

| Feature / 能力 | Windows, Linux, Android | Web |
| --- | --- | --- |
| `http`, `cookies`, `http-stream` | HTTP/1.1 client: keep-alive pool, cookie container, redirects, gzip/deflate | `fetch` (the browser's CORS, cookies, redirects) |
| `cookie-control` | `ClearCookies` | Unsupported |
| `websocket` | client and server upgrades, `ws` and `wss` | the browser's `WebSocket` (no custom headers) |
| `tcp`, `tls`, `udp`, `dns` | sockets, Mbed TLS | Unsupported |
| `tcp-listen`, `http-server` | listeners, HTTP and HTTPS servers | Unsupported |

`網路.支援(name)` and `網路.能力()` report the table for the running target. Unsupported functions fail with
`Unsupported / 不支援：<platform> / <feature>` as before.

## How it is built

- `src/` (shared by every target): registration (`network.c` expanding `api.def` with X-macros), option validation,
  records, errors, JSON (`values.c`), URLs (`url.c`), client bookkeeping and streamed text (`client.c`) and WebSocket
  objects (`websocket.c`). `engine.h` is what they need from an engine.
- `src/native/` (Windows, Linux, Android): blocking I/O on a pool of worker threads (`hub.c`) with deadlines,
  cancellation and the runtime's stop flag checked every 50 ms, results delivered with `aqua_post`; non-blocking
  sockets with staggered connection attempts (`socket.c`); Mbed TLS (`tls.c`); the HTTP/1.1 wire format (`http.c`);
  the client (`transfer.c`, `cookies.c`, `inflate.c`), WebSocket frames (`frames.c`), raw sockets (`streams.c`) and the
  server (`server.c`). Scripts wait without blocking the runtime (`aqua_defer`).
- `src/platform/<os>.c`: the clock, randomness, the system's trusted roots — Windows `CertOpenSystemStore("ROOT")`,
  Linux the distribution's CA bundle (or `/etc/ssl/certs`), Android the `AndroidCAStore` key store (system and user
  certificates, through JNI; the certificate directories otherwise) — and, on Android, the app's network security
  policy for cleartext traffic (`NetworkSecurityPolicy.isCleartextTrafficPermitted`).
- `src/web/web.c`: the same requests through `web-glue/network` (fetch with a streamed body and an AbortController,
  WebSocket, setTimeout). Request building, limits, deadlines, redirect modes and error mapping stay in C.
- The library compiles as one translation unit per kind of target (`src/network_native.c`, `src/network_web.c`), so
  every function is static and `aqua_library_network` is the only symbol it adds.

### Dependencies

- **Mbed TLS 3.6.7** (LTS; Apache-2.0 OR GPL-2.0-or-later, `third_party/mbedtls/LICENSE`), downloaded by
  `CMakeLists.txt` from the official GitHub release, pinned by SHA-256, compiled into the library with
  `third_party/mbedtls/aqua_mbedtls_config.h` (TLS 1.2 and 1.3, ECDHE, AES-GCM, ChaCha20-Poly1305, RSA/ECDSA, PEM and
  PKCS#12 keys, thread-safe PSA crypto). `aqua_mbedtls_names.h` renames its ~950 external symbols to
  `aqua_network_*`, so no other copy of Mbed TLS can clash; refresh that list when updating Mbed TLS (instructions
  inside). Mbed TLS rather than BearSSL (no TLS 1.3, unmaintained) or the operating systems' TLS stacks (three
  different APIs, no shared code): one portable, audited implementation for every native target. The 3.6 branch
  keeps the classic crypto API the PKCS#12 reader uses.
- gzip/deflate decoding is the library's own (`src/native/inflate.c`); the web needs neither (the browser decodes).

## Tests

`tests/<case>.aqua` with the exact stdout in `tests/<case>.expected`, run from the `tests` folder (they read `certs/`
and `public/`):

| Case | Targets | Covers |
| --- | --- | --- |
| `basics` | all | aliases, capabilities, URLs, query encoding, option validation, `Try`, closed clients |
| `http_server` | sockets | server and client round trips: routes, middleware, JSON, forms, cookies, sessions, redirects, errors, limits, timeouts, cancellation, CORS, static files, gzip/deflate, SSE |
| `websocket` | sockets | client and server WebSocket, limits, closing, failed handshakes (handlers run concurrently under `Serve`) |
| `tcp_udp` | sockets | TCP streams, listeners, UDP, multicast, DNS |
| `tls` | sockets | HTTPS servers from AES and 3DES PKCS#12 and PEM identities; untrusted peers refused |
| `web_client` | all, the web included | HTTP, streams and WebSocket against `tests/servers/peer.aqua` (`http://127.0.0.1:18790`), started first on a native runtime |

The certificates in `tests/certs` belong to a private test CA (`ca.pem`, password of the PFX files `aquarius`);
with `SSL_CERT_FILE=certs/ca.pem` a native client trusts it, which checks successful HTTPS and WSS by hand.

## Differences from the old implementation

The old behavior these differences replace came from .NET (HttpClient, Kestrel) or the browser/Java adapters:

- **Concurrent `Serve`**: handlers run as tasks; one that waits for network I/O (a WebSocket `Receive`, a client
  request) no longer blocks the others. `Poll` still handles one request and returns when its handler finishes.
  Before, the Aquarius thread itself blocked on I/O.
- **Android** has every native feature now (`tcp-listen`, `http-server`, `http-stream` too): the same C code runs
  there. Cleartext HTTP/WebSocket clients still follow the app's network security configuration.
- Argument count and type errors come from the runtime's signature check ("Network.Get expects 1 to 2 arguments")
  instead of "Expected / 參數數量"; a null optional argument counts as absent.
- JSON written by the library keeps non-ASCII text as UTF-8 (System.Text.Json escaped it as `\uXXXX`); numbers use the
  shortest round-trip form. Both read back the same.
- `Response.Url` is the escaped URL (`/%E5%9B%9E`), as fetch reports it, on every target (.NET printed it unescaped).
- `ParseURL` follows the same rules on every target (the web used the browser's `URL`); an invalid URL fails with
  `InvalidArgument: Invalid URL / 網址無效。`.
- The native client sends `Accept-Encoding: gzip, deflate` (no Brotli) and does not use system HTTP proxies; HTTP/2
  is not negotiated (ALPN `http/1.1`). The server does not compress responses (Kestrel's response compression).
- Messages that quoted .NET or OS exceptions (connection refused, DNS) are now fixed bilingual texts with the same
  error kinds; DNS failures of raw sockets read `No such host is known / 找不到主機。`.
- HTTPS identities: PKCS#12 files protected with PBES2 (AES or 3DES, PBKDF2) or PKCS#12 SHA-1 3DES, or a PEM file with
  the certificate chain and key. RC2-protected PFX files (old Windows "legacy" exports) are not readable.
- Connections to names with several addresses start the next address after 250 ms (Happy Eyeballs), so `localhost`
  no longer waits seconds for a refused `::1` on Windows. Windows UDP sockets ignore ICMP "port unreachable".
- `Request.Session()` called twice in one request returns the same new session (it used to create two).
- `SSL_CERT_FILE`, when set, adds that file's certificates to the trusted roots (OpenSSL's convention).
