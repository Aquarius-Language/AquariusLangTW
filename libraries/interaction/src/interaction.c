/* Interaction / 互動: native-feeling input for any graphics library.
 *
 * A session listens to a window of aqua/window.h (Processing, WGPU, GLFW and any library built on the SDK's windows
 * hand theirs over as a shared window object) or, for a library with windows of its own, takes the raw events that
 * library feeds in (Session.Feed). Its recognizer (recognizer.c) turns them into semantic events with the platform's
 * own thresholds; the session calls the script's handlers for them, newest first, until one returns true. A back
 * request no handler takes gets the system's behavior (Android closes the activity, the web leaves the page).
 * Handlers run at the runtime's next turn, outside the window system's event processing; Session.Dispatch() runs them
 * at once for loops that drive their own frames. */
#include "interaction.h"
#if defined(AQUA_OS_ANDROID)
#include <aqua/android.h>
#endif
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define QUEUE_LIMIT 4096
#define TYPE_LENGTH 24

static aqua_class *session_class, *event_class;

static const conventions *platform(void) {
    static conventions c;
    static bool known;
    if (!known) { platform_conventions(&c); known = true; }
    return &c;
}

/* ---- Records: script objects whose fields are fixed values (conventions, safe areas) ---- */

#define RECORD_FIELDS 20
typedef struct record { uint32_t count; aqua_value values[RECORD_FIELDS]; aqua_ref refs[RECORD_FIELDS]; } record;

static void record_finalize(aqua_runtime *rt, void *data) {
    record *r = data;
    for (uint32_t i = 0; i < r->count; i++) aqua_release(rt, r->refs[i]);
    free(r);
}
#define FIELD(N) static void field_##N(aqua_call *call) { \
        record *r = aqua_self(call); \
        aqua_return(call, r->refs[N] ? aqua_ref_value(aqua_call_runtime(call), r->refs[N]) : r->values[N]); }
FIELD(0) FIELD(1) FIELD(2) FIELD(3) FIELD(4) FIELD(5) FIELD(6) FIELD(7) FIELD(8) FIELD(9)
FIELD(10) FIELD(11) FIELD(12) FIELD(13) FIELD(14) FIELD(15) FIELD(16) FIELD(17) FIELD(18) FIELD(19)
static const aqua_function fields[RECORD_FIELDS] = {field_0, field_1, field_2, field_3, field_4, field_5, field_6, field_7, field_8,
    field_9, field_10, field_11, field_12, field_13, field_14, field_15, field_16, field_17, field_18, field_19};

static aqua_class *define_record(aqua_library *library, const char *name, const char *const *names, uint32_t count) {
    aqua_class *c = aqua_define_class(library, name, record_finalize);
    for (uint32_t i = 0; i < count; i++) aqua_define_property(c, names[i], fields[i]);
    return c;
}

static aqua_value new_record(aqua_runtime *rt, aqua_class *cls, const aqua_value *values, uint32_t count) {
    record *r = calloc(1, sizeof *r);
    r->count = count;
    for (uint32_t i = 0; i < count; i++) {
        r->values[i] = values[i];
        if (values[i].type == AQUA_STRING) r->refs[i] = aqua_retain(rt, values[i]);
    }
    return aqua_object_new(rt, cls, r);
}

static const char *const convention_fields[] = {"platform", "formFactor", "primaryModifier", "doubleClickTime", "longPressTime",
    "dragThreshold", "touchSlop", "wheelStep", "caretBlinkTime", "minimumTarget", "scrollbars", "contextMenuOnRelease",
    "hover", "backButton", "darkMode", "reducedMotion", "highContrast"};
static aqua_class *conventions_class, *safe_area_class;

/* ---- Event objects ---- */

static void event_finalize(aqua_runtime *rt, void *data) { (void)rt; free(data); }

