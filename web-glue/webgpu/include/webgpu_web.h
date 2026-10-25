/* webgpu_web.h — what webgpu.h code needs on the web beyond wgpu-native's webgpu.h (web-glue/webgpu).
 * A surface on the web is a <canvas> chosen by a CSS selector, chained into WGPUSurfaceDescriptor like
 * WGPUSurfaceSourceWindowsHWND on Windows. AquariusLang windows are canvases with the id "aqua-window-<n>"
 * (aqua_window_surface gives n as the surface handle), so the selector is "#aqua-window-<n>".
 * The struct and its sType are Emscripten's (emdawnwebgpu webgpu.h), so code written for Emscripten builds unchanged.
 * Include it only on the web:
 *     #if defined(AQUA_OS_WEB)
 *     #include <webgpu_web.h>
 *     #endif
 */
#ifndef AQUA_WEBGPU_WEB_H
#define AQUA_WEBGPU_WEB_H
#include <webgpu/webgpu.h>

#define WGPUSType_EmscriptenSurfaceSourceCanvasHTMLSelector ((WGPUSType)0x00040000)

typedef struct WGPUEmscriptenSurfaceSourceCanvasHTMLSelector {
    WGPUChainedStruct chain;
    WGPUStringView selector;
} WGPUEmscriptenSurfaceSourceCanvasHTMLSelector;

#define WGPU_EMSCRIPTEN_SURFACE_SOURCE_CANVAS_HTML_SELECTOR_INIT \
    _wgpu_MAKE_INIT_STRUCT(WGPUEmscriptenSurfaceSourceCanvasHTMLSelector, { \
        _wgpu_MAKE_INIT_STRUCT(WGPUChainedStruct, { NULL _wgpu_COMMA WGPUSType_EmscriptenSurfaceSourceCanvasHTMLSelector _wgpu_COMMA }) _wgpu_COMMA \
        WGPU_STRING_VIEW_INIT _wgpu_COMMA \
    })

#endif
