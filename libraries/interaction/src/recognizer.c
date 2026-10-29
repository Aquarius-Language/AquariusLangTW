/* The recognizer: raw window events (aqua/window.h codes, the same on every target) become the semantic events
 * native applications react to: presses with click counts, clicks, drags with release velocity, long presses, pinches,
 * context menu requests (right button, long press, Menu key, Shift+F10), back and forward (system back, mouse buttons
 * 4 and 5, Alt+Left / Command+[), scrolling in logical pixels (Shift+wheel scrolls sideways, primary+wheel zooms) and
 * keyboard commands from commands.def. The thresholds are the platform's conventions, so the same code feels native on
 * every target. Nothing here touches the runtime: it is plain C over plain structs. */
#include "interaction.h"
#include <math.h>
#include <stdio.h>
#include <string.h>

enum { KEY_ESCAPE = 256, KEY_ENTER, KEY_TAB, KEY_BACKSPACE, KEY_INSERT, KEY_DELETE, KEY_RIGHT, KEY_LEFT,
       KEY_F1 = 290, KEY_F2, KEY_F3, KEY_F4, KEY_F5, KEY_F10 = 299, KEY_F11,
       KEY_KP_SUBTRACT = 333, KEY_KP_ADD, KEY_KP_ENTER,
       KEY_LEFT_SHIFT = 340, KEY_LEFT_CONTROL, KEY_LEFT_ALT, KEY_LEFT_SUPER,
       KEY_RIGHT_SHIFT, KEY_RIGHT_CONTROL, KEY_RIGHT_ALT, KEY_RIGHT_SUPER, KEY_MENU };
enum { PRIMARY = 16, CONTROL = MOD_CONTROL, SHIFT = MOD_SHIFT, ALT = MOD_ALT };
enum { APPLE = 1, OTHERS = 2, ALL = 3 };
enum { TYPE_MOUSE = 0, TYPE_TOUCH = 1, TYPE_PEN = 2 };

typedef struct shortcut { const char *name; int32_t key, modifiers, where; } shortcut;
static const shortcut shortcuts[] = {
#define COMMAND(name, key, modifiers, where) {name, key, modifiers, where},
#include "commands.def"
#undef COMMAND
};
#define SHORTCUT_COUNT (sizeof shortcuts / sizeof *shortcuts)

/* ---- Commands ---- */

static bool shortcut_matches(const conventions *c, const shortcut *s, int32_t key, int32_t modifiers) {
    if (s->key != key || !(s->where & (c->apple ? APPLE : OTHERS))) return false;
    int32_t plain = s->modifiers & MOD_KEYS;
    if (!(s->modifiers & PRIMARY)) return plain == modifiers;
    if (c->either_primary) return (plain | MOD_CONTROL) == modifiers || (plain | MOD_SUPER) == modifiers;
    return (plain | c->primary) == modifiers;
}

const char *command_of(const conventions *c, int32_t key, int32_t modifiers) {
    for (size_t i = 0; i < SHORTCUT_COUNT; i++)
        if (shortcut_matches(c, &shortcuts[i], key, modifiers & MOD_KEYS)) return shortcuts[i].name;
    return NULL;
}

static const char *key_label(int32_t key, bool apple, char *buffer) {
    switch (key) {
    case KEY_ESCAPE: return apple ? "⎋" : "Esc";
    case KEY_ENTER: case KEY_KP_ENTER: return apple ? "↩" : "Enter";
    case KEY_TAB: return apple ? "⇥" : "Tab";
    case KEY_BACKSPACE: return apple ? "⌫" : "Backspace";
    case KEY_INSERT: return "Insert";
    case KEY_DELETE: return apple ? "⌦" : "Del";
    case KEY_LEFT: return apple ? "←" : "Left";
    case KEY_RIGHT: return apple ? "→" : "Right";
    case KEY_KP_ADD: return "Num +";
    case KEY_KP_SUBTRACT: return "Num -";
    case KEY_MENU: return "Menu";
    }
    if (key >= KEY_F1 && key <= KEY_F1 + 24) { snprintf(buffer, 8, "F%d", key - KEY_F1 + 1); return buffer; }
    buffer[0] = (char)key;
    buffer[1] = 0;
    return buffer;
}

