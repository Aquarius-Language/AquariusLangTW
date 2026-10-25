/* webgpu.h surfaces for aqua/window.h windows: HWND, X11, Wayland and ANativeWindow natively, the window's canvas
   ("#aqua-window-<n>") on the web. */
#include "gpu.h"
#include <stdio.h>
#if defined(AQUA_OS_WEB)
#include <webgpu_web.h>
#endif

WGPUSurface gpu_surface_create(WGPUInstance instance, aqua_window *window) {
    aqua_surface s;
    if (!aqua_window_surface(window, &s)) return NULL;
    WGPUSurfaceDescriptor descriptor = WGPU_SURFACE_DESCRIPTOR_INIT;
    switch (s.kind) {
#if defined(AQUA_OS_WEB)
    case AQUA_SURFACE_CANVAS: {
        char selector[64];
        snprintf(selector, sizeof selector, "#aqua-window-%u", (unsigned)(uintptr_t)s.handle);
        WGPUEmscriptenSurfaceSourceCanvasHTMLSelector canvas = WGPU_EMSCRIPTEN_SURFACE_SOURCE_CANVAS_HTML_SELECTOR_INIT;
        canvas.selector = gpu_text(selector);
        descriptor.nextInChain = &canvas.chain;
        return wgpuInstanceCreateSurface(instance, &descriptor);
    }
#else
    case AQUA_SURFACE_WIN32: {
        WGPUSurfaceSourceWindowsHWND source = WGPU_SURFACE_SOURCE_WINDOWS_HWND_INIT;
        source.hinstance = s.display; source.hwnd = s.handle;
        descriptor.nextInChain = &source.chain;
        return wgpuInstanceCreateSurface(instance, &descriptor);
    }
    case AQUA_SURFACE_X11: {
        WGPUSurfaceSourceXlibWindow source = WGPU_SURFACE_SOURCE_XLIB_WINDOW_INIT;
        source.display = s.display; source.window = (uint64_t)(uintptr_t)s.handle;
        descriptor.nextInChain = &source.chain;
        return wgpuInstanceCreateSurface(instance, &descriptor);
    }
    case AQUA_SURFACE_WAYLAND: {
        WGPUSurfaceSourceWaylandSurface source = WGPU_SURFACE_SOURCE_WAYLAND_SURFACE_INIT;
        source.display = s.display; source.surface = s.handle;
        descriptor.nextInChain = &source.chain;
        return wgpuInstanceCreateSurface(instance, &descriptor);
    }
    case AQUA_SURFACE_ANDROID: {
        WGPUSurfaceSourceAndroidNativeWindow source = WGPU_SURFACE_SOURCE_ANDROID_NATIVE_WINDOW_INIT;
        source.window = s.handle;
        descriptor.nextInChain = &source.chain;
        return wgpuInstanceCreateSurface(instance, &descriptor);
    }
#endif
    default: return NULL;
    }
}
