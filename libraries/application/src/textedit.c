/* TextEdit: an editor whose positions are extended grapheme clusters (UAX #29, utf8proc), with UTF-16 and Unicode
 * scalar conversions, IME composition kept apart from the text, bounded undo/redo that coalesces typing, word and
 * line navigation and clipboard shortcuts. The text is kept as UTF-16, the language's string representation. */
#include "application.h"
#include "platform/platform.h"
#include <stdlib.h>
#include <string.h>
#include <utf8proc.h>

enum { HISTORY_LIMIT = 200 };
typedef enum unit { UNIT_GRAPHEME, UNIT_UTF16, UNIT_SCALAR } unit;
typedef enum group { GROUP_NONE, GROUP_TYPE, GROUP_BACKSPACE, GROUP_DELETE, GROUP_INSERT } group;

typedef struct text { uint16_t *units; size_t length; } text;
typedef struct snapshot { text content; int32_t anchor, caret; } snapshot;
typedef struct history { snapshot *items; size_t count, capacity; } history;

typedef struct editor {
    text content;
    size_t *offsets; size_t offset_count;   /* grapheme start offsets in UTF-16 units, then the length */
    int32_t anchor, caret;
    bool composing; text composition; int32_t composition_cursor;
    history undo, redo;
    group edit_group; int32_t edit_group_caret;
} editor;

/* ---- Text helpers ---- */

static void text_free(text *t) { free(t->units); t->units = NULL; t->length = 0; }
static text text_copy(const uint16_t *units, size_t length) {
    text t = {malloc((length ? length : 1) * sizeof *units), length};
    if (length) memcpy(t.units, units, length * sizeof *units);
    return t;
}
static bool text_equal(const text *a, const text *b) { return a->length == b->length && (!a->length || memcmp(a->units, b->units, a->length * 2) == 0); }

static int32_t scalar_at(const text *t, size_t i, size_t *width) {
    uint16_t c = t->units[i];
    if (c >= 0xD800 && c <= 0xDBFF && i + 1 < t->length && t->units[i + 1] >= 0xDC00 && t->units[i + 1] <= 0xDFFF) {
        *width = 2;
        return 0x10000 + ((c - 0xD800) << 10) + (t->units[i + 1] - 0xDC00);
    }
    *width = 1;
    return c;
}

/* Editor text is valid UTF-16 of at most APP_MAX_EDITOR units. */
static bool validate(const text *t, app_error *e) {
    if (t->length > APP_MAX_EDITOR) return app_error_set(e, APP_LIMIT_EXCEEDED, "Editor text exceeds 1 Mi UTF-16 units.");
    for (size_t i = 0; i < t->length; i++) {
        uint16_t c = t->units[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < t->length && t->units[i + 1] >= 0xDC00 && t->units[i + 1] <= 0xDFFF) { i++; continue; }
        if (c >= 0xD800 && c <= 0xDFFF) return app_error_set(e, APP_INVALID_DATA, "The text contains an unpaired surrogate.");
    }
    return true;
}

/* Grapheme start offsets of `t` followed by its length. */
static size_t *boundaries(const text *t, size_t *count) {
    size_t *offsets = malloc((t->length + 1) * sizeof *offsets);
    size_t n = 0, width;
    int32_t state = 0, previous = -1;
    for (size_t i = 0; i < t->length; i += width) {
        int32_t c = scalar_at(t, i, &width);
        if (previous < 0 || utf8proc_grapheme_break_stateful(previous, c, &state)) offsets[n++] = i;
        previous = c;
    }
    offsets[n++] = t->length;
    *count = n;
    return offsets;
}

static size_t grapheme_count(const text *t) {
    size_t count;
    free(boundaries(t, &count));
    return count - 1;
}

static size_t scalar_count(const text *t, size_t end) {
    size_t count = 0, width;
    for (size_t i = 0; i < end; i += width) { scalar_at(t, i, &width); count++; }
    return count;
}

static bool unit_of(const char *name, unit *out, app_error *e) {
    static const char *const names[] = {"Grapheme", "Utf16", "UnicodeScalar"};
    for (int i = 0; i < 3; i++) if (app_equals_ignore_case(name, names[i])) { *out = (unit)i; return true; }
    return app_error_set(e, APP_INVALID_DATA, "Requested value '%s' was not found.", name);
}

