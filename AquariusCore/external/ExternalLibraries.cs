using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AquariusLang.Physics;

namespace AquariusLang.External;

public enum ExternalLibraryProfile { HostCapability, CoreWasm, Emscripten }
public sealed record ExternalLibraryDescriptor(string Id, string Version, int ContractVersion, ExternalLibraryProfile Profile, Type Contract, string[] Assets,
    IReadOnlyDictionary<string,string>? DeploymentNames=null)
{
    public string DeploymentName(string resource) => DeploymentNames!=null && DeploymentNames.TryGetValue(resource,out var name) ? name : $"external-{Id}-{resource}";
}

/// <summary>Core owns versions, contracts and portable assets. Hosts supply engines and capability adapters.</summary>
public static class ExternalLibraries
{
    public static ExternalLibraryDescriptor Jolt { get; } = new("jolt", "0.24.0", 1, ExternalLibraryProfile.Emscripten, typeof(IPhysicsBackend),
        new[] { "vendor-jolt.mjs", "vendor-jolt.wasm", "vendor-jolt.LICENSE.txt", "physics.mjs" },
        new Dictionary<string,string> { ["vendor-jolt.mjs"]="vendor-jolt.mjs", ["vendor-jolt.wasm"]="vendor-jolt.wasm", ["vendor-jolt.LICENSE.txt"]="vendor-jolt.LICENSE.txt", ["physics.mjs"]="physics-core.mjs" });
    public static ExternalLibraryDescriptor WebGpu { get; } = new("webgpu", "1", 1, ExternalLibraryProfile.HostCapability, typeof(AquariusLang.Graphics.IWgpuBackend), System.Array.Empty<string>());
    public static IReadOnlyList<ExternalLibraryDescriptor> All { get; } = System.Array.AsReadOnly(new[] { Jolt, WebGpu });
    public static byte[] ReadAsset(ExternalLibraryDescriptor library, string name)
    {
        if (!library.Assets.Contains(name, StringComparer.Ordinal)) throw new ArgumentException("Asset is not declared by this library.");
        string resource = $"AquariusLang.External.{library.Id}.{name}";
        using var stream = typeof(ExternalLibraries).Assembly.GetManifestResourceStream(resource)
            ?? throw new IOException($"Missing external library asset: {resource}. Run prepare:browser before building.");
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
    public static string Sha256(ExternalLibraryDescriptor library, string name) => Convert.ToHexString(SHA256.HashData(ReadAsset(library, name))).ToLowerInvariant();
}

/// <summary>Per-host composition with explicit contract versions. Missing adapters fail without silently changing engines.</summary>
public sealed class ExternalLibraryRegistry : IDisposable
{
    private readonly Dictionary<string, (ExternalLibraryDescriptor Descriptor, Func<object> Factory)> factories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> instances = new(StringComparer.Ordinal);
    private bool disposed;
    public void Register<T>(ExternalLibraryDescriptor descriptor, int contractVersion, Func<T> factory) where T : class
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (descriptor.Contract != typeof(T) || descriptor.ContractVersion != contractVersion) throw new ArgumentException("Incompatible external library contract.");
        if (!factories.TryAdd(descriptor.Id, (descriptor, () => factory() ?? throw new InvalidOperationException("Adapter factory returned null.")))) throw new ArgumentException($"Duplicate external library adapter: {descriptor.Id}");
    }
    public T Resolve<T>(ExternalLibraryDescriptor descriptor) where T : class
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!factories.TryGetValue(descriptor.Id, out var entry) || entry.Descriptor != descriptor || descriptor.Contract != typeof(T)) throw new NotSupportedException($"Host does not implement {descriptor.Id} contract {descriptor.ContractVersion}.");
        if (!instances.TryGetValue(descriptor.Id, out var instance)) { instance = entry.Factory(); instances.Add(descriptor.Id, instance); }
        return (T)instance;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; var errors = new List<Exception>();
        foreach (var instance in instances.Values.Reverse()) if (instance is IDisposable d)
            {
                try { d.Dispose(); } catch (Exception e) { errors.Add(e); }
            }
        instances.Clear(); if (errors.Count != 0) throw new AggregateException("External library disposal failed.", errors);
    }
}
