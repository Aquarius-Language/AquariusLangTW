using System.Runtime.InteropServices;
using AquariusLang.Object;

namespace AquariusLang.Desktop.Graphics;

internal sealed partial class GraphicsRuntime {
    private void RegisterImages() {
        var env = Module("STBImage");
        Bind(env, "Load", 2, a => {
            string path = ResourcePath(Text(a[0]));
            if (a[1] is not BooleanObj flip) throw new ArgumentException("Expected a Boolean flip flag.");
            IntPtr pixels = Native.aqua_image(path, flip.Value ? 1 : 0, out int width, out int height);
            if (pixels == IntPtr.Zero) throw new InvalidOperationException(Marshal.PtrToStringUTF8(Native.aqua_image_error()) ?? "Image decoding failed.");
            try {
                int length = checked(width * height * 4);
                var bytes = new byte[length]; Marshal.Copy(pixels, bytes, 0, length);
                var data = new DataObject(length, this); Marshal.Copy(bytes, 0, data.Handle, length); buffers.Add(data);
                return new ArrayObj(new IObject[] { N(width), N(height), N(4), data });
            } finally { Native.aqua_image_free(pixels); }
        });
    }
}
