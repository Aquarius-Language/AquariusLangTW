using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;
using static AquariusLang.Application.ApplicationValues;

namespace AquariusLang.Application;

/// <summary>Language registration and ownership. Hosts supply implementations; core never locates OS resources.</summary>
public sealed partial class ApplicationRuntime : IDisposable {
    private readonly IApplicationHost host;
    private readonly FileService files;
    private readonly ApplicationLimits limits = new();
    private readonly Dictionary<string, ModuleObj> modules = new();
    private readonly Dictionary<IObject, object> objects = new();
    private readonly List<IDisposable> disposables = new();
    private readonly List<IAsyncDisposable> asyncDisposables = new();
    private CancellationToken cancellation;
    private bool disposed;
    public ApplicationRuntime(IApplicationHost host) { this.host = host; files = new(host.Files, limits); RegisterFiles(); RegisterData(); RegisterEditing(); RegisterHost(); }
    public bool TryImport(string name, out ModuleObj module) => modules.TryGetValue(name, out module!);
    private ModuleObj Module(string name) { var m = new ModuleObj(AquaEnvironment.NewEnvironment()); modules.Add(name, m); return m; }
    private void Bind(ModuleObj module, string name, int min, int max, Func<IObject[], IObject> action) {
        var function = new BuiltinObj(args => {
            try { if (disposed) throw new ObjectDisposedException(nameof(ApplicationRuntime)); if (args.Length < min || args.Length > max) throw new ArgumentException($"Expected {min}..{max} arguments."); return action(args); }
            catch (Exception e) when (e is ApplicationFailure or ArgumentException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException or OverflowException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException) {
                string kind = e is ApplicationFailure failure ? failure.Kind.ToString() : e is OperationCanceledException ? "Cancelled" : e is UnauthorizedAccessException ? "PermissionDenied" : e is NotSupportedException or DllNotFoundException or EntryPointNotFoundException ? "Unsupported" : e is FileNotFoundException or DirectoryNotFoundException ? "NotFound" : e is IOException ? "Unavailable" : "InvalidData";
                return new ErrorObj($"{name}: {kind}: {e.Message}");
            }
        });
        function.RetainedEnvironment = module._Environment;
        FunctionRegistration.Define(module._Environment, function, ApplicationCatalog.TraditionalChinese(name), name);
    }
    private void Action(ModuleObj module, string name, int min, int max, Action<IObject[]> action) => Bind(module, name, min, max, a => { action(a); return RepeatedPrimitives.NULL; });
    private T Owned<T>(IObject value) where T : class => objects.TryGetValue(value, out var obj) && obj is T result ? result : throw new ArgumentException("Expected a resource owned by this application runtime.");
    private static T Await<T>(ValueTask<T> task) => task.AsTask().GetAwaiter().GetResult();
    private static void Await(ValueTask task) => task.AsTask().GetAwaiter().GetResult();
    public ModuleObj Resource(FileResource resource) { var m = Record(resource); objects.Add(m, resource); return m; }
    public ModuleObj Image(PixelImage image) {
        var m = Record(new { image.Width, image.Height, pixelFormat = "RGBA8", alpha = image.Alpha.ToString(), image.Metadata }); objects.Add(m, image);
        Bind(m, "Pixels", 0, 0, _ => Binary(image.Pixels.ToArray())); return m;
    }
    public PixelImage GetImage(IObject image) => Owned<PixelImage>(image);
    public void CollectImages(HashSet<object> reachable) {
        foreach (var pair in objects.Where(p => p.Value is PixelImage && p.Key is ModuleObj m && !reachable.Contains(m._Environment)).ToArray()) objects.Remove(pair.Key);
    }
    public void Dispose() {
        if (disposed) return;
        // Dispose all resources even if one close/cleanup fails.
        var errors = new List<Exception>();
        foreach (var resource in disposables.AsEnumerable().Reverse()) try { resource.Dispose(); } catch (Exception e) { errors.Add(e); }
        foreach (var resource in asyncDisposables.AsEnumerable().Reverse()) try { Await(resource.DisposeAsync()); } catch (Exception e) { errors.Add(e); }
        objects.Clear(); disposed = true; if (errors.Count > 0) throw new AggregateException(errors);
    }
}
