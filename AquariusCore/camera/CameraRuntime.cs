using System;
using System.Collections.Generic;
using System.Linq;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.Camera;

public sealed class CameraRuntime : IDisposable
{
    private readonly ICameraBackend backend;
    private readonly Func<Application.PixelImage, IObject> projectImage;
    private readonly HashSet<ICameraSession> sessions = new();
    private readonly ModuleObj module = Record();
    private bool disposed;
    public CameraRuntime(ICameraBackend backend, Func<Application.PixelImage, IObject> projectImage)
    {
        this.backend = backend; this.projectImage = projectImage;
        Bind(module, "Camera", "IsSupported", 0, _ => new BooleanObj(backend.IsSupported));
        Bind(module, "Camera", "Enumerate", 0, _ => new ArrayObj(backend.Enumerate().AsTask().GetAwaiter().GetResult().Select(d =>
            (IObject)Record(("識別碼", new StringObj(d.Id)), ("名稱", new StringObj(d.Name)), ("方向", new StringObj(d.Facing.ToString())))).ToArray()));
        Bind(module, "Camera", "Open", 4, a =>
        {
            if (a[0] is not StringObj id) throw new CameraFailure(CameraFailureKind.InvalidArgument, "裝置識別碼必須是字串。");
            var options = new CameraOptions(Integer(a[1]), Integer(a[2]), Integer(a[3])); options.Validate();
            foreach (var s in sessions.Where(s => !s.IsOpen).ToArray()) { sessions.Remove(s); s.Dispose(); }
            if (sessions.Count >= CameraOptions.MaxSessions) throw new CameraFailure(CameraFailureKind.Unavailable, "最多四個相機工作階段。");
            var session = backend.Open(id.Value, options).AsTask().GetAwaiter().GetResult(); sessions.Add(session);
            var result = Record();
            Bind(result, "Camera.Session", "IsOpen", 0, _ => new BooleanObj(sessions.Contains(session) && session.IsOpen));
            Bind(result, "Camera.Session", "Dispose", 0, _ => { if (sessions.Remove(session)) session.Dispose(); return RepeatedPrimitives.NULL; });
            Bind(result, "Camera.Session", "Capture", 0, _ =>
            {
                if (!sessions.Contains(session) || !session.IsOpen) throw new CameraFailure(CameraFailureKind.Closed, "相機工作階段已關閉。");
                CameraFrame frame;
                try { frame = session.Capture().AsTask().GetAwaiter().GetResult(); }
                catch { if (!session.IsOpen && sessions.Remove(session)) session.Dispose(); throw; }
                var record = Record(("寬度", new IntegerObj(frame.Image.Width)), ("高度", new IntegerObj(frame.Image.Height)),
                    ("序號", new DoubleObj(frame.Sequence)), ("時間戳記", new DoubleObj(frame.TimestampMilliseconds)),
                    ("方向", new StringObj(frame.Facing.ToString())), ("影像", projectImage(frame.Image)));
                Bind(record, "Camera.Frame", "GetPixels", 0, _ => new ArrayObj(frame.Image.Pixels.ToArray().Select(b => (IObject)new IntegerObj(b)).ToArray()));
                return record;
            }); return result;
        });
    }
    public bool TryImport(string name, out ModuleObj result) { result = module; return name is "Camera" or "相機"; }
    private void Bind(ModuleObj owner, string library, string name, int count, Func<IObject[], IObject> action)
    {
        var function = new BuiltinObj(args =>
        {
            try
            {
                if (disposed) throw new CameraFailure(CameraFailureKind.Closed, "相機執行環境已關閉。");
                if (args.Length != count) throw new CameraFailure(CameraFailureKind.InvalidArgument, $"需要 {count} 個參數。");
                return action(args);
            }
            catch (CameraFailure e) { return new ErrorObj($"相機.{name}：{e.Kind}：{e.Message}"); }
            catch (OperationCanceledException) { return new ErrorObj($"相機.{name}：Cancelled：操作已取消。"); }
        })
        { RetainedEnvironment = owner._Environment };
        FunctionRegistration.Define(owner._Environment, function, CameraCatalog.Functions.Single(f => f.Library == library && f.EnglishName == name).TraditionalChineseName, name);
    }
    private static int Integer(IObject value)
    {
        if (value is not INumberObj n || !double.IsFinite(n.GetNumValue()) || n.GetNumValue() < 1 || n.GetNumValue() > int.MaxValue || n.GetNumValue() != Math.Truncate(n.GetNumValue()))
            throw new CameraFailure(CameraFailureKind.InvalidArgument, "尺寸及影格率必須是正整數。");
        return (int)n.GetNumValue();
    }
    private static ModuleObj Record(params (string Name, IObject Value)[] fields)
    {
        var result = new ModuleObj(AquaEnvironment.NewEnvironment()); foreach (var f in fields) result._Environment.Create(f.Name, f.Value); return result;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; var errors = new List<Exception>();
        foreach (var s in sessions) try { s.Dispose(); } catch (Exception e) { errors.Add(e); }
        sessions.Clear(); if (errors.Count > 0) throw new AggregateException(errors);
    }
}
