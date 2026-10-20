using AquariusLang.Object;
using AquariusLang.Application;

namespace AquariusLang.Desktop.Graphics;

internal sealed partial class GraphicsRuntime {
    private readonly Dictionary<WindowObject, ModuleObj> applicationWindows = new();
    private readonly Dictionary<WindowObject, Application.DesktopAccessibility> accessibilityWindows = new();
    private void RegisterApplicationGraphics() {
        ApplicationHost.AccessibilityUpdater = nodes => {
            var window = sketchWindow ?? current ?? windows.FirstOrDefault() ?? throw new InvalidOperationException("Create a graphics window first.");
            if (!accessibilityWindows.TryGetValue(window, out var accessibility)) { accessibility = new(Window(window)); accessibilityWindows.Add(window, accessibility); } accessibility.Update(nodes);
        };
        var env = Module("Window");
        Bind(env, "Attach", 1, a => AttachApplicationWindow(a[0]));
        Bind(env, "Current", 0, _ => sketchWindow == null ? throw new InvalidOperationException("Create a Processing window first.") : AttachApplicationWindow(sketchWindow));
        Bind(env, "FromProcessingImage", 1, a => { var image = ImageObject(a[0]); return Application.Image(new(image.Width, image.Height, ImagePixels(image))); });
        PBind(env, "ToProcessingImage", 1, 2, a => {
            var image = Application.GetImage(a[0]);
            var p = a.Length == 2 ? ImageObject(a[1]) : ProcessingImage.Create(this, image.Width, image.Height);
            if (p.Canvas != null || p.Width != image.Width || p.Height != image.Height)
                throw new ArgumentException("The reusable Processing image must be a regular image with matching dimensions.");
            p.Bytes = image.StraightPixels(); p.Dirty = true;
            return a.Length == 2 ? p.Module : RegisterImage(p).Module;
        });
    }
    private ModuleObj AttachApplicationWindow(IObject value) {
        _ = Window(value); var window = (WindowObject)value;
        if (applicationWindows.TryGetValue(window, out var result)) return result;
        var adapter = new Application.DesktopWindow(() => Window(window), ApplicationHost.FileProvider, () => {
            // A paused sketch must run its draw callback to drain application events,
            // especially intercepted close requests that would otherwise stay pending.
            if (window == sketchWindow) redraw = true;
        });
        result = Application.Window(adapter); applicationWindows.Add(window, result); return result;
    }
}