bool command_label(const conventions *c, const char *command, char *out, size_t size) {
    for (size_t i = 0; i < SHORTCUT_COUNT; i++) {
        const shortcut *s = &shortcuts[i];
        if (strcmp(s->name, command) != 0 || !(s->where & (c->apple ? APPLE : OTHERS))) continue;
        bool primary = s->modifiers & PRIMARY, control = (s->modifiers & CONTROL) || (primary && !c->apple);
        bool command_key = primary && c->apple, shift = s->modifiers & SHIFT, alt = s->modifiers & ALT;
        char key[8];
        const char *name = key_label(s->key, c->apple, key);
        if (c->apple)   /* Apple's order: Control, Option, Shift, Command, then the key, without separators. */
            snprintf(out, size, "%s%s%s%s%s", control ? "⌃" : "", alt ? "⌥" : "", shift ? "⇧" : "", command_key ? "⌘" : "", name);
        else
            snprintf(out, size, "%s%s%s%s", control ? "Ctrl+" : "", shift ? "Shift+" : "", alt ? "Alt+" : "", name);
        return true;
    }
    return false;
}

/* ---- Events ---- */

ievent event_new(const char *type, double time) {
    ievent e;
    memset(&e, 0, sizeof e);
    e.type = type;
    e.time = time;
    e.x = e.y = e.start_x = e.start_y = e.dx = e.dy = e.velocity_x = e.velocity_y = e.scale = e.width = e.height = NAN;
    e.button = e.count = e.pointer_id = e.key = e.state = INT32_MIN;
    return e;
}

static void emit(recognizer *r, ievent *e) {
    e->modifiers = r->modifiers;
    r->emit(r->data, e);
}

static const char *type_name(int32_t type) { return type == TYPE_TOUCH ? "touch" : type == TYPE_PEN ? "pen" : "mouse"; }

static ievent pointer_event(const char *type, const track *t, double time) {
    ievent e = event_new(type, time);
    e.x = t->x; e.y = t->y;
    e.pointer_id = (int32_t)t->id;
    e.pointer_type = type_name(t->type);
    e.button = t->button;
    e.count = t->count;
    return e;
}

/* ---- Pointers ---- */

static track *find(recognizer *r, uint32_t id, int32_t type) {
    for (int i = 0; i < POINTER_LIMIT; i++)
        if (r->tracks[i].active && r->tracks[i].id == id && r->tracks[i].type == type) return &r->tracks[i];
    return NULL;
}

static uint32_t active_touches(recognizer *r, track **first, track **second) {
    uint32_t n = 0;
    for (int i = 0; i < POINTER_LIMIT; i++) {
        track *t = &r->tracks[i];
        if (!t->active || t->type != TYPE_TOUCH) continue;
        if (n == 0 && first) *first = t;
        if (n == 1 && second) *second = t;
        n++;
    }
    return n;
}

static void sample(track *t, double x, double y, double time) {
    uint32_t i = t->samples++ % VELOCITY_SAMPLES;
    t->sample_x[i] = x; t->sample_y[i] = y; t->sample_t[i] = time;
}

/* Logical units per second over the last 100 ms; zero when the pointer rested before it was released. */
static void velocity(const track *t, double time, double *vx, double *vy) {
    *vx = *vy = 0;
    if (t->samples < 2) return;
    uint32_t newest = (t->samples - 1) % VELOCITY_SAMPLES, oldest = newest;
    if (time - t->sample_t[newest] > 0.05) return;
    uint32_t available = t->samples < VELOCITY_SAMPLES ? t->samples : VELOCITY_SAMPLES;
    for (uint32_t k = 1; k < available; k++) {
        uint32_t i = (t->samples - 1 - k) % VELOCITY_SAMPLES;
        if (t->sample_t[newest] - t->sample_t[i] > 0.1) break;
        oldest = i;
    }
    double dt = t->sample_t[newest] - t->sample_t[oldest];
    if (dt < 0.005) return;
    *vx = (t->sample_x[newest] - t->sample_x[oldest]) / dt;
    *vy = (t->sample_y[newest] - t->sample_y[oldest]) / dt;
}

static void pinch_measure(track *a, track *b, double *distance, double *x, double *y) {
    *distance = hypot(a->x - b->x, a->y - b->y);
    *x = (a->x + b->x) / 2;
    *y = (a->y + b->y) / 2;
}

