# Network / 網路

`匯入("Network")` 與 `匯入("網路")` 共用同一個預設函式庫。所有公開函式、
物件方法及結果欄位提供英文與繁體中文名稱，兩個名稱指向同一個函式或值。
整個 API 定義在 `src/api.def` 一張表中；語言伺服器由執行環境的 `aquarius --catalog` 取得名稱與參數。

## 平台能力 / Platform capabilities

| 能力 / Feature | Windows、Linux、Android | Web |
| --- | --- | --- |
| `http` / `超文字傳輸` | HTTP / HTTPS（HTTP/1.1，連線池、gzip/deflate） | Fetch、CORS、瀏覽器安全政策 |
| `cookies` / `Cookie` | 每個用戶端獨立容器 | 瀏覽器管理 |
| `cookie-control` / `Cookie管理` | 清除容器 | 不支援 |
| `websocket` / `網頁通訊端` | 用戶端及伺服器升級 | 瀏覽器用戶端 |
| `tcp` / `傳輸控制協定` | 原始 TCP | 不支援 |
| `tls` / `傳輸層安全` | 憑證驗證、主機名稱驗證（Mbed TLS，系統信任根憑證） | HTTPS / WSS 由瀏覽器處理 |
| `udp` / `使用者資料報` | UDP、廣播、多點傳送 | 不支援 |
| `dns` / `網域名稱解析` | IPv4 / IPv6 | HTTP 引擎內部解析 |
| `tcp-listen` / `監聽傳輸控制` | TCP 監聽及接受 | 不支援 |
| `http-server` / `網頁伺服器` | HTTP、HTTPS、路由、靜態檔案、工作階段、SSE | 不支援 |
| `http-stream` / `串流傳輸` | 逐段讀取回應 | Fetch `ReadableStream` |

```text
變數 網路 = 匯入("網路");
如果 (網路.支援("tcp")) { 印出("可以使用原始 TCP"); }
印出(網路.能力());
```

