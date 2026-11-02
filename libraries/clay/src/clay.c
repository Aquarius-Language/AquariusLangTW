/* Clay: Nic Barker's Clay layout library for AquariusLang. A Clay.Layout is one Clay context with its own arena;
 * between layout.Begin() and layout.End() the Clay.* functions declare elements immediately, as Clay's CLAY(...) { }
 * blocks do: Clay.Element(declaration, children) or Clay.Open(declaration) ... Clay.Close(), and Clay.Text(text,
 * config). End() returns Clay's render commands as a UI draw list (the format drawUI and every UI library share);
 * elements with an "embed" function get another UI library's draw list spliced at their place. Text is measured by the
 * function given to Clay.SetMeasureText (normally the renderer's metrics, the same contract as Saturn's measurer) or a
 * fixed approximation that is the same on every target. */
#define CLAY_IMPLEMENTATION
#include "clay.h"
#include <aqua/aqua.h>

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { OP_RECT = 1, OP_BORDER = 2, OP_TEXT = 4, OP_IMAGE = 5, OP_CLIP_PUSH = 6, OP_CLIP_POP = 7, OP_TRANSFORM_PUSH = 8,
       OP_TRANSFORM_POP = 9, OP_MESH = 10, OP_TEXTURE = 11, OP_LIMIT = 16 };
static const int op_sizes[OP_LIMIT] = {0, 12, 16, 14, 14, 17, 8, 0, 6, 0, 5, 8, 9, 20, 1, 0};

typedef struct font { char *family; int weight; bool italic; } font;

typedef struct state state;

typedef struct layout {
    aqua_runtime *rt;
    state *owner;
    Clay_Context *clay;
    void *memory;
    bool in_frame, pointer_down;
    float pointer_x, pointer_y;
    font *fonts; size_t font_count, font_capacity;
    char **strings; size_t string_count, string_capacity;        /* this frame's texts and ids (Clay keeps pointers) */
    char **previous; size_t previous_count, previous_capacity;   /* the last frame's, which Clay's hover state still names */
    aqua_ref *refs; size_t ref_count, ref_capacity;              /* this frame's images and embed functions */
    char error[512];
    aqua_value self;
} layout;

struct state {
    aqua_runtime *rt;
    layout *current;
    aqua_ref measurer;
    char error[512];
};

static aqua_class *layout_class;

/* ---- Frame memory ---- */

static bool grow(void **items, size_t *capacity, size_t count, size_t size) {
    if (count < *capacity) return true;
    size_t next = *capacity ? *capacity * 2 : 32;
    void *grown = realloc(*items, next * size);
    if (!grown) return false;
    *items = grown;
    *capacity = next;
    return true;
}
static Clay_String keep_text(layout *l, const char *text, size_t length) {
    Clay_String s = {false, 0, ""};
    if (!grow((void **)&l->strings, &l->string_capacity, l->string_count, sizeof *l->strings)) return s;
    char *copy = malloc(length + 1);
    if (!copy) return s;
    memcpy(copy, text, length);
    copy[length] = 0;
    l->strings[l->string_count++] = copy;
    s.length = (int32_t)length;
    s.chars = copy;
    return s;
}
static intptr_t keep_value(layout *l, aqua_value v) {
    if (!grow((void **)&l->refs, &l->ref_capacity, l->ref_count, sizeof *l->refs)) return 0;
    l->refs[l->ref_count++] = aqua_retain(l->rt, v);
    return (intptr_t)l->ref_count;   /* 1-based, so 0 means none */
}
/* Starts a frame's memory: the last frame's strings are freed, this frame's become the last frame's. */
static void clear_frame(layout *l) {
    for (size_t i = 0; i < l->previous_count; i++) free(l->previous[i]);
    char **swap = l->previous;
    size_t capacity = l->previous_capacity;
    l->previous = l->strings; l->previous_count = l->string_count; l->previous_capacity = l->string_capacity;
    l->strings = swap; l->string_count = 0; l->string_capacity = capacity;
    for (size_t i = 0; i < l->ref_count; i++) aqua_release(l->rt, l->refs[i]);
    l->ref_count = 0;
}

/* ---- Text measuring ---- */

static uint32_t next_code_point(const char *s, size_t length, size_t *i) {
    uint32_t c = (unsigned char)s[*i];
    size_t extra = c < 0x80 ? 0 : c < 0xE0 ? 1 : c < 0xF0 ? 2 : 3;
    if (c >= 0x80) c &= extra == 1 ? 0x1F : extra == 2 ? 0x0F : 0x07;
    (*i)++;
    for (size_t k = 0; k < extra && *i < length; k++, (*i)++) c = (c << 6) | ((unsigned char)s[*i] & 0x3F);
    return c;
}
static bool wide(uint32_t c) {
    return (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xAC00 && c <= 0xD7AF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F) ||
           (c >= 0xFF00 && c <= 0xFFEF) || (c >= 0x20000 && c <= 0x3FFFF);
}