static void pinch_emit(recognizer *r, const char *phase, double time) {
    track *a = NULL, *b = NULL;
    double distance = r->pinch_distance, x = r->pinch_x, y = r->pinch_y;
    if (active_touches(r, &a, &b) >= 2) pinch_measure(a, b, &distance, &x, &y);
    ievent e = event_new("pinch", time);
    e.phase = phase;
    e.pointer_type = "touch";
    e.x = x; e.y = y;
    e.scale = r->pinch_distance > 0 && distance > 0 ? distance / r->pinch_distance : 1;
    e.dx = x - r->pinch_x; e.dy = y - r->pinch_y;
    r->pinch_distance = distance; r->pinch_x = x; r->pinch_y = y;
    emit(r, &e);
}

static void drag_end(recognizer *r, track *t, const char *phase, double time) {
    ievent e = pointer_event("dragEnd", t, time);
    e.phase = phase;
    e.start_x = t->down_x; e.start_y = t->down_y;
    e.dx = e.dy = 0;
    if (strcmp(phase, "end") == 0) velocity(t, time, &e.velocity_x, &e.velocity_y);
    else e.velocity_x = e.velocity_y = 0;
    emit(r, &e);
    t->dragging = false;
}

static void moved(recognizer *r, track *t, double x, double y, double time) {
    double previous_x = t->x, previous_y = t->y;
    t->x = x; t->y = y; t->time = time;
    sample(t, x, y, time);
    if (r->pinching && t->type == TYPE_TOUCH) { pinch_emit(r, "change", time); return; }
    if (t->suppressed) return;
    const char *type = "drag";
    if (!t->dragging) {
        double threshold = t->type == TYPE_TOUCH ? r->conventions.touch_slop : r->conventions.drag_threshold;
        if (hypot(x - t->down_x, y - t->down_y) < threshold) return;
        t->dragging = true;
        type = "dragStart";
        previous_x = t->down_x; previous_y = t->down_y;
    }
    ievent e = pointer_event(type, t, time);
    e.start_x = t->down_x; e.start_y = t->down_y;
    e.dx = x - previous_x; e.dy = y - previous_y;
    emit(r, &e);
}

static void press(recognizer *r, const aqua_window_event *in) {
    track *t = find(r, in->pointer, in->pointer_type);
    if (!t)
        for (int i = 0; i < POINTER_LIMIT && !t; i++) if (!r->tracks[i].active) t = &r->tracks[i];
    if (!t) return;
    memset(t, 0, sizeof *t);
    t->active = true;
    t->id = in->pointer; t->type = in->pointer_type; t->button = in->button;
    t->down_x = t->x = in->x; t->down_y = t->y = in->y; t->down_time = t->time = in->time;
    sample(t, in->x, in->y, in->time);

    /* Click counting: the same button again soon enough and close enough continues the series. */
    double reach = t->type == TYPE_TOUCH ? r->conventions.touch_slop * 2 : r->conventions.drag_threshold;
    bool series = r->click_count > 0 && in->button == r->click_button && in->time - r->click_time <= r->conventions.double_click_ms / 1000 &&
                  hypot(in->x - r->click_x, in->y - r->click_y) <= reach;
    t->count = series ? r->click_count + 1 : 1;
    r->click_count = t->count; r->click_button = in->button; r->click_time = in->time; r->click_x = in->x; r->click_y = in->y;

    ievent e = pointer_event("press", t, in->time);
    emit(r, &e);

    track *first = NULL, *second = NULL;
    uint32_t touches = t->type == TYPE_TOUCH ? active_touches(r, &first, &second) : 0;
    if (touches == 2 && !r->pinching) {
        /* A second finger turns the gesture into a pinch: the first finger's drag or tap ends here. */
        track *other = first == t ? second : first;
        if (other->dragging) drag_end(r, other, "cancel", in->time);
        other->suppressed = t->suppressed = true;
        r->pinching = true;
        pinch_measure(first, second, &r->pinch_distance, &r->pinch_x, &r->pinch_y);
        pinch_emit(r, "start", in->time);
    } else if (touches > 2) t->suppressed = true;

    if (in->button == 1 && !r->conventions.context_menu_on_release) {
        ievent menu = pointer_event("contextMenu", t, in->time);
        menu.source = type_name(t->type);
        emit(r, &menu);
    } else if (in->button == 3 || in->button == 4) {
        ievent nav = pointer_event(in->button == 3 ? "back" : "forward", t, in->time);
        nav.source = "mouse";
        emit(r, &nav);
    }
}

