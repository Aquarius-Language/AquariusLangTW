/* The interaction library's shared declarations: platform conventions, semantic events and the recognizer that turns
 * raw window events (aqua/window.h, or events a graphics library feeds in) into them, and the per-OS layer
 * (src/platform/<os>.c). */
#ifndef INTERACTION_H
#define INTERACTION_H
#include <aqua/aqua.h>
#include <aqua/window.h>

/* GLFW modifier bits (aqua/window.h events use GLFW codes on every target). */
enum { MOD_SHIFT = 1, MOD_CONTROL = 2, MOD_ALT = 4, MOD_SUPER = 8, MOD_KEYS = 15 };

/* What the platform's own applications do; src/platform/<os>.c fills it in. Distances are logical units, times
   milliseconds. */
typedef struct conventions {
    const char *form_factor;      /* "desktop" or "mobile" */
    int32_t primary;              /* the modifier of shortcuts: MOD_CONTROL, or MOD_SUPER (Command) on Apple targets */
    bool either_primary;          /* web: Control or Command, whichever the browser's OS uses */
    bool apple;                   /* shortcut labels in Apple style (⇧⌘Z) */
    double double_click_ms, long_press_ms, drag_threshold, touch_slop, wheel_step, caret_blink_ms, minimum_target;
    const char *scrollbars;       /* "classic" (always shown, draggable) or "overlay" (shown while scrolling) */
    bool context_menu_on_release; /* Windows opens context menus when the right button is released */
    bool hover, back_button, dark, reduced_motion, high_contrast;
} conventions;

/* A semantic event. Absent numbers are NaN, absent integers INT32_MIN, absent strings NULL. */
typedef struct ievent {
    const char *type, *source, *command, *pointer_type, *action, *phase;
    char text[512];               /* a key's name, or typed / composed text */
    double time, x, y, start_x, start_y, dx, dy, velocity_x, velocity_y, scale, width, height;
    int32_t button, count, pointer_id, modifiers, key, state;
} ievent;

#define POINTER_LIMIT 10
#define VELOCITY_SAMPLES 8

typedef struct track {
    bool active, dragging, long_pressed, suppressed;
    uint32_t id;
    int32_t type, button, count;
    double down_x, down_y, down_time, x, y, time;
    double sample_x[VELOCITY_SAMPLES], sample_y[VELOCITY_SAMPLES], sample_t[VELOCITY_SAMPLES];
    uint32_t samples;
} track;

typedef struct recognizer {
    conventions conventions;
    track tracks[POINTER_LIMIT];
    double click_time, click_x, click_y;
    int32_t click_button, click_count;
    bool pinching;
    double pinch_distance, pinch_x, pinch_y;
    int32_t modifiers;
    double mouse_x, mouse_y, time;
    bool mouse_known;
    void (*emit)(void *data, const ievent *event);
    void *data;
} recognizer;

void recognizer_init(recognizer *r, const conventions *c, void (*emit)(void *, const ievent *), void *data);
void recognizer_input(recognizer *r, const aqua_window_event *e);
/* Long presses that came due by `time` (seconds, the events' clock); the earliest time one still could, or -1. */
double recognizer_timers(recognizer *r, double time);
/* Ends every gesture in progress (lost focus, paused application). */
void recognizer_cancel(recognizer *r, double time);
ievent event_new(const char *type, double time);
/* The command a key press is (copy, back, focusNext, ...), or NULL. */
const char *command_of(const conventions *c, int32_t key, int32_t modifiers);
/* "Ctrl+Shift+Z", "⇧⌘Z": the first shortcut of a command on this platform, written into `out`. */
bool command_label(const conventions *c, const char *command, char *out, size_t size);

/* ---- The per-OS layer: src/platform/<os>.c ---- */
void platform_conventions(conventions *c);
/* What the system does with a back request no handler took: Android finishes the activity, the web leaves the page. */
void platform_back_unhandled(aqua_window *window);
/* Web: keeps a history entry so the browser's back button reaches the application while `enabled`; `back` runs on
   the runtime's thread when it is used. Other targets: nothing. */
void platform_back_guard(bool enabled, void (*back)(void *data), void *data);
/* System bar and cutout insets in logical units and whether a soft keyboard covers the window. */
void platform_safe_area(float *left, float *top, float *right, float *bottom, bool *keyboard);
#endif