#define EVENT_FIELDS(STRING, NUMBER, INTEGER, BOOLEAN) \
    STRING("type", type) STRING("source", source) STRING("command", command) STRING("pointerType", pointer_type) \
    STRING("action", action) STRING("phase", phase) NUMBER("time", time) NUMBER("x", x) NUMBER("y", y) \
    NUMBER("startX", start_x) NUMBER("startY", start_y) NUMBER("dx", dx) NUMBER("dy", dy) NUMBER("velocityX", velocity_x) \
    NUMBER("velocityY", velocity_y) NUMBER("scale", scale) NUMBER("width", width) NUMBER("height", height) \
    INTEGER("button", button) INTEGER("count", count) INTEGER("pointerId", pointer_id) INTEGER("modifiers", modifiers) \
    INTEGER("key", key) BOOLEAN("state", state)
#define STRING_GETTER(name, field) static void event_##field(aqua_call *call) { \
        const ievent *e = aqua_self(call); if (e->field) aqua_return_cstring(call, e->field); else aqua_return_null(call); }
#define NUMBER_GETTER(name, field) static void event_##field(aqua_call *call) { \
        const ievent *e = aqua_self(call); if (isnan(e->field)) aqua_return_null(call); else aqua_return_double(call, e->field); }
#define INTEGER_GETTER(name, field) static void event_##field(aqua_call *call) { \
        const ievent *e = aqua_self(call); if (e->field == INT32_MIN) aqua_return_null(call); else aqua_return_int(call, e->field); }
#define BOOLEAN_GETTER(name, field) static void event_##field(aqua_call *call) { \
        const ievent *e = aqua_self(call); if (e->field == INT32_MIN) aqua_return_null(call); else aqua_return_bool(call, e->field != 0); }
EVENT_FIELDS(STRING_GETTER, NUMBER_GETTER, INTEGER_GETTER, BOOLEAN_GETTER)

static void event_text(aqua_call *call) {
    const ievent *e = aqua_self(call);
    if (e->text[0]) aqua_return_cstring(call, e->text); else aqua_return_null(call);
}
static void event_shift(aqua_call *call) { aqua_return_bool(call, (((ievent *)aqua_self(call))->modifiers & MOD_SHIFT) != 0); }
static void event_alt(aqua_call *call) { aqua_return_bool(call, (((ievent *)aqua_self(call))->modifiers & MOD_ALT) != 0); }
/* The shortcut modifier of the platform: Control, or Command on Apple targets (either on the web). */
static void event_primary(aqua_call *call) {
    const conventions *c = platform();
    int32_t mask = c->either_primary ? MOD_CONTROL | MOD_SUPER : c->primary;
    aqua_return_bool(call, (((ievent *)aqua_self(call))->modifiers & mask) != 0);
}

/* ---- Sessions ---- */

typedef struct handler { uint32_t id; char type[TYPE_LENGTH]; aqua_ref function; } handler;

typedef struct session session;
typedef struct state { session *list; } state;

struct session {
    aqua_runtime *rt;
    state *owner;
    aqua_window *window;          /* listened to; NULL for fed sessions and once the window is gone */
    bool attached;                /* attached to a window (system defaults apply) */
    aqua_ref self;                /* held while the window lives */
    recognizer recognizer;
    double input_wall_ms;         /* aqua_now_ms of the latest input, to advance the events' clock */
    handler *handlers; uint32_t handler_count, handler_capacity, next_id;
    ievent *queue; uint32_t head, count, capacity;
    bool overflow, posted, scheduled, pumping, waiting, sync, dead, guarded;
    aqua_ref current;             /* the event object being dispatched */
    ievent current_event;
    uint32_t cursor;              /* id of the handler called last for the current event */
    session *next;
};

static void pump(session *s);

static bool releasable(session *s) { return s->dead && !s->posted && !s->scheduled && !s->waiting; }

