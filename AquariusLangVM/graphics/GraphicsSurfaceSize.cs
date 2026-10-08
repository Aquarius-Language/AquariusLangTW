using System;

namespace AquariusLang.Graphics;

/// <summary>
/// Renderer-independent surface dimensions supplied by a host at the start of a frame.
/// Logical dimensions govern layout/input/cameras; physical pixels govern GPU attachments
/// and presentation. A hidden/minimized surface retains its last logical size and has no
/// drawable pixels. Hosts resize attachments before notifying scripts and skip presentation
/// while not drawable. Native window/canvas handles remain in the host/backend.
/// </summary>
public readonly record struct GraphicsSurfaceSize {
    public int Width { get; }
    public int Height { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public bool Drawable => PixelWidth > 0 && PixelHeight > 0;

    public GraphicsSurfaceSize(int width, int height, int pixelWidth, int pixelHeight) {
        if (width <= 0 || height <= 0 || pixelWidth < 0 || pixelHeight < 0)
            throw new ArgumentException("Surface dimensions require positive logical sizes and nonnegative pixel sizes.");
        Width = width; Height = height;
        PixelWidth = pixelWidth > 0 && pixelHeight > 0 ? pixelWidth : 0;
        PixelHeight = pixelWidth > 0 && pixelHeight > 0 ? pixelHeight : 0;
    }

    public GraphicsSurfaceSize Resize(int width, int height, int pixelWidth, int pixelHeight) {
        if (width < 0 || height < 0) throw new ArgumentException("Host surface sizes cannot be negative.");
        return new(width > 0 && height > 0 ? width : Width,
            width > 0 && height > 0 ? height : Height, pixelWidth, pixelHeight);
    }
}