static void release(recognizer *r, const aqua_window_event *in) {
    track *t = find(r, in->pointer, in->pointer_type);
    if (!t) return;
    if (in->x != t->x || in->y != t->y) moved(r, t, in->x, in->y, in->time);
    ievent e = pointer_event("release", t, in->time);
    emit(r, &e);
    if (r->pinching && t->type == TYPE_TOUCH) {
        pinch_emit(r, "end", in->time);
        r->pinching = false;
        for (int i = 0; i < POINTER_LIMIT; i++) if (r->tracks[i].active && r->tracks[i].type == TYPE_TOUCH) r->tracks[i].suppressed = true;
    } else if (!t->suppressed) {
        if (t->dragging) drag_end(r, t, "end", in->time);
        else if (!t->long_pressed) {
            ievent click = pointer_event("click", t, in->time);
            emit(r, &click);
            if (t->button == 1 && r->conventions.context_menu_on_release) {
                ievent menu = pointer_event("contextMenu", t, in->time);
                menu.source = type_name(t->type);
                emit(r, &menu);
            }
        }
    }
    t->active = false;
}

double recognizer_timers(recognizer *r, double time) {
    double next = -1;
    for (int i = 0; i < POINTER_LIMIT; i++) {
        track *t = &r->tracks[i];
        if (!t->active || t->type == TYPE_MOUSE || t->dragging || t->long_pressed || t->suppressed) continue;
        double due = t->down_time + r->conventions.long_press_ms / 1000;
        if (time < due) { if (next < 0 || due < next) next = due; continue; }
        t->long_pressed = true;
        ievent e = pointer_event("longPress", t, time);
        emit(r, &e);
        ievent menu = pointer_event("contextMenu", t, time);
        menu.source = type_name(t->type);
        emit(r, &menu);
    }
    return next;
}

void recognizer_cancel(recognizer *r, double time) {
    if (r->pinching) { pinch_emit(r, "cancel", time); r->pinching = false; }
    for (int i = 0; i < POINTER_LIMIT; i++) {
        track *t = &r->tracks[i];
        if (t->active && t->dragging && !t->suppressed) drag_end(r, t, "cancel", time);
        t->active = false;
    }
    r->modifiers = 0;
}

/* ---- Keys, wheel and window events ---- */

static int32_t modifier_bit(int32_t key) {
    switch (key) {
    case KEY_LEFT_SHIFT: case KEY_RIGHT_SHIFT: return MOD_SHIFT;
    case KEY_LEFT_CONTROL: case KEY_RIGHT_CONTROL: return MOD_CONTROL;
    case KEY_LEFT_ALT: case KEY_RIGHT_ALT: return MOD_ALT;
    case KEY_LEFT_SUPER: case KEY_RIGHT_SUPER: return MOD_SUPER;
    }
    return 0;
}

static bool primary_held(const recognizer *r) {
    if (r->conventions.either_primary) return (r->modifiers & (MOD_CONTROL | MOD_SUPER)) != 0;
    return (r->modifiers & r->conventions.primary) != 0;
}

static void key(recognizer *r, const aqua_window_event *in) {
    int32_t bit = modifier_bit(in->key);
    if (bit) r->modifiers = in->action ? r->modifiers | bit : r->modifiers & ~bit;
    else r->modifiers = in->mods & MOD_KEYS;
    ievent e = event_new("key", in->time);
    e.key = in->key;
    e.action = in->action == 0 ? "release" : in->action == 2 ? "repeat" : "press";
    if (in->text) snprintf(e.text, sizeof e.text, "%s", in->text);
    emit(r, &e);
    if (in->action == 0 || bit) return;

    const char *command = command_of(&r->conventions, in->key, r->modifiers);
    if (!command) return;
    bool navigation = strcmp(command, "back") == 0 || strcmp(command, "forward") == 0 || strcmp(command, "contextMenu") == 0;
    if (navigation && in->action == 2) return;
    ievent c = event_new(navigation ? command : "command", in->time);
    c.source = "keyboard";
    c.key = in->key;
    c.action = e.action;
    if (!navigation) c.command = command;
    emit(r, &c);
}

