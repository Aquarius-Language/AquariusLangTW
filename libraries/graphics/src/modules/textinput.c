/* TextInput: committed text and input-method composition for any window (GLFW, WGPU or the shared window objects of
   other libraries). Start listens to the window and turns on its text input; Poll returns the events gathered since
   the previous poll as records {type, text, cursor, timestamp}. Processing sketches receive these through
   Processing.on() instead. */
#include "../graphics.h"
#include "../common/records.h"
#include "../common/unicode.h"
#include "../processing/processing.h"
#include "wgpu.h"
#include <stdlib.h>
#include <string.h>

#define QUEUE_LIMIT (1024 * 1024)

typedef struct text_event { int type; char *text; int cursor; double time; } text_event;
typedef struct text_session {
    aqua_window *window;
    text_event *events; size_t count, capacity, bytes;
    bool overflow;
    struct text_session *next;
} text_session;
struct textinput_state { text_session *sessions; };

static textinput_state *state_of(aqua_call *call) {
    graphics *g = gfx_graphics_of(call);
    if (!g->text_input) g->text_input = calloc(1, sizeof *g->text_input);
    return g->text_input;
}

static void clear_events(text_session *s) {
    for (size_t i = 0; i < s->count; i++) free(s->events[i].text);
    s->count = 0;
    s->bytes = 0;
}

static void on_event(aqua_window *window, const aqua_window_event *e, void *data) {
    (void)window;
    text_session *s = data;
    if (e->type == AQUA_EVENT_DESTROYED) { s->window = NULL; return; }
    if (e->type != AQUA_EVENT_TEXT && e->type != AQUA_EVENT_COMPOSITION_START && e->type != AQUA_EVENT_COMPOSITION_UPDATE &&
        e->type != AQUA_EVENT_COMPOSITION_END) return;
    const char *text = e->text && (e->type == AQUA_EVENT_TEXT || e->type == AQUA_EVENT_COMPOSITION_UPDATE) ? e->text : "";
    size_t n = strlen(text);
    if (s->overflow || s->bytes + n + 32 > QUEUE_LIMIT) { s->overflow = true; return; }
    text_event event = {e->type, malloc(n + 1), e->cursor, e->time};
    memcpy(event.text, text, n + 1);
    GFX_PUSH(s->events, s->count, s->capacity, event);
    s->bytes += n + 32;
}

static text_session *session_of(textinput_state *state, aqua_window *window) {
    for (text_session *s = state->sessions; s; s = s->next) if (s->window == window) return s;
    return NULL;
}

static void session_end(textinput_state *state, text_session *s) {
    if (s->window) aqua_window_unlisten(s->window, on_event, s);
    for (text_session **p = &state->sessions; *p; p = &(*p)->next) if (*p == s) { *p = s->next; break; }
    clear_events(s);
    free(s->events);
    free(s);
}

/* Sessions of windows destroyed since are dropped. */
static void prune(textinput_state *state) {
    for (text_session *s = state->sessions, *next; s; s = next) { next = s->next; if (!s->window) session_end(state, s); }
}

static aqua_window *window_argument(aqua_call *call) {
    aqua_window *w = gfx_wgpu_window_of(aqua_call_runtime(call), aqua_arg(call, 0));
    if (!w) gfx_fail(call, "Expected a live GLFW window owned by this runtime.");
    return w;
}

static void text_Start(aqua_call *call) {
    aqua_window *w = window_argument(call);
    if (!w) return;
    textinput_state *state = state_of(call);
    prune(state);
    aqua_window_text_input(w, true);
    /* A sketch window's text arrives through Processing's events, which follow this switch as before. */
    processing *p = gfx_graphics_of(call)->processing;
    if (p && p->window == w) { p->text_input_enabled = true; return; }
    if (session_of(state, w)) return;
    text_session *s = calloc(1, sizeof *s);
    s->window = w;
    s->next = state->sessions;
    state->sessions = s;
    aqua_window_listen(w, on_event, s);
}

