/* Sketch input: window events (aqua_window_listen) are kept until the next frame, then turned into Processing's
   events and fields in the earlier desktop's order: mouse buttons and motion, committed text and input-method
   composition (while text input is on), key transitions, typed characters, the wheel. Each queued event carries the
   fields its callback reads; lifecycle.c dispatches them. */
#include "processing.h"
#include "../common/unicode.h"
#include <stdlib.h>
#include <string.h>

const char *const gfx_event_names[EVENT_COUNT] = {
    "mousePressed", "mouseReleased", "mouseClicked", "mouseMoved", "mouseDragged", "keyPressed", "keyReleased", "keyTyped",
    "mouseWheel", "windowResized", "textInput", "compositionStarted", "compositionUpdated", "compositionEnded", "error"};

/* GLFW leaves gaps in its key numbering. */
static bool valid_key(int c) {
    return c == 32 || c == 39 || (c >= 44 && c <= 57) || c == 59 || c == 61 || (c >= 65 && c <= 93) || c == 96 || c == 161 || c == 162 ||
           (c >= 256 && c <= 269) || (c >= 280 && c <= 284) || (c >= 290 && c <= 314) || (c >= 320 && c <= 336) || (c >= 340 && c <= 348);
}
bool gfx_processing_valid_key(int code) { return valid_key(code); }

static char *copy_text(const char *text) {
    if (!text) text = "";
    size_t n = strlen(text);
    char *c = malloc(n + 1);
    memcpy(c, text, n + 1);
    return c;
}

static void on_event(aqua_window *window, const aqua_window_event *e, void *data) {
    (void)window;
    processing *p = data;
    switch (e->type) {
    case AQUA_EVENT_KEY: case AQUA_EVENT_BUTTON: case AQUA_EVENT_SCROLL: case AQUA_EVENT_TEXT:
    case AQUA_EVENT_COMPOSITION_START: case AQUA_EVENT_COMPOSITION_UPDATE: case AQUA_EVENT_COMPOSITION_END: break;
    case AQUA_EVENT_POINTER: if (e->pointer == 0) { p->moved = true; } return;
    default: return;
    }
    if (p->raw_count >= 65536) return; /* a frame that never comes; keep memory bounded */
    raw_event r = {e->type, e->key, e->action, e->button, e->cursor, e->x, e->y, e->time, NULL};
    if (e->type == AQUA_EVENT_TEXT || e->type == AQUA_EVENT_COMPOSITION_UPDATE) r.text = copy_text(e->text);
    GFX_PUSH(p->raw, p->raw_count, p->raw_capacity, r);
}

void gfx_input_attach(processing *p) {
    if (p->listening || !p->window) return;
    aqua_window_listen(p->window, on_event, p);
    p->listening = true;
}

void gfx_input_detach(processing *p) {
    if (p->listening && p->window) aqua_window_unlisten(p->window, on_event, p);
    p->listening = false;
    for (size_t i = 0; i < p->raw_count; i++) free(p->raw[i].text);
    p->raw_count = 0;
    gfx_input_clear_queue(p);
}

void gfx_input_clear_queue(processing *p) {
    for (size_t i = 0; i < p->queue_count; i++) free(p->queue[i].text);
    p->queue_count = 0;
}

static void set_text(char **slot, const char *text) { free(*slot); *slot = copy_text(text); }

/* The state a new sketch (or text input session) starts with. */
void gfx_input_reset(processing *p) {
    p->text_input_enabled = false;
    set_text(&p->input_text, "");
    p->input_timestamp = 0;
    p->input.key_timestamp = 0;
    set_text(&p->composition_text, "");
    p->composition_cursor = 0;
    p->is_composing = false;
}

static void queue(processing *p, int event, const input_fields *fields, const char *text, int cursor, double timestamp) {
    sketch_event e = {event, *fields, text ? copy_text(text) : NULL, cursor, timestamp};
    GFX_PUSH(p->queue, p->queue_count, p->queue_capacity, e);
}

