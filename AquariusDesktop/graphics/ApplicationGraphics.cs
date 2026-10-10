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
        Bind(env, "ToProcessingImage", 1, a => { var image = Application.GetImage(a[0]); var p = ProcessingImage.Create(this, image.Width, image.Height); p.Bytes = image.StraightPixels(); return RegisterImage(p).Module; });
    }
    private ModuleObj AttachApplicationWindow(IObject value) {
        _ = Window(value); var window = (WindowObject)value;
        if (applicationWindows.TryGetValue(window, out var result)) return result;
        var adapter = new Application.DesktopWindow(() => Window(window), ApplicationHost.FileProvider);
        result = Application.Window(adapter); applicationWindows.Add(window, result); return result;
    }
}
