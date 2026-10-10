using AquariusLang.Object;
namespace AquariusLang.Desktop.Physics;
// Compatibility facade; all Aquarius-facing validation and registration lives in core.
internal sealed class PhysicsRuntime : IDisposable {
    private readonly AquariusLang.External.ExternalLibraryRegistry libraries = new();
    private readonly AquariusLang.Physics.PhysicsRuntime shared;
    internal PhysicsRuntime() {
        libraries.Register<AquariusLang.Physics.IPhysicsBackend>(AquariusLang.External.ExternalLibraries.Jolt,1,()=>new WasmJoltBackend());
        shared=new(libraries.Resolve<AquariusLang.Physics.IPhysicsBackend>(AquariusLang.External.ExternalLibraries.Jolt));
    }
    internal bool TryImport(string name, out ModuleObj module) => shared.TryImport(name, out module);
    public void Dispose() { try { shared.Dispose(); } finally { libraries.Dispose(); } }
}