static Clay_Dimensions measure(Clay_StringSlice text, Clay_TextElementConfig *config, void *data) {
    layout *l = data;
    state *s = l->owner;
    float size = config->fontSize ? config->fontSize : 16;
    float line = config->lineHeight ? config->lineHeight : 0;
    const font *f = config->fontId < l->font_count ? &l->fonts[config->fontId] : NULL;
    float width = 0, ascent = size * 0.8f, descent = size * 0.2f;
    if (s->measurer) {
        aqua_value arguments[5] = {aqua_text(l->rt, text.chars, (size_t)text.length), aqua_cstring(l->rt, f && f->family ? f->family : ""),
            aqua_double(size), aqua_int(f ? f->weight : 400), aqua_bool(f && f->italic)};
        aqua_value result;
        if (!aqua_invoke(l->rt, aqua_ref_value(l->rt, s->measurer), arguments, 5, &result)) {
            if (!l->error[0]) {
                char *shown = aqua_display(l->rt, result);
                snprintf(l->error, sizeof l->error, "%s", shown ? shown : "Clay: the text measurer failed.");
                aqua_free(shown);
            }
        } else if (aqua_is_numeric(result)) {
            width = (float)aqua_number_of(result);
        } else if (result.type == AQUA_ARRAY && aqua_array_length(l->rt, result) >= 1) {
            aqua_value w = aqua_array_get(l->rt, result, 0), a = aqua_array_get(l->rt, result, 1), d = aqua_array_get(l->rt, result, 2);
            if (aqua_is_numeric(w)) width = (float)aqua_number_of(w);
            if (aqua_is_numeric(a)) ascent = (float)aqua_number_of(a);
            if (aqua_is_numeric(d)) descent = (float)aqua_number_of(d);
        }
    } else {
        for (size_t i = 0; i < (size_t)text.length;) {
            uint32_t c = next_code_point(text.chars, (size_t)text.length, &i);
            width += wide(c) ? 1.0f : c == ' ' ? 0.3f : (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ? 0.62f : 0.52f;
        }
        width *= size * (f && f->weight >= 600 ? 1.05f : 1.0f);
    }
    width += (float)config->letterSpacing * (float)text.length;
    Clay_Dimensions d = {width, line > 0 ? line : ascent + descent};
    return d;
}

static void clay_error(Clay_ErrorData error) {
    layout *l = (layout *)error.userData;
    if (l && !l->error[0]) snprintf(l->error, sizeof l->error, "Clay: %.*s / Clay 錯誤。", (int)error.errorText.length, error.errorText.chars);
}

/* ---- Values ---- */

static int hex(char c) { return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; }

/* Colors: "#rgb[a]", "#rrggbb[aa]", [r, g, b(, a)] with 0..255 components, or 0xAARRGGBB. */
static bool color_of(aqua_runtime *rt, aqua_value v, Clay_Color *out) {
    if (aqua_is_nullish(v)) { *out = (Clay_Color){0, 0, 0, 0}; return true; }
    if (aqua_is_numeric(v)) {
        uint32_t argb = (uint32_t)(int64_t)aqua_number_of(v);
        *out = (Clay_Color){(float)((argb >> 16) & 255), (float)((argb >> 8) & 255), (float)(argb & 255), (float)(argb >> 24)};
        return true;
    }
    if (v.type == AQUA_ARRAY) {
        uint32_t n = aqua_array_length(rt, v);
        float c[4] = {0, 0, 0, 255};
        if ((n != 3 && n != 4) || !aqua_array_read_f32(rt, v, 0, n, c)) return false;
        *out = (Clay_Color){c[0], c[1], c[2], c[3]};
        return true;
    }
    if (v.type != AQUA_STRING) return false;
    char *s = aqua_text_dup(rt, v, NULL);
    static const struct { const char *name; Clay_Color color; } names[] = {
        {"white", {255, 255, 255, 255}}, {"black", {0, 0, 0, 255}}, {"transparent", {0, 0, 0, 0}}, {"red", {255, 0, 0, 255}},
        {"green", {0, 128, 0, 255}}, {"blue", {0, 0, 255, 255}}, {"gray", {128, 128, 128, 255}}, {"yellow", {255, 255, 0, 255}}};
    for (size_t i = 0; i < sizeof names / sizeof *names; i++)
        if (!strcmp(s, names[i].name)) { *out = names[i].color; aqua_free(s); return true; }
    size_t n = strlen(s);
    bool ok = s[0] == '#' && (n == 4 || n == 5 || n == 7 || n == 9);
    int d[8] = {0};
    for (size_t i = 1; ok && i < n; i++) ok = (d[i - 1] = hex(s[i])) >= 0;
    if (ok && n <= 5) *out = (Clay_Color){(float)(d[0] * 17), (float)(d[1] * 17), (float)(d[2] * 17), n == 5 ? (float)(d[3] * 17) : 255};
    else if (ok) *out = (Clay_Color){(float)(d[0] * 16 + d[1]), (float)(d[2] * 16 + d[3]), (float)(d[4] * 16 + d[5]), n == 9 ? (float)(d[6] * 16 + d[7]) : 255};
    aqua_free(s);
    return ok;
}

static bool field(aqua_runtime *rt, aqua_value h, const char *name, aqua_value *out) {
    if (h.type != AQUA_HASH && h.type != AQUA_STRUCT) return false;
    return aqua_hash_field(rt, h, name, out) && !aqua_is_nullish(*out);
}
static bool text_is(aqua_runtime *rt, aqua_value v, const char *name) {
    if (v.type != AQUA_STRING) return false;
    char *s = aqua_text_dup(rt, v, NULL);
    bool same = strcmp(s, name) == 0;
    aqua_free(s);
    return same;
}

/* A size along one axis: a number (fixed pixels), "grow", "fit", "50%", or ["grow" | "fit", min, max] / ["fixed", n]
   / ["percent", 0..1] (Clay.Grow, Clay.Fit, Clay.Fixed, Clay.Percent build these). */
static bool sizing_of(aqua_runtime *rt, aqua_value v, Clay_SizingAxis *out) {
    Clay_SizingAxis a = {0};
    a.size.minMax.max = 3.4e38f;
    if (aqua_is_numeric(v)) {
        a.type = CLAY__SIZING_TYPE_FIXED;
        a.size.minMax.min = a.size.minMax.max = (float)aqua_number_of(v);
    } else if (v.type == AQUA_STRING) {
        char *s = aqua_text_dup(rt, v, NULL);
        size_t n = strlen(s);
        bool ok = true;
        if (!strcmp(s, "grow")) a.type = CLAY__SIZING_TYPE_GROW;
        else if (!strcmp(s, "fit")) a.type = CLAY__SIZING_TYPE_FIT;
        else if (n > 1 && s[n - 1] == '%') { a.type = CLAY__SIZING_TYPE_PERCENT; a.size.percent = strtof(s, NULL) / 100.0f; }
        else ok = false;
        aqua_free(s);
        if (!ok) return false;
    } else if (v.type == AQUA_ARRAY && aqua_array_length(rt, v) >= 1) {
        aqua_value kind = aqua_array_get(rt, v, 0), first = aqua_array_get(rt, v, 1), second = aqua_array_get(rt, v, 2);
        if (text_is(rt, kind, "grow")) a.type = CLAY__SIZING_TYPE_GROW;
        else if (text_is(rt, kind, "fit")) a.type = CLAY__SIZING_TYPE_FIT;
        else if (text_is(rt, kind, "fixed")) a.type = CLAY__SIZING_TYPE_FIXED;
        else if (text_is(rt, kind, "percent")) a.type = CLAY__SIZING_TYPE_PERCENT;
        else return false;
        if (a.type == CLAY__SIZING_TYPE_PERCENT) a.size.percent = aqua_is_numeric(first) ? (float)aqua_number_of(first) : 0;
        else if (a.type == CLAY__SIZING_TYPE_FIXED) a.size.minMax.min = a.size.minMax.max = aqua_is_numeric(first) ? (float)aqua_number_of(first) : 0;
        else {
            if (aqua_is_numeric(first)) a.size.minMax.min = (float)aqua_number_of(first);
            if (aqua_is_numeric(second)) a.size.minMax.max = (float)aqua_number_of(second);
        }
    } else return false;
    *out = a;
    return true;
}

/* Four sides in CSS order: n, [vertical, horizontal], [top, horizontal, bottom] or [top, right, bottom, left]. */
static bool sides_of(aqua_runtime *rt, aqua_value v, float *top, float *right, float *bottom, float *left) {
    if (aqua_is_numeric(v)) { *top = *right = *bottom = *left = (float)aqua_number_of(v); return true; }
    float s[4];
    uint32_t n = v.type == AQUA_ARRAY ? aqua_array_length(rt, v) : 0;
    if (n < 2 || n > 4 || !aqua_array_read_f32(rt, v, 0, n, s)) return false;
    *top = s[0]; *right = s[1]; *bottom = n > 2 ? s[2] : s[0]; *left = n > 3 ? s[3] : s[1];
    return true;
}

static int font_id(layout *l, const char *family, int weight, bool italic) {
    for (size_t i = 0; i < l->font_count; i++)
        if (!strcmp(l->fonts[i].family, family) && l->fonts[i].weight == weight && l->fonts[i].italic == italic) return (int)i;
    if (l->font_count >= 65535 || !grow((void **)&l->fonts, &l->font_capacity, l->font_count, sizeof *l->fonts)) return 0;
    size_t n = strlen(family);
    char *copy = malloc(n + 1);
    memcpy(copy, family, n + 1);
    l->fonts[l->font_count] = (font){copy, weight, italic};
    return (int)l->font_count++;
}

/* ---- Calls ---- */

static state *state_of(aqua_call *call) { return aqua_state(call); }
static layout *current(aqua_call *call) {
    state *s = state_of(call);
    if (!s->current || !s->current->in_frame) {
        aqua_fail(call, "Clay: declare elements between layout.Begin() and layout.End() / 請在 Begin 與 End 之間宣告元素。");
        return NULL;
    }
    Clay_SetCurrentContext(s->current->clay);
    return s->current;
}
/* A script error forwarded from a callback, without the "ERROR: " the runtime puts in front of messages. */
static void fail_with(aqua_call *call, aqua_runtime *rt, aqua_value error, const char *fallback) {
    char *shown = aqua_display(rt, error);
    const char *text = shown ? shown : fallback;
    while (!strncmp(text, "ERROR: ", 7)) text += 7;
    aqua_fail(call, "%s", text);
    aqua_free(shown);
}
static bool report(aqua_call *call, layout *l) {
    if (!l->error[0]) return false;
    aqua_fail(call, "%s", l->error);
    l->error[0] = 0;
    return true;
}
static bool fail_key(aqua_call *call, const char *key) {
    aqua_fail(call, "Clay: unsupported value for %s / 不支援的設定值。", key);
    return false;
}

/* The declaration of an element from a hash (see README.md for the keys). */
static bool declaration_of(aqua_call *call, layout *l, aqua_value h, Clay_ElementDeclaration *d) {
    aqua_runtime *rt = l->rt;
    memset(d, 0, sizeof *d);
    aqua_value v;
    if (h.type == AQUA_STRING) {
        char *id = aqua_text_dup(rt, h, NULL);
        d->id = Clay__HashString(keep_text(l, id, strlen(id)), 0, 0);
        aqua_free(id);
        return true;
    }
    if (aqua_is_nullish(h)) return true;
    if (h.type != AQUA_HASH && h.type != AQUA_STRUCT) { aqua_fail(call, "Clay: a declaration is a hash or an id / 宣告必須是雜湊或識別碼。"); return false; }
    if (field(rt, h, "id", &v)) {
        char *id = v.type == AQUA_STRING ? aqua_text_dup(rt, v, NULL) : aqua_display(rt, v);
        d->id = Clay__HashString(keep_text(l, id, strlen(id)), 0, 0);
        aqua_free(id);
    }
    d->layout.sizing.width.size.minMax.max = d->layout.sizing.height.size.minMax.max = 3.4e38f;
    if (field(rt, h, "width", &v) && !sizing_of(rt, v, &d->layout.sizing.width)) return fail_key(call, "width");
    if (field(rt, h, "height", &v) && !sizing_of(rt, v, &d->layout.sizing.height)) return fail_key(call, "height");
    if (field(rt, h, "padding", &v)) {
        float t, r, b, lft;
        if (!sides_of(rt, v, &t, &r, &b, &lft)) return fail_key(call, "padding");
        d->layout.padding = (Clay_Padding){(uint16_t)lft, (uint16_t)r, (uint16_t)t, (uint16_t)b};
    }
    if (field(rt, h, "gap", &v)) { if (!aqua_is_numeric(v)) return fail_key(call, "gap"); d->layout.childGap = (uint16_t)aqua_number_of(v); }
    if (field(rt, h, "direction", &v)) {
        if (text_is(rt, v, "column") || text_is(rt, v, "topToBottom")) d->layout.layoutDirection = CLAY_TOP_TO_BOTTOM;
        else if (!text_is(rt, v, "row") && !text_is(rt, v, "leftToRight")) return fail_key(call, "direction");
    }
    if (field(rt, h, "alignX", &v)) {
        if (text_is(rt, v, "center")) d->layout.childAlignment.x = CLAY_ALIGN_X_CENTER;
        else if (text_is(rt, v, "right")) d->layout.childAlignment.x = CLAY_ALIGN_X_RIGHT;
        else if (!text_is(rt, v, "left")) return fail_key(call, "alignX");
    }
    if (field(rt, h, "alignY", &v)) {
        if (text_is(rt, v, "center")) d->layout.childAlignment.y = CLAY_ALIGN_Y_CENTER;
        else if (text_is(rt, v, "bottom")) d->layout.childAlignment.y = CLAY_ALIGN_Y_BOTTOM;
        else if (!text_is(rt, v, "top")) return fail_key(call, "alignY");
    }
    if (field(rt, h, "background", &v) && !color_of(rt, v, &d->backgroundColor)) return fail_key(call, "background");
    if (field(rt, h, "radius", &v)) {
        float tl, tr, br, bl;
        if (!sides_of(rt, v, &tl, &tr, &br, &bl)) return fail_key(call, "radius");
        d->cornerRadius = (Clay_CornerRadius){tl, tr, bl, br};
    }
    if (field(rt, h, "aspectRatio", &v)) { if (!aqua_is_numeric(v)) return fail_key(call, "aspectRatio"); d->aspectRatio.aspectRatio = (float)aqua_number_of(v); }
    if (field(rt, h, "image", &v)) d->image.imageData = (void *)keep_value(l, v);
    if (field(rt, h, "embed", &v)) {
        if (!aqua_is_callable(v)) return fail_key(call, "embed");
        d->custom.customData = (void *)keep_value(l, v);
    }
    if (field(rt, h, "scroll", &v)) {
        bool on = v.type == AQUA_BOOL && v.number != 0;
        d->clip.horizontal = d->clip.vertical = on;
        if (text_is(rt, v, "vertical")) d->clip.vertical = true;
        if (text_is(rt, v, "horizontal")) d->clip.horizontal = true;
        /* The scroll position of the open element: open() fills childOffset once the element is open. */
    }
    if (field(rt, h, "clip", &v)) {
        aqua_value x, y;
        if (v.type == AQUA_BOOL) d->clip.horizontal = d->clip.vertical = v.number != 0;
        else {
            d->clip.horizontal = field(rt, v, "horizontal", &x) && x.type == AQUA_BOOL && x.number != 0;
            d->clip.vertical = field(rt, v, "vertical", &y) && y.type == AQUA_BOOL && y.number != 0;
        }
    }
    if (field(rt, h, "border", &v)) {
        aqua_value c, w;
        if (field(rt, v, "color", &c) && !color_of(rt, c, &d->border.color)) return fail_key(call, "border.color");
        if (field(rt, v, "width", &w)) {
            float t, r, b, lft;
            if (!sides_of(rt, w, &t, &r, &b, &lft)) return fail_key(call, "border.width");
            d->border.width = (Clay_BorderWidth){(uint16_t)lft, (uint16_t)r, (uint16_t)t, (uint16_t)b, 0};
        }
        if (field(rt, v, "between", &w) && aqua_is_numeric(w)) d->border.width.betweenChildren = (uint16_t)aqua_number_of(w);
    }
    if (field(rt, h, "floating", &v)) {
        Clay_FloatingElementConfig *f = &d->floating;
        aqua_value e;
        f->attachTo = CLAY_ATTACH_TO_PARENT;
        if (field(rt, v, "attachTo", &e)) {
            if (text_is(rt, e, "root")) f->attachTo = CLAY_ATTACH_TO_ROOT;
            else if (text_is(rt, e, "element")) f->attachTo = CLAY_ATTACH_TO_ELEMENT_WITH_ID;
            else if (!text_is(rt, e, "parent")) return fail_key(call, "floating.attachTo");
        }
        if (field(rt, v, "parentId", &e) && e.type == AQUA_STRING) {
            char *id = aqua_text_dup(rt, e, NULL);
            f->parentId = Clay__HashString(keep_text(l, id, strlen(id)), 0, 0).id;
            f->attachTo = CLAY_ATTACH_TO_ELEMENT_WITH_ID;
            aqua_free(id);
        }
        float xy[2];
        if (field(rt, v, "offset", &e) && (e.type != AQUA_ARRAY || aqua_array_length(rt, e) != 2 || !aqua_array_read_f32(rt, e, 0, 2, xy))) return fail_key(call, "floating.offset");
        if (field(rt, v, "offset", &e)) f->offset = (Clay_Vector2){xy[0], xy[1]};
        if (field(rt, v, "expand", &e) && e.type == AQUA_ARRAY && aqua_array_length(rt, e) == 2 && aqua_array_read_f32(rt, e, 0, 2, xy))
            f->expand = (Clay_Dimensions){xy[0], xy[1]};
        if (field(rt, v, "zIndex", &e) && aqua_is_numeric(e)) f->zIndex = (int16_t)aqua_number_of(e);
        static const char *const points[] = {"leftTop", "leftCenter", "leftBottom", "centerTop", "centerCenter", "centerBottom",
            "rightTop", "rightCenter", "rightBottom"};
        for (int k = 0; k < 9; k++) {
            if (field(rt, v, "element", &e) && text_is(rt, e, points[k])) f->attachPoints.element = (Clay_FloatingAttachPointType)k;
            if (field(rt, v, "parent", &e) && text_is(rt, e, points[k])) f->attachPoints.parent = (Clay_FloatingAttachPointType)k;
        }
        if (field(rt, v, "pointerCapture", &e) && text_is(rt, e, "passthrough")) f->pointerCaptureMode = CLAY_POINTER_CAPTURE_MODE_PASSTHROUGH;
        if (field(rt, v, "clipTo", &e) && text_is(rt, e, "parent")) f->clipTo = CLAY_CLIP_TO_ATTACHED_PARENT;
    }
    return true;
}

/* Opens an element with its declaration (checked before anything is opened, so a bad one leaves Clay's tree intact). */
static bool open(aqua_call *call, layout *l, aqua_value declaration) {
    Clay_ElementDeclaration d;
    if (!declaration_of(call, l, declaration, &d)) return false;
    Clay__OpenElement();
    if (d.clip.horizontal || d.clip.vertical) d.clip.childOffset = Clay_GetScrollOffset();
    Clay__ConfigureOpenElement(d);
    return true;
}
static void module_open(aqua_call *call) {
    layout *l = current(call);
    if (!l || !open(call, l, aqua_arg(call, 0))) return;
    report(call, l);
}
static void module_close(aqua_call *call) {
    layout *l = current(call);
    if (!l) return;
    Clay__CloseElement();
    report(call, l);
}
/* Element(declaration | children): opens the element, runs children() to declare its children, and closes it. */
static void module_element(aqua_call *call) {
    layout *l = current(call);
    if (!l) return;
    if (!open(call, l, aqua_arg(call, 0))) return;
    if (aqua_argc(call) > 1 && !aqua_is_nullish(aqua_arg(call, 1))) {
        aqua_value result;
        if (!aqua_invoke(l->rt, aqua_arg(call, 1), NULL, 0, &result)) {
            Clay_SetCurrentContext(l->clay);
            Clay__CloseElement();
            fail_with(call, l->rt, result, "Clay.Element: the children function failed.");
            return;
        }
        Clay_SetCurrentContext(l->clay);
    }
    Clay__CloseElement();
    report(call, l);
}
/* Text(text | config): config {"fontSize", "fontFamily", "fontWeight", "fontStyle", "color", "lineHeight",
   "letterSpacing", "wrap": "words" | "newlines" | "none", "align": "left" | "center" | "right"}. */
static void module_text(aqua_call *call) {
    layout *l = current(call);
    if (!l) return;
    aqua_runtime *rt = l->rt;
    Clay_TextElementConfig config = {0};
    config.fontSize = 16;
    config.textColor = (Clay_Color){255, 255, 255, 255};
    aqua_value h = aqua_argc(call) > 1 ? aqua_arg(call, 1) : aqua_null(), v;
    char *family = NULL;
    int weight = 400;
    bool italic = false;
    if (field(rt, h, "fontSize", &v) && aqua_is_numeric(v)) config.fontSize = (uint16_t)aqua_number_of(v);
    if (field(rt, h, "fontFamily", &v) && v.type == AQUA_STRING) family = aqua_text_dup(rt, v, NULL);
    if (field(rt, h, "fontWeight", &v)) weight = aqua_is_numeric(v) ? (int)aqua_number_of(v) : text_is(rt, v, "bold") ? 700 : 400;
    if (field(rt, h, "fontStyle", &v)) italic = text_is(rt, v, "italic");
    if (field(rt, h, "color", &v) && !color_of(rt, v, &config.textColor)) { aqua_free(family); fail_key(call, "color"); return; }
    if (field(rt, h, "lineHeight", &v) && aqua_is_numeric(v)) config.lineHeight = (uint16_t)aqua_number_of(v);
    if (field(rt, h, "letterSpacing", &v) && aqua_is_numeric(v)) config.letterSpacing = (uint16_t)aqua_number_of(v);
    if (field(rt, h, "wrap", &v)) config.wrapMode = text_is(rt, v, "none") ? CLAY_TEXT_WRAP_NONE : text_is(rt, v, "newlines") ? CLAY_TEXT_WRAP_NEWLINES : CLAY_TEXT_WRAP_WORDS;
    if (field(rt, h, "align", &v)) config.textAlignment = text_is(rt, v, "center") ? CLAY_TEXT_ALIGN_CENTER : text_is(rt, v, "right") ? CLAY_TEXT_ALIGN_RIGHT : CLAY_TEXT_ALIGN_LEFT;
    config.fontId = (uint16_t)font_id(l, family ? family : "", weight, italic);
    aqua_free(family);
    aqua_value text = aqua_arg(call, 0);
    size_t length;
    char *utf8 = text.type == AQUA_STRING ? aqua_text_dup(rt, text, &length) : aqua_display(rt, text);
    if (text.type != AQUA_STRING) length = strlen(utf8);
    Clay_String s = keep_text(l, utf8, length);
    aqua_free(utf8);
    Clay__OpenTextElement(s, Clay__StoreTextElementConfig(config));
    report(call, l);
}
static Clay_ElementId id_arg(aqua_call *call, layout *l) {
    aqua_str id = aqua_arg_str(call, 0);
    return Clay__HashString(keep_text(l, id.data, id.length), 0, 0);
}
static void module_hovered(aqua_call *call) { layout *l = current(call); if (l) aqua_return_bool(call, Clay_Hovered()); }
static void module_pointer_over(aqua_call *call) { layout *l = current(call); if (l) aqua_return_bool(call, Clay_PointerOver(id_arg(call, l))); }
static void module_scroll_offset(aqua_call *call) {
    layout *l = current(call);
    if (!l) return;
    Clay_Vector2 o = Clay_GetScrollOffset();
    double xy[2] = {o.x, o.y};
    aqua_return(call, aqua_array_from_f64(l->rt, xy, 2, AQUA_DOUBLE));
}
/* Sizing helpers: ["grow", min, max], ["fit", min, max], ["fixed", n], ["percent", 0..1]. */
static void sizing(aqua_call *call, const char *kind) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value list = aqua_array_new(rt, 0);
    aqua_array_push(rt, list, aqua_cstring(rt, kind));
    for (uint32_t i = 0; i < aqua_argc(call); i++) aqua_array_push(rt, list, aqua_arg(call, i));
    aqua_return(call, list);
}
static void module_grow(aqua_call *call) { sizing(call, "grow"); }
static void module_fit(aqua_call *call) { sizing(call, "fit"); }
static void module_fixed(aqua_call *call) { sizing(call, "fixed"); }
static void module_percent(aqua_call *call) { sizing(call, "percent"); }
static void module_set_measure(aqua_call *call) {
    state *s = state_of(call);
    aqua_release(s->rt, s->measurer);
    s->measurer = aqua_argc(call) > 0 && !aqua_is_nullish(aqua_arg(call, 0)) ? aqua_retain(s->rt, aqua_arg(call, 0)) : 0;
    for (layout *l = s->current; l; l = NULL) { Clay_SetCurrentContext(l->clay); Clay_ResetMeasureTextCache(); }
}