static void session_free(session *s) {
    for (uint32_t i = 0; i < s->handler_count; i++) aqua_release(s->rt, s->handlers[i].function);
    if (s->current) aqua_release(s->rt, s->current);
    free(s->handlers);
    free(s->queue);
    free(s);
}

static void posted(aqua_runtime *rt, void *data) {
    (void)rt;
    session *s = data;
    s->posted = false;
    if (releasable(s)) { session_free(s); return; }
    if (!s->dead) pump(s);
}

static void emit_event(void *data, const ievent *e) {
    session *s = data;
    if (s->count - s->head >= QUEUE_LIMIT) { s->overflow = true; return; }
    if (s->head && s->head == s->count) s->head = s->count = 0;
    if (s->count == s->capacity) {
        if (s->head) {
            memmove(s->queue, s->queue + s->head, (s->count - s->head) * sizeof *s->queue);
            s->count -= s->head;
            s->head = 0;
        }
        if (s->count == s->capacity) {
            s->capacity = s->capacity ? s->capacity * 2 : 64;
            s->queue = realloc(s->queue, s->capacity * sizeof *s->queue);
        }
    }
    ievent *copy = &s->queue[s->count++];
    *copy = *e;
    if (strcmp(e->type, "resize") == 0 && s->window) copy->scale = aqua_window_content_scale(s->window);
    if (!s->posted) { s->posted = true; aqua_post(s->rt, posted, s); }
}

/* Long presses come due while nothing else happens: a tick advances the events' clock by the time that passed. */
static double tick(aqua_runtime *rt, double now_ms, void *data) {
    (void)now_ms;
    session *s = data;
    if (s->dead) { s->scheduled = false; if (releasable(s)) session_free(s); return -1; }
    double now = s->recognizer.time + (aqua_now_ms(rt) - s->input_wall_ms) / 1000;
    double next = recognizer_timers(&s->recognizer, now);
    if (next < 0) { s->scheduled = false; return -1; }
    return (next - now) * 1000 + 1;
}

static void input(session *s, const aqua_window_event *e) {
    s->input_wall_ms = aqua_now_ms(s->rt);
    recognizer_input(&s->recognizer, e);
    double next = recognizer_timers(&s->recognizer, e->time);
    if (next >= 0 && !s->scheduled) {
        s->scheduled = true;
        aqua_schedule(s->rt, (next - e->time) * 1000 + 1, tick, s);
    }
}

static void on_window(aqua_window *window, const aqua_window_event *e, void *data) {
    (void)window;
    session *s = data;
    if (e->type == AQUA_EVENT_DESTROYED) {
        s->window = NULL;
        if (s->self) { aqua_ref self = s->self; s->self = 0; aqua_release(s->rt, self); }
        return;
    }
    input(s, e);
}

/* ---- Dispatch: one event at a time, its handlers newest first until one returns true ---- */

static bool type_matches(const handler *h, const char *type) { return strcmp(h->type, type) == 0 || strcmp(h->type, "*") == 0; }

static handler *next_handler(session *s) {
    handler *best = NULL;
    for (uint32_t i = 0; i < s->handler_count; i++) {
        handler *h = &s->handlers[i];
        if (h->id < s->cursor && type_matches(h, s->current_event.type) && (!best || h->id > best->id)) best = h;
    }
    return best;
}

static void web_back(void *data);

static void finish_event(session *s, bool consumed) {
    ievent *e = &s->current_event;
    if (strcmp(e->type, "back") == 0 && e->source && strcmp(e->source, "system") == 0 && s->attached) {
        if (!consumed) platform_back_unhandled(s->window);
        else if (s->guarded) platform_back_guard(true, web_back, s);   /* the browser used up the guard entry */
    }
    aqua_release(s->rt, s->current);
    s->current = 0;
}