static void text_Stop(aqua_call *call) {
    aqua_window *w = window_argument(call);
    if (!w) return;
    textinput_state *state = state_of(call);
    aqua_window_text_input(w, false);
    processing *p = gfx_graphics_of(call)->processing;
    if (p && p->window == w) p->text_input_enabled = false;
    text_session *s = session_of(state, w);
    if (s) session_end(state, s);
}

static void text_SetCaretRect(aqua_call *call) {
    aqua_window *w = window_argument(call);
    if (!w) return;
    int x = gfx_int(call, 1), y = gfx_int(call, 2), width = gfx_int(call, 3), height = gfx_int(call, 4);
    if (aqua_failed(call)) return;
    if (width < 0 || height < 0) { gfx_fail(call, "Caret dimensions must be nonnegative."); return; }
    if ((int64_t)x + width > INT32_MAX || (int64_t)y + height > INT32_MAX) { gfx_fail(call, "Arithmetic operation resulted in an overflow."); return; }
    aqua_window_text_rect(w, x, y, width, height);
}

static const char *type_name(int type) {
    switch (type) {
    case AQUA_EVENT_TEXT: return "textInput";
    case AQUA_EVENT_COMPOSITION_START: return "compositionStarted";
    case AQUA_EVENT_COMPOSITION_UPDATE: return "compositionUpdated";
    default: return "compositionEnded";
    }
}

static void text_Poll(aqua_call *call) {
    aqua_window *w = window_argument(call);
    if (!w) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    graphics *g = gfx_graphics_of(call);
    if (g->processing && g->processing->window == w) { gfx_fail(call, "Processing owns text events for its sketch; use Processing.on()."); return; }
    text_session *s = session_of(state_of(call), w);
    if (s && s->overflow) {
        s->overflow = false;
        clear_events(s);
        gfx_fail(call, "Text input queue exceeded 1 MiB or allocation failed; poll input more often.");
        return;
    }
    aqua_value array = aqua_array_new(rt, s ? (uint32_t)s->count : 0);
    static const char *const names[] = {"type", "text", "cursor", "timestamp"};
    for (size_t i = 0; s && i < s->count; i++) {
        text_event *e = &s->events[i];
        aqua_value values[4] = {aqua_cstring(rt, type_name(e->type)), aqua_cstring(rt, e->text), aqua_int(e->cursor), aqua_double(e->time)};
        aqua_array_set(rt, array, (uint32_t)i, gfx_record_of(rt, 4, names, values));
    }
    if (s) clear_events(s);
    aqua_return(call, array);
}

static void text_SupportsComposition(aqua_call *call) { aqua_return_bool(call, aqua_window_composition_supported(NULL)); }

/* The text without its last extended grapheme cluster. */
static void text_Backspace(aqua_call *call) {
    aqua_str text = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    aqua_return_text(call, text.data, gfx_grapheme_last_start(text.data, text.length));
}

void gfx_textinput_destroy(textinput_state *state) {
    if (!state) return;
    while (state->sessions) session_end(state, state->sessions);
    free(state);
}

#define TEXTINPUT_MODULE(X) \
    X(text_Backspace, "Backspace|刪除末字", 1) X(text_Poll, "Poll|輪詢", 1) X(text_SetCaretRect, "SetCaretRect|設定插入點矩形", 5) \
    X(text_Start, "Start|開始", 1) X(text_Stop, "Stop|停止", 1) X(text_SupportsComposition, "SupportsComposition|支援組字", 0)
#define WRAPPER(function, names, count) GFX_WRAP("", function, names, function)
TEXTINPUT_MODULE(WRAPPER)
#undef WRAPPER

void gfx_textinput_register(aqua_library *library) {
    aqua_class *module = aqua_define_module(library, "TextInput");
#define DEFINE(function, names, count) aqua_define_function(module, names, wrapper_##function, gfx_signature(count, count));
    TEXTINPUT_MODULE(DEFINE)
#undef DEFINE
}
