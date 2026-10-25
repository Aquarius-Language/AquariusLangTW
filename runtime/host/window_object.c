/* Script objects for platform windows (aqua_window_object in aqua/window.h). The class belongs to the core library, so
   a window one library creates (GLFW.CreateWindow, Processing's sketch window) can be passed to another (TextInput,
   Window.Attach). An object stops referring to its window once the window is destroyed. */
#include "host.h"
#include <stdlib.h>

static aqua_class *window_class;

typedef struct window_object {
    aqua_window *window;
    aqua_ref ref;          /* the object, held while the window exists so every library sees the same object */
    struct window_object *next;
} window_object;
static window_object *objects;

static void window_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    for (window_object **p = &objects; *p; p = &(*p)->next) if (*p == data) { *p = ((window_object *)data)->next; break; }
    free(data);
}

static void window_event(aqua_window *window, const aqua_window_event *event, void *data) {
    window_object *object = data;
    if (event->type != AQUA_EVENT_DESTROYED || object->window != window) return;
    object->window = NULL;
    aqua_ref ref = object->ref;
    object->ref = 0;
    aqua_release(aqua_window_runtime(window), ref);
}

static window_object *self(aqua_call *call) { return aqua_self(call); }
static void is_open(aqua_call *call) { aqua_return_bool(call, self(call)->window != NULL); }

void window_object_library(aqua_library *library) {
    window_class = aqua_define_class(library, "window", window_finalize);
    aqua_define_property(window_class, "isOpen|已開啟", is_open);
}

aqua_value aqua_window_object(aqua_window *window) {
    aqua_runtime *rt = aqua_window_runtime(window);
    for (window_object *o = objects; o; o = o->next) if (o->window == window) return aqua_ref_value(rt, o->ref);
    window_object *object = calloc(1, sizeof *object);
    object->window = window;
    object->next = objects;
    objects = object;
    aqua_value value = aqua_object_new(rt, window_class, object);
    object->ref = aqua_retain(rt, value);
    aqua_window_listen(window, window_event, object);
    return value;
}

aqua_window *aqua_window_of(aqua_runtime *rt, aqua_value value) {
    window_object *object = aqua_object_data(rt, value, window_class);
    return object ? object->window : NULL;
}