/* ---- Layout objects ---- */

static void layout_finalize(aqua_runtime *rt, void *data) {
    layout *l = data;
    (void)rt;
    if (l->owner && l->owner->current == l) l->owner->current = NULL;
    clear_frame(l);
    clear_frame(l);
    free(l->strings);
    free(l->previous);
    free(l->refs);
    for (size_t i = 0; i < l->font_count; i++) free(l->fonts[i].family);
    free(l->fonts);
    free(l->memory);
    free(l);
}

/* Clay.Create(width, height | maxElements) */
static void module_create(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    state *s = state_of(call);
    int32_t elements = aqua_argc(call) > 2 && aqua_is_numeric(aqua_arg(call, 2)) ? (int32_t)aqua_number_of(aqua_arg(call, 2)) : 8192;
    if (elements < 64 || elements > 1000000) { aqua_fail(call, "Clay.Create: maxElements is 64..1000000."); return; }
    layout *l = calloc(1, sizeof *l);
    if (!l) { aqua_fail(call, "Clay.Create: out of memory."); return; }
    l->rt = rt;
    l->owner = s;
    /* The element count of a new context comes from the current one, or the defaults when there is none. */
    Clay_Context *previous = Clay_GetCurrentContext();
    Clay_SetCurrentContext(NULL);
    Clay_SetMaxElementCount(elements);
    uint32_t bytes = Clay_MinMemorySize();
    l->memory = malloc(bytes);
    if (!l->memory) { free(l); aqua_fail(call, "Clay.Create: out of memory."); return; }
    Clay_Arena arena = Clay_CreateArenaWithCapacityAndMemory(bytes, l->memory);
    Clay_ErrorHandler handler = {clay_error, (void *)l};
    l->clay = Clay_Initialize(arena, (Clay_Dimensions){(float)aqua_arg_number(call, 0), (float)aqua_arg_number(call, 1)}, handler);
    Clay_SetMeasureTextFunction(measure, l);
    if (previous) Clay_SetCurrentContext(previous);
    l->self = aqua_object_new(rt, layout_class, l);
    if (l->self.type == AQUA_ERROR) { aqua_return(call, l->self); layout_finalize(rt, l); return; }
    aqua_return(call, l->self);
}

