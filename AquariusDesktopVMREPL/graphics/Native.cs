using System.Runtime.InteropServices;

namespace AquariusREPL.Graphics;

internal static class Native {
    private const string Library = "aquarius_graphics";
    static Native() {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, assembly, search) => {
            if (name != Library) return IntPtr.Zero;
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            string rid = os + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            string file = os == "win" ? Library + ".dll" : os == "osx" ? "lib" + Library + ".dylib" : "lib" + Library + ".so";
            string path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", file);
            if (File.Exists(path)) return NativeLibrary.Load(path);
            if (NativeLibrary.TryLoad(file, assembly, search, out IntPtr handle)) return handle;
            throw new DllNotFoundException($"Graphics library missing for {rid}. Build native/CMakeLists.txt and install to runtimes/{rid}/native; on Windows run native/build.ps1, then rebuild AquariusDesktopVMREPL.");
        });
    }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aqua_init();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_terminate();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr aqua_error();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_hint(int name, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr aqua_window(int w, int h, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aqua_wgpu_handles(IntPtr window, out IntPtr display, out IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_destroy(IntPtr window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_current(IntPtr window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aqua_load();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr aqua_proc([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aqua_should_close(IntPtr window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_close(IntPtr window, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_swap(IntPtr window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_poll();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_interval(int interval);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern double aqua_time();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aqua_key(IntPtr window, int key);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aqua_mouse(IntPtr window, int button);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_cursor(IntPtr window, out double x, out double y);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_input(IntPtr window, int mode, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_framebuffer(IntPtr window, out int w, out int h);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_window_size(IntPtr window, out int w, out int h);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_set_window_size(IntPtr window, int w, int h);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern double aqua_scroll();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aqua_character();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_text_input(IntPtr window, int enabled);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr aqua_text_event(IntPtr window, out int kind, out int cursor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aqua_text_composition_supported();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_text_rect(IntPtr window, int x, int y, int width, int height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aqua_monitor_size(out int w, out int h);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aqua_fullscreen(IntPtr window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr aqua_image([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flip, out int w, out int h);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aqua_image_free(IntPtr pixels);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr aqua_image_error();
}