/* UTF-8 (from the clipboard) to UTF-16. */
static text text_of_utf8(const char *s, size_t length) {
    text t = {malloc((length + 1) * sizeof(uint16_t)), 0};
    for (size_t i = 0; i < length;) {
        uint8_t c = (uint8_t)s[i];
        uint32_t v = 0xFFFD; size_t n = 1;
        if (c < 0x80) v = c;
        else if ((c & 0xE0) == 0xC0 && i + 1 < length) { v = (c & 0x1F) << 6 | (s[i + 1] & 0x3F); n = 2; }
        else if ((c & 0xF0) == 0xE0 && i + 2 < length) { v = (c & 0x0F) << 12 | (s[i + 1] & 0x3F) << 6 | (s[i + 2] & 0x3F); n = 3; }
        else if ((c & 0xF8) == 0xF0 && i + 3 < length) { v = (c & 0x07) << 18 | (s[i + 1] & 0x3F) << 12 | (s[i + 2] & 0x3F) << 6 | (s[i + 3] & 0x3F); n = 4; }
        i += n;
        if (v >= 0x10000) { t.units[t.length++] = (uint16_t)(0xD800 + ((v - 0x10000) >> 10)); t.units[t.length++] = (uint16_t)(0xDC00 + ((v - 0x10000) & 0x3FF)); }
        else t.units[t.length++] = (uint16_t)v;
    }
    return t;
}

/* UTF-8 of a UTF-16 range (to the clipboard). */
static char *utf8_of(const text *t, size_t start, size_t end, size_t *length) {
    app_bytes out = {0};
    size_t width;
    for (size_t i = start; i < end; i += width) {
        uint32_t c = (uint32_t)scalar_at(t, i, &width);
        uint8_t b[4]; size_t n;
        if (c < 0x80) { b[0] = (uint8_t)c; n = 1; }
        else if (c < 0x800) { b[0] = (uint8_t)(0xC0 | c >> 6); b[1] = (uint8_t)(0x80 | (c & 63)); n = 2; }
        else if (c < 0x10000) { b[0] = (uint8_t)(0xE0 | c >> 12); b[1] = (uint8_t)(0x80 | (c >> 6 & 63)); b[2] = (uint8_t)(0x80 | (c & 63)); n = 3; }
        else { b[0] = (uint8_t)(0xF0 | c >> 18); b[1] = (uint8_t)(0x80 | (c >> 12 & 63)); b[2] = (uint8_t)(0x80 | (c >> 6 & 63)); b[3] = (uint8_t)(0x80 | (c & 63)); n = 4; }
        app_bytes_append(&out, b, n);
    }
    app_bytes_byte(&out, 0);
    *length = out.length - 1;
    return (char *)out.data;
}

/* ---- Editor operations (the old TextEditor) ---- */

static int32_t graphemes(const editor *ed) { return (int32_t)ed->offset_count - 1; }
static int32_t sel_start(const editor *ed) { return ed->anchor < ed->caret ? ed->anchor : ed->caret; }
static int32_t sel_end(const editor *ed) { return ed->anchor > ed->caret ? ed->anchor : ed->caret; }

static void set_content(editor *ed, text content) {
    text_free(&ed->content);
    free(ed->offsets);
    ed->content = content;
    ed->offsets = boundaries(&ed->content, &ed->offset_count);
}

static bool set_selection(editor *ed, int32_t anchor, int32_t caret, app_error *e) {
    if (anchor < 0 || caret < 0 || anchor > graphemes(ed) || caret > graphemes(ed))
        return app_error_set(e, APP_INVALID_DATA, "Specified argument was out of the range of valid values. (Parameter 'caret')");
    ed->anchor = anchor;
    ed->caret = caret;
    return true;
}

static bool select_range(editor *ed, int32_t anchor, int32_t caret, app_error *e) {
    if (!set_selection(ed, anchor, caret, e)) return false;
    ed->edit_group = GROUP_NONE;
    return true;
}

static void history_push(history *h, const text *content, int32_t anchor, int32_t caret) {
    if (h->count == h->capacity) { h->capacity = h->capacity ? h->capacity * 2 : 16; h->items = realloc(h->items, h->capacity * sizeof *h->items); }
    h->items[h->count++] = (snapshot){text_copy(content->units, content->length), anchor, caret};
}
static void history_clear(history *h) { for (size_t i = 0; i < h->count; i++) text_free(&h->items[i].content); h->count = 0; }

