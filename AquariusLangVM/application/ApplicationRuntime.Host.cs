using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using AquariusLang.Object;
using static AquariusLang.Application.ApplicationValues;

namespace AquariusLang.Application;

public sealed partial class ApplicationRuntime {
    private static T Options<T>(IObject value) => JsonSerializer.Deserialize<T>(DocumentSerialization.Json(JsonValue(value)), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ArgumentException("Invalid options.");
    private void RegisterHost() {
        var m = Module("Application");
        Bind(m, "Capabilities", 0, 0, _ => Record(host.Capabilities));
        Bind(m, "LaunchFiles", 0, 0, _ => new ArrayObj(host.Services.LaunchFiles.Select(r => (IObject)Resource(r)).ToArray()));
        Bind(m, "OpenFiles", 1, 1, a => new ArrayObj(Await(host.Services.OpenFiles(Options<PickerOptions>(a[0]), cancellation)).Select(r => (IObject)Resource(r)).ToArray()));
        Bind(m, "SaveFile", 1, 1, a => Await(host.Services.SaveFile(Options<PickerOptions>(a[0]), cancellation)) is FileResource resource ? Resource(resource) : AquariusLang.runtime.RepeatedPrimitives.NULL);
        Bind(m, "PickDirectory", 0, 0, _ => Await(host.Services.PickDirectory(cancellation)) is FileResource resource ? Resource(resource) : AquariusLang.runtime.RepeatedPrimitives.NULL);
        Action(m, "Print", 1, 1, a => Await(host.Services.Print(GetImage(a[0]), cancellation)));
        Bind(m, "AcquireImage", 0, 0, _ => Await(host.Services.AcquireImage(cancellation)) is PixelImage image ? Image(image) : AquariusLang.runtime.RepeatedPrimitives.NULL);
        Action(m, "Accessibility", 1, 1, a => host.Services.UpdateAccessibility(Options<AccessibilityNode[]>(a[0])));
        Action(m, "RegisterFileAssociation", 1, 1, a => host.Services.RegisterFileAssociation(Options<FileAssociation>(a[0])));
        var tasks = Module("Tasks");
        Bind(tasks, "CreateCancellation", 0, 0, _ => {
            var source = new CancellationTokenSource(); disposables.Add(source); var token = Record(new { }); objects.Add(token, source);
            Action(token, "Cancel", 0, 0, _ => source.Cancel()); Bind(token, "IsCancelled", 0, 0, _ => new BooleanObj(source.IsCancellationRequested));
            Action(token, "Dispose", 0, 0, _ => { source.Dispose(); objects.Remove(token); }); return token;
        });
        Action(tasks, "UseCancellation", 1, 1, a => cancellation = a[0] is NullObj ? default : Owned<CancellationTokenSource>(a[0]).Token);
    }
    public ModuleObj Window(IWindowIntegration window) {
        var m = Record(new { });
        Bind(m, "Capabilities", 0, 0, _ => Record(window.Capabilities));
        Action(m, "SetTitle", 1, 1, a => window.SetTitle(Text(a[0]))); Action(m, "SetCursor", 1, 1, a => window.SetCursor(Enum.Parse<CursorShape>(Text(a[0]), true)));
        Action(m, "SetCustomCursor", 3, 3, a => window.SetCustomCursor(GetImage(a[0]), Integer(a[1]), Integer(a[2])));
        Action(m, "CapturePointer", 1, 1, a => window.CapturePointer(Integer(a[0]))); Action(m, "ReleasePointer", 1, 1, a => window.ReleasePointer(Integer(a[0])));
        Action(m, "ResolveClose", 1, 1, a => window.ResolveClose(Enum.Parse<CloseDecision>(Text(a[0]), true)));
        Bind(m, "Poll", 0, 0, _ => new ArrayObj(window.Poll().Select(e => {
            var record = Record(e); if (e.Files != null) record._Environment.Create("files", new ArrayObj(e.Files.Select(r => (IObject)Resource(r)).ToArray())); return (IObject)record;
        }).ToArray())); return m;
    }
}