static void wheel(recognizer *r, const aqua_window_event *in) {
    ievent e = event_new("scroll", in->time);
    e.source = "wheel";
    if (r->mouse_known) { e.x = r->mouse_x; e.y = r->mouse_y; }
    if (primary_held(r)) {
        /* Primary + wheel zooms around the pointer, as browsers, editors and image viewers do. */
        if (in->y == 0) return;
        e.type = "zoom";
        e.scale = pow(1.1, in->y);
        emit(r, &e);
        return;
    }
    /* dx, dy: how far the content should move toward its end (right, bottom), in logical units. */
    e.dx = -in->x * r->conventions.wheel_step;
    e.dy = -in->y * r->conventions.wheel_step;
    if ((r->modifiers & MOD_SHIFT) && !r->conventions.apple && e.dx == 0) { e.dx = e.dy; e.dy = 0; }
    emit(r, &e);
}

void recognizer_init(recognizer *r, const conventions *c, void (*emit_event)(void *, const ievent *), void *data) {
    memset(r, 0, sizeof *r);
    r->conventions = *c;
    r->emit = emit_event;
    r->data = data;
}

void recognizer_input(recognizer *r, const aqua_window_event *in) {
    r->time = in->time;
    recognizer_timers(r, in->time);
    ievent e;
    switch (in->type) {
    case AQUA_EVENT_POINTER: {
        bool changed = !r->mouse_known || in->x != r->mouse_x || in->y != r->mouse_y;
        if (in->pointer_type == TYPE_MOUSE) { r->mouse_x = in->x; r->mouse_y = in->y; r->mouse_known = true; }
        track *t = find(r, in->pointer, in->pointer_type);
        if (t) { if (in->x != t->x || in->y != t->y) moved(r, t, in->x, in->y, in->time); break; }
        if (in->pointer_type == TYPE_TOUCH || (in->pointer_type == TYPE_MOUSE && !changed)) break;
        e = event_new("move", in->time);   /* hover: a mouse or a pen above the screen with nothing pressed */
        e.x = in->x; e.y = in->y;
        e.pointer_id = (int32_t)in->pointer;
        e.pointer_type = type_name(in->pointer_type);
        emit(r, &e);
        break;
    }
    case AQUA_EVENT_BUTTON:
        r->modifiers = in->mods & MOD_KEYS;
        if (in->pointer_type == TYPE_MOUSE) { r->mouse_x = in->x; r->mouse_y = in->y; r->mouse_known = true; }
        if (in->action) press(r, in);
        else release(r, in);
        break;
    case AQUA_EVENT_SCROLL: wheel(r, in); break;
    case AQUA_EVENT_KEY: key(r, in); break;
    case AQUA_EVENT_BACK:
        e = event_new("back", in->time);
        e.source = "system";
        emit(r, &e);
        break;
    case AQUA_EVENT_RESIZE:
        e = event_new("resize", in->time);
        e.width = in->x; e.height = in->y;
        emit(r, &e);
        break;
    case AQUA_EVENT_FOCUS:
        if (!in->action) recognizer_cancel(r, in->time);
        e = event_new("focus", in->time);
        e.state = in->action != 0;
        emit(r, &e);
        break;
    case AQUA_EVENT_VISIBLE:
        e = event_new("visibility", in->time);
        e.state = in->action != 0;
        emit(r, &e);
        break;
    case AQUA_EVENT_LIFECYCLE:
        if (!in->action) recognizer_cancel(r, in->time);
        e = event_new(in->action ? "resume" : "pause", in->time);
        emit(r, &e);
        break;
    case AQUA_EVENT_CAPTURE_LOST: recognizer_cancel(r, in->time); break;
    /* Typed text and input-method composition, while the window takes text input. */
    case AQUA_EVENT_TEXT:
        e = event_new("text", in->time);
        if (in->text) snprintf(e.text, sizeof e.text, "%s", in->text);
        emit(r, &e);
        break;
    case AQUA_EVENT_COMPOSITION_START: case AQUA_EVENT_COMPOSITION_UPDATE: case AQUA_EVENT_COMPOSITION_END:
        e = event_new("composition", in->time);
        e.phase = in->type == AQUA_EVENT_COMPOSITION_START ? "start" : in->type == AQUA_EVENT_COMPOSITION_END ? "end" : "update";
        if (in->text) snprintf(e.text, sizeof e.text, "%s", in->text);
        emit(r, &e);
        break;
    default: break;
    }
}