static void report_failure(session *s, aqua_value error) {
    char *message = aqua_display(s->rt, error);
    char line[160];
    int n = snprintf(line, sizeof line, "Interaction: the %s handler failed: ", s->current_event.type);
    aqua_write_error(s->rt, line, (size_t)n);
    aqua_write_error(s->rt, message, strlen(message));
    aqua_write_error(s->rt, "\n", 1);
    aqua_free(message);
}

static void handled(aqua_runtime *rt, bool ok, aqua_value result, void *data) {
    (void)rt;
    session *s = data;
    s->waiting = false;
    if (!ok) report_failure(s, result);
    if (s->current && ok && result.type == AQUA_BOOL && result.number != 0) finish_event(s, true);
    if (s->dead) { if (releasable(s)) session_free(s); return; }
    if (!s->pumping) pump(s);
}

static void pump(session *s) {
    if (s->pumping) return;
    s->pumping = true;
    while (!s->waiting && !s->dead) {
        if (!s->current) {
            if (s->head == s->count) break;
            s->current_event = s->queue[s->head++];
            ievent *copy = malloc(sizeof *copy);
            *copy = s->current_event;
            s->current = aqua_retain(s->rt, aqua_object_new(s->rt, event_class, copy));
            s->cursor = UINT32_MAX;
        }
        handler *h = next_handler(s);
        if (!h) { finish_event(s, false); continue; }
        s->cursor = h->id;
        aqua_value function = aqua_ref_value(s->rt, h->function), argument = aqua_ref_value(s->rt, s->current);
        s->waiting = true;
        if (s->sync) {
            aqua_value result;
            bool ok = aqua_invoke(s->rt, function, &argument, 1, &result);
            s->waiting = false;
            if (!ok) report_failure(s, result);
            if (s->current && ok && result.type == AQUA_BOOL && result.number != 0) finish_event(s, true);
        } else aqua_invoke_async(s->rt, function, &argument, 1, handled, s);
    }
    s->pumping = false;
}

/* The browser's back button: an event like Android's back gesture. */
static void web_back(void *data) {
    session *s = data;
    if (s->dead) return;
    aqua_window_event e = {AQUA_EVENT_BACK};
    e.time = s->recognizer.time + (aqua_now_ms(s->rt) - s->input_wall_ms) / 1000;
    input(s, &e);
}

static void update_guard(session *s) {
    bool wanted = false;
    for (uint32_t i = 0; i < s->handler_count; i++) if (strcmp(s->handlers[i].type, "back") == 0) wanted = true;
    wanted = wanted && s->attached && !s->dead;
    if (wanted != s->guarded) { s->guarded = wanted; platform_back_guard(wanted, web_back, s); }
}

static void session_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    session *s = data;
    if (s->window) aqua_window_unlisten(s->window, on_window, s);
    s->window = NULL;
    if (s->guarded) { s->guarded = false; platform_back_guard(false, web_back, s); }
    if (s->owner)
        for (session **p = &s->owner->list; *p; p = &(*p)->next) if (*p == s) { *p = s->next; break; }
    s->dead = true;
    if (releasable(s)) session_free(s);
}

static void *state_create(aqua_runtime *rt) { (void)rt; return calloc(1, sizeof(state)); }
static void state_destroy(aqua_runtime *rt, void *data) {
    (void)rt;
    state *st = data;
    for (session *s = st->list; s; s = s->next) {
        if (s->window) aqua_window_unlisten(s->window, on_window, s);
        s->window = NULL;
        s->owner = NULL;
    }
    free(st);
}

static session *session_new(aqua_call *call) {
    session *s = calloc(1, sizeof *s);
    s->rt = aqua_call_runtime(call);
    s->owner = aqua_state(call);
    s->next = s->owner->list;
    s->owner->list = s;
    s->next_id = 1;
    s->input_wall_ms = aqua_now_ms(s->rt);
    recognizer_init(&s->recognizer, platform(), emit_event, s);
    s->recognizer.time = s->input_wall_ms / 1000;
    return s;
}

