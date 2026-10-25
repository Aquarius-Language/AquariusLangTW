/* Desktop window system internals shared with the launcher. */
#ifndef AQUA_WINDOW_DESKTOP_H
#define AQUA_WINDOW_DESKTOP_H
#include <aqua/window.h>
/* Closes the windows an ending runtime left open and shuts GLFW down when none remain. */
void desktop_windows_close(aqua_runtime *rt);
#endif
