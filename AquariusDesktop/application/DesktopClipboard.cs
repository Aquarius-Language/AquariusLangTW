using System.Runtime.InteropServices;
using AquariusLang.Application;
using AquariusLang.Desktop.Graphics;

namespace AquariusLang.Desktop.Application;

public sealed class DesktopClipboard(IImageCodec images) : IClipboard {
    private static uint Png => RegisterClipboardFormatW("PNG");
    private static bool Windows => OperatingSystem.IsWindows();
    public IReadOnlyDictionary<string, Capability> Capabilities { get; } = new Dictionary<string, Capability> {
        ["text"] = new(true), ["image"] = new(Windows, Windows ? null : "Native image clipboard adapter is currently Windows-only."), ["multiple"] = new(Windows)
    };
    [ThreadStatic] private static IntPtr owner;
    private static void Open() {
        owner = CreateWindowExW(0, "STATIC", "Aquarius clipboard", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (owner == IntPtr.Zero || !OpenClipboard(owner)) { if (owner != IntPtr.Zero) DestroyWindow(owner); owner = IntPtr.Zero; throw new ApplicationFailure(FailureKind.Unavailable, "Clipboard is busy; retry after returning to the event loop."); }
    }
    private static void Close() { CloseClipboard(); DestroyWindow(owner); owner = IntPtr.Zero; }
    public ValueTask<IReadOnlyList<string>> Formats(CancellationToken cancellation = default) {
        cancellation.ThrowIfCancellationRequested(); var result = new List<string>();
        if (!Windows) { if (Marshal.PtrToStringUTF8(Native.aqua_clipboard_get()) is string text && text.Length > 0) result.Add("text/plain"); }
        else { Open(); try { if (IsClipboardFormatAvailable(13)) result.Add("text/plain"); if (IsClipboardFormatAvailable(Png) || IsClipboardFormatAvailable(17) || IsClipboardFormatAvailable(8)) result.Add("image/png"); } finally { Close(); } }
        return ValueTask.FromResult<IReadOnlyList<string>>(result);
    }
    private static byte[]? Get(uint format) {
        IntPtr handle = GetClipboardData(format); if (handle == IntPtr.Zero) return null;
        long size = checked((long)GlobalSize(handle)); new ApplicationLimits().Bytes(size); IntPtr address = GlobalLock(handle);
        if (address == IntPtr.Zero) throw new ApplicationFailure(FailureKind.Unavailable, "Clipboard data could not be locked.");
        try { var bytes = new byte[(int)size]; Marshal.Copy(address, bytes, 0, bytes.Length); return bytes; } finally { GlobalUnlock(handle); }
    }
    public async ValueTask<ClipboardContent> Read(CancellationToken cancellation = default) {
        cancellation.ThrowIfCancellationRequested(); if (!Windows) return new(Marshal.PtrToStringUTF8(Native.aqua_clipboard_get()));
        byte[]? text, png, dib; Open(); try { text = Get(13); png = Get(Png); dib = png == null ? Get(17) ?? Get(8) : null; } finally { Close(); }
        string? value = text == null ? null : TextEncoding.Get("utf-16le").GetString(text).TrimEnd('\0'); PixelImage? image = null;
        if (png != null) image = await images.Decode(png, 0, new(), cancellation).ConfigureAwait(false);
        else if (dib != null) {
            if (dib.Length < 40) throw new ApplicationFailure(FailureKind.InvalidData, "Clipboard DIB is truncated.");
            int header = BitConverter.ToInt32(dib), bits = BitConverter.ToUInt16(dib, 14), compression = BitConverter.ToInt32(dib, 16), used = BitConverter.ToInt32(dib, 32);
            if (header < 40 || header > dib.Length || used < 0 || used > 256) throw new ApplicationFailure(FailureKind.InvalidData, "Unsupported clipboard DIB header.");
            int colors = bits <= 8 ? used == 0 ? 1 << bits : used : 0;
            int offset = checked(14 + header + colors * 4 + (header == 40 && compression == 3 ? 12 : 0));
            var bmp = new byte[dib.Length + 14]; bmp[0] = 66; bmp[1] = 77; BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2); BitConverter.GetBytes(offset).CopyTo(bmp, 10); dib.CopyTo(bmp, 14);
            image = await images.Decode(bmp, 0, new(), cancellation).ConfigureAwait(false);
        }
        return new(value, image);
    }
    private static void Put(uint format, byte[] bytes) {
        IntPtr handle = GlobalAlloc(0x42, (nuint)bytes.Length); if (handle == IntPtr.Zero) throw new ApplicationFailure(FailureKind.Unavailable, "Clipboard allocation failed.");
        bool transferred = false;
        try { IntPtr address = GlobalLock(handle); if (address == IntPtr.Zero) throw new ApplicationFailure(FailureKind.Unavailable, "Clipboard allocation cannot be locked.");
            try { Marshal.Copy(bytes, 0, address, bytes.Length); } finally { GlobalUnlock(handle); }
            transferred = SetClipboardData(format, handle) != IntPtr.Zero; if (!transferred) throw new ApplicationFailure(FailureKind.Unavailable, "Publishing clipboard data failed.");
        } finally { if (!transferred) GlobalFree(handle); }
    }
    public async ValueTask Write(ClipboardContent content, CancellationToken cancellation = default) {
        if (content.Text == null && content.Image == null) throw new ArgumentException("Clipboard content is empty."); cancellation.ThrowIfCancellationRequested();
        if (!Windows) { if (content.Image != null) throw new ApplicationFailure(FailureKind.Unsupported, "Image clipboard adapter unavailable on this OS."); Native.aqua_clipboard_set(content.Text!); return; }
        byte[]? text = content.Text == null ? null : TextEncoding.Get("utf-16le").GetBytes(content.Text + '\0');
        byte[]? png = content.Image == null ? null : await images.Encode(content.Image, ImageFormat.Png, new(), new(), cancellation).ConfigureAwait(false);
        byte[]? dib = null;
        if (content.Image is PixelImage image) { var pixels = image.StraightPixels(); dib = new byte[124 + pixels.Length];
            BitConverter.GetBytes(124).CopyTo(dib, 0); BitConverter.GetBytes(image.Width).CopyTo(dib, 4); BitConverter.GetBytes(-image.Height).CopyTo(dib, 8);
            BitConverter.GetBytes((ushort)1).CopyTo(dib, 12); BitConverter.GetBytes((ushort)32).CopyTo(dib, 14); BitConverter.GetBytes(3).CopyTo(dib, 16);
            foreach (var (offset, mask) in new[] { (40, 0x00ff0000u), (44, 0x0000ff00u), (48, 0x000000ffu), (52, 0xff000000u), (56, 0x73524742u) }) BitConverter.GetBytes(mask).CopyTo(dib, offset);
            for (int i = 0; i < pixels.Length; i += 4) { dib[124 + i] = pixels[i + 2]; dib[125 + i] = pixels[i + 1]; dib[126 + i] = pixels[i]; dib[127 + i] = pixels[i + 3]; }
        }
        cancellation.ThrowIfCancellationRequested(); Open(); try { if (!EmptyClipboard()) throw new ApplicationFailure(FailureKind.Unavailable, "Clipboard cannot be cleared.");
            if (text != null) Put(13, text); if (png != null) Put(Png, png); if (dib != null) Put(17, dib);
        } finally { Close(); }
    }
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint extended, string cls, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
}