一般網站沒有原始 TCP／UDP 權限。Chrome 的 Direct Sockets 是
[Isolated Web Apps 專用能力](https://developer.chrome.com/docs/iwa/direct-sockets)，
不是一般跨瀏覽器網站的 API。這個函式庫會回報不支援，不會模擬或悄悄改成其他協定。
Android 包含 INTERNET 權限，預設要求 HTTPS；僅對 localhost、127.0.0.1 和模擬器主機
10.0.2.2 開放開發用 HTTP。其他例外應在產生專案的
[Network Security Configuration](https://developer.android.com/privacy-and-security/security-config) 中明確設定。
Wi-Fi 多點傳送是否能接收還取決於裝置／網路與應用程式的 multicast lock。

## HTTP 用戶端 / HTTP clients

```text
變數 網路 = 匯入("網路");
變數 用戶端 = 網路.建立用戶端({"逾時":5000, "認證":"include"});
變數 回應 = 用戶端.請求("POST", "https://example.com/api", {"JSON":{"訊息":"你好"}});
如果 (回應.成功) { 印出(回應.解析JSON()); }
否則 { 印出(回應.狀態碼, 回應.錯誤.訊息); }
用戶端.關閉();
```

| English | 繁體中文 | 參數 / Arguments |
| --- | --- | --- |
| `CreateClient` | `建立用戶端` | `[options]`，獨立連線池與 Cookie 容器 |
| `Request` | `請求` | `method, url, [options]` |
| `Get` | `取得` | `url, [options]` |
| `Post` | `提交` | `url, body, [options]` |
| `StartRequest` | `開始請求` | `method, url, [options]`，立即回傳操作物件 |
| `StartStream` | `開始串流` | `method, url, [options]`，逐段讀取回應（`http-stream`，見下方串流回應） |
| `ClearCookies` | `清除Cookie` | 用戶端方法；Web 無法直接清除 HTTP-only Cookie |
| `Close` | `關閉` | 用戶端方法，可重複關閉 |
| `ParseURL` | `解析網址` | `url` → Scheme/協定、Host/主機、Port/連接埠、Path/路徑、Query/查詢字串、Fragment/片段 |
| `EncodeQuery` | `編碼查詢` | 字串鍵／值雜湊表，使用 UTF-8 百分比編碼 |
| `Try` | `嘗試` | 無參數回呼 → Success/成功、Value/值、Error/錯誤 |

模組本身也有 Request/Get/Post/StartRequest，使用共用的預設用戶端。
用戶端的預設標頭和選項可由個別請求覆寫。Request 接受所有標準 HTTP 方法；
GET/HEAD 不應附帶本文。Post 的本文可為字串、位元組陣列或 JSON 雜湊表；
用 `json` 選項可同時設定正確 Content-Type。

選項：`timeout/逾時`（預設 10000 ms，1..300000）、
`headers/標頭`、`body/本文`、`json/JSON`、`form/表單`（網址編碼表單）、
`maxBodyBytes/本文上限`（預設 16 MiB，最多 16 MiB）、
`followRedirects/跟隨重新導向`、`maxRedirects/重新導向上限`（預設 10，0..20）、
`credentials/認證`（omit、same-origin、include）。瀏覽器的重新導向次數、Cookie、
禁止的標頭、CORS、混合內容、憑證與 same-origin 規則由瀏覽器決定；
手動重新導向的 opaque response 回報 Unsupported。原生用戶端的 same-origin/include
使用 Cookie 容器，omit 停用 Cookie。Cookie 不會寫入磁碟。

回應欄位：`Success/成功`（2xx）、`Status/狀態碼`、`Url/網址`、`Body/本文`（UTF-8）、
`Headers/標頭`、`Error/錯誤`；方法 `JSON/解析JSON()`、`Bytes/取得位元組()`、
`Header/取得標頭(name)`。HTTP 4xx/5xx 保留狀態與本文，錯誤種類為 HTTP；
連線失敗的狀態為 0。錯誤提供 Kind/種類、Message/訊息、Retryable/可重試。
輸入錯誤、資源關閉與 socket 操作失敗可包在 Try/嘗試中處理。
函式庫不自動重送 POST 等可能造成副作用的請求。

`StartRequest/開始請求` 可讓 UI 保持繪製：操作物件的 `IsDone/已完成()`、
`Poll/輪詢()`（尚未完成回傳空值）、`Wait/等待()`（等待回應）、
`Cancel/取消()`、`Close/關閉()` 控制生命週期。取消回報 Cancelled；
範例前端使用這種方式，每個影格輪詢結果。

## 串流回應 / Streaming responses

`StartStream/開始串流(method,url,[options])` 與用戶端的同名方法需要 `http-stream`
能力，適合 Server-Sent Events（SSE）、大型語言模型逐字輸出或長時間下載。請求立即回傳
串流操作物件；UI 可在每個影格讀取已到達的文字，不必等待完整回應：

```text
變數 網路 = 匯入("網路");
變數 串流 = 網路.開始串流("POST", 網址, {"JSON": 請求, "逾時": 60000});
# 每個影格：
迴圈 (變數 i = 0; i < 長度(串流.讀取行()); i++) { ... }   # 只回傳完整的行
如果 (串流.已完成()) { 變數 回應 = 串流.輪詢(); 印出(回應.狀態碼); }
```

| English | 繁體中文 | 說明 |
| --- | --- | --- |
| `Read()` | `讀取()` | 取出目前已解碼、尚未讀取的全部文字（可能是不完整的一行）；沒有新資料時為空字串。 |
| `ReadLines()` | `讀取行()` | 取出完整的行並移除 `\r\n`／`\n`；未完成的行保留到下次讀取，傳輸結束後連同最後一行一起回傳。 |
| `Status()` | `狀態碼()` | 收到標頭前為 0，之後為 HTTP 狀態碼。 |
| `IsDone()` / `Poll()` / `Wait()` | `已完成()` / `輪詢()` / `等待()` | 與 `開始請求` 相同；完成的回應 `本文` 仍包含完整內容（受 `本文上限` 限制）。 |
| `Cancel()` / `Close()` | `取消()` / `關閉()` | 取消傳輸；已收到的文字仍可讀取。 |

UTF-8 多位元組字元跨越網路分段時會在完整後才解碼。串流的 `timeout/逾時` 是**閒置**
逾時：收到標頭及每一段資料都會重新計時，因此長時間但持續輸出的回應不會被截斷。
`讀取` 與 `讀取行` 共用同一個緩衝區。尚未讀取的文字最多 16 MiB。不支援的平台
呼叫時回報 `Unsupported`；可先以 `網路.支援("串流傳輸")` 判斷，必要時改用
`開始請求` 取得完整回應。

English: `StartStream` returns an operation whose `ReadLines()` yields complete lines
as they arrive (ideal for SSE), `Read()` drains all decoded text, and `Status()` reports
the HTTP status once headers arrive. The timeout restarts after headers and each chunk.

## Socket / 通訊端

`ConnectTCP/連接傳輸控制(host,port,[options])`、`ConnectTLS/連接安全傳輸(...)`、
`ListenTCP/監聽傳輸控制(address,port,[options])`、`OpenUDP/開啟資料報(...)`、
`Resolve/解析主機(host)`、`ConnectWebSocket/連接網頁通訊端(url,[options])`。
監聽位址為 IP；連接埠 0 表示由作業系統選擇。TCP 連線接受 DNS 主機名稱及 IPv4／IPv6。

TCP 串流有 LocalAddress/本機位址、RemoteAddress/遠端位址，以及
`Read/讀取(maxBytes)`、`ReadText/讀取文字(maxBytes)`、`Write/寫入(bytes)`、
`WriteText/寫入文字(text)`、`ShutdownWrite/結束寫入()`、`Close/關閉()`。
讀取是串流的一段，空位元組陣列表示 EOF；應用程式須自行處理訊息邊界。
`ReadText` 對每段獨立 UTF-8 解碼；跨讀取的多位元字元請累積位元組後再解碼。
監聽器的 `Accept/接受([timeout])` 逾時回傳空值，0 表示立即輪詢。

UDP 有 `Send/傳送(host,port,bytesOrText)`、`Receive/接收([maxBytes])`、
`JoinMulticast/加入多點傳送(address)`、`LeaveMulticast/離開多點傳送(address)`、
`SetBroadcast/設定廣播(bool)`、`Close/關閉()`。接收保留單一資料報邊界，回傳
Data/資料、Text/文字、Address/位址、Port/連接埠；過大的資料報回報錯誤，不靜默截斷。

連接時的 `protocols/子協定` 選項（字串陣列）依序提供 RFC 6455 子協定，伺服器以 `UpgradeWebSocket/升級網頁通訊端([子協定...])` 選擇用戶端提供的第一個支援者；雙方的 `Protocol/子協定` 欄位是選定結果（沒有則為空字串），瀏覽器也支援。
`RandomBytes/隨機位元組(n)` 回傳作業系統或瀏覽器的密碼學亂數位元組（1..65536），`PageOrigin/頁面來源()` 在網頁回傳載入程式的頁面來源（例如 `https://example.com:8443`，可組成 `wss://` 網址），其他平台為空字串。

WebSocket 有 `SendText/傳送文字(text)`、`SendBytes/傳送位元組(bytes)`、
`Receive/接收()`、`Close/關閉([code,reason])`。Receive 回傳 Data/資料、Text/文字、
IsText/是文字、Closed/已關閉。引擎處理分片、ping/pong 與關閉握手；
接收與傳送佇列有大小限制。瀏覽器不能自訂握手標頭。

## 桌面伺服器 / Desktop server

```text
變數 網路 = 匯入("網路");
變數 伺服器 = 網路.監聽("http://127.0.0.1:8080");
伺服器.處理("GET", "/hello/{名字}", 函式(請求, 回應) {
    回應.回傳JSON({"你好":請求.路由參數("名字")});
});
伺服器.靜態檔案("/", "public");
伺服器.服務();
```

English: `Network.Listen` → `server.Handle(method,path,handler)` → `server.Serve()`.
處理器接收 Request 與 Writer；不需要控制器、註冊依賴或額外框架。

伺服器方法：Handle/處理、Use/使用中介(handler)、Static/靜態檔案(prefix,root)、
CORS/跨來源([exactOrigins])、Poll/處理下一筆([timeout])、Serve/服務、Close/關閉。
路由支援 `{name}` 與尾端 `*`；依註冊順序選擇，HEAD 可使用 GET 處理器，
找不到路由回傳 404，方法不符回傳 405 及 Allow。中介依序執行，回傳假可終止處理。
每個處理器都在 Aquarius 執行緒執行，背景 I/O 執行緒只排隊；長任務會延後其他處理器。
佇列最多 128 筆、連線最多 256 個，滿載回傳 503。

Request 欄位：Method/方法、Path/路徑、Url/網址、RemoteAddress/遠端位址、
IsSecure/安全傳輸、Body/本文、Headers/標頭。方法：Header/取得標頭、Query/查詢、
Cookie/取得Cookie、Param/路由參數、JSON/解析JSON、Form/表單、Bytes/取得位元組、Session/工作階段。
Writer 方法：Status/狀態、Header/設定標頭、Cookie/設定Cookie、Text/文字、
JSON/回傳JSON、Bytes/寫入位元組、Redirect/重新導向、Event/事件(name,text,[id])、
UpgradeWebSocket/升級網頁通訊端。SSE 寫入後立即 flush。
請求及 Writer 只能在該次處理器內使用；結束後關閉。

Cookie 選項：path/路徑、sameSite/同站、secure/安全、httpOnly/僅HTTP、maxAge/有效秒數。
預設 Path=/、SameSite=Lax、HttpOnly，在 HTTPS 自動設 Secure；SameSite=None 要求 Secure。
工作階段提供 Get/取得、Set/設定、Destroy/銷毀；伺服器隨機產生識別碼，
不接受未登記的識別碼作為新工作階段。閒置 30 分鐘到期，最多 10000 個工作階段，
每個最多 128 欄位、每值 64 KiB。適合單一程序；多程序／持久化可擴充 adapter。

預設拒絕不相符的 Origin；CORS 明確列出來源後才放行，回應預檢與 Vary。
萬用來源不開啟 credentials。來源檢查不能取代登入與授權，非瀏覽器用戶端可省略 Origin。
處理器失敗在回應開始前回傳 500，隱藏內部錯誤，並繼續下一個請求；
已開始的串流應由應用程式自行決定錯誤訊息格式。

靜態檔案有 MIME、index.html、HEAD、ETag / If-None-Match；沒有目錄清單。
路徑穿越、編碼穿越與符號連結遭拒絕。檔案上限為 16 MiB。
HTTPS 使用 Mbed TLS，憑證可為 PKCS#12（PBES2 或 3DES）或 PEM：
`網路.監聽("https://127.0.0.1:8443", {"憑證":"server.pfx", "憑證密碼":"..."})`。
憑證由應用程式部署，函式庫不關閉驗證，也不自動產生受信任憑證。

## 非同步 / Async variants

會等待的函式都有立即回傳承諾（`承諾` / `Promise`）的版本：英文名稱加 `Async`，中文名稱前加 `非同步`，例如
`GetAsync` / `非同步取得`。同步版本照常運作（等待時其他工作仍繼續執行）；承諾版本適合同時發出多個請求再用
`承諾.全部` 收集，或在 `非同步` 函式中 `等待`。

Every function that waits has a variant that returns a promise at once: `RequestAsync`, `GetAsync`, `PostAsync`
(module and client), `ResolveAsync`, `ConnectTCPAsync`, `ConnectTLSAsync`, `ConnectWebSocketAsync`, the operations'
`WaitAsync`, the streams' `ReadAsync` / `ReadTextAsync` / `WriteAsync` / `WriteTextAsync`, `AcceptAsync`,
`ReceiveAsync` (UDP and WebSocket), `SendTextAsync` / `SendBytesAsync`, and the server's `PollAsync`, `ServeAsync`,
`ShutdownAsync` and `ListenAndServeAsync`. Chinese names put `非同步` first: `非同步請求`, `非同步取得`, `非同步服務`, ...

```text
var net = import("Network");
var pages = await Promise.All([net.GetAsync("https://example.com/a"), net.GetAsync("https://example.com/b")]);
var server = net.Listen(":8080");
server.Handle("GET /hello/{name}", function(request, writer) { writer.Text("hello " + request.Param("name")); });
var serving = server.ServeAsync();   # serves while the script goes on
```

A failure the waiting function reports as an error rejects the promise instead (`Catch`, `Promise.AllSettled`); an
HTTP response, including 4xx/5xx and connection errors with status 0, fulfills it as before.

## Go 風格伺服器 / Go-style server

伺服器的介面參考 Go 的 `net/http`：模式含方法、最明確的路由優先、群組、包裝式中介、`http.Error` 等輔助函式，
以及優雅關閉。原本的寫法全部照常可用。

The server follows Go's `net/http`; every earlier form keeps working.

```text
var net = import("Network");
var server = net.Listen(":8080");                         # "host:port", ":port" (every interface) or "http(s)://..."
server.Use(function(request, writer, next) {              # wrapping middleware: work before and after next()
    var start = currentTime();
    next();
    print(request.Method, request.Path, currentTime() - start);
});
server.Handle("GET /items/{id}", function(request, writer) { writer.JSON({"id": request.Param("id")}); });
server.Handle("GET /items/new", function(request, writer) { writer.Text("form"); });  # more specific: wins for /items/new
server.Handle("/health", function(request, writer) { writer.Text("ok"); });            # any method
server.Handle("GET /files/{path...}", function(request, writer) { writer.File("public/" + request.Param("path")); });
var api = server.Group("/api");
api.Use(function(request, writer) { if (request.Header("X-Key") != "secret") { writer.Error(401); return false; } });
api.Handle("POST /upload", function(request, writer) { writer.JSON({"files": len(request.Files())}); });
server.Serve();
```

```text
變數 網路 = 匯入("網路");
網路.監聽並服務(":8080", {
    "GET /": 函式(請求, 回應) { 回應.文字("首頁"); },
    "GET /使用者/{名稱}": 非同步 函式(請求, 回應) { 等待 承諾.延遲(10); 回應.回傳JSON({"名稱": 請求.路由參數("名稱")}); }
});
```

| English | 繁體中文 | 說明 / Description |
| --- | --- | --- |
| `Network.ListenAndServe(address, routes?, options?)` | `網路.監聽並服務(位址, 路由?, 選項?)` | Listen, Handle each `"METHOD /path": handler`, Serve. / 監聽、註冊路由並服務。 |
| `Network.StatusText(code)` | `網路.狀態說明(代碼)` | `"Not Found"` for 404, `""` for unknown codes (Go's `http.StatusText`). |
| `server.Handle(pattern, handler)` | `伺服器.處理(模式, 函式)` | Pattern `"METHOD /path"` or `"/path"` (any method); `Handle(method, path, handler)` still works. |
| `server.Group(prefix)` / `router.Group(prefix)` | `伺服器.群組(前綴)` | A router: `Handle`, `Use` and `Group` under the prefix; its middleware runs only for its routes. |
| `server.Use(function(request, writer, next))` | `伺服器.使用中介(函式(請求, 回應, 下一步))` | With a third parameter the middleware wraps the rest: `next()` runs it and returns once it finished. Without it, returning false stops the chain, as before. |
| `server.Shutdown(timeout?)` | `伺服器.優雅關閉(逾時?)` | Stop accepting, let requests in progress finish (idle keep-alive connections close), then close; true when everything finished within the timeout (default 30000 ms). |
| `writer.Error(status, message?)` | `回應.錯誤(狀態碼, 訊息?)` | 4xx/5xx with a plain-text body (the status text by default). |
| `writer.NotFound()` | `回應.找不到()` | 404. |
| `writer.Write(textOrBytes)` | `回應.寫入(文字或位元組)` | A piece of the body, sent at once (chunked). |
| `writer.AddHeader(name, value)` | `回應.加入標頭(名稱, 值)` | Adds a header line (`Header` / `設定標頭` replaces). |
| `writer.File(path)` | `回應.傳送檔案(路徑)` | A file or packaged asset with Content-Type, ETag, Last-Modified, `If-None-Match` and single `Range` requests (206 / 416). |
| `request.Host`, `request.Proto` | `請求.主機`, `請求.協定版本` | The Host header and `HTTP/1.1` / `HTTP/1.0`. |
| `request.FormValue(name)` | `請求.表單值(名稱)` | A URL-encoded or multipart form field, else the query parameter; `""` when absent. |
| `request.Files()` | `請求.上傳檔案()` | Multipart uploads: `[{"field", "filename", "contentType", "size", "data"}]`; `Form()` reads multipart text fields too. |
| `request.BasicAuth()` | `請求.基本認證()` | `{"user", "password"}` of `Authorization: Basic`, or null. |

Routing: among the routes whose method and pattern match, the most specific wins, as in Go 1.22: compared segment by
segment a literal beats a `{parameter}`, which beats a wildcard (`*` or `{name...}`, the rest of the path), a pattern
that ends beats a wildcard that goes on, and a route for the method beats one for any method; registration order breaks
ties. `{name...}` also matches the empty rest (`/files/{path...}` matches `/files`). Handlers and middleware may be
async functions: the request finishes when their promise settles. Server options add `readTimeout` / `讀取逾時` (a
request's head, default 15000 ms) and `idleTimeout` / `閒置逾時` (an idle keep-alive connection, default 30000 ms).

## 實作 / Implementation

同一份 C 原始碼（`src/`）在 Windows、Linux、Android 使用原生 socket 與 Mbed TLS，
網頁則透過 `web-glue/network` 呼叫 fetch 與 WebSocket；選項驗證、逾時、重新導向與錯誤對應都在 C 中。
建置方式、相依套件、測試與和舊版的差異見 [函式庫說明](../README.md)。
所有開啟的資源在執行環境結束時關閉。