void gfx_input_collect(processing *p) {
    input_fields w = p->input;
    double x = p->cursor_x, y = p->cursor_y;
    if (p->window) aqua_window_cursor(p->window, &x, &y);
    w.pmouse_x = p->cursor_x; w.pmouse_y = p->cursor_y;
    w.mouse_x = x; w.mouse_y = y;
    bool moved = x != p->cursor_x || y != p->cursor_y;
    p->cursor_x = x; p->cursor_y = y;
    p->moved = false;

    /* Mouse buttons in event order (a click shorter than a frame still presses and releases). */
    for (size_t i = 0; i < p->raw_count; i++) {
        raw_event *r = &p->raw[i];
        if (r->type != AQUA_EVENT_BUTTON || r->button < 0 || r->button >= 8) continue;
        p->buttons[r->button] = r->action != 0;
        int first = p->buttons[0] ? 0 : p->buttons[1] ? 1 : p->buttons[2] ? 2 : -1;
        bool pressed = first >= 0;
        if (pressed) w.mouse_button = first;
        if (pressed && !w.mouse_pressed) { w.mouse_pressed = true; queue(p, EVENT_MOUSE_PRESSED, &w, NULL, 0, r->time); }
        else if (!pressed && w.mouse_pressed) {
            w.mouse_pressed = false;
            queue(p, EVENT_MOUSE_RELEASED, &w, NULL, 0, r->time);
            queue(p, EVENT_MOUSE_CLICKED, &w, NULL, 0, r->time);
        }
    }
    if (moved) queue(p, w.mouse_pressed ? EVENT_MOUSE_DRAGGED : EVENT_MOUSE_MOVED, &w, NULL, 0, 0);

    /* Committed text and composition; keys during composition belong to the input method. */
    bool ime_handled = false;
    if (p->text_input_enabled) {
        ime_handled = p->is_composing;
        for (size_t i = 0; i < p->raw_count; i++) {
            raw_event *r = &p->raw[i];
            switch (r->type) {
            case AQUA_EVENT_TEXT: {
                queue(p, EVENT_TEXT_INPUT, &w, r->text, 0, r->time);
                /* keyTyped keeps its one Unicode scalar per callback contract. */
                size_t n = strlen(r->text);
                for (size_t k = 0; k < n;) {
                    size_t start = k;
                    gfx_utf8_next(r->text, n, &k);
                    memset(w.key, 0, sizeof w.key);
                    memcpy(w.key, r->text + start, k - start < 7 ? k - start : 7);
                    queue(p, EVENT_KEY_TYPED, &w, NULL, 0, r->time);
                }
                break;
            }
            case AQUA_EVENT_COMPOSITION_START: queue(p, EVENT_COMPOSITION_STARTED, &w, NULL, 0, r->time); ime_handled = true; break;
            case AQUA_EVENT_COMPOSITION_UPDATE: queue(p, EVENT_COMPOSITION_UPDATED, &w, r->text, r->cursor, r->time); ime_handled = true; break;
            case AQUA_EVENT_COMPOSITION_END: queue(p, EVENT_COMPOSITION_ENDED, &w, NULL, 0, r->time); ime_handled = true; break;
            }
        }
    }

    /* Key transitions (auto-repeat is not a transition). */
    for (size_t i = 0; i < p->raw_count; i++) {
        raw_event *r = &p->raw[i];
        if (r->type != AQUA_EVENT_KEY || r->action == 2 || r->key < 32 || r->key >= 349 || !valid_key(r->key)) continue;
        bool down = r->action != 0;
        if ((p->keys[r->key] != 0) == down) continue;
        p->keys[r->key] = down;
        w.key_code = r->key;
        w.key_timestamp = r->time;
        memset(w.key, 0, sizeof w.key);
        if (r->key < 127) w.key[0] = (char)r->key;
        bool any = false;
        for (int k = 0; k < 349; k++) if (p->keys[k]) { any = true; break; }
        w.key_pressed = any;
        if (!ime_handled) queue(p, down ? EVENT_KEY_PRESSED : EVENT_KEY_RELEASED, &w, NULL, 0, r->time);
    }

    /* Typed characters when the text input is off. */
    if (!p->text_input_enabled)
        for (size_t i = 0; i < p->raw_count; i++) {
            raw_event *r = &p->raw[i];
            if (r->type != AQUA_EVENT_TEXT) continue;
            size_t n = strlen(r->text);
            for (size_t k = 0; k < n;) {
                size_t start = k;
                gfx_utf8_next(r->text, n, &k);
                memset(w.key, 0, sizeof w.key);
                memcpy(w.key, r->text + start, k - start < 7 ? k - start : 7);
                queue(p, EVENT_KEY_TYPED, &w, NULL, 0, r->time);
            }
        }

    double wheel = 0;
    for (size_t i = 0; i < p->raw_count; i++) if (p->raw[i].type == AQUA_EVENT_SCROLL) wheel += p->raw[i].y;
    w.wheel_count = wheel;
    if (wheel != 0) queue(p, EVENT_MOUSE_WHEEL, &w, NULL, 0, 0);
    queue(p, EVENT_APPLY, &w, NULL, 0, 0);

    /* Escape closes the sketch unless text input is on (Processing's own, or another library's such as an editor's).
       A web page has no window to close, so there Escape stays an ordinary key. */
#if !defined(AQUA_OS_WEB)
    if (p->keys[256] && !p->text_input_enabled && !aqua_window_text_input_enabled(p->window)) p->exiting = true;
#endif
    for (size_t i = 0; i < p->raw_count; i++) free(p->raw[i].text);
    p->raw_count = 0;
}