static layout *self_layout(aqua_call *call) {
    layout *l = aqua_self(call);
    Clay_SetCurrentContext(l->clay);
    return l;
}
static void layout_set_dimensions(aqua_call *call) {
    layout *l = self_layout(call);
    Clay_SetLayoutDimensions((Clay_Dimensions){(float)aqua_arg_number(call, 0), (float)aqua_arg_number(call, 1)});
    report(call, l);
}
static void layout_set_pointer(aqua_call *call) {
    layout *l = self_layout(call);
    l->pointer_x = (float)aqua_arg_number(call, 0);
    l->pointer_y = (float)aqua_arg_number(call, 1);
    l->pointer_down = aqua_arg_bool(call, 2);
    Clay_SetPointerState((Clay_Vector2){l->pointer_x, l->pointer_y}, l->pointer_down);
}
/* Scroll(dx, dy, seconds | dragScrolling) moves the scroll containers under the pointer. */
static void layout_scroll(aqua_call *call) {
    self_layout(call);
    bool drag = aqua_argc(call) > 3 && aqua_arg(call, 3).type == AQUA_BOOL && aqua_arg(call, 3).number != 0;
    Clay_UpdateScrollContainers(drag, (Clay_Vector2){(float)aqua_arg_number(call, 0), (float)aqua_arg_number(call, 1)}, (float)aqua_arg_number(call, 2));
}
/* Feed(event): Interaction events (press, release, move, drag, scroll) update the pointer and scrolling. Returns whether
   the pointer is over one of the layout's elements. */
