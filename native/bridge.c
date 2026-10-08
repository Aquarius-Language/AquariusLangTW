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
#include <stdlib.h>
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
static void clear_application_windows(void);
AQUA void aqua_terminate(void) { clear_application_windows(); glfwTerminate(); }
AQUA const char* aqua_error(void) { const char* text = NULL; glfwGetError(&text); return text ? text : "Unknown GLFW error"; }
AQUA void aqua_hint(int name, int value) { glfwWindowHint(name, value); }
static double pending_scroll;
static unsigned int pending_chars[256];
static unsigned int char_read, char_write;
typedef void (*application_callback)(GLFWwindow*, int, int, int, int, double, double, const char*);
typedef intptr_t (*accessibility_callback)(uintptr_t, intptr_t);
typedef struct ApplicationWindow {
    GLFWwindow* window;
    application_callback event;
    accessibility_callback accessible;
    GLFWcursor* cursor;
    struct ApplicationWindow* next;
} ApplicationWindow;
static ApplicationWindow* application_windows;
static ApplicationWindow* application_state(GLFWwindow* w) {
    for(ApplicationWindow* s=application_windows;s;s=s->next) if(s->window==w)return s;
    return NULL;
}
static void clear_application_windows(void) {
    while(application_windows){ApplicationWindow* s=application_windows;application_windows=s->next;
        if(s->event)s->event(s->window,11,0,0,0,0,0,NULL);
        if(s->cursor)glfwDestroyCursor(s->cursor);free(s);}
}
intptr_t aqua_application_get_object(GLFWwindow* w, uintptr_t parameter, intptr_t object_id) {
    ApplicationWindow* s=application_state(w);return s&&s->accessible ? s->accessible(parameter,object_id) : 0;
}
AQUA void aqua_accessibility_subscribe(GLFWwindow* w, accessibility_callback callback) { ApplicationWindow* s=application_state(w);if(s)s->accessible=callback; }
static void emit_application(GLFWwindow* w, int type, int a, int b, int c, double x, double y, const char* text) {
    ApplicationWindow* s=application_state(w);if(s&&s->event)s->event(w,type,a,b,c,x,y,text);
}
static void on_scroll(GLFWwindow* window, double x, double y) { pending_scroll -= y; emit_application(window,4,0,0,0,x,y,NULL); }
static void on_key(GLFWwindow* w,int key,int scan,int action,int mods) { emit_application(w,1,key,scan,mods,(double)action,0,glfwGetKeyName(key,scan)); }
static void on_pointer(GLFWwindow* w,double x,double y) { emit_application(w,2,0,0,0,x,y,NULL); }
static void on_button(GLFWwindow* w,int button,int action,int mods) { double x,y;glfwGetCursorPos(w,&x,&y);emit_application(w,3,button,action,mods,x,y,NULL); }
static void on_focus(GLFWwindow* w,int focused) { emit_application(w,5,focused,0,0,0,0,NULL); }
static void on_iconify(GLFWwindow* w,int value) { emit_application(w,6,!value,0,0,0,0,NULL); }
static void on_enter(GLFWwindow* w,int value) { emit_application(w,7,value,0,0,0,0,NULL); }
static void on_drop(GLFWwindow* w,int count,const char** paths) { for(int i=0;i<count;i++)emit_application(w,8,i,count,0,0,0,paths[i]); }
static void on_close(GLFWwindow* w) { ApplicationWindow* s=application_state(w);if(s&&s->event){glfwSetWindowShouldClose(w,0);emit_application(w,9,0,0,0,0,0,NULL);} }
void aqua_application_capture_lost(GLFWwindow* w) { emit_application(w,10,0,0,0,0,0,NULL); }
static void on_character(GLFWwindow* window, unsigned int c) {
    if (!c || c > 0x10ffff || (c >= 0xd800 && c <= 0xdfff)) return;
    aqua_text_character(window, c);
    if (char_write - char_read < 256) pending_chars[char_write++ % 256] = c;
}
AQUA GLFWwindow* aqua_window(int w, int h, const char* title) {
    GLFWwindow* window = glfwCreateWindow(w, h, title, NULL, NULL);
    pending_scroll = 0; char_read = char_write = 0;
    if (window && !aqua_text_create(window, on_character)) { glfwDestroyWindow(window); return NULL; }
    if (window) { ApplicationWindow* s=(ApplicationWindow*)calloc(1,sizeof(ApplicationWindow));
        if(!s){aqua_text_destroy(window);glfwDestroyWindow(window);return NULL;}
        s->window=window;s->next=application_windows;application_windows=s; }
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
AQUA void aqua_destroy(GLFWwindow* window) {
    emit_application(window,11,0,0,0,0,0,NULL);
    ApplicationWindow** link=&application_windows;
    while(*link){ApplicationWindow* s=*link;if(s->window==window){*link=s->next;if(s->cursor)glfwDestroyCursor(s->cursor);free(s);break;}link=&s->next;}
    aqua_text_destroy(window); glfwDestroyWindow(window);
}
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

/* Opt-in events preserve the legacy polling and IME queues. Text is delivered only by text_input.c. */
AQUA int aqua_application_version(void) { return 1; }
AQUA void aqua_application_subscribe(GLFWwindow* w, application_callback callback) {
    ApplicationWindow* s=application_state(w);if(!s)return;s->event=callback;
    glfwSetKeyCallback(w,on_key);glfwSetCursorPosCallback(w,on_pointer);glfwSetMouseButtonCallback(w,on_button);
    glfwSetWindowFocusCallback(w,on_focus);glfwSetWindowIconifyCallback(w,on_iconify);glfwSetCursorEnterCallback(w,on_enter);
    glfwSetDropCallback(w,on_drop);glfwSetWindowCloseCallback(w,on_close);
    glfwSetInputMode(w,GLFW_LOCK_KEY_MODS,GLFW_TRUE);
    emit_application(w,12,0,0,0,0,0,NULL);
    emit_application(w,6,glfwGetWindowAttrib(w,GLFW_VISIBLE),0,0,0,0,NULL);
}
AQUA void aqua_title(GLFWwindow* w,const char* title) { glfwSetWindowTitle(w,title); }
AQUA int aqua_standard_cursor(GLFWwindow* w,int shape) {
    ApplicationWindow* s=application_state(w);if(!s)return 0;GLFWcursor* cursor=glfwCreateStandardCursor(shape);if(!cursor)return 0;
    glfwSetCursor(w,cursor);if(s->cursor)glfwDestroyCursor(s->cursor);s->cursor=cursor;return 1;
}
AQUA int aqua_custom_cursor(GLFWwindow* w,int width,int height,const unsigned char* pixels,int x,int y) {
    ApplicationWindow* s=application_state(w);if(!s)return 0;GLFWimage image={width,height,(unsigned char*)pixels};GLFWcursor* cursor=glfwCreateCursor(&image,x,y);if(!cursor)return 0;
    glfwSetCursor(w,cursor);if(s->cursor)glfwDestroyCursor(s->cursor);s->cursor=cursor;return 1;
}
AQUA int aqua_capture(GLFWwindow* w,int capture) {
#ifdef _WIN32
    if(capture){SetCapture(glfwGetWin32Window(w));return GetCapture()==glfwGetWin32Window(w);}return ReleaseCapture()!=0;
#else
    (void)w;(void)capture;return 0;
#endif
}
AQUA const char* aqua_clipboard_get(void) { return glfwGetClipboardString(NULL); }
AQUA void aqua_clipboard_set(const char* value) { glfwSetClipboardString(NULL,value); }