static bool mergeable(const editor *ed, group kind) {
    return (kind == GROUP_TYPE || kind == GROUP_BACKSPACE || kind == GROUP_DELETE) && kind == ed->edit_group &&
           sel_start(ed) == sel_end(ed) && ed->caret == ed->edit_group_caret;
}

static void record_edit(editor *ed, group kind, bool merge, int32_t anchor, int32_t caret) {
    if (!merge) {
        history_push(&ed->undo, &ed->content, anchor, caret);
        if (ed->undo.count > HISTORY_LIMIT) {
            text_free(&ed->undo.items[0].content);
            memmove(ed->undo.items, ed->undo.items + 1, (ed->undo.count - 1) * sizeof *ed->undo.items);
            ed->undo.count--;
        }
    }
    history_clear(&ed->redo);
    ed->edit_group = kind;
}

/* Replaces the selection with `insert`; `anchor`/`caret` is the selection to restore on undo. */
static bool replace(editor *ed, const text *insert, group kind, bool merge, int32_t anchor, int32_t caret, app_error *e) {
    if (!validate(insert, e)) return false;
    size_t start = ed->offsets[sel_start(ed)], end = ed->offsets[sel_end(ed)];
    text updated = {malloc((ed->content.length - (end - start) + insert->length + 1) * 2), ed->content.length - (end - start) + insert->length};
    memcpy(updated.units, ed->content.units, start * 2);
    if (insert->length) memcpy(updated.units + start, insert->units, insert->length * 2);
    memcpy(updated.units + start + insert->length, ed->content.units + end, (ed->content.length - end) * 2);
    if (!validate(&updated, e)) { text_free(&updated); return false; }
    bool changed = !text_equal(&updated, &ed->content);
    if (changed) record_edit(ed, kind, merge, anchor, caret);
    set_content(ed, updated);
    size_t target = start + insert->length;
    int32_t position = 0;
    while ((size_t)position < ed->offset_count && ed->offsets[position] < target) position++;
    set_selection(ed, position, position, NULL);
    if (changed) ed->edit_group_caret = position;
    return true;
}

static bool is_white_space(uint32_t c) {
    return (c >= 9 && c <= 13) || c == 0x20 || c == 0x85 || c == 0xA0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200A) ||
           c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000;
}

static bool insert_text(editor *ed, const text *t, app_error *e) {
    bool white = true;
    for (size_t i = 0; i < t->length; i++) if (!is_white_space(t->units[i])) white = false;
    group kind = t->length > 0 && grapheme_count(t) == 1 && !white ? GROUP_TYPE : GROUP_INSERT;
    return replace(ed, t, kind, mergeable(ed, kind), ed->anchor, ed->caret, e);
}

static bool backspace(editor *ed, app_error *e) {
    if (ed->composing) return true;
    int32_t anchor = ed->anchor, caret = ed->caret;
    bool merge = mergeable(ed, GROUP_BACKSPACE);
    if (sel_start(ed) == sel_end(ed)) {
        if (ed->caret == 0) return true;
        set_selection(ed, ed->caret - 1, ed->caret, NULL);
    }
    text empty = {NULL, 0};
    return replace(ed, &empty, GROUP_BACKSPACE, merge, anchor, caret, e);
}

static bool delete_forward(editor *ed, app_error *e) {
    if (ed->composing) return true;
    int32_t anchor = ed->anchor, caret = ed->caret;
    bool merge = mergeable(ed, GROUP_DELETE);
    if (sel_start(ed) == sel_end(ed)) {
        if (ed->caret == graphemes(ed)) return true;
        set_selection(ed, ed->caret, ed->caret + 1, NULL);
    }
    text empty = {NULL, 0};
    return replace(ed, &empty, GROUP_DELETE, merge, anchor, caret, e);
}

/* Words: graphemes starting with a letter, a decimal digit or "_". */
static bool word_at(const editor *ed, int32_t index) {
    size_t width;
    int32_t c = scalar_at(&ed->content, ed->offsets[index], &width);
    if (c == '_') return true;
    utf8proc_category_t category = utf8proc_category(c);
    return (category >= UTF8PROC_CATEGORY_LU && category <= UTF8PROC_CATEGORY_LO) || category == UTF8PROC_CATEGORY_ND;
}
static bool line_break_at(const editor *ed, int32_t index) {
    uint16_t c = ed->content.units[ed->offsets[index]];
    return c == '\r' || c == '\n';
}