static aqua_value event_field(aqua_runtime *rt, aqua_value e, const char *name) {
    aqua_value v = aqua_null();
    if (e.type == AQUA_NATIVE) v = aqua_object_member(rt, e, name);
    else if (e.type == AQUA_HASH || e.type == AQUA_STRUCT) aqua_hash_field(rt, e, name, &v);
    return v;
}
static void layout_feed(aqua_call *call) {
    layout *l = self_layout(call);
    aqua_runtime *rt = l->rt;
    aqua_value e = aqua_arg(call, 0), type = event_field(rt, e, "type"), x = event_field(rt, e, "x"), y = event_field(rt, e, "y");
    if (aqua_is_numeric(x) && aqua_is_numeric(y)) { l->pointer_x = (float)aqua_number_of(x); l->pointer_y = (float)aqua_number_of(y); }
    aqua_value button = event_field(rt, e, "button");
    bool primary = !aqua_is_numeric(button) || aqua_number_of(button) == 0;
    if (text_is(rt, type, "press") && primary) l->pointer_down = true;
    if (text_is(rt, type, "release") && primary) l->pointer_down = false;
    Clay_SetPointerState((Clay_Vector2){l->pointer_x, l->pointer_y}, l->pointer_down);
    if (text_is(rt, type, "scroll")) {
        aqua_value dx = event_field(rt, e, "dx"), dy = event_field(rt, e, "dy");
        Clay_UpdateScrollContainers(false, (Clay_Vector2){aqua_is_numeric(dx) ? -(float)aqua_number_of(dx) : 0, aqua_is_numeric(dy) ? -(float)aqua_number_of(dy) : 0}, 1.0f / 60.0f);
    }
    aqua_return_bool(call, Clay_GetPointerOverIds().length > 0);
}
static void layout_begin(aqua_call *call) {
    layout *l = self_layout(call);
    if (l->in_frame) { Clay_EndLayout(); }
    clear_frame(l);
    l->error[0] = 0;
    Clay_BeginLayout();
    l->in_frame = true;
    l->owner->current = l;
    report(call, l);
}

