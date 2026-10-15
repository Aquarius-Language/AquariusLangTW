using System.Runtime.InteropServices;
using AquariusLang.Application;

namespace AquariusLang.Desktop.Graphics;

// Pango uses Fontconfig's installed fonts and fallback/shaping for CJK, Arabic,
// combining marks and supplementary characters. Cairo supplies the alpha mask.
internal static class LinuxTextRaster {
    internal static (int w, int h) Measure(string text, string font, float size) {
        using var layout = new Layout(text, font, size);
        return (layout.Width, layout.Height);
    }

    internal static (int w, int h, byte[] bytes) Rasterize(string text, string font, float size) {
        using var layout = new Layout(text, font, size);
        int w = layout.Width, h = layout.Height;
        if (w > 8192 || h > 8192) throw new ArgumentException("Rendered text is too large.");
        IntPtr surface = cairo_image_surface_create(0, w, h), context = IntPtr.Zero;
        try {
            if (cairo_surface_status(surface) != 0) throw new InvalidOperationException("Font bitmap allocation failed.");
            context = cairo_create(surface);
            cairo_set_source_rgba(context, 1, 1, 1, 1);
            cairo_move_to(context, -layout.X, -layout.Y);
            pango_cairo_show_layout(context, layout.Handle);
            if (cairo_status(context) != 0) throw new InvalidOperationException("Text rendering failed.");
            cairo_surface_flush(surface);
            int stride = cairo_image_surface_get_stride(surface);
            byte[] row = new byte[stride], bytes = new byte[checked(w * h * 4)];
            IntPtr data = cairo_image_surface_get_data(surface);
            for (int y = 0; y < h; y++) {
                Marshal.Copy(IntPtr.Add(data, y * stride), row, 0, stride);
                for (int x = 0; x < w; x++) {
                    int p = (y * w + x) * 4;
                    bytes[p] = bytes[p + 1] = bytes[p + 2] = 255;
                    bytes[p + 3] = row[x * 4 + (BitConverter.IsLittleEndian ? 3 : 0)];
                }
            }
            return (w, h, bytes);
        } finally {
            if (context != IntPtr.Zero) cairo_destroy(context);
            cairo_surface_destroy(surface);
        }
    }

    internal static IReadOnlyList<FontInfo> Fonts(CancellationToken cancellation) {
        var result = new HashSet<FontInfo>();
        pango_font_map_list_families(pango_cairo_font_map_get_default(), out var families, out int count);
        try {
            for (int i = 0; i < count; i++) {
                cancellation.ThrowIfCancellationRequested();
                IntPtr family = Marshal.ReadIntPtr(families, i * IntPtr.Size);
                string name = Marshal.PtrToStringUTF8(pango_font_family_get_name(family))!;
                pango_font_family_list_faces(family, out var faces, out int faceCount);
                try {
                    for (int j = 0; j < faceCount; j++) {
                        string style = Marshal.PtrToStringUTF8(pango_font_face_get_face_name(Marshal.ReadIntPtr(faces, j * IntPtr.Size)))!;
                        result.Add(new(name, style));
                    }
                } finally { g_free(faces); }
            }
        } finally { g_free(families); }
        return result.OrderBy(f => f.Family, StringComparer.Ordinal).ThenBy(f => f.Style, StringComparer.Ordinal).ToArray();
    }

