using AquariusLang.External;
using AquariusLang.Physics;

namespace AquariusTests.Compiler;

public class ExternalLibraryTest
{
    private interface IExample { }
    private sealed class Adapter(List<int> disposed, int id) : IExample, IDisposable
    {
        public void Dispose() => disposed.Add(id);
    }
    private static ExternalLibraryDescriptor Descriptor(string id) => new(id, "1.0", 1, ExternalLibraryProfile.CoreWasm, typeof(IExample), Array.Empty<string>());
    [Fact]
    public void RegistryChecksVersionsCreatesLazilyAndDisposesInReverseOrder()
    {
        var disposed = new List<int>(); var registry = new ExternalLibraryRegistry(); int calls = 0;
        ExternalLibraryDescriptor first = Descriptor("first"), second = Descriptor("second");
        registry.Register<IExample>(first, 1, () => { calls++; return new Adapter(disposed, 1); });
        registry.Register<IExample>(second, 1, () => new Adapter(disposed, 2));
        Assert.Equal(0, calls); Assert.Same(registry.Resolve<IExample>(first), registry.Resolve<IExample>(first)); Assert.Equal(1, calls);
        registry.Resolve<IExample>(second); registry.Dispose(); registry.Dispose(); Assert.Equal(new[] { 2, 1 }, disposed);
        Assert.Throws<ObjectDisposedException>(() => registry.Resolve<IExample>(first));
    }
    [Fact]
    public void RegistryRejectsMissingDuplicateAndMismatchedAdapters()
    {
        using var registry = new ExternalLibraryRegistry(); var descriptor = Descriptor("test");
        Assert.Throws<NotSupportedException>(() => registry.Resolve<IExample>(descriptor));
        Assert.Throws<ArgumentException>(() => registry.Register<IExample>(descriptor, 2, () => new Adapter(new(), 1)));
        Assert.Throws<ArgumentException>(() => registry.Register<IDisposable>(descriptor, 1, () => new Adapter(new(), 1)));
        registry.Register<IExample>(descriptor, 1, () => new Adapter(new(), 1));
        Assert.Throws<ArgumentException>(() => registry.Register<IExample>(descriptor, 1, () => new Adapter(new(), 2)));
    }
    [Fact]
    public void CoreOwnsPinnedJoltAssetsWithoutEngineDependencies()
    {
        Assert.Equal(ExternalLibraryProfile.Emscripten, ExternalLibraries.Jolt.Profile);
        Assert.Equal(typeof(IPhysicsBackend), ExternalLibraries.Jolt.Contract);
        Assert.Equal("0.24.0", ExternalLibraries.Jolt.Version);
        foreach (var asset in ExternalLibraries.Jolt.Assets) { Assert.NotEmpty(ExternalLibraries.ReadAsset(ExternalLibraries.Jolt, asset)); Assert.Equal(64, ExternalLibraries.Sha256(ExternalLibraries.Jolt, asset).Length); }
        Assert.Throws<ArgumentException>(() => ExternalLibraries.ReadAsset(ExternalLibraries.Jolt, "undeclared.wasm"));
        Assert.DoesNotContain(typeof(ExternalLibraries).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Wasmtime") || a.Name.StartsWith("ClearScript"));
    }
}