static bool move(editor *ed, int32_t direction, const char *unit_name, bool extend, app_error *e) {
    if (direction != -1 && direction != 1) return app_error_set(e, APP_INVALID_DATA, "Direction must be -1 or 1.");
    int32_t next = ed->caret, length = graphemes(ed);
    bool grapheme = strcmp(unit_name, "grapheme") == 0;
    if (!extend && sel_start(ed) != sel_end(ed) && grapheme) next = direction < 0 ? sel_start(ed) : sel_end(ed);
    else if (grapheme) { next += direction; if (next < 0) next = 0; if (next > length) next = length; }
    else if (strcmp(unit_name, "line") == 0) {
        if (direction < 0) { while (next > 0 && !line_break_at(ed, next - 1)) next--; }
        else { while (next < length && !line_break_at(ed, next)) next++; }
    } else if (strcmp(unit_name, "word") == 0) {
        if (direction > 0) { while (next < length && word_at(ed, next)) next++; while (next < length && !word_at(ed, next)) next++; }
        else { while (next > 0 && !word_at(ed, next - 1)) next--; while (next > 0 && word_at(ed, next - 1)) next--; }
    } else if (strcmp(unit_name, "document") == 0) next = direction < 0 ? 0 : length;
    else return app_error_set(e, APP_INVALID_DATA, "Navigation unit must be grapheme, word, line or document.");
    return select_range(ed, extend ? ed->anchor : next, next, e);
}

static bool delete_word(editor *ed, int32_t direction, app_error *e) {
    if (ed->composing) return true;
    int32_t anchor = ed->anchor, caret = ed->caret;
    if (sel_start(ed) == sel_end(ed)) {
        if (!move(ed, direction, "word", true, e)) return false;
        if (ed->caret == caret) return true;
    }
    text empty = {NULL, 0};
    return replace(ed, &empty, GROUP_INSERT, false, anchor, caret, e);
}

static void restore(editor *ed, snapshot *s) {
    set_content(ed, s->content);
    s->content.units = NULL;
    ed->composing = false;
    set_selection(ed, s->anchor, s->caret, NULL);
    ed->edit_group = GROUP_NONE;
}

static bool undo(editor *ed) {
    if (ed->composing || !ed->undo.count) return false;
    history_push(&ed->redo, &ed->content, ed->anchor, ed->caret);
    restore(ed, &ed->undo.items[--ed->undo.count]);
    return true;
}
static bool redo(editor *ed) {
    if (ed->composing || !ed->redo.count) return false;
    history_push(&ed->undo, &ed->content, ed->anchor, ed->caret);
    restore(ed, &ed->redo.items[--ed->redo.count]);
    return true;
}

/* Converts a position between units over text `t` with grapheme offsets `offsets`. */
static bool convert(const text *t, const size_t *offsets, size_t offset_count, int32_t position, unit from, unit to, int32_t *out, app_error *e) {
    size_t utf16 = 0;
    if (from == UNIT_GRAPHEME) {
        if (position < 0 || (size_t)position >= offset_count) return app_error_set(e, APP_INVALID_DATA, "Specified argument was out of the range of valid values. (Parameter 'position')");
        utf16 = offsets[position];
    } else if (from == UNIT_UTF16) {
        if (position < 0 || (size_t)position > t->length) return app_error_set(e, APP_INVALID_DATA, "Specified argument was out of the range of valid values. (Parameter 'position')");
        utf16 = (size_t)position;
    } else {
        if (position < 0) return app_error_set(e, APP_INVALID_DATA, "Specified argument was out of the range of valid values. (Parameter 'position')");
        size_t count = 0, width, i = 0;
        for (; i < t->length && count < (size_t)position; i += width) { scalar_at(t, i, &width); count++; }
        if (count != (size_t)position) return app_error_set(e, APP_INVALID_DATA, "Specified argument was out of the range of valid values. (Parameter 'position')");
        utf16 = i;
    }
    if (utf16 > 0 && utf16 < t->length && t->units[utf16] >= 0xDC00 && t->units[utf16] <= 0xDFFF && t->units[utf16 - 1] >= 0xD800 && t->units[utf16 - 1] <= 0xDBFF)
        return app_error_set(e, APP_INVALID_DATA, "Position splits a Unicode scalar.");
    if (to == UNIT_UTF16) { *out = (int32_t)utf16; return true; }
    if (to == UNIT_SCALAR) { *out = (int32_t)scalar_count(t, utf16); return true; }
    for (size_t i = 0; i < offset_count; i++) if (offsets[i] == utf16) { *out = (int32_t)i; return true; }
    return app_error_set(e, APP_INVALID_DATA, "Position splits a grapheme.");
}