/* ---- Module functions ---- */

static void fail(aqua_call *call, const char *function, const char *message) { aqua_fail(call, "Interaction.%s: %s", function, message); }

static void interaction_conventions(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    conventions c;
    platform_conventions(&c);   /* fresh: dark mode and reduced motion can change while the application runs */
    const char *primary = c.either_primary ? "Ctrl or Cmd" : c.apple ? "Cmd" : "Ctrl";
    aqua_value values[17] = {aqua_cstring(rt, aqua_platform(rt)), aqua_cstring(rt, c.form_factor), aqua_cstring(rt, primary),
        aqua_double(c.double_click_ms), aqua_double(c.long_press_ms), aqua_double(c.drag_threshold), aqua_double(c.touch_slop),
        aqua_double(c.wheel_step), aqua_double(c.caret_blink_ms), aqua_double(c.minimum_target), aqua_cstring(rt, c.scrollbars),
        aqua_bool(c.context_menu_on_release), aqua_bool(c.hover), aqua_bool(c.back_button), aqua_bool(c.dark),
        aqua_bool(c.reduced_motion), aqua_bool(c.high_contrast)};
    aqua_return(call, new_record(rt, conventions_class, values, 17));
}

static void interaction_attach(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_window *window = aqua_has_arg(call, 0) ? aqua_window_of(rt, aqua_arg(call, 0)) : NULL;
#if defined(AQUA_OS_ANDROID)
    if (!aqua_has_arg(call, 0)) window = aqua_android_window();
#endif
    if (!window) {
        fail(call, "Attach", aqua_has_arg(call, 0) ? "Expected a live window (Processing's platformWindow, WGPU's PlatformWindow, a GLFW window)."
                                                  : "Expected a window: Attach() without one works on Android once a library opened its window.");
        return;
    }
    state *st = aqua_state(call);
    for (session *s = st->list; s; s = s->next)
        if (s->window == window && s->self) { aqua_return(call, aqua_ref_value(rt, s->self)); return; }
    session *s = session_new(call);
    s->window = window;
    s->attached = true;
    aqua_window_listen(window, on_window, s);
    aqua_value object = aqua_object_new(rt, session_class, s);
    s->self = aqua_retain(rt, object);
    aqua_return(call, object);
}

static void interaction_create(aqua_call *call) {
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), session_class, session_new(call)));
}

static void interaction_shortcut_label(aqua_call *call) {
    char label[48];
    if (command_label(platform(), aqua_arg_str(call, 0).data, label, sizeof label)) aqua_return_cstring(call, label);
    else aqua_return_null(call);
}

/* ---- Session members ---- */

static void session_on(aqua_call *call) {
    session *s = aqua_self(call);
    aqua_str type = aqua_arg_str(call, 0);
    if (type.length == 0 || type.length >= TYPE_LENGTH) { fail(call, "On", "Expected an event type such as \"back\" or \"click\", or \"*\"."); return; }
    if (s->handler_count == s->handler_capacity) {
        s->handler_capacity = s->handler_capacity ? s->handler_capacity * 2 : 8;
        s->handlers = realloc(s->handlers, s->handler_capacity * sizeof *s->handlers);
    }
    handler *h = &s->handlers[s->handler_count++];
    h->id = s->next_id++;
    memcpy(h->type, type.data, type.length + 1);
    h->function = aqua_retain(s->rt, aqua_arg(call, 1));
    update_guard(s);
    aqua_return_int(call, (int32_t)h->id);
}

static void session_off(aqua_call *call) {
    session *s = aqua_self(call);
    int32_t id = aqua_arg_int(call, 0);
    for (uint32_t i = 0; i < s->handler_count; i++) {
        if (s->handlers[i].id != (uint32_t)id) continue;
        aqua_release(s->rt, s->handlers[i].function);
        memmove(s->handlers + i, s->handlers + i + 1, (s->handler_count - i - 1) * sizeof *s->handlers);
        s->handler_count--;
        update_guard(s);
        aqua_return_bool(call, true);
        return;
    }
    aqua_return_bool(call, false);
}