    private sealed class Layout : IDisposable {
        private IntPtr surface, context, description;
        internal IntPtr Handle { get; private set; }
        internal int X { get; }
        internal int Y { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal Layout(string text, string font, float size) {
            if (text.Length > 4096) throw new ArgumentException("Text is limited to 4096 characters per call.");
            if (!float.IsFinite(size) || size <= 0) throw new ArgumentException("Font size must be positive and finite.");
            try {
                surface = cairo_image_surface_create(0, 1, 1);
                context = cairo_create(surface);
                if (cairo_status(context) != 0) throw new InvalidOperationException("Font context allocation failed.");
                Handle = pango_cairo_create_layout(context);
                description = pango_font_description_new();
                pango_font_description_set_family(description, font);
                pango_font_description_set_absolute_size(description, size * 1024.0);
                pango_layout_set_font_description(Handle, description);
                pango_layout_set_text(Handle, text, -1);
                pango_layout_get_pixel_extents(Handle, out var ink, out var logical);
                X = Math.Min(ink.X, logical.X); Y = Math.Min(ink.Y, logical.Y);
                Width = Math.Max(1, Math.Max(ink.X + ink.Width, logical.X + logical.Width) - X);
                Height = Math.Max(1, Math.Max(ink.Y + ink.Height, logical.Y + logical.Height) - Y);
            } catch { Dispose(); throw; }
        }
        public void Dispose() {
            if (Handle != IntPtr.Zero) { g_object_unref(Handle); Handle = IntPtr.Zero; }
            if (description != IntPtr.Zero) { pango_font_description_free(description); description = IntPtr.Zero; }
            if (context != IntPtr.Zero) { cairo_destroy(context); context = IntPtr.Zero; }
            if (surface != IntPtr.Zero) { cairo_surface_destroy(surface); surface = IntPtr.Zero; }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int X, Y, Width, Height; }
    private const string Pango = "libpango-1.0.so.0", PangoCairo = "libpangocairo-1.0.so.0", Cairo = "libcairo.so.2";
    [DllImport(PangoCairo)] private static extern IntPtr pango_cairo_create_layout(IntPtr context);
    [DllImport(PangoCairo)] private static extern void pango_cairo_show_layout(IntPtr context, IntPtr layout);
    [DllImport(PangoCairo)] private static extern IntPtr pango_cairo_font_map_get_default();
    [DllImport(Pango)] private static extern IntPtr pango_font_description_new();
    [DllImport(Pango)] private static extern void pango_font_description_free(IntPtr description);
    [DllImport(Pango)] private static extern void pango_font_description_set_family(IntPtr description, [MarshalAs(UnmanagedType.LPUTF8Str)] string family);
    [DllImport(Pango)] private static extern void pango_font_description_set_absolute_size(IntPtr description, double size);
    [DllImport(Pango)] private static extern void pango_layout_set_font_description(IntPtr layout, IntPtr description);
    [DllImport(Pango)] private static extern void pango_layout_set_text(IntPtr layout, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int length);
    [DllImport(Pango)] private static extern void pango_layout_get_pixel_extents(IntPtr layout, out Rectangle ink, out Rectangle logical);
    [DllImport(Pango)] private static extern void pango_font_map_list_families(IntPtr map, out IntPtr families, out int count);
    [DllImport(Pango)] private static extern IntPtr pango_font_family_get_name(IntPtr family);
    [DllImport(Pango)] private static extern void pango_font_family_list_faces(IntPtr family, out IntPtr faces, out int count);
    [DllImport(Pango)] private static extern IntPtr pango_font_face_get_face_name(IntPtr face);
    [DllImport("libgobject-2.0.so.0")] private static extern void g_object_unref(IntPtr obj);
    [DllImport("libglib-2.0.so.0")] private static extern void g_free(IntPtr memory);
    [DllImport(Cairo)] private static extern IntPtr cairo_image_surface_create(int format, int width, int height);
    [DllImport(Cairo)] private static extern int cairo_surface_status(IntPtr surface);
    [DllImport(Cairo)] private static extern void cairo_surface_destroy(IntPtr surface);
    [DllImport(Cairo)] private static extern IntPtr cairo_create(IntPtr surface);
    [DllImport(Cairo)] private static extern int cairo_status(IntPtr context);
    [DllImport(Cairo)] private static extern void cairo_destroy(IntPtr context);
    [DllImport(Cairo)] private static extern void cairo_set_source_rgba(IntPtr context, double r, double g, double b, double a);
    [DllImport(Cairo)] private static extern void cairo_move_to(IntPtr context, double x, double y);
    [DllImport(Cairo)] private static extern void cairo_surface_flush(IntPtr surface);
    [DllImport(Cairo)] private static extern int cairo_image_surface_get_stride(IntPtr surface);
    [DllImport(Cairo)] private static extern IntPtr cairo_image_surface_get_data(IntPtr surface);
}