static bool convert_text(const text *t, int32_t position, unit from, unit to, int32_t *out, app_error *e) {
    if (!validate(t, e)) return false;
    size_t count;
    size_t *offsets = boundaries(t, &count);
    bool ok = convert(t, offsets, count, position, from, to, out, e);
    free(offsets);
    return ok;
}

static bool set_composition(editor *ed, const text *t, int32_t cursor, unit cursor_unit, app_error *e) {
    int32_t scalar;
    if (!convert_text(t, cursor, cursor_unit, UNIT_SCALAR, &scalar, e)) return false;
    if (scalar < 0 || (size_t)scalar > scalar_count(t, t->length)) return app_error_set(e, APP_INVALID_DATA, "Specified argument was out of the range of valid values. (Parameter 'cursorScalar')");
    text_free(&ed->composition);
    ed->composition = text_copy(t->units, t->length);
    ed->composition_cursor = scalar;
    ed->composing = true;
    return true;
}

/* ---- Language objects ---- */

static text text_argument(aqua_call *call, uint32_t index) {
    size_t count;
    uint16_t *units = app_text16(aqua_call_runtime(call), aqua_arg(call, index), &count);
    text t = text_copy(units, count);
    aqua_free(units);
    return t;
}

void app_editor_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    editor *ed = data;
    text_free(&ed->content);
    text_free(&ed->composition);
    free(ed->offsets);
    history_clear(&ed->undo); free(ed->undo.items);
    history_clear(&ed->redo); free(ed->redo.items);
    free(ed);
}

void app_editor_position_unit(aqua_call *call) { aqua_return_cstring(call, "grapheme"); }

void app_textedit_Create(aqua_call *call) {
    app_error e = {0};
    text t = text_argument(call, 0);
    if (!validate(&t, &e)) { text_free(&t); app_fail(call, &e); return; }
    editor *ed = calloc(1, sizeof *ed);
    set_content(ed, t);
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), app_editor_class, ed));
}

void app_textedit_Convert(aqua_call *call) {
    app_error e = {0};
    unit from, to;
    int32_t result;
    text t = text_argument(call, 0);
    bool ok = unit_of(aqua_arg_str(call, 2).data, &from, &e) && unit_of(aqua_arg_str(call, 3).data, &to, &e) &&
              convert_text(&t, aqua_arg_int(call, 1), from, to, &result, &e);
    text_free(&t);
    if (!ok) { app_fail(call, &e); return; }
    aqua_return_int(call, result);
}

static editor *self(aqua_call *call) { return aqua_self(call); }

void app_editor_State(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    editor *ed = self(call);
    aqua_value state = aqua_hash_new(rt, 4), selection = aqua_hash_new(rt, 4);
    app_set(rt, state, "text", aqua_text16(rt, ed->content.units, ed->content.length));
    app_set(rt, state, "length", aqua_int(graphemes(ed)));
    app_set(rt, selection, "anchor", aqua_int(ed->anchor));
    app_set(rt, selection, "caret", aqua_int(ed->caret));
    app_set(rt, selection, "start", aqua_int(sel_start(ed)));
    app_set(rt, selection, "end", aqua_int(sel_end(ed)));
    app_set(rt, state, "selection", selection);
    if (ed->composing) {
        aqua_value composition = aqua_hash_new(rt, 2);
        app_set(rt, composition, "text", aqua_text16(rt, ed->composition.units, ed->composition.length));
        app_set(rt, composition, "cursorScalar", aqua_int(ed->composition_cursor));
        app_set(rt, state, "composition", composition);
    } else app_set(rt, state, "composition", aqua_null());
    aqua_return(call, app_record(rt, state));
}

