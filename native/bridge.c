#include <glad/gl.h>
#define GLFW_INCLUDE_NONE
#include <GLFW/glfw3.h>
#include <string.h>
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
AQUA GLFWwindow* aqua_window(int w, int h, const char* title) { return glfwCreateWindow(w, h, title, NULL, NULL); }
AQUA void aqua_destroy(GLFWwindow* window) { glfwDestroyWindow(window); }
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
AQUA unsigned char* aqua_image(const char* path, int flip, int* w, int* h) {
    int channels;
    stbi_set_flip_vertically_on_load(flip);
    return stbi_load(path, w, h, &channels, 4);
}
AQUA void aqua_image_free(void* pixels) { stbi_image_free(pixels); }
AQUA const char* aqua_image_error(void) { return stbi_failure_reason(); }