/* ---- End: render commands as a UI draw list ---- */

typedef struct builder {
    aqua_runtime *rt;
    double *commands; size_t count, capacity;
    aqua_value strings, images, vertices, indices;
    uint32_t vertex_count, index_count;
    bool failed;
} builder;

static void op(builder *b, int code, int n, const double *args) {
    while (!b->failed && b->count + (size_t)n + 1 > b->capacity)
        if (!grow((void **)&b->commands, &b->capacity, b->capacity, sizeof *b->commands)) b->failed = true;
    if (b->failed) return;
    b->commands[b->count++] = code;
    for (int i = 0; i < n; i++) b->commands[b->count++] = args[i];
}
#define OP(b, code, ...) do { const double args_[] = {__VA_ARGS__}; op(b, code, (int)(sizeof args_ / sizeof *args_), args_); } while (0)
#define OP0(b, code) op(b, code, 0, NULL)

/* Splices a draw list from an embed function, renumbering its strings, images and meshes. */
static bool splice(builder *b, aqua_value list) {
    aqua_runtime *rt = b->rt;
    aqua_value commands, strings, images, vertices, indices;
    if (list.type != AQUA_HASH || !aqua_hash_field(rt, list, "commands", &commands) || commands.type != AQUA_ARRAY) return false;
    uint32_t string_base = aqua_array_length(rt, b->strings), image_base = aqua_array_length(rt, b->images);
    uint32_t vertex_base = b->vertex_count / 8, index_base = b->index_count;
    if (aqua_hash_field(rt, list, "strings", &strings) && strings.type == AQUA_ARRAY)
        for (uint32_t i = 0, n = aqua_array_length(rt, strings); i < n; i++) aqua_array_push(rt, b->strings, aqua_array_get(rt, strings, i));
    if (aqua_hash_field(rt, list, "images", &images) && images.type == AQUA_ARRAY)
        for (uint32_t i = 0, n = aqua_array_length(rt, images); i < n; i++) aqua_array_push(rt, b->images, aqua_array_get(rt, images, i));
    if (aqua_hash_field(rt, list, "vertices", &vertices) && vertices.type == AQUA_ARRAY)
        for (uint32_t i = 0, n = aqua_array_length(rt, vertices); i < n; i++, b->vertex_count++) aqua_array_push(rt, b->vertices, aqua_array_get(rt, vertices, i));
    if (aqua_hash_field(rt, list, "indices", &indices) && indices.type == AQUA_ARRAY)
        for (uint32_t i = 0, n = aqua_array_length(rt, indices); i < n; i++, b->index_count++) aqua_array_push(rt, b->indices, aqua_array_get(rt, indices, i));
    uint32_t count = aqua_array_length(rt, commands);
    double *in = malloc(((size_t)count + 1) * sizeof *in);
    if (!in || (count && !aqua_array_read_f64(rt, commands, 0, count, in))) { free(in); return false; }
    for (uint32_t at = 0; at < count;) {
        int code = (int)in[at];
        if (code <= 0 || code >= OP_LIMIT || at + 1 + (uint32_t)op_sizes[code] > count) { free(in); return false; }
        double *a = in + at + 1;
        if (code == OP_TEXT) { a[4] += string_base; if (a[5] >= 0) a[5] += string_base; }
        if (code == OP_IMAGE) a[4] += image_base;
        if (code == OP_TEXTURE && a[3] >= 0) a[3] += image_base;
        if (code == OP_MESH) { a[0] += vertex_base; a[2] += index_base; if (a[4] <= -2) a[4] -= image_base; }
        op(b, code, op_sizes[code], a);
        at += 1 + (uint32_t)op_sizes[code];
    }
    free(in);
    return !b->failed;
}

