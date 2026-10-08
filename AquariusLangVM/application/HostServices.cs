using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AquariusLang.Application;

public sealed record ClipboardContent(string? Text = null, PixelImage? Image = null);
public interface IClipboard {
    IReadOnlyDictionary<string, Capability> Capabilities { get; }
    ValueTask<IReadOnlyList<string>> Formats(CancellationToken cancellation = default);
    ValueTask<ClipboardContent> Read(CancellationToken cancellation = default);
    ValueTask Write(ClipboardContent content, CancellationToken cancellation = default);
}
public sealed record FileFilter(string Name, IReadOnlyList<string> Extensions);
public sealed record PickerOptions(string Title = "", string SuggestedName = "", bool Multiple = false, IReadOnlyList<FileFilter>? Filters = null);
public sealed record FontInfo(string Family, string Style);
public sealed record TextRectangle(double X, double Y, double Width, double Height);
public interface IFontService {
    ValueTask<IReadOnlyList<FontInfo>> Enumerate(CancellationToken cancellation = default);
    bool Available(string family);
    TextRectangle Measure(string text, string family, double size);
    IReadOnlyList<TextRectangle> Selection(string text, int startUtf16, int endUtf16, string family, double size);
}
public sealed record AccessibilityNode(string Id, string Role, string Label, string? Value = null, bool Disabled = false);
public sealed record FileAssociation(string Extension, string ApplicationId, string Description, string Executable, IReadOnlyList<string> Arguments);
public interface IHostServices {
    IReadOnlyList<FileResource> LaunchFiles { get; }
    ValueTask<IReadOnlyList<FileResource>> OpenFiles(PickerOptions options, CancellationToken cancellation = default);
    ValueTask<FileResource?> SaveFile(PickerOptions options, CancellationToken cancellation = default);
    ValueTask<FileResource?> PickDirectory(CancellationToken cancellation = default);
    ValueTask Print(PixelImage image, CancellationToken cancellation = default);
    ValueTask<PixelImage?> AcquireImage(CancellationToken cancellation = default);
    void UpdateAccessibility(IReadOnlyList<AccessibilityNode> nodes);
    void RegisterFileAssociation(FileAssociation association);
}
public enum CloseDecision { Accept, Defer, Cancel }
public enum CursorShape { Arrow, Text, Crosshair, Hand, ResizeHorizontal, ResizeVertical, ResizeDiagonal1, ResizeDiagonal2, Move, NotAllowed }
public interface IWindowIntegration {
    IReadOnlyDictionary<string, Capability> Capabilities { get; }
    void SetTitle(string title);
    void SetCursor(CursorShape shape);
    void SetCustomCursor(PixelImage image, int hotspotX, int hotspotY);
    void CapturePointer(long deviceId);
    void ReleasePointer(long deviceId);
    void ResolveClose(CloseDecision decision);
    IReadOnlyList<ApplicationEvent> Poll();
}
[Flags] public enum KeyModifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Super = 8, CapsLock = 16, NumLock = 32 }
public sealed record PointerSample(double X, double Y, double Timestamp, double? Pressure = null, double? TiltX = null, double? TiltY = null);
/// <summary>Time is monotonic seconds. Positions are window logical units (CSS pixels in browsers). Missing optional properties stay null.</summary>
public sealed record ApplicationEvent(string Type, double Timestamp, string? LogicalKey = null, string? PhysicalKey = null,
    KeyModifiers Modifiers = KeyModifiers.None, long? DeviceId = null, string? Device = null, int? Button = null,
    double? WheelX = null, double? WheelY = null, string? WheelUnit = null, IReadOnlyList<PointerSample>? Samples = null, bool? Contact = null,
    bool? Eraser = null, IReadOnlyList<FileResource>? Files = null, bool? State = null);
