/* Shared by the iOS layer's halves: platform.c (host services and the runtime thread) and window.m (UIKit). */
#ifndef AQUA_IOS_H
#define AQUA_IOS_H
#include <aqua/window.h>
#include <stdbool.h>

/* platform.c: runs the application of the bundle (Resources/application.aquarius) on the calling thread; returns its
   exit code. `resources` is the bundle folder, `data` the sandbox folder the application may write. */
int ios_run(const char *resources, const char *data);
/* window.m: dispatches the UIKit events queued since the last call to the window's listeners (runtime thread). */
void ios_dispatch_events(void);
/* window.m: whether events are waiting; the runtime thread's wait ends when one arrives (platform_wake). */
bool ios_events_pending(void);
void ios_windows_close(aqua_runtime *rt);
#endif