static double add_string(builder *b, const char *text, size_t length) {
    aqua_array_push(b->rt, b->strings, aqua_text(b->rt, text, length));
    return aqua_array_length(b->rt, b->strings) - 1;
}

static void layout_end(aqua_call *call) {
    layout *l = self_layout(call);
    if (!l->in_frame) { aqua_fail(call, "Clay.Layout.End: call Begin first / 請先呼叫 Begin。"); return; }
    Clay_RenderCommandArray commands = Clay_EndLayout();
    l->in_frame = false;
    if (report(call, l)) return;
    aqua_runtime *rt = l->rt;
    builder b = {rt};
    b.strings = aqua_array_new(rt, 0);
    b.images = aqua_array_new(rt, 0);
    b.vertices = aqua_array_new(rt, 0);
    b.indices = aqua_array_new(rt, 0);
    for (int32_t i = 0; i < commands.length && !b.failed; i++) {
        Clay_RenderCommand *c = Clay_RenderCommandArray_Get(&commands, i);
        Clay_BoundingBox box = c->boundingBox;
        switch (c->commandType) {
        case CLAY_RENDER_COMMAND_TYPE_RECTANGLE: {
            Clay_RectangleRenderData *r = &c->renderData.rectangle;
            Clay_CornerRadius k = r->cornerRadius;
            OP(&b, OP_RECT, box.x, box.y, box.width, box.height, k.topLeft, k.topRight, k.bottomRight, k.bottomLeft,
               r->backgroundColor.r, r->backgroundColor.g, r->backgroundColor.b, r->backgroundColor.a);
            break;
        }
        case CLAY_RENDER_COMMAND_TYPE_BORDER: {
            Clay_BorderRenderData *r = &c->renderData.border;
            Clay_CornerRadius k = r->cornerRadius;
            OP(&b, OP_BORDER, box.x, box.y, box.width, box.height, k.topLeft, k.topRight, k.bottomRight, k.bottomLeft,
               r->width.left, r->width.top, r->width.right, r->width.bottom, r->color.r, r->color.g, r->color.b, r->color.a);
            break;
        }
        case CLAY_RENDER_COMMAND_TYPE_TEXT: {
            Clay_TextRenderData *t = &c->renderData.text;
            const font *f = t->fontId < l->font_count ? &l->fonts[t->fontId] : NULL;
            double family = f && f->family[0] ? add_string(&b, f->family, strlen(f->family)) : -1;
            double size = t->fontSize ? t->fontSize : 16;
            /* Clay's line box: the glyph box (size * 1.25 high in the measurer's approximation) centered in it. */
            double glyph = fmin(box.height, size * 1.0), top = box.y + (box.height - glyph) / 2;
            OP(&b, OP_TEXT, box.x, top, box.width, glyph, add_string(&b, t->stringContents.chars, (size_t)t->stringContents.length), family,
               size, f ? f->weight : 400, f && f->italic ? 1 : 0, glyph * 0.8, t->textColor.r, t->textColor.g, t->textColor.b, t->textColor.a);
            break;
        }
        case CLAY_RENDER_COMMAND_TYPE_IMAGE: {
            Clay_ImageRenderData *r = &c->renderData.image;
            intptr_t index = (intptr_t)r->imageData;
            if (index <= 0 || (size_t)index > l->ref_count) break;
            aqua_array_push(rt, b.images, aqua_ref_value(rt, l->refs[index - 1]));
            Clay_Color tint = r->backgroundColor.a > 0 ? r->backgroundColor : (Clay_Color){255, 255, 255, 255};
            Clay_CornerRadius k = r->cornerRadius;
            OP(&b, OP_IMAGE, box.x, box.y, box.width, box.height, aqua_array_length(rt, b.images) - 1, 0, 0, 1, 1,
               k.topLeft, k.topRight, k.bottomRight, k.bottomLeft, tint.r, tint.g, tint.b, tint.a);
            break;
        }
        case CLAY_RENDER_COMMAND_TYPE_SCISSOR_START: OP(&b, OP_CLIP_PUSH, box.x, box.y, box.width, box.height, 0, 0, 0, 0); break;
        case CLAY_RENDER_COMMAND_TYPE_SCISSOR_END: OP0(&b, OP_CLIP_POP); break;
        case CLAY_RENDER_COMMAND_TYPE_CUSTOM: {
            intptr_t index = (intptr_t)c->renderData.custom.customData;
            if (index <= 0 || (size_t)index > l->ref_count) break;
            aqua_value arguments[4] = {aqua_double(box.x), aqua_double(box.y), aqua_double(box.width), aqua_double(box.height)}, result;
            if (!aqua_invoke(rt, aqua_ref_value(rt, l->refs[index - 1]), arguments, 4, &result)) {
                fail_with(call, rt, result, "Clay: an embed function failed.");
                free(b.commands);
                return;
            }
            if (aqua_is_nullish(result)) break;
            OP(&b, OP_CLIP_PUSH, box.x, box.y, box.width, box.height, 0, 0, 0, 0);
            OP(&b, OP_TRANSFORM_PUSH, 1, 0, 0, 1, box.x, box.y);
            if (!splice(&b, result)) { aqua_fail(call, "Clay: an embed function must return a UI draw list / 嵌入函式必須回傳繪製清單。"); free(b.commands); return; }
            OP0(&b, OP_TRANSFORM_POP);
            OP0(&b, OP_CLIP_POP);
            break;
        }
        default: break;
        }
    }
    if (b.failed) { free(b.commands); aqua_fail(call, "Clay: out of memory."); return; }
    aqua_value h = aqua_hash_new(rt, 6);
    aqua_hash_set_field(rt, h, "version", aqua_int(1));
    aqua_hash_set_field(rt, h, "commands", aqua_array_from_f64(rt, b.commands, (uint32_t)b.count, AQUA_FLOAT));
    aqua_hash_set_field(rt, h, "strings", b.strings);
    aqua_hash_set_field(rt, h, "images", b.images);
    aqua_hash_set_field(rt, h, "vertices", b.vertices);
    aqua_hash_set_field(rt, h, "indices", b.indices);
    free(b.commands);
    aqua_return(call, h);
}

