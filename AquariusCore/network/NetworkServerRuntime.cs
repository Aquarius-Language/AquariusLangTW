using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using AquariusLang.Object;
using AquariusLang.runtime;
using static AquariusLang.Application.ApplicationValues;

namespace AquariusLang.Network;

public sealed partial class NetworkRuntime {
    private sealed record Route(string Method,string Pattern,IObject Handler);
    private sealed class SessionData {
        public readonly Dictionary<string,string> Values=new();
        public DateTime Expires=DateTime.UtcNow.AddMinutes(30);
    }
    private ModuleObj Server(INetworkServer server) {
        Own(server);var m=NewModule();bool closed=false,handling=false;var routes=new List<Route>();var middleware=new List<IObject>();
        var files=new List<(string Prefix,string Root)>();var origins=new HashSet<string>(StringComparer.Ordinal);var sessions=new Dictionary<string,SessionData>();int nextHandler=0;
        Field(m,"Address","位址",new StringObj(server.Address));
        void Retain(IObject fn){if(fn is not(FunctionObj or BuiltinObj))throw new ArgumentException("Expected handler function / 處理器必須是函式。");m._Environment.Create("__network_handler_"+nextHandler++,fn);}
        Bind(m,"Network.Server","Handle",a=>{var method=Text(a[0]).ToUpperInvariant();var pattern=Text(a[1]);if(!pattern.StartsWith('/')||pattern.Contains('?'))throw new ArgumentException("Invalid route / 路由須以 / 開頭。");Retain(a[2]);routes.Add(new(method,pattern,a[2]));return m;});
        Bind(m,"Network.Server","Use",a=>{Retain(a[0]);middleware.Add(a[0]);return m;});
        Bind(m,"Network.Server","Static",a=>{string prefix=Text(a[0]);if(!prefix.StartsWith('/')||prefix.Contains(".."))throw new ArgumentException("Invalid public prefix / 無效的公開前綴。");files.Add((prefix.TrimEnd('/')+"/",ResourcePath(Text(a[1]))));return m;});
        Bind(m,"Network.Server","CORS",a=>{if(a[0] is not ArrayObj array)throw new ArgumentException("Expected origin array / 請傳入來源陣列。");foreach(var origin in array.Elements){var text=Text(origin);if(text!="*"&&(!Uri.TryCreate(text,UriKind.Absolute,out var uri)||uri.GetLeftPart(UriPartial.Authority)!=text))throw new ArgumentException("Expected exact origin / 請填入完整來源。");origins.Add(text);}return m;});
        Bind(m,"Network.Server","Close",_=>{closed=true;return handling?RepeatedPrimitives.NULL:Close(server);});
        Bind(m,"Network.Server","Poll",a=>new BooleanObj(Poll(a.Length>0?Wait(a[0]):0)));
        Bind(m,"Network.Server","Serve",_=>{while(!closed)Poll(100);return RepeatedPrimitives.NULL;});
        bool Poll(int timeout) {
            if(closed)throw new ObjectDisposedException("server");using var request=Await(server.Accept(timeout));if(request==null)return false;
            handling=true;var parameters=new Dictionary<string,string>();var req=NewModule();var writer=NewModule();bool wrote=false;bool active=true;
            void Live(){if(!active)throw new ObjectDisposedException("request");}
            void Header(string name,string value){Live();NetworkOptions.ValidateHeader(name,value);request.Header(name,value);}
            void Write(byte[] bytes){Live();if(bytes.Length>NetworkOptions.MaximumBodyBytes)throw new ArgumentException("Body too large / 本文過大。");Await(request.Write(bytes));wrote=true;}
            void Type(string type){if(!request.HasStarted)Header("Content-Type",type);}
            void Cookie(string name,string value,IReadOnlyDictionary<string,IObject>? options=null) {
                Live();if(name.Length==0||name.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'&&c!='-'))throw new ArgumentException("Invalid cookie name / Cookie 名稱無效。");
                IObject? Get(string en,string zh)=>options?.GetValueOrDefault(en)??options?.GetValueOrDefault(zh);
                string path=Get("path","路徑") is {} p?Text(p):"/";if(!path.StartsWith('/')||path.Any(c=>c<32||c==';'))throw new ArgumentException("Invalid cookie path / Cookie 路徑無效。");
                string sameSite=Get("sameSite","同站") is {} ss?Text(ss):"Lax";if(sameSite is not("Lax" or "Strict" or "None"))throw new ArgumentException("Invalid SameSite / 無效同站設定。");
                bool secure=Get("secure","安全") is {} sec?Boolean(sec):request.IsSecure;
                if(sameSite=="None"&&!secure)throw new ArgumentException("SameSite=None requires Secure / 跨站 Cookie 需要安全傳輸。");
                var cookie=name+"="+Uri.EscapeDataString(value)+"; Path="+path+"; SameSite="+sameSite;
                if(Get("httpOnly","僅HTTP") is not {} ho||Boolean(ho))cookie+="; HttpOnly";
                if(secure)cookie+="; Secure";if(Get("maxAge","有效秒數") is {} age)cookie+="; Max-Age="+Integer(age);
                request.Header("Set-Cookie",cookie,true);
            }
            foreach(var f in new[]{("Method","方法",request.Method),("Path","路徑",request.Path),("Url","網址",request.Url),("RemoteAddress","遠端位址",request.RemoteAddress),("Body","本文",Encoding.UTF8.GetString(request.Body))})Field(req,f.Item1,f.Item2,new StringObj(f.Item3));
            Field(req,"Headers","標頭",StringHash(request.Headers));Field(req,"IsSecure","安全傳輸",new BooleanObj(request.IsSecure));
            Bind(req,"Network.Request","Header",a=>{Live();return new StringObj(Lookup(request.Headers,Text(a[0])));});
            Bind(req,"Network.Request","Query",a=>{Live();return new StringObj(request.Query.GetValueOrDefault(Text(a[0]))??"");});
            Bind(req,"Network.Request","Cookie",a=>{Live();return new StringObj(request.Cookies.GetValueOrDefault(Text(a[0]))??"");});
            Bind(req,"Network.Request","Param",a=>{Live();return new StringObj(parameters.GetValueOrDefault(Text(a[0]))??"");});
            Bind(req,"Network.Request","Bytes",_=>{Live();return Binary(request.Body);});
            Bind(req,"Network.Request","JSON",_=>{Live();return Json(request.Body);});
            Bind(req,"Network.Request","Form",_=>{Live();if(!Lookup(request.Headers,"Content-Type").StartsWith("application/x-www-form-urlencoded",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Expected URL-encoded form / 需要網址編碼表單。");return StringHash(ParseQuery(Encoding.UTF8.GetString(request.Body)));});
            Bind(req,"Network.Request","Session",_=>{
                Live();foreach(var expired in sessions.Where(p=>p.Value.Expires<DateTime.UtcNow).Select(p=>p.Key).ToArray())sessions.Remove(expired);
                string token=request.Cookies.GetValueOrDefault("aqua_session")??"";
                if(!sessions.TryGetValue(token,out var data)){if(sessions.Count>=10000)throw new InvalidOperationException("Session limit / 工作階段已滿。");token=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");sessions[token]=data=new();Cookie("aqua_session",token);}
                data.Expires=DateTime.UtcNow.AddMinutes(30);var session=NewModule();bool destroyed=false;
                void SessionLive(){Live();if(destroyed)throw new ObjectDisposedException("session");}
                Bind(session,"Network.Session","Get",a=>{SessionLive();return data.Values.TryGetValue(Text(a[0]),out var value)?Json(Encoding.UTF8.GetBytes(value)):RepeatedPrimitives.NULL;});
                Bind(session,"Network.Session","Set",a=>{SessionLive();var value=JsonSerializer.Serialize(JsonValue(a[1]));if(data.Values.Count>=128&&!data.Values.ContainsKey(Text(a[0]))||value.Length>65536)throw new ArgumentException("Session limit / 工作階段資料超過上限。");data.Values[Text(a[0])]=value;return RepeatedPrimitives.NULL;});
                Bind(session,"Network.Session","Destroy",_=>{SessionLive();sessions.Remove(token);destroyed=true;Cookie("aqua_session","",new Dictionary<string,IObject>{{"maxAge",new IntegerObj(0)}});return RepeatedPrimitives.NULL;});return session;
            });
            Bind(writer,"Network.Writer","Status",a=>{Live();int status=Integer(a[0]);if(status<200||status>599)throw new ArgumentException("Invalid status / 狀態碼須介於 200..599。");request.Status(status);return writer;});
            Bind(writer,"Network.Writer","Header",a=>{Header(Text(a[0]),Text(a[1]));return writer;});
            Bind(writer,"Network.Writer","Cookie",a=>{Cookie(Text(a[0]),Text(a[1]),a.Length>2?Dictionary(a[2]):null);return writer;});
            Bind(writer,"Network.Writer","Text",a=>{Type("text/plain; charset=utf-8");Write(Encoding.UTF8.GetBytes(Text(a[0])));return writer;});
            Bind(writer,"Network.Writer","JSON",a=>{Type("application/json; charset=utf-8");Write(JsonSerializer.SerializeToUtf8Bytes(JsonValue(a[0])));return writer;});
            Bind(writer,"Network.Writer","Bytes",a=>{Type("application/octet-stream");Write(Bytes(a[0]));return writer;});
            Bind(writer,"Network.Writer","Redirect",a=>{Live();int status=a.Length>1?Integer(a[1]):302;if(status is not(301 or 302 or 303 or 307 or 308))throw new ArgumentException("Invalid redirect / 重新導向狀態無效。");request.Status(status);Header("Location",Text(a[0]));Write(Array.Empty<byte>());return writer;});
            Bind(writer,"Network.Writer","Event",a=>{string name=Text(a[0]);string id=a.Length>2?Text(a[2]):"";if(name.Contains('\n')||name.Contains('\r')||id.Contains('\n')||id.Contains('\r')||id.Contains('\0'))throw new ArgumentException("Invalid SSE field / 事件欄位無效。");Type("text/event-stream; charset=utf-8");if(!request.HasStarted)Header("Cache-Control","no-cache");string value=Text(a[1]).Replace("\r\n","\n").Replace('\r','\n');Write(Encoding.UTF8.GetBytes("event: "+name+"\n"+(id==""?"":"id: "+id+"\n")+string.Join("\n",value.Split('\n').Select(line=>"data: "+line))+"\n\n"));return writer;});
            Bind(writer,"Network.Writer","UpgradeWebSocket",_=>{Live();wrote=true;return WebSocket(Await(request.UpgradeWebSocket()));});
            try {
                Header("X-Content-Type-Options","nosniff");
                string origin=Lookup(request.Headers,"Origin");
                bool sameOrigin=origin==new Uri(request.Url).GetLeftPart(UriPartial.Authority);
                bool allowed=origin.Length==0||sameOrigin||origins.Contains(origin)||origins.Contains("*");
                if(!allowed){request.Status(403);Write(Encoding.UTF8.GetBytes("Origin denied / 來源遭拒。"));return true;}
                if(origin.Length>0&&!sameOrigin){Header("Access-Control-Allow-Origin",origins.Contains("*")?"*":origin);Header("Vary","Origin");if(!origins.Contains("*"))Header("Access-Control-Allow-Credentials","true");}
                if(request.Method=="OPTIONS"&&origin.Length>0){Header("Access-Control-Allow-Methods","GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS");Header("Access-Control-Allow-Headers",Lookup(request.Headers,"Access-Control-Request-Headers"));request.Status(204);return true;}
                foreach(var fn in middleware){var result=InvokeCallback(fn,req,writer);if(result is ErrorObj error)throw new InvalidOperationException(error.Message);if(result is BooleanObj{Value:false})return true;}
                var matched=routes.Select(r=>(Route:r,Params:Match(r.Pattern,request.Path))).Where(p=>p.Params!=null).ToArray();
                var route=matched.FirstOrDefault(p=>p.Route.Method==request.Method||p.Route.Method=="*"||request.Method=="HEAD"&&p.Route.Method=="GET");
                if(route.Route!=null){foreach(var p in route.Params!)parameters[p.Key]=p.Value;var result=InvokeCallback(route.Route.Handler,req,writer);if(result is ErrorObj error)throw new InvalidOperationException(error.Message);return true;}
                if(request.Method is "GET" or "HEAD")foreach(var f in files.Where(f=>request.Path.StartsWith(f.Prefix,StringComparison.Ordinal))){
                    var file=Await(backend.ReadPublicFile(f.Root,request.Path[f.Prefix.Length..]));if(file==null)continue;
                    Header("ETag",file.ETag);Header("Content-Type",file.ContentType);if(Lookup(request.Headers,"If-None-Match")==file.ETag){request.Status(304);return true;}Write(file.Bytes);return true;
                }
                request.Status(matched.Length>0?405:404);if(matched.Length>0)Header("Allow",string.Join(", ",matched.Select(p=>p.Route.Method).Distinct()));Write(Encoding.UTF8.GetBytes(matched.Length>0?"Method Not Allowed / 方法不允許":"Not Found / 找不到資源"));return true;
            } catch(Exception) {
                // A failed handler affects this response, not the server's request loop. The client may already
                // have disconnected (for example by cancelling a stream); then there is nobody left to answer.
                try{if(!request.HasStarted){request.Status(500);Header("Content-Type","text/plain; charset=utf-8");Write(Encoding.UTF8.GetBytes("Internal Server Error / 伺服器內部錯誤"));}}
                catch(Exception e) when(e is ObjectDisposedException or OperationCanceledException or System.IO.IOException){}
                return true;
            } finally {active=false;handling=false;request.Dispose();if(closed)Close(server);}
        }
        return m;
    }
    private static Dictionary<string,string>? Match(string pattern,string path) {
        var a=pattern.Split('/');var b=path.Split('/');var result=new Dictionary<string,string>();
        for(int i=0;i<a.Length;i++){if(a[i]=="*"){result["*"]=string.Join("/",b.Skip(i));return result;}if(i>=b.Length)return null;if(a[i].StartsWith('{')&&a[i].EndsWith('}'))result[a[i][1..^1]]=Uri.UnescapeDataString(b[i]);else if(a[i]!=b[i])return null;}return a.Length==b.Length?result:null;
    }
    private static Dictionary<string,string> ParseQuery(string text)=>text.Split('&',StringSplitOptions.RemoveEmptyEntries).Select(p=>p.Split('=',2)).GroupBy(p=>Uri.UnescapeDataString(p[0].Replace('+',' '))).ToDictionary(g=>g.Key,g=>Uri.UnescapeDataString(g.Last().ElementAtOrDefault(1)?.Replace('+',' ')??""));
}
