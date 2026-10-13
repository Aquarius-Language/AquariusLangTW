using System;
using System.Collections.Generic;
using System.Linq;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.Webcam;

/// <summary>負責語言註冊、參數驗證與工作階段所有權；平台注入擷取及影像投影。</summary>
public sealed class WebcamRuntime : IDisposable {
    private readonly IWebcamBackend backend;
    private readonly Func<Application.PixelImage, IObject> image;
    private readonly HashSet<IWebcamSession> sessions = new();
    private readonly ModuleObj module = new(AquaEnvironment.NewEnvironment());
    private bool disposed;
    public WebcamRuntime(IWebcamBackend backend, Func<Application.PixelImage, IObject> image) {
        this.backend = backend; this.image = image;
        Bind(module, "是否支援", 0, _ => new BooleanObj(backend.IsSupported));
        Bind(module, "列出裝置", 0, _ => new ArrayObj(backend.Enumerate().AsTask().GetAwaiter().GetResult()
            .Select(d => (IObject)Record(("識別碼", new StringObj(d.Id)), ("名稱", new StringObj(d.Name)))).ToArray()));
        Bind(module, "開啟", 4, a => {
            if (a[0] is not StringObj id) throw new WebcamFailure(WebcamFailureKind.InvalidArgument, "裝置識別碼必須是字串；空字串表示預設裝置。");
            var options = new WebcamOptions(Integer(a[1]), Integer(a[2]), Integer(a[3])); options.Validate();
            foreach (var closed in sessions.Where(s => !s.IsOpen).ToArray()) { sessions.Remove(closed); closed.Dispose(); }
            if (sessions.Count >= WebcamOptions.MaxSessions) throw new WebcamFailure(WebcamFailureKind.Unavailable, "最多只能同時開啟四個攝影機工作階段。");
            var session = backend.Open(id.Value, options).AsTask().GetAwaiter().GetResult(); sessions.Add(session);
            var m = Record();
            Bind(m, "是否開啟", 0, _ => new BooleanObj(sessions.Contains(session) && session.IsOpen));
            Bind(m, "釋放", 0, _ => { if (sessions.Remove(session)) session.Dispose(); return RepeatedPrimitives.NULL; });
            Bind(m, "擷取影格", 0, _ => {
                if (!sessions.Contains(session) || !session.IsOpen) throw new WebcamFailure(WebcamFailureKind.Closed, "攝影機工作階段已關閉。");
                WebcamFrame frame;
                try { frame = session.Capture().AsTask().GetAwaiter().GetResult(); }
                catch { if (!session.IsOpen && sessions.Remove(session)) session.Dispose(); throw; }
                var result = Record(("寬度", new IntegerObj(frame.Image.Width)), ("高度", new IntegerObj(frame.Image.Height)),
                    ("序號", new DoubleObj(frame.Sequence)), ("時間戳記", new DoubleObj(frame.TimestampMilliseconds)), ("影像", image(frame.Image)));
                Bind(result, "取得像素", 0, _ => new ArrayObj(frame.Image.Pixels.ToArray().Select(b => (IObject)new IntegerObj(b)).ToArray()));
                return result;
            });
            return m;
        });
    }
    public bool TryImport(string name, out ModuleObj result) { result = module; return name is "攝影機" or "Webcam" or "網路攝影機"; }
    private void Bind(ModuleObj m, string name, int count, Func<IObject[], IObject> action) {
        var fn = new BuiltinObj(args => {
            try {
                if (disposed) throw new WebcamFailure(WebcamFailureKind.Closed, "攝影機執行環境已關閉。");
                if (args.Length != count) throw new WebcamFailure(WebcamFailureKind.InvalidArgument, $"需要 {count} 個參數，實際收到 {args.Length} 個。");
                return action(args);
            } catch (WebcamFailure e) { return new ErrorObj($"攝影機.{name}：{WebcamFailure.Name(e.Kind)}：{e.Message}"); }
            catch (OperationCanceledException) { return new ErrorObj($"攝影機.{name}：已取消：攝影機操作已取消。"); }
        }) { RetainedEnvironment = m._Environment };
        FunctionRegistration.Define(m._Environment, fn, name);
    }
    private static int Integer(IObject value) {
        if (value is not INumberObj n || !double.IsFinite(n.GetNumValue()) || n.GetNumValue() != Math.Truncate(n.GetNumValue()) || n.GetNumValue() < 1 || n.GetNumValue() > int.MaxValue)
            throw new WebcamFailure(WebcamFailureKind.InvalidArgument, "尺寸及影格率必須是正整數。");
        return (int)n.GetNumValue();
    }
    private static ModuleObj Record(params (string Name, IObject Value)[] fields) {
        var m = new ModuleObj(AquaEnvironment.NewEnvironment()); foreach (var (name, value) in fields) m._Environment.Create(name, value); return m;
    }
    public void Dispose() {
        if (disposed) return; disposed = true; var errors = new List<Exception>();
        foreach (var session in sessions) try { session.Dispose(); } catch (Exception e) { errors.Add(e); }
        sessions.Clear(); if (errors.Count > 0) throw new AggregateException("攝影機資源釋放失敗。", errors);
    }
}