static bool number_field(aqua_runtime *rt, aqua_value hash, const char *name, double *out) {
    aqua_value v;
    if (!aqua_hash_field(rt, hash, name, &v) || !aqua_is_number(v)) return false;
    *out = v.number;
    return true;
}

static const struct { const char *name; int32_t type; } raw_types[] = {
    {"pointer", AQUA_EVENT_POINTER}, {"button", AQUA_EVENT_BUTTON}, {"scroll", AQUA_EVENT_SCROLL}, {"key", AQUA_EVENT_KEY},
    {"back", AQUA_EVENT_BACK}, {"resize", AQUA_EVENT_RESIZE}, {"focus", AQUA_EVENT_FOCUS}, {"visible", AQUA_EVENT_VISIBLE},
    {"lifecycle", AQUA_EVENT_LIFECYCLE}, {"captureLost", AQUA_EVENT_CAPTURE_LOST}, {"tick", 0}};

/* A raw event of a graphics library with windows of its own, in aqua/window.h's terms:
   {"type": "pointer" | "button" | "scroll" | "key" | "back" | "resize" | "focus" | "visible" | "lifecycle" | "captureLost"
    | "tick", "time": seconds, "x", "y", "pointer", "pointerType": "mouse" | "touch" | "pen", "button", "action",
    "mods", "key"}. "tick" only advances the clock (long presses). */