void app_editor_Select(aqua_call *call) {
    app_error e = {0};
    if (!select_range(self(call), aqua_arg_int(call, 0), aqua_arg_int(call, 1), &e)) app_fail(call, &e);
}

void app_editor_Insert(aqua_call *call) {
    app_error e = {0};
    text t = text_argument(call, 0);
    if (!insert_text(self(call), &t, &e)) app_fail(call, &e);
    text_free(&t);
}

void app_editor_Backspace(aqua_call *call) { app_error e = {0}; if (!backspace(self(call), &e)) app_fail(call, &e); }
void app_editor_Delete(aqua_call *call) { app_error e = {0}; if (!delete_forward(self(call), &e)) app_fail(call, &e); }
void app_editor_DeleteWord(aqua_call *call) { app_error e = {0}; if (!delete_word(self(call), aqua_arg_int(call, 0), &e)) app_fail(call, &e); }

void app_editor_Move(aqua_call *call) {
    app_error e = {0};
    if (!move(self(call), aqua_arg_int(call, 0), aqua_arg_str(call, 1).data, aqua_arg_bool(call, 2), &e)) app_fail(call, &e);
}

void app_editor_Convert(aqua_call *call) {
    app_error e = {0};
    editor *ed = self(call);
    unit from, to;
    int32_t result;
    if (!unit_of(aqua_arg_str(call, 1).data, &from, &e) || !unit_of(aqua_arg_str(call, 2).data, &to, &e) ||
        !convert(&ed->content, ed->offsets, ed->offset_count, aqua_arg_int(call, 0), from, to, &result, &e)) { app_fail(call, &e); return; }
    aqua_return_int(call, result);
}

void app_editor_Composition(aqua_call *call) {
    app_error e = {0};
    unit cursor_unit = UNIT_SCALAR;
    if (aqua_has_arg(call, 2) && !unit_of(aqua_arg_str(call, 2).data, &cursor_unit, &e)) { app_fail(call, &e); return; }
    text t = text_argument(call, 0);
    if (!set_composition(self(call), &t, aqua_arg_int(call, 1), cursor_unit, &e)) app_fail(call, &e);
    text_free(&t);
}

void app_editor_CancelComposition(aqua_call *call) { editor *ed = self(call); ed->composing = false; text_free(&ed->composition); }

void app_editor_Commit(aqua_call *call) {
    app_error e = {0};
    editor *ed = self(call);
    ed->composing = false;
    text_free(&ed->composition);
    text t = text_argument(call, 0);
    if (!insert_text(ed, &t, &e)) app_fail(call, &e);
    text_free(&t);
}

void app_editor_Undo(aqua_call *call) { aqua_return_bool(call, undo(self(call))); }
void app_editor_Redo(aqua_call *call) { aqua_return_bool(call, redo(self(call))); }
void app_editor_CanUndo(aqua_call *call) { aqua_return_bool(call, self(call)->undo.count > 0); }
void app_editor_CanRedo(aqua_call *call) { aqua_return_bool(call, self(call)->redo.count > 0); }

/* ---- Clipboard operations: they may wait for the clipboard (web) ---- */

typedef struct edit_task {
    app_task task;
    editor *ed;
    aqua_ref self;          /* keeps the editor alive while the call waits */
    bool cut;
    aqua_value result;      /* what the call returns once the clipboard finished */
} edit_task;

static void edit_cleanup(app_task *task) { aqua_release(task->rt, ((edit_task *)task)->self); }

static edit_task *edit_task_new(aqua_call *call, aqua_value result) {
    edit_task *t = app_task_new(call, sizeof *t);
    t->ed = self(call);
    t->self = aqua_retain(t->task.rt, aqua_self_value(call));
    t->task.cleanup = edit_cleanup;
    t->result = result;
    return t;
}

static void copied(app_task *task, const app_error *e) {
    edit_task *t = (edit_task *)task;
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    if (t->cut) {
        app_error error = {0};
        text empty = {NULL, 0};
        if (!replace(t->ed, &empty, GROUP_INSERT, false, t->ed->anchor, t->ed->caret, &error)) { app_task_fail(task, &error); return; }
    }
    app_task_return(task, t->result);
}

