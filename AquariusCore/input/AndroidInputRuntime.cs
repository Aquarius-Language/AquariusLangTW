using System;
using System.Linq;
using AquariusLang.Application;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.Input;

public sealed class AndroidInputRuntime
{
    private readonly ModuleObj module = Record();
    public AndroidInputRuntime(IAndroidInputBackend backend)
    {
        Bind("IsSupported", _ => new BooleanObj(backend.IsSupported));
        Bind("Poll", _ =>
        {
            var s = backend.Poll();
            return Record(("orientation", new StringObj(s.Orientation)), ("keyboardVisible", new BooleanObj(s.KeyboardVisible)), ("backRequested", new BooleanObj(s.BackRequested)),
                ("insets", Record(("left", new DoubleObj(s.Insets.Left)), ("top", new DoubleObj(s.Insets.Top)), ("right", new DoubleObj(s.Insets.Right)), ("bottom", new DoubleObj(s.Insets.Bottom)))),
                ("touches", new ArrayObj(s.Touches.Select(t => (IObject)Record(("id", new DoubleObj(t.Id)), ("phase", new StringObj(t.Phase)), ("x", new DoubleObj(t.X)), ("y", new DoubleObj(t.Y)), ("pressure", new DoubleObj(t.Pressure)), ("tool", new StringObj(t.Tool)), ("timestamp", new DoubleObj(t.Timestamp)),
                    ("history", new ArrayObj(t.History.Select(p => (IObject)Record(("x", new DoubleObj(p.X)), ("y", new DoubleObj(p.Y)), ("timestamp", new DoubleObj(p.Timestamp)), ("pressure", p.Pressure.HasValue ? new DoubleObj(p.Pressure.Value) : RepeatedPrimitives.NULL))).ToArray())))).ToArray())));
        });
        Bind("ShowKeyboard", args => { if (args[0] is not BooleanObj b) throw new ArgumentException("Expected a boolean."); backend.ShowKeyboard(b.Value); return RepeatedPrimitives.NULL; });
        Bind("Vibrate", args =>
        {
            if (args[0] is not INumberObj n || !double.IsFinite(n.GetNumValue()) || n.GetNumValue() != Math.Truncate(n.GetNumValue()) || n.GetNumValue() < 1 || n.GetNumValue() > 10000) throw new ArgumentException("Vibration must be 1..10000 milliseconds.");
            backend.Vibrate((int)n.GetNumValue()); return RepeatedPrimitives.NULL;
        });
        Bind("ResolveBack", args => { if (args[0] is not StringObj s || s.Value is not ("Accept" or "Defer" or "Cancel")) throw new ArgumentException("Expected Accept, Defer or Cancel."); backend.ResolveBack(Enum.Parse<CloseDecision>(s.Value)); return RepeatedPrimitives.NULL; });
    }
    public bool TryImport(string name, out ModuleObj result) { result = module; return name is "AndroidInput" or "Android輸入"; }
    private void Bind(string name, Func<IObject[], IObject> action)
    {
        var definition = AndroidInputCatalog.Functions.Single(f => f.EnglishName == name); var fn = new BuiltinObj(args =>
        {
            try { if (args.Length != definition.MinimumArguments) throw new ArgumentException("Invalid argument count."); return action(args); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException) { return new ErrorObj($"AndroidInput.{name}: {e.Message}"); }
        })
        { RetainedEnvironment = module._Environment }; FunctionRegistration.Define(module._Environment, fn, definition.TraditionalChineseName, name);
    }
    private static ModuleObj Record(params (string Name, IObject Value)[] values) { var m = new ModuleObj(AquaEnvironment.NewEnvironment()); foreach (var v in values) m._Environment.Create(v.Name, v.Value); return m; }
}
public sealed class UnavailableAndroidInputBackend : IAndroidInputBackend
{
    public bool IsSupported => false;
    public AndroidInputSnapshot Poll() => throw new NotSupportedException("Android input is unavailable on this platform.");
    public void ShowKeyboard(bool visible) => throw new NotSupportedException("Android input is unavailable on this platform.");
    public void Vibrate(int milliseconds) => throw new NotSupportedException("Android input is unavailable on this platform.");
    public void ResolveBack(CloseDecision decision) => throw new NotSupportedException("Android input is unavailable on this platform.");
}
