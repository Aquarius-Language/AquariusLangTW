/* POSIX services shared by the Linux and Android layers (posix.c). */
#ifndef AQUA_POSIX_H
#define AQUA_POSIX_H
#include "../../host/host.h"
typedef struct platform_signal platform_signal;
platform_signal *platform_signal_create(void);
void platform_signal_set(platform_signal *s);
bool platform_signal_wait(platform_signal *s, int32_t timeout_ms);
/* Waits like platform_signal_wait and clears the signal (the loop's wake event); a negative timeout waits forever. */
bool platform_signal_take(platform_signal *s, double timeout_ms);
void platform_signal_destroy(platform_signal *s);
uint8_t *platform_read_tail(const char *path, size_t count, size_t *length, uint64_t *size);
char *platform_full_path(const char *path);
#endif