static void session_feed(aqua_call *call) {
    session *s = aqua_self(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value hash = aqua_arg(call, 0), v;
    if (!aqua_hash_field(rt, hash, "type", &v) || v.type != AQUA_STRING) { fail(call, "Feed", "Expected a \"type\" string."); return; }
    aqua_str type = aqua_str_of(call, v);
    int32_t kind = -1;
    for (size_t i = 0; i < sizeof raw_types / sizeof *raw_types; i++) if (strcmp(raw_types[i].name, type.data) == 0) kind = raw_types[i].type;
    if (kind < 0) { aqua_fail(call, "Interaction.Feed: Unknown event type '%s'.", type.data); return; }
    aqua_window_event e = {kind};
    double n;
    e.time = number_field(rt, hash, "time", &n) ? n : s->recognizer.time + (aqua_now_ms(rt) - s->input_wall_ms) / 1000;
    if (number_field(rt, hash, "x", &n)) e.x = n;
    if (number_field(rt, hash, "y", &n)) e.y = n;
    if (number_field(rt, hash, "pointer", &n)) e.pointer = (uint32_t)n;
    if (number_field(rt, hash, "button", &n)) e.button = (int32_t)n;
    if (number_field(rt, hash, "action", &n)) e.action = (int32_t)n;
    if (number_field(rt, hash, "mods", &n)) e.mods = (int32_t)n;
    if (number_field(rt, hash, "key", &n)) e.key = (int32_t)n;
    if (aqua_hash_field(rt, hash, "pointerType", &v) && v.type == AQUA_STRING) {
        aqua_str name = aqua_str_of(call, v);
        e.pointer_type = strcmp(name.data, "touch") == 0 ? 1 : strcmp(name.data, "pen") == 0 ? 2 : 0;
    }
    if (kind == 0) {
        s->input_wall_ms = aqua_now_ms(rt);
        s->recognizer.time = e.time;
        recognizer_timers(&s->recognizer, e.time);
    } else input(s, &e);
}

static void session_dispatch(aqua_call *call) {
    session *s = aqua_self(call);
    if (s->pumping || s->waiting) { fail(call, "Dispatch", "Handlers are already running."); return; }
    s->sync = true;
    pump(s);
    s->sync = false;
    if (s->overflow) { s->overflow = false; fail(call, "Dispatch", "More than 4096 events waited; some were dropped."); }
}

static void session_safe_area(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    float left = 0, top = 0, right = 0, bottom = 0;
    bool keyboard = false;
    platform_safe_area(&left, &top, &right, &bottom, &keyboard);
    aqua_value values[5] = {aqua_double(left), aqua_double(top), aqua_double(right), aqua_double(bottom), aqua_bool(keyboard)};
    aqua_return(call, new_record(rt, safe_area_class, values, 5));
}

static const char *const cursor_names[] = {"arrow", "text", "crosshair", "hand", "resizeHorizontal", "resizeVertical",
                                           "resizeDiagonal", "resizeAntiDiagonal", "move", "notAllowed"};

static void session_set_cursor(aqua_call *call) {
    session *s = aqua_self(call);
    aqua_str name = aqua_arg_str(call, 0);
    int shape = -1;
    for (int i = 0; i < 10; i++) if (strcmp(name.data, cursor_names[i]) == 0) shape = i;
    if (shape < 0) { aqua_fail(call, "Interaction.SetCursor: Unknown cursor '%s'.", name.data); return; }
    /* Touch screens have no cursor: false tells the caller nothing changed. */
    aqua_return_bool(call, s->window && platform()->hover && aqua_window_set_cursor_shape(s->window, shape ? 0x36001 + shape : 0));
}

static void session_detach(aqua_call *call) {
    session *s = aqua_self(call);
    if (s->window) aqua_window_unlisten(s->window, on_window, s);
    s->window = NULL;
    s->attached = false;
    update_guard(s);
    if (s->self) { aqua_ref self = s->self; s->self = 0; aqua_release(s->rt, self); }
}

static void session_attached(aqua_call *call) { aqua_return_bool(call, ((session *)aqua_self(call))->window != NULL); }

AQUA_LIBRARY(interaction) {
    aqua_library_state(library, state_create, state_destroy);
    aqua_class *module = aqua_define_module(library, "Interaction|互動");
    aqua_define_function(module, "Conventions|慣例", interaction_conventions, "");
    aqua_define_function(module, "Attach|附加", interaction_attach, "|v");
    aqua_define_function(module, "Create|建立", interaction_create, "");
    aqua_define_function(module, "ShortcutLabel|快捷鍵標籤", interaction_shortcut_label, "s");

    session_class = aqua_define_class(library, "Interaction.Session", session_finalize);
    aqua_define_function(session_class, "On|註冊", session_on, "sf");
    aqua_define_function(session_class, "Off|取消註冊", session_off, "i");
    aqua_define_function(session_class, "Feed|輸入事件", session_feed, "h");
    aqua_define_function(session_class, "Dispatch|派送", session_dispatch, "");
    aqua_define_function(session_class, "SafeArea|安全區域", session_safe_area, "");
    aqua_define_function(session_class, "SetCursor|設定游標", session_set_cursor, "s");
    aqua_define_function(session_class, "Detach|解除", session_detach, "");
    aqua_define_property(session_class, "attached", session_attached);

    event_class = aqua_define_class(library, "Interaction.Event", event_finalize);
#define DEFINE_GETTER(name, field) aqua_define_property(event_class, name, event_##field);
    EVENT_FIELDS(DEFINE_GETTER, DEFINE_GETTER, DEFINE_GETTER, DEFINE_GETTER)
#undef DEFINE_GETTER
    aqua_define_property(event_class, "text", event_text);
    aqua_define_property(event_class, "shift", event_shift);
    aqua_define_property(event_class, "alt", event_alt);
    aqua_define_property(event_class, "primary", event_primary);

    conventions_class = define_record(library, "Interaction.Conventions", convention_fields, 17);
    static const char *const safe_area_fields[] = {"left", "top", "right", "bottom", "keyboardVisible"};
    safe_area_class = define_record(library, "Interaction.SafeArea", safe_area_fields, 5);
}
