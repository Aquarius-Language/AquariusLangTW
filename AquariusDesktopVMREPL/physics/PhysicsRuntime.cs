using AquariusLang.Object;
namespace AquariusREPL.Physics;
// Compatibility facade; all Aquarius-facing validation and registration lives in core.
internal sealed class PhysicsRuntime : IDisposable {
    private readonly AquariusLang.Physics.PhysicsRuntime shared = new(new NativeJoltBackend());
    internal bool TryImport(string name, out ModuleObj module) => shared.TryImport(name, out module);
    public void Dispose() => shared.Dispose();
}
