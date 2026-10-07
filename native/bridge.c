#include <glad/gl.h>
#define GLFW_INCLUDE_NONE
#include <GLFW/glfw3.h>
#ifdef _WIN32
#define GLFW_EXPOSE_NATIVE_WIN32
#elif defined(__APPLE__)
#define GLFW_EXPOSE_NATIVE_COCOA
#else
#ifdef AQUA_GLFW_X11
#define GLFW_EXPOSE_NATIVE_X11
#endif
#ifdef AQUA_GLFW_WAYLAND
#define GLFW_EXPOSE_NATIVE_WAYLAND
#endif
#endif
#include <GLFW/glfw3native.h>
#include <stdint.h>
#include <string.h>
#include "text_input.h"
#define STB_IMAGE_IMPLEMENTATION
#define STBI_WINDOWS_UTF8
#include "stb_image.h"
#ifdef _WIN32
#define AQUA __declspec(dllexport)
#else
#define AQUA __attribute__((visibility("default")))
#endif

/* Cdecl bridge exports; OpenGL pointers retain GLAD_API_PTR (Winapi). */
AQUA int aqua_init(void) { return glfwInit(); }
AQUA void aqua_terminate(void) { glfwTerminate(); }
AQUA const char* aqua_error(void) { const char* text = NULL; glfwGetError(&text); return text ? text : "Unknown GLFW error"; }
AQUA void aqua_hint(int name, int value) { glfwWindowHint(name, value); }
static double pending_scroll;
static unsigned int pending_chars[256];
static unsigned int char_read, char_write;
static void on_scroll(GLFWwindow* window, double x, double y) { (void)window; (void)x; pending_scroll -= y; }
static void on_character(GLFWwindow* window, unsigned int c) {
    if (!c || c > 0x10ffff || (c >= 0xd800 && c <= 0xdfff)) return;
    aqua_text_character(window, c);
    if (char_write - char_read < 256) pending_chars[char_write++ % 256] = c;
}
AQUA GLFWwindow* aqua_window(int w, int h, const char* title) {
    GLFWwindow* window = glfwCreateWindow(w, h, title, NULL, NULL);
    pending_scroll = 0; char_read = char_write = 0;
    if (window && !aqua_text_create(window, on_character)) { glfwDestroyWindow(window); return NULL; }
    if (window) { glfwSetScrollCallback(window, on_scroll); glfwSetCharCallback(window, on_character); }
    return window;
}
/* GLFW_NO_API windows let wgpu own rendering and presentation. */
AQUA int aqua_wgpu_handles(GLFWwindow* window, void** display, void** handle) {
#ifdef _WIN32
    *display = GetModuleHandleW(NULL); *handle = glfwGetWin32Window(window); return 1;
#elif defined(__APPLE__)
    extern void* aqua_metal_layer(void* window);
    *display = NULL; *handle = aqua_metal_layer(glfwGetCocoaWindow(window)); return 4;
#else
#ifdef AQUA_GLFW_WAYLAND
    if (glfwGetPlatform() == GLFW_PLATFORM_WAYLAND) {
        *display = glfwGetWaylandDisplay(); *handle = glfwGetWaylandWindow(window); return 3;
    }
#endif
#ifdef AQUA_GLFW_X11
    *display = glfwGetX11Display(); *handle = (void*)(uintptr_t)glfwGetX11Window(window); return 2;
#else
    *display = NULL; *handle = NULL; return 0;
#endif
#endif
}
AQUA void aqua_destroy(GLFWwindow* window) { aqua_text_destroy(window); glfwDestroyWindow(window); }
AQUA void aqua_current(GLFWwindow* window) { glfwMakeContextCurrent(window); }
AQUA int aqua_load(void) { return gladLoadGL(glfwGetProcAddress); }
AQUA GLADapiproc aqua_proc(const char* name) {
#include "gl_table.inc"
    return NULL;
}
AQUA int aqua_should_close(GLFWwindow* window) { return glfwWindowShouldClose(window); }
AQUA void aqua_close(GLFWwindow* window, int value) { glfwSetWindowShouldClose(window, value); }
AQUA void aqua_swap(GLFWwindow* window) { glfwSwapBuffers(window); }
AQUA void aqua_poll(void) { glfwPollEvents(); }
AQUA void aqua_interval(int interval) { glfwSwapInterval(interval); }
AQUA double aqua_time(void) { return glfwGetTime(); }
AQUA int aqua_key(GLFWwindow* window, int key) { return glfwGetKey(window, key); }
AQUA int aqua_mouse(GLFWwindow* window, int button) { return glfwGetMouseButton(window, button); }
AQUA void aqua_cursor(GLFWwindow* window, double* x, double* y) { glfwGetCursorPos(window, x, y); }
AQUA void aqua_input(GLFWwindow* window, int mode, int value) { glfwSetInputMode(window, mode, value); }
AQUA void aqua_framebuffer(GLFWwindow* window, int* w, int* h) { glfwGetFramebufferSize(window, w, h); }
AQUA void aqua_window_size(GLFWwindow* window, int* w, int* h) { glfwGetWindowSize(window, w, h); }
AQUA void aqua_set_window_size(GLFWwindow* window, int w, int h) { glfwSetWindowSize(window, w, h); }
AQUA double aqua_scroll(void) { double value = pending_scroll; pending_scroll = 0; return value; }
AQUA unsigned int aqua_character(void) { return char_read == char_write ? 0 : pending_chars[char_read++ % 256]; }
AQUA int aqua_monitor_size(int* w, int* h) {
    GLFWmonitor* monitor = glfwGetPrimaryMonitor();
    const GLFWvidmode* mode = monitor ? glfwGetVideoMode(monitor) : NULL;
    if (!mode) return 0;
    *w = mode->width; *h = mode->height; return 1;
}
AQUA int aqua_fullscreen(GLFWwindow* window) {
    GLFWmonitor* monitor = glfwGetPrimaryMonitor();
    const GLFWvidmode* mode = monitor ? glfwGetVideoMode(monitor) : NULL;
    if (!mode) return 0;
    glfwSetWindowMonitor(window, monitor, 0, 0, mode->width, mode->height, mode->refreshRate); return 1;
}
AQUA unsigned char* aqua_image(const char* path, int flip, int* w, int* h) {
    int channels;
    stbi_set_flip_vertically_on_load(flip);
    return stbi_load(path, w, h, &channels, 4);
}
AQUA void aqua_image_free(void* pixels) { stbi_image_free(pixels); }
AQUA const char* aqua_image_error(void) { return stbi_failure_reason(); }
