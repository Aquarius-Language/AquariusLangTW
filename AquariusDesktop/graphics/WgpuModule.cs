using AquariusLang.Graphics;
namespace AquariusLang.Desktop.Graphics;
internal sealed partial class GraphicsRuntime {
    private WgpuRuntime sharedWgpu = null!;
    private void RegisterWgpu() => sharedWgpu = new(new SilkWgpuBackend(), WriteImage);
}
