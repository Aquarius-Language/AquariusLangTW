using System.Runtime.InteropServices;
using AquariusLang.Application;

namespace AquariusLang.Desktop.Application;

internal static class DesktopPrinting {
    internal static void Print(PixelImage image, CancellationToken cancellation) {
        if (!OperatingSystem.IsWindows()) throw new ApplicationFailure(FailureKind.Unsupported, "Native printing adapter unavailable on this OS.");
        cancellation.ThrowIfCancellationRequested(); var dialog = new PrintDialog { Size = (uint)Marshal.SizeOf<PrintDialog>(), Flags = 0x100 | 0x40000, Copies = 1 };
        if (!PrintDlgW(ref dialog)) { uint error = CommDlgExtendedError(); if (error != 0) throw new ApplicationFailure(FailureKind.Unavailable, "Print dialog error " + error); throw new OperationCanceledException("Print dialog cancelled."); }
        bool started = false;
        try {
            cancellation.ThrowIfCancellationRequested(); var info = new DocumentInfo { Size = Marshal.SizeOf<DocumentInfo>(), Name = "Aquarius document" };
            if (StartDocW(dialog.Dc, ref info) <= 0) throw new ApplicationFailure(FailureKind.Unavailable, "Printer cannot start document."); started = true;
            if (StartPage(dialog.Dc) <= 0) throw new ApplicationFailure(FailureKind.Unavailable, "Printer cannot start page.");
            var rgba = new ImageEncodingOptions(Background: 0xffffff).Prepare(image, ImageFormat.Bmp); var bgra = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4) { bgra[i] = rgba[i + 2]; bgra[i + 1] = rgba[i + 1]; bgra[i + 2] = rgba[i]; }
            var bitmap = new BitmapInfo { Size = 40, Width = image.Width, Height = -image.Height, Planes = 1, Bits = 32 };
            double scale = Math.Min((double)GetDeviceCaps(dialog.Dc, 8) / image.Width, (double)GetDeviceCaps(dialog.Dc, 10) / image.Height);
            int copied = StretchDIBits(dialog.Dc, 0, 0, (int)(image.Width * scale), (int)(image.Height * scale), 0, 0, image.Width, image.Height, bgra, ref bitmap, 0, 0xcc0020);
            if (copied == 0 || copied == -1) throw new ApplicationFailure(FailureKind.Unavailable, "Printer cannot render image.");
            cancellation.ThrowIfCancellationRequested(); if (EndPage(dialog.Dc) <= 0 || EndDoc(dialog.Dc) <= 0) throw new ApplicationFailure(FailureKind.Unavailable, "Printer could not finish document."); started = false;
        } finally { if (started) AbortDoc(dialog.Dc); DeleteDC(dialog.Dc); if (dialog.DevMode != IntPtr.Zero) GlobalFree(dialog.DevMode); if (dialog.DevNames != IntPtr.Zero) GlobalFree(dialog.DevNames); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct PrintDialog { public uint Size; public IntPtr Owner, DevMode, DevNames, Dc; public uint Flags; public ushort FromPage, ToPage, MinPage, MaxPage, Copies; public IntPtr Instance, CustomData, PrintHook, SetupHook, PrintTemplate, SetupTemplate, PrintTemplateHandle, SetupTemplateHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DocumentInfo { public int Size; public string Name; public string? Output, Type; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int Xppm, Yppm; public uint Used, Important, Color; }
    [DllImport("comdlg32.dll")] private static extern bool PrintDlgW(ref PrintDialog dialog);
    [DllImport("comdlg32.dll")] private static extern uint CommDlgExtendedError();
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int StartDocW(IntPtr dc, ref DocumentInfo info);
    [DllImport("gdi32.dll")] private static extern int StartPage(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int EndPage(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int EndDoc(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int AbortDoc(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("gdi32.dll")] private static extern int StretchDIBits(IntPtr dc, int x, int y, int width, int height, int sx, int sy, int sw, int sh, byte[] pixels, ref BitmapInfo info, uint usage, uint operation);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
