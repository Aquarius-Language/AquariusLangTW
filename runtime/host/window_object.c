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

static void window_event(aqua_window *window, const aqua_window_event *event, void *data);

/* Also runs while the runtime ends with the window still open, so the window must stop notifying this object. */
static void window_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    window_object *object = data;
    if (object->window) aqua_window_unlisten(object->window, window_event, object);
    for (window_object **p = &objects; *p; p = &(*p)->next) if (*p == object) { *p = object->next; break; }
    free(object);
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

int32_t host_keypad_key(int32_t key, bool num_lock) {
    /* GLFW keypad 0..9 and the decimal point; -1 keeps the key (5 has no navigation meaning). */
    static const int32_t navigation[11] = {260, 269, 264, 267, 263, -1, 262, 268, 265, 266, 261};
    if (num_lock || key < 320 || key > 330) return key;
    return navigation[key - 320] < 0 ? key : navigation[key - 320];
}

aqua_window *aqua_window_of(aqua_runtime *rt, aqua_value value) {
    window_object *object = aqua_object_data(rt, value, window_class);
    return object ? object->window : NULL;
}
