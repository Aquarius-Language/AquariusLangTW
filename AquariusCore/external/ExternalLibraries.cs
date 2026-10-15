using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

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
    public static ExternalLibraryDescriptor WebGpu { get; } = new("webgpu", "1", 1, ExternalLibraryProfile.HostCapability, typeof(AquariusLang.Graphics.IWgpuBackend), System.Array.Empty<string>());
    public static ExternalLibraryDescriptor Audio { get; } = new("audio", "miniaudio-0.11.25", 1, ExternalLibraryProfile.CoreWasm, typeof(AquariusLang.Audio.IAudioBackend),
        new[] { "audio.wasm", "audio-core.mjs", "MINIAUDIO-LICENSE.txt", "AUDIO-THIRD-PARTY-NOTICES.txt" },
        new Dictionary<string,string> { ["audio.wasm"]="audio.wasm", ["audio-core.mjs"]="audio-core.mjs", ["MINIAUDIO-LICENSE.txt"]="MINIAUDIO-LICENSE.txt", ["AUDIO-THIRD-PARTY-NOTICES.txt"]="AUDIO-THIRD-PARTY-NOTICES.txt" });
    public static ExternalLibraryDescriptor Network { get; } = new("network", "1", 1, ExternalLibraryProfile.HostCapability, typeof(AquariusLang.Network.INetworkBackend), System.Array.Empty<string>());
    public static ExternalLibraryDescriptor Webcam { get; } = new("webcam", "1", 1, ExternalLibraryProfile.HostCapability, typeof(AquariusLang.Webcam.IWebcamBackend), System.Array.Empty<string>());
    // Explicit native integration: desktop WebGPU requires ORT's native Dawn plugin; browser kernels use ORT Wasm/WebGPU.
    public static ExternalLibraryDescriptor Onnx { get; } = new("onnx", "1.30.0", 1, ExternalLibraryProfile.HostCapability, typeof(AquariusLang.Onnx.IOnnxBackend),
        new[] { "ort.webgpu.min.mjs", "ort-wasm-simd-threaded.asyncify.mjs", "ort-wasm-simd-threaded.asyncify.wasm", "LICENSE.txt", "ThirdPartyNotices.txt" },
        new Dictionary<string,string> { ["ort.webgpu.min.mjs"]="ort.webgpu.min.mjs", ["ort-wasm-simd-threaded.asyncify.mjs"]="ort-wasm-simd-threaded.asyncify.mjs", ["ort-wasm-simd-threaded.asyncify.wasm"]="ort-wasm-simd-threaded.asyncify.wasm", ["LICENSE.txt"]="ONNX-LICENSE.txt", ["ThirdPartyNotices.txt"]="ONNX-THIRD-PARTY-NOTICES.txt" });
    public static ExternalLibraryDescriptor MediaPipe { get; } = new("mediapipe", "1", 1, ExternalLibraryProfile.CoreWasm, typeof(AquariusLang.MediaPipe.PoseTracker),
        new[] { "pose.wasm" }, new Dictionary<string,string> { ["pose.wasm"]="pose.wasm" });
    public static IReadOnlyList<ExternalLibraryDescriptor> All { get; } = System.Array.AsReadOnly(new[] { WebGpu, Audio, Webcam, Network, Onnx, MediaPipe });
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
        if (descriptor.Profile == ExternalLibraryProfile.CoreWasm)
            throw new ArgumentException("CoreWasm libraries require verified Wasm artifacts and a library ABI. A CLR factory cannot admit a computational library.");
        if (descriptor.Profile == ExternalLibraryProfile.Emscripten)
            throw new ArgumentException("New computational libraries must use the Wasm library ABI. Use a third-party DynamicLibrary package for Emscripten integrations.");
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
