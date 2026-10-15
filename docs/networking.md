# Network / 網路

`匯入("Network")` 與 `匯入("網路")` 共用同一個預設函式庫。所有公開函式、
物件方法及結果欄位提供英文與繁體中文名稱，兩個名稱指向同一個函式或值。
編譯器與語言伺服器由 `NetworkCatalog` 取得名稱、參數數量、回傳型別及必要能力。

## 平台能力 / Platform capabilities

| 能力 / Feature | 桌面 / Desktop | Web | Android |
| --- | --- | --- | --- |
| `http` / `超文字傳輸` | HTTP / HTTPS | Fetch、CORS、瀏覽器安全政策 | OkHttp、Android 安全政策 |
| `cookies` / `Cookie` | 每個用戶端獨立容器 | 瀏覽器管理 | 每個用戶端獨立容器 |
| `cookie-control` / `Cookie管理` | 清除容器 | 不支援 | 清除容器 |
| `websocket` / `網頁通訊端` | 用戶端及伺服器升級 | 瀏覽器用戶端 | OkHttp 用戶端 |
| `tcp` / `傳輸控制協定` | 原始 TCP | 不支援 | 原始 TCP |
| `tls` / `傳輸層安全` | 憑證驗證、主機名稱驗證 | HTTPS / WSS 由瀏覽器處理 | 憑證及主機名稱驗證 |
| `udp` / `使用者資料報` | UDP、廣播、多點傳送 | 不支援 | UDP、廣播、多點傳送 |
| `dns` / `網域名稱解析` | IPv4 / IPv6 | HTTP 引擎內部解析 | IPv4 / IPv6 |
| `tcp-listen` / `監聽傳輸控制` | TCP 監聽及接受 | 不支援 | 不提供後端 API |
| `http-server` / `網頁伺服器` | HTTP、HTTPS、路由、靜態檔案、工作階段、SSE | 不支援 | 不提供後端 API |

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
HTTPS 直接使用 Kestrel：
`網路.監聽("https://127.0.0.1:8443", {"憑證":"server.pfx", "憑證密碼":"..."})`。
憑證由應用程式部署，函式庫不關閉驗證，也不自動產生受信任憑證。

## 擴充架構 / Extension architecture

`INetworkBackend` 與 stream/datagram/client/server/request/websocket 子契約只描述 I/O。
`NetworkRuntime` 擁有雙語註冊、驗證、路由、工作階段和生命週期；
`NetworkBackend` 基底提供明確 Unsupported，未實作平台仍可匯入及查詢能力。
核心沒有 Kestrel、OkHttp、Wasmtime 或作業系統 socket 實作相依。

通用的 `LibraryCapabilities` / `LibraryFeature` 以契約版本與可選能力描述平台差異；
`LibraryFunction.RequiredFeature` 描述個別功能需求。網頁／Android 部署 metadata 保留
`libraryFeatures` 和每個函式的 `feature`，編輯器可保留完整 API，而宿主只宣告真正可用的功能。
新增平台不需要複製 API 清單或刪除不支援的函式。新增功能採 additive feature，
不破壞既有 core contract。其他預設函式庫也可採用同一個能力模型。

`LibraryProperty` 另描述雙語唯讀欄位與欄位回傳型別，純資料物件無須虛構方法。
語言伺服器可補全 `回應.錯誤.種類`、網址欄位和 UDP 封包欄位，並在 hover 顯示能力需求。

共用 JavaScript 註冊在 `AquariusCore/hosting/network-registration.mjs`；
Web 提供 Fetch / WebSocket adapter，Android 的 Java executor 透過有限佇列非同步傳回結果。
桌面使用 .NET socket / TLS / HttpClient 和 Kestrel。所有開啟的資源在 runtime dispose 時關閉。

驗證：`AquariusTests/network`、`AquariusWebCompiler/tests/network.test.mjs`、
`AquariusAndroid/tests/network.test.mjs` 和 `examples/network_lab` 的實際三平台往返測試。
