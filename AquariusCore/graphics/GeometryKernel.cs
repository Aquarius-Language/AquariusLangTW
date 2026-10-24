using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AquariusLang.External;
using AquariusLang.Wasm;

namespace AquariusLang.Graphics;

/// <summary>
/// The Processing geometry kernel (geometry.wasm, built from geometry.c): triangulation, strokes with joins and caps,
/// ellipses, rounded rectangles, curves and primitive meshes, shared by every host. A host writes <see cref="State"/>
/// and the operation's input, runs an operation and draws <see cref="Ranges"/> of <see cref="Vertices"/> (12 floats
/// per vertex: position, normal, uv, color) in order.
/// </summary>
public sealed class GeometryKernel : IDisposable
{
    public const int Abi = 1;
    // State block layout; mirrors the S_* enum in geometry.c.
    public const int Weight = 0, Cap = 1, Join = 2, Is3D = 3, Width = 4, Height = 5, StrokeColor = 6, FillColor = 10, Normal = 14,
        HasFill = 17, HasStroke = 18, RectMode = 19, EllipseMode = 20, Detail = 21, Tightness = 22, Mvp = 23, StateSize = 39;
    public const int FloatsPerVertex = 12;
    public enum RangeKind { Fill = 0, UnlitFill = 1, Stroke = 2 }
    [StructLayout(LayoutKind.Sequential)] public readonly record struct Range(int Start, int Count, RangeKind Kind);

    private readonly IWasmInstance wasm;
    private readonly int state;
    private readonly Func<int> vertices, vertexCount, ranges, rangeCount, indices, invalidate;
    private readonly Func<int, int> input;

    public GeometryKernel(IWasmEngine engine)
    {
        wasm = engine.Instantiate(ExternalLibraries.ReadAsset(ExternalLibraries.Geometry, "geometry.wasm"), new Dictionary<string, Delegate>());
        try {
            wasm.Invoke("_initialize");
            if (wasm.Invoke("geo_abi") != Abi) throw new NotSupportedException("Unsupported geometry kernel ABI.");
            invalidate = wasm.Export("geo_state"); state = invalidate();
            vertices = wasm.Export("geo_vertices"); vertexCount = wasm.Export("geo_vertex_count");
            ranges = wasm.Export("geo_ranges"); rangeCount = wasm.Export("geo_range_count");
            indices = wasm.Export("geo_indices"); input = wasm.Export1("geo_input");
        } catch { wasm.Dispose(); throw; }
    }

    /// <summary>The state block. Writing it is enough for the next operation; <see cref="StateChanged"/> drops the
    /// kernel's cached inverse transform.</summary>
    public Span<float> State => MemoryMarshal.Cast<byte, float>(wasm.Memory(state, StateSize * 4));
    public void StateChanged() => invalidate();
    public Span<float> Input(int floats)
    {
        int pointer = input(Math.Max(1, floats));
        if (pointer == 0) throw new OutOfMemoryException("Geometry kernel input could not grow.");
        return MemoryMarshal.Cast<byte, float>(wasm.Memory(pointer, floats * 4));
    }
    /// <summary>Runs an operation (geo_polygon, geo_ellipse, ...) and returns its non-negative result.</summary>
    public int Run(string operation, params int[] arguments) => Check(wasm.Invoke(operation, arguments));
    public ReadOnlySpan<float> Vertices
    {
        get { int count = vertexCount(); return count == 0 ? default : MemoryMarshal.Cast<byte, float>(wasm.Memory(vertices(), count * FloatsPerVertex * 4)); }
    }
    public ReadOnlySpan<Range> Ranges
    {
        get { int count = rangeCount(); return count == 0 ? default : MemoryMarshal.Cast<byte, Range>(wasm.Memory(ranges(), count * 12)); }
    }
    public ReadOnlySpan<int> Indices(int count) => count == 0 ? default : MemoryMarshal.Cast<byte, int>(wasm.Memory(indices(), count * 4));
    /// <summary>The input buffer as floats, after an operation (geo_curve_points) wrote results there.</summary>
    public ReadOnlySpan<float> Output(int offset, int floats) => Input(offset + floats)[offset..];

    private static int Check(int result) => result switch {
        -1 => throw new ArgumentException("Polygon has zero area."),
        -2 => throw new ArgumentException("Polygon boundary must be simple and have nonzero area; use beginContour() for holes."),
        -3 => throw new OutOfMemoryException("Geometry kernel memory exhausted."),
        < 0 => throw new InvalidOperationException($"Geometry kernel error {result}."),
        _ => result };

    public void Dispose() => wasm.Dispose();
}