/* The box of an element of the last layout: {x, y, width, height}, or null. */
static void layout_element_box(aqua_call *call) {
    layout *l = self_layout(call);
    Clay_ElementData d = Clay_GetElementData(id_arg(call, l));
    if (!d.found) { aqua_return_null(call); return; }
    aqua_runtime *rt = l->rt;
    aqua_value h = aqua_hash_new(rt, 4);
    aqua_hash_set_field(rt, h, "x", aqua_double(d.boundingBox.x));
    aqua_hash_set_field(rt, h, "y", aqua_double(d.boundingBox.y));
    aqua_hash_set_field(rt, h, "width", aqua_double(d.boundingBox.width));
    aqua_hash_set_field(rt, h, "height", aqua_double(d.boundingBox.height));
    aqua_return(call, h);
}
static void layout_pointer_over(aqua_call *call) { layout *l = self_layout(call); aqua_return_bool(call, Clay_PointerOver(id_arg(call, l))); }
static void layout_pointer_over_ids(aqua_call *call) {
    layout *l = self_layout(call);
    Clay_ElementIdArray ids = Clay_GetPointerOverIds();
    aqua_value list = aqua_array_new(l->rt, 0);
    for (int32_t i = 0; i < ids.length; i++) {
        Clay_ElementId id = ids.internalArray[i];
        if (id.stringId.length) aqua_array_push(l->rt, list, aqua_text(l->rt, id.stringId.chars, (size_t)id.stringId.length));
    }
    aqua_return(call, list);
}
static void layout_debug(aqua_call *call) { self_layout(call); Clay_SetDebugModeEnabled(aqua_arg_bool(call, 0)); }

static void *state_create(aqua_runtime *rt) { state *s = calloc(1, sizeof *s); s->rt = rt; return s; }
static void state_destroy(aqua_runtime *rt, void *data) { state *s = data; aqua_release(rt, s->measurer); free(s); }

AQUA_LIBRARY(clay) {
    aqua_library_state(library, state_create, state_destroy);
    aqua_class *m = aqua_define_module(library, "Clay");
    layout_class = aqua_define_class(library, "Clay.Layout", layout_finalize);
    aqua_define_function(m, "Create|建立", module_create, "nn|n");
    aqua_define_function(m, "SetMeasureText|設定文字測量", module_set_measure, "|f");
    aqua_define_function(m, "Element|元素", module_element, "v|v");
    aqua_define_function(m, "Open|開啟", module_open, "v");
    aqua_define_function(m, "Close|關閉", module_close, "");
    aqua_define_function(m, "Text|文字", module_text, "v|v");
    aqua_define_function(m, "Hovered|懸停", module_hovered, "");
    aqua_define_function(m, "PointerOver|指標在上", module_pointer_over, "s");
    aqua_define_function(m, "ScrollOffset|捲動位移", module_scroll_offset, "");
    aqua_define_function(m, "Grow|成長", module_grow, "|nn");
    aqua_define_function(m, "Fit|適應", module_fit, "|nn");
    aqua_define_function(m, "Fixed|固定", module_fixed, "n");
    aqua_define_function(m, "Percent|百分比", module_percent, "n");
    aqua_class *c = layout_class;
    aqua_define_function(c, "SetDimensions|設定尺寸", layout_set_dimensions, "nn");
    aqua_define_function(c, "SetPointer|設定指標", layout_set_pointer, "nnb");
    aqua_define_function(c, "Scroll|捲動", layout_scroll, "nnn|b");
    aqua_define_function(c, "Feed|輸入事件", layout_feed, "v");
    aqua_define_function(c, "Begin|開始", layout_begin, "");
    aqua_define_function(c, "End|結束", layout_end, "");
    aqua_define_function(c, "ElementBox|元素範圍", layout_element_box, "s");
    aqua_define_function(c, "PointerOver|指標在上", layout_pointer_over, "s");
    aqua_define_function(c, "PointerOverIds|指標下元素", layout_pointer_over_ids, "");
    aqua_define_function(c, "SetDebugMode|除錯模式", layout_debug, "b");
}
