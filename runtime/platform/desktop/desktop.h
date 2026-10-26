/* Shared by the Windows and Linux layers: the desktop launcher and hooks the desktop window system installs. */
#ifndef AQUA_DESKTOP_H
#define AQUA_DESKTOP_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Runs `aquarius` with the process's UTF-8 arguments; returns the exit code. */
int desktop_main(int count, char **arguments);
/* While windows are open, the window system waits for its events instead of the plain wake event. Returns false when
   no window system is active. */
bool desktop_wait(double timeout_ms);
void desktop_wake(void);
void desktop_set_window_loop(bool (*wait)(double timeout_ms), void (*wake)(void));

void platform_flush(void);
char *platform_executable_path(void);
/* The last `count` bytes of a file (fewer when it is shorter, *length says how many); *size is the file's size. */
uint8_t *platform_read_tail(const char *path, size_t count, size_t *length, uint64_t *size);
#endif