void gfx_input_apply(processing *p, const sketch_event *e) {
    p->input = e->fields;
    switch (e->event) {
    case EVENT_TEXT_INPUT:
        p->redraw = true;
        set_text(&p->input_text, e->text);
        p->input_timestamp = e->timestamp;
        set_text(&p->composition_text, "");
        p->composition_cursor = 0;
        break;
    case EVENT_COMPOSITION_STARTED:
        set_text(&p->composition_text, ""); p->composition_cursor = 0; p->is_composing = true; p->redraw = true;
        break;
    case EVENT_COMPOSITION_UPDATED:
        set_text(&p->composition_text, e->text); p->composition_cursor = e->cursor; p->is_composing = true; p->redraw = true;
        break;
    case EVENT_COMPOSITION_ENDED:
        set_text(&p->composition_text, ""); p->composition_cursor = 0; p->is_composing = false; p->redraw = true;
        break;
    }
}

/* ---- Text input members ---- */

static void p_startTextInput(aqua_call *call, processing *p) {
    if (!gfx_require_sketch(call, p)) return;
    if (p->text_input_enabled) return;
    aqua_window_text_input(p->window, true);
    gfx_input_reset(p);
    p->text_input_enabled = true;
}
static void p_stopTextInput(aqua_call *call, processing *p) {
    if (!gfx_require_sketch(call, p)) return;
    aqua_window_text_input(p->window, false);
    gfx_input_reset(p);
}
static void p_textInputRect(aqua_call *call, processing *p) {
    if (!gfx_require_sketch(call, p)) return;
    int x = gfx_int(call, 0), y = gfx_int(call, 1), w = gfx_int(call, 2), h = gfx_int(call, 3);
    if (aqua_failed(call)) return;
    if (w < 0 || h < 0) { gfx_fail(call, "Caret dimensions must be nonnegative."); return; }
    if ((int64_t)x + w > INT32_MAX || (int64_t)y + h > INT32_MAX) { gfx_fail(call, "Arithmetic operation resulted in an overflow."); return; }
    aqua_window_text_rect(p->window, x, y, w, h);
}
static void p_keyDown(aqua_call *call, processing *p) {
    if (!gfx_require_sketch(call, p)) return;
    int code = gfx_int(call, 0);
    if (aqua_failed(call)) return;
    if (!valid_key(code)) { gfx_fail(call, "Invalid GLFW key code."); return; }
    aqua_return_bool(call, aqua_window_key(p->window, code) == 1);
}
/* cursor(), cursor(ARROW | CROSS | HAND | MOVE | TEXT | WAIT) as GLFW's standard shapes (WAIT has none: the arrow),
   cursor(image[, x, y]) with the image's pixels and hot spot. */
static void p_cursor(aqua_call *call, processing *p) {
    if (!gfx_require_sketch(call, p)) return;
    uint32_t n = aqua_argc(call);
    if (n == 2) { gfx_fail(call, "cursor() takes a kind, or an image with an optional hot spot x, y."); return; }
    if (n >= 1 && aqua_arg(call, 0).type == AQUA_NATIVE) {
        pimage *image = gfx_image_argument(call, p, 0);
        if (!image) return;
        if (!image->bytes) { gfx_fail(call, "Use a PImage for the cursor, not a PGraphics."); return; }
        int x = n == 3 ? gfx_int(call, 1) : image->width / 2, y = n == 3 ? gfx_int(call, 2) : image->height / 2;
        if (aqua_failed(call)) return;
        aqua_window_set_cursor_mode(p->window, 0x34001);
        aqua_window_set_cursor_image(p->window, image->width, image->height, image->bytes, x, y);
        return;
    }
    int kind = n == 1 ? GFX_CHOICE(call, 0, 0, 1, 2, 3, 12, 13) : 0;
    if (aqua_failed(call)) return;
    aqua_window_set_cursor_mode(p->window, 0x34001);
    aqua_window_set_cursor_shape(p->window, kind == 1 ? 0x36003 : kind == 2 ? 0x36002 : kind == 12 ? 0x36004 : kind == 13 ? 0x36009 : 0);
}
static void p_noCursor(aqua_call *call, processing *p) { if (gfx_require_sketch(call, p)) aqua_window_set_cursor_mode(p->window, 0x34002); }

#define INPUT_MODULE(X) \
    X(startTextInput, "startTextInput|開始文字輸入", 0, 0) X(stopTextInput, "stopTextInput|停止文字輸入", 0, 0) \
    X(textInputRect, "textInputRect|文字輸入矩形", 4, 4) X(keyDown, "keyDown|按鍵是否按下", 1, 1) X(cursor, "cursor|游標", 0, 3) \
    X(noCursor, "noCursor|隱藏游標", 0, 0)
INPUT_MODULE(MODULE_WRAPPER)

void gfx_input_register(aqua_class *module) { INPUT_MODULE(MODULE_DEFINE) }
