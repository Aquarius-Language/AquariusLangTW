/* Remote windows on the desktop (aqua/remote_window.h): with AQUARIUS_REMOTE_WINDOW=host:port every window the process
   opens is shown by a viewer over one TCP connection instead of on the screen. window_glfw.c keeps aqua/window.h's
   functions and hands each remote window's work to these. */
#ifndef AQUA_WINDOW_REMOTE_H
#define AQUA_WINDOW_REMOTE_H
#include <aqua/window.h>

typedef struct remote_window remote_window;

/* Whether this process shows its windows remotely (AQUARIUS_REMOTE_WINDOW is set). */
bool remote_enabled(void);
/* Opens a window on the viewer (connecting on the first one); NULL with *error set when the viewer is unreachable. */
remote_window *remote_open(aqua_window *window, int32_t width, int32_t height, const char *title, uint32_t flags, char **error);
void remote_close(remote_window *r);

void remote_size(remote_window *r, int32_t *width, int32_t *height);
void remote_framebuffer_size(remote_window *r, int32_t *width, int32_t *height);
float remote_scale(remote_window *r);
uint32_t remote_id(remote_window *r);
void remote_set_size(remote_window *r, int32_t width, int32_t height);
void remote_set_title(remote_window *r, const char *title);
bool remote_should_close(remote_window *r);
void remote_set_should_close(remote_window *r, bool close);
bool remote_focused(remote_window *r);
int32_t remote_key(remote_window *r, int32_t key);
int32_t remote_mouse_button(remote_window *r, int32_t button);
void remote_cursor(remote_window *r, double *x, double *y);
void remote_set_cursor(remote_window *r, int32_t shape);
void remote_text_input(remote_window *r, bool enabled);
void remote_text_rect(remote_window *r, int32_t x, int32_t y, int32_t width, int32_t height);
/* Sends a finished frame (the latest one waits while the viewer has not caught up). */
bool remote_present(remote_window *r, const uint8_t *rgba, int32_t width, int32_t height, int32_t stride);

/* Reads the viewer's messages and delivers its events through window_glfw.c's dispatch. */
void remote_poll(void);
double remote_time(void);
char *remote_clipboard_text(void);
bool remote_clipboard_set(const char *utf8);

/* window_glfw.c: delivers an event to a window's listeners and resizes the OpenGL context of a remote OpenGL window. */
void desktop_window_emit(aqua_window *window, aqua_window_event *event);
void desktop_window_resized(aqua_window *window);
#endif
