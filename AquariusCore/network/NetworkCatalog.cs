using System;
using System.Collections.Generic;
using AquariusLang.runtime;

namespace AquariusLang.Network;

public static class NetworkCatalog {
    public static IReadOnlyList<LibraryProperty> Properties { get; } = Array.AsReadOnly(new[] {
        P("Network","Platform","平台","String"), P("Network","ContractVersion","契約版本","Integer"),
        P("Network.Response","Success","成功","Boolean"), P("Network.Response","Status","狀態碼","Integer"), P("Network.Response","Url","網址","String"),
        P("Network.Response","Body","本文","String"), P("Network.Response","Headers","標頭","Hash"), P("Network.Response","Error","錯誤","Network.Error?","Network.Error"),
        P("Network.Error","Kind","種類","String"), P("Network.Error","Message","訊息","String"), P("Network.Error","Retryable","可重試","Boolean"),
        P("Network.Result","Success","成功","Boolean"), P("Network.Result","Value","值","Any"), P("Network.Result","Error","錯誤","Network.Error?","Network.Error"),
        P("Network.URL","Scheme","協定","String"), P("Network.URL","Host","主機","String"), P("Network.URL","Port","連接埠","Integer"),
        P("Network.URL","Path","路徑","String"), P("Network.URL","Query","查詢字串","String"), P("Network.URL","Fragment","片段","String"),
        P("Network.Packet","Data","資料","Byte[]"), P("Network.Packet","Text","文字","String"), P("Network.Packet","Address","位址","String"), P("Network.Packet","Port","連接埠","Integer"),
        P("Network.Message","Data","資料","Byte[]"), P("Network.Message","Text","文字","String"), P("Network.Message","IsText","是文字","Boolean"), P("Network.Message","Closed","已關閉","Boolean"),
        P("Network.Stream","LocalAddress","本機位址","String"), P("Network.Stream","RemoteAddress","遠端位址","String"),
        P("Network.Listener","LocalAddress","本機位址","String"), P("Network.Datagram","LocalAddress","本機位址","String"), P("Network.Server","Address","位址","String"),
        P("Network.Request","Method","方法","String"), P("Network.Request","Path","路徑","String"), P("Network.Request","Url","網址","String"),
        P("Network.Request","RemoteAddress","遠端位址","String"), P("Network.Request","Body","本文","String"), P("Network.Request","Headers","標頭","Hash"), P("Network.Request","IsSecure","安全傳輸","Boolean"),
    });
    private static LibraryProperty P(string library,string en,string zh,string type,string? returns=null)=>new(library,en,zh,type,returns);
    private static readonly string[] All = { "windows", "linux", "macos", "web", "android" };
    private static readonly string[] Native = { "windows", "linux", "macos", "android" };
    private static readonly string[] Desktop = { "windows", "linux", "macos" };
    private static readonly string[] Streaming = { "windows", "linux", "macos", "web" };
    public static IReadOnlyList<LibraryFeature> Features { get; } = Array.AsReadOnly(new[] {
        new LibraryFeature("http", "超文字傳輸", All, "HTTP(S), headers, bodies, timeouts / HTTP(S)、標頭、本文、逾時"),
        new("cookie-control", "Cookie管理", Native, "Explicit cookie jar control / 明確控制 Cookie 容器"),
        new("cookies", "Cookie", All, "Browser-managed on web / 網頁由瀏覽器管理"),
        new("websocket", "網頁通訊端", All, "Text and binary messages / 文字及二進位訊息"),
        new("tcp", "傳輸控制協定", Native, "Raw TCP streams / 原始 TCP 串流"),
        new("tls", "傳輸層安全", Native, "Certificate-verified TLS streams / 驗證憑證的 TLS 串流"),
        new("udp", "使用者資料報", Native, "Datagrams, broadcast, multicast / 資料報、廣播、多點傳送"),
        new("dns", "網域名稱解析", Native, "Host address resolution / 主機位址解析"),
        new("tcp-listen", "監聽傳輸控制", Desktop, "TCP listeners / TCP 監聽器"),
        new("http-server", "網頁伺服器", Desktop, "HTTP(S), routes, files, sessions, SSE, WebSocket / 路由、靜態檔案、工作階段"),
        new("http-stream", "串流傳輸", Streaming, "Incremental HTTP response bodies such as SSE / 逐段讀取 HTTP 回應，例如 SSE"),
    });
    public static IReadOnlyList<LibraryFunction> Functions { get; } = Array.AsReadOnly(new[] {
        F("Network","Try","嘗試",1,1,"Network.Result"), F("Network","Supports","支援",1,1), F("Network","Capabilities","能力",0,0),
        F("Network","CreateClient","建立用戶端",0,1,"Network.Client","http"),
        F("Network","Request","請求",2,3,"Network.Response","http"), F("Network","Get","取得",1,2,"Network.Response","http"), F("Network","Post","提交",2,3,"Network.Response","http"),
        F("Network","StartRequest","開始請求",2,3,"Network.Operation","http"),
        F("Network","StartStream","開始串流",2,3,"Network.StreamOperation","http-stream"),
        F("Network","Resolve","解析主機",1,1,null,"dns"),
        F("Network","ConnectTCP","連接傳輸控制",2,3,"Network.Stream","tcp"), F("Network","ConnectTLS","連接安全傳輸",2,3,"Network.Stream","tls"),
        F("Network","ListenTCP","監聽傳輸控制",2,3,"Network.Listener","tcp-listen"), F("Network","OpenUDP","開啟資料報",2,3,"Network.Datagram","udp"),
        F("Network","ConnectWebSocket","連接網頁通訊端",1,2,"Network.WebSocket","websocket"), F("Network","Listen","監聽",1,2,"Network.Server","http-server"),
        F("Network","EncodeQuery","編碼查詢",1,1), F("Network","ParseURL","解析網址",1,1,"Network.URL"),
        F("Network.Client","Request","請求",2,3,"Network.Response"), F("Network.Client","Get","取得",1,2,"Network.Response"), F("Network.Client","Post","提交",2,3,"Network.Response"),
        F("Network.Client","StartRequest","開始請求",2,3,"Network.Operation"),
        F("Network.Client","StartStream","開始串流",2,3,"Network.StreamOperation","http-stream"),
        F("Network.StreamOperation","Read","讀取",0,0), F("Network.StreamOperation","ReadLines","讀取行",0,0), F("Network.StreamOperation","Status","狀態碼",0,0),
        F("Network.StreamOperation","IsDone","已完成",0,0), F("Network.StreamOperation","Cancel","取消",0,0), F("Network.StreamOperation","Poll","輪詢",0,0,"Network.Response"),
        F("Network.StreamOperation","Wait","等待",0,0,"Network.Response"), F("Network.StreamOperation","Close","關閉",0,0),
        F("Network.Operation","IsDone","已完成",0,0), F("Network.Operation","Cancel","取消",0,0), F("Network.Operation","Poll","輪詢",0,0,"Network.Response"), F("Network.Operation","Wait","等待",0,0,"Network.Response"), F("Network.Operation","Close","關閉",0,0),
        F("Network.Client","ClearCookies","清除Cookie",0,0,null,"cookie-control"), F("Network.Client","Close","關閉",0,0),
        F("Network.Response","JSON","解析JSON",0,0), F("Network.Response","Bytes","取得位元組",0,0), F("Network.Response","Header","取得標頭",1,1),
        F("Network.Stream","Read","讀取",1,1), F("Network.Stream","Write","寫入",1,1), F("Network.Stream","ReadText","讀取文字",1,1), F("Network.Stream","WriteText","寫入文字",1,1),
        F("Network.Stream","ShutdownWrite","結束寫入",0,0), F("Network.Stream","Close","關閉",0,0),
        F("Network.Listener","Accept","接受",0,1,"Network.Stream"), F("Network.Listener","Close","關閉",0,0),
        F("Network.Datagram","Send","傳送",3,3), F("Network.Datagram","Receive","接收",0,1,"Network.Packet"),
        F("Network.Datagram","JoinMulticast","加入多點傳送",1,1), F("Network.Datagram","LeaveMulticast","離開多點傳送",1,1), F("Network.Datagram","SetBroadcast","設定廣播",1,1), F("Network.Datagram","Close","關閉",0,0),
        F("Network.WebSocket","SendText","傳送文字",1,1), F("Network.WebSocket","SendBytes","傳送位元組",1,1), F("Network.WebSocket","Receive","接收",0,0,"Network.Message"), F("Network.WebSocket","Close","關閉",0,2),
        F("Network.Server","Handle","處理",3,3), F("Network.Server","Use","使用中介",1,1), F("Network.Server","Static","靜態檔案",2,2), F("Network.Server","CORS","跨來源",1,1),
        F("Network.Server","Poll","處理下一筆",0,1), F("Network.Server","Serve","服務",0,0), F("Network.Server","Close","關閉",0,0),
        F("Network.Request","Header","取得標頭",1,1), F("Network.Request","Query","查詢",1,1), F("Network.Request","Cookie","取得Cookie",1,1), F("Network.Request","Param","路由參數",1,1),
        F("Network.Request","JSON","解析JSON",0,0), F("Network.Request","Form","表單",0,0), F("Network.Request","Session","工作階段",0,0,"Network.Session"), F("Network.Request","Bytes","取得位元組",0,0),
        F("Network.Writer","Status","狀態",1,1), F("Network.Writer","Header","設定標頭",2,2), F("Network.Writer","Cookie","設定Cookie",2,3),
        F("Network.Writer","Text","文字",1,1), F("Network.Writer","JSON","回傳JSON",1,1), F("Network.Writer","Bytes","寫入位元組",1,1),
        F("Network.Writer","Redirect","重新導向",1,2), F("Network.Writer","Event","事件",2,3), F("Network.Writer","UpgradeWebSocket","升級網頁通訊端",0,0,"Network.WebSocket"),
        F("Network.Session","Get","取得",1,1), F("Network.Session","Set","設定",2,2), F("Network.Session","Destroy","銷毀",0,0),
    });
    private static LibraryFunction F(string library,string en,string zh,int min,int max,string? returns=null,string? feature=null) => new(library,en,zh,min,max,returns,feature);
}
