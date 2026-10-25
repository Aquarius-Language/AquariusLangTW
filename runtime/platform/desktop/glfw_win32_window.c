/* GLFW 3.4's polling code reads the active HWND's "GLFW" property as a local
   pointer. Windows automation can temporarily attach input queues across
   processes, so GetActiveWindow may then belong to another GLFW application.
   Restrict active-window queries to this GLFW instance's own window list. */
#include "internal.h"

static HWND aquarius_owned_active_window(void) {
    HWND handle = GetActiveWindow();
    for (_GLFWwindow* window = _glfw.windowListHead; window; window = window->next)
        if (window->win32.handle == handle) return handle;
    return NULL;
}

#define GetActiveWindow aquarius_owned_active_window
#include "win32_window.c"