/* Copies (or cuts) the selection; an empty selection does nothing. */
static void copy_selection(edit_task *t, bool cut) {
    editor *ed = t->ed;
    t->cut = cut;
    if (sel_start(ed) == sel_end(ed)) { app_task_return(&t->task, t->result); return; }
    if (app_task_cancelled(&t->task)) return;
    size_t length;
    char *utf8 = utf8_of(&ed->content, ed->offsets[sel_start(ed)], ed->offsets[sel_end(ed)], &length);
    app_os_clipboard_write(&t->task, utf8, length, NULL, copied);
    free(utf8);
}

static void pasted(app_task *task, const app_error *e, const char *utf8, size_t length, const uint8_t *image, size_t image_length) {
    (void)image; (void)image_length;
    edit_task *t = (edit_task *)task;
    if (e) { app_task_fail(task, e); return; }
    if (app_task_cancelled(task)) return;
    if (utf8) {
        app_error error = {0};
        text pasted_text = text_of_utf8(utf8, length);
        t->ed->composing = false;
        text_free(&t->ed->composition);
        bool ok = replace(t->ed, &pasted_text, GROUP_INSERT, false, t->ed->anchor, t->ed->caret, &error);
        text_free(&pasted_text);
        if (!ok) { app_task_fail(task, &error); return; }
    }
    app_task_return(task, t->result);
}

static void paste(edit_task *t) {
    if (app_task_cancelled(&t->task)) return;
    app_os_clipboard_read(&t->task, false, pasted);
}

void app_editor_Copy(aqua_call *call) { edit_task *t = edit_task_new(call, aqua_void()); copy_selection(t, false); app_task_yield(&t->task); }
void app_editor_Cut(aqua_call *call) { edit_task *t = edit_task_new(call, aqua_void()); copy_selection(t, true); app_task_yield(&t->task); }
void app_editor_Paste(aqua_call *call) { edit_task *t = edit_task_new(call, aqua_void()); paste(t); app_task_yield(&t->task); }

/* Shortcut(logicalKey, modifiers): Shift 1, Control 2, Alt 4, Super 8. Declines during composition; never inserts. */
void app_editor_Shortcut(aqua_call *call) {
    editor *ed = self(call);
    aqua_str key = aqua_arg_str(call, 0);
    int32_t modifiers = aqua_arg_int(call, 1);
    if (ed->composing) { aqua_return_bool(call, false); return; }
    bool command = (modifiers & (2 | 8)) != 0, extend = (modifiers & 1) != 0;
    app_error e = {0};
    if (command && key.length == 1) {
        char lower = key.data[0] >= 'A' && key.data[0] <= 'Z' ? (char)(key.data[0] + 32) : key.data[0];
        edit_task *t;
        switch (lower) {
        case 'a': select_range(ed, 0, graphemes(ed), NULL); aqua_return_bool(call, true); return;
        case 'c': case 'x':
            t = edit_task_new(call, aqua_bool(true));
            copy_selection(t, lower == 'x');
            app_task_yield(&t->task);
            return;
        case 'v':
            t = edit_task_new(call, aqua_bool(true));
            paste(t);
            app_task_yield(&t->task);
            return;
        case 'z': if (extend) redo(ed); else undo(ed); aqua_return_bool(call, true); return;
        case 'y': redo(ed); aqua_return_bool(call, true); return;
        default: break;
        }
    }
    bool handled = true, ok = true;
    if (strcmp(key.data, "ArrowLeft") == 0) ok = move(ed, -1, command ? "word" : "grapheme", extend, &e);
    else if (strcmp(key.data, "ArrowRight") == 0) ok = move(ed, 1, command ? "word" : "grapheme", extend, &e);
    else if (strcmp(key.data, "Home") == 0) ok = move(ed, -1, command ? "document" : "line", extend, &e);
    else if (strcmp(key.data, "End") == 0) ok = move(ed, 1, command ? "document" : "line", extend, &e);
    else if (strcmp(key.data, "Backspace") == 0) ok = command ? delete_word(ed, -1, &e) : backspace(ed, &e);
    else if (strcmp(key.data, "Delete") == 0) ok = command ? delete_word(ed, 1, &e) : delete_forward(ed, &e);
    else handled = false;
    if (!ok) { app_fail(call, &e); return; }
    aqua_return_bool(call, handled);
}
