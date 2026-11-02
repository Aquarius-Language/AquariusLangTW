/* Saturn / 土星: text nodes. Text is measured run by run through the runtime's text measurer (Saturn.SetTextMeasurer,
 * normally the renderer's font metrics, so layout matches what is drawn; without one a fixed approximation that is the
 * same on every target) and broken into lines here: between words, around CJK characters (not before closing or after
 * opening punctuation), at newlines and, for a word wider than the line, between characters. */
#include "saturn.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>

namespace saturn {

static uint32_t next_code_point(std::string_view s, size_t *i) {
    auto byte = [&](size_t k) { return (uint32_t)(unsigned char)s[k]; };
    uint32_t c = byte(*i);
    size_t extra = c < 0x80 ? 0 : c < 0xE0 ? 1 : c < 0xF0 ? 2 : 3;
    if (c >= 0x80) c &= extra == 1 ? 0x1F : extra == 2 ? 0x0F : 0x07;
    (*i)++;
    for (size_t k = 0; k < extra && *i < s.size(); k++, (*i)++) c = (c << 6) | (byte(*i) & 0x3F);
    return c;
}
static bool is_cjk(uint32_t c) {
    return (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xAC00 && c <= 0xD7AF) || (c >= 0xF900 && c <= 0xFAFF) ||
           (c >= 0xFE30 && c <= 0xFE4F) || (c >= 0xFF00 && c <= 0xFFEF) || (c >= 0x20000 && c <= 0x3FFFF);
}
/* Characters a line may not start with (they stay on the previous line) or end with (they move to the next). */
static bool is_closing(uint32_t c) {
    static const uint32_t list[] = {',', '.', '!', '?', ';', ':', ')', ']', '}', 0x3001, 0x3002, 0xFF0C, 0xFF0E, 0xFF01,
        0xFF1F, 0xFF1B, 0xFF1A, 0x300D, 0x300F, 0xFF09, 0x3011, 0x3015, 0x3009, 0x300B, 0x2026, 0x30FC};
    return std::find(std::begin(list), std::end(list), c) != std::end(list);
}
static bool is_opening(uint32_t c) {
    static const uint32_t list[] = {'(', '[', '{', 0x300C, 0x300E, 0xFF08, 0x3010, 0x3014, 0x3008, 0x300A};
    return std::find(std::begin(list), std::end(list), c) != std::end(list);
}
static bool is_space(uint32_t c) { return c == ' ' || c == '\t'; }

/* The measurer-free approximation: identical on every target, used by tests and before a renderer is set up. */
static Metrics approximate(std::string_view text, const TextStyle &style) {
    float width = 0;
    for (size_t i = 0; i < text.size();) {
        uint32_t c = next_code_point(text, &i);
        width += is_cjk(c) ? 1.0f : is_space(c) ? 0.3f : (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ? 0.62f : 0.52f;
    }
    float scale = style.weight >= 600 ? 1.05f : 1.0f;
    return {width * style.size * scale, style.size * 0.8f, style.size * 0.2f};
}

Metrics measure_run(State *state, std::string_view text, const TextStyle &style) {
    char head[96];
    snprintf(head, sizeof head, "%g|%d|%d|", style.size, style.weight, style.italic ? 1 : 0);
    std::string key = std::string(head) + style.family + '\x1f' + std::string(text);
    auto found = state->cache.find(key);
    if (found != state->cache.end()) return found->second;
    Metrics m = approximate(text, style);
    if (state->measurer) {
        aqua_runtime *rt = state->rt;
        aqua_value arguments[5] = {aqua_text(rt, text.data(), text.size()), aqua_text(rt, style.family.data(), style.family.size()),
            aqua_double(style.size), aqua_int(style.weight), aqua_bool(style.italic)};
        aqua_value result;
        if (!aqua_invoke(rt, aqua_ref_value(rt, state->measurer), arguments, 5, &result)) {
            if (state->error.empty()) {
                char *shown = aqua_display(rt, result);
                state->error = shown ? shown : "Saturn: the text measurer failed.";
                aqua_free(shown);
            }
        } else if (aqua_is_numeric(result)) {
            m.width = (float)aqua_number_of(result);
        } else if (result.type == AQUA_ARRAY && aqua_array_length(rt, result) >= 1) {
            aqua_value w = aqua_array_get(rt, result, 0), a = aqua_array_get(rt, result, 1), d = aqua_array_get(rt, result, 2);
            if (aqua_is_numeric(w)) m.width = (float)aqua_number_of(w);
            if (aqua_is_numeric(a)) m.ascent = (float)aqua_number_of(a);
            if (aqua_is_numeric(d)) m.descent = (float)aqua_number_of(d);
        } else if (result.type == AQUA_HASH || result.type == AQUA_STRUCT || result.type == AQUA_NATIVE) {
            aqua_value w, a, d;
            if (result.type == AQUA_NATIVE) {
                w = aqua_object_member(rt, result, "width"); a = aqua_object_member(rt, result, "ascent"); d = aqua_object_member(rt, result, "descent");
            } else {
                aqua_hash_field(rt, result, "width", &w); aqua_hash_field(rt, result, "ascent", &a); aqua_hash_field(rt, result, "descent", &d);
            }
            if (aqua_is_numeric(w)) m.width = (float)aqua_number_of(w);
            if (aqua_is_numeric(a)) m.ascent = (float)aqua_number_of(a);
            if (aqua_is_numeric(d)) m.descent = (float)aqua_number_of(d);
        }
    }
    if (state->cache.size() > 50000) state->cache.clear();
    state->cache.emplace(std::move(key), m);
    return m;
}

static std::string_view trim_end(std::string_view s) {
    while (!s.empty() && (s.back() == ' ' || s.back() == '\t')) s.remove_suffix(1);
    return s;
}

/* Splits a paragraph into unbreakable pieces, each with its trailing spaces. */
static std::vector<std::string> tokens_of(std::string_view paragraph) {
    std::vector<std::string> tokens;
    std::string current;
    bool boundary = false, glue = false;
    for (size_t i = 0; i < paragraph.size();) {
        size_t start = i;
        uint32_t c = next_code_point(paragraph, &i);
        std::string_view bytes = paragraph.substr(start, i - start);
        if (is_space(c)) { current += bytes; boundary = true; continue; }
        if (is_closing(c)) { current += bytes; continue; }
        bool starts = !current.empty() && !glue && (boundary || is_cjk(c) || is_opening(c));
        if (starts) { tokens.push_back(current); current.clear(); }
        current += bytes;
        glue = is_opening(c);
        boundary = is_cjk(c);
    }
    if (!current.empty()) tokens.push_back(current);
    return tokens;
}

static void add_line(Text &t, State *state, std::string text) {
    std::string_view trimmed = trim_end(text);
    t.lines.push_back({std::string(trimmed), measure_run(state, trimmed, t.resolved).width});
}

/* Replaces the end of the last line with an ellipsis so that it fits `width`. */
static void ellipsize(Text &t, State *state, float width) {
    if (t.lines.empty()) return;
    static const char mark[] = "\xE2\x80\xA6";
    std::string text = t.lines.back().text;
    for (;;) {
        std::string candidate = std::string(trim_end(text)) + mark;
        float w = measure_run(state, candidate, t.resolved).width;
        if (w <= width || text.empty()) { t.lines.back() = {candidate, w}; return; }
        size_t cut = text.size() - 1;
        while (cut > 0 && ((unsigned char)text[cut] & 0xC0) == 0x80) cut--;
        text.resize(cut);
    }
}

void layout_text(Node *n, float width, bool bounded) {
    Text &t = *n->text;
    State *state = n->state;
    t.resolved = effective_text_style(n);
    const TextStyle &style = t.resolved;
    t.lines.clear();
    Metrics probe = measure_run(state, "Hg", style);
    t.ascent = probe.ascent;
    t.descent = probe.descent;
    bool wrap = bounded && !style.nowrap;
    /* Yoga rounds boxes to the pixel grid, so the width painted can be a fraction under the width measured. */
    const float slack = 1.0f;
    bool truncated = false;
    std::string_view content = t.content;
    size_t start = 0;
    for (;;) {
        size_t end = content.find('\n', start);
        std::string_view paragraph = content.substr(start, end == std::string_view::npos ? std::string_view::npos : end - start);
        if (!wrap) {
            add_line(t, state, std::string(paragraph));
        } else {
            std::string line;
            for (std::string &token : tokens_of(paragraph)) {
                /* The candidate line is measured whole: renderers' boxes are not additive (kerning, overhang). */
                float fit = measure_run(state, trim_end(token), style).width;
                float candidate = line.empty() ? fit : measure_run(state, trim_end(line + token), style).width;
                if (line.empty() || candidate <= width + slack) {
                    if (line.empty() && fit > width + slack) {
                        /* A word wider than the line breaks between characters. */
                        std::string piece;
                        for (size_t i = 0; i < token.size();) {
                            size_t from = i;
                            next_code_point(token, &i);
                            std::string longer = piece + token.substr(from, i - from);
                            if (!piece.empty() && measure_run(state, trim_end(longer), style).width > width + slack) {
                                add_line(t, state, piece);
                                piece = token.substr(from, i - from);
                            } else piece = longer;
                        }
                        line = piece;
                        continue;
                    }
                    line += token;
                } else {
                    add_line(t, state, line);
                    line = token;
                }
            }
            add_line(t, state, line);
        }
        if (style.max_lines > 0 && (int)t.lines.size() >= style.max_lines) {
            truncated = (int)t.lines.size() > style.max_lines || end != std::string_view::npos;
            t.lines.resize((size_t)style.max_lines);
            break;
        }
        if (end == std::string_view::npos) break;
        start = end + 1;
    }
    if (style.ellipsis && bounded && (truncated || t.lines.back().width > width + 0.01f)) ellipsize(t, state, width);
}

static float line_height(const Text &t) { return t.resolved.size * t.resolved.line_height; }

YGSize measure_text(YGNodeConstRef node, float width, YGMeasureMode width_mode, float, YGMeasureMode) {
    Node *n = node_of(node);
    bool bounded = width_mode != YGMeasureModeUndefined && !std::isnan(width);
    layout_text(n, bounded ? width : 0, bounded);
    float w = 0;
    for (const TextLine &line : n->text->lines) w = std::max(w, line.width);
    if (width_mode == YGMeasureModeExactly) w = width;
    else if (width_mode == YGMeasureModeAtMost) w = std::min(w, width);
    float h = (float)std::max<size_t>(1, n->text->lines.size()) * line_height(*n->text);
    return {std::ceil(w), std::ceil(h)};
}

void update_image_size(Node *n) {
    Visual &look = n->look();
    look.image_width = look.image_height = 0;
    if (!look.image) return;
    aqua_value image = aqua_ref_value(n->rt, look.image);
    aqua_value w = aqua_null(), h = aqua_null();
    if (image.type == AQUA_NATIVE) { w = aqua_object_member(n->rt, image, "width"); h = aqua_object_member(n->rt, image, "height"); }
    else if (image.type == AQUA_HASH || image.type == AQUA_STRUCT) { aqua_hash_field(n->rt, image, "width", &w); aqua_hash_field(n->rt, image, "height", &h); }
    if (aqua_is_numeric(w) && aqua_is_numeric(h)) { look.image_width = (float)aqua_number_of(w); look.image_height = (float)aqua_number_of(h); }
}

/* ---- Script functions ---- */

static Node *self_node(aqua_call *call) {
    auto *n = static_cast<Node *>(aqua_self(call));
    if (!n->yg) { aqua_fail(call, "Saturn.Node: the node was freed / 節點已釋放。"); return nullptr; }
    return n;
}

static void node_set_text(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    aqua_value v = aqua_arg(call, 0);
    if (aqua_is_nullish(v)) {
        if (n->text && !n->measure) { YGNodeSetMeasureFunc(n->yg, nullptr); n->text->content.clear(); n->text->lines.clear(); }
        return;
    }
    if (YGNodeGetChildCount(n->yg) > 0) { aqua_fail(call, "Saturn.Node.SetText: a node with children cannot hold text / 有子節點的節點不能放文字。"); return; }
    char *text = v.type == AQUA_STRING ? aqua_text_dup(aqua_call_runtime(call), v, nullptr) : aqua_display(aqua_call_runtime(call), v);
    if (!n->text) n->text = std::make_unique<Text>();
    bool same = YGNodeHasMeasureFunc(n->yg) && !n->measure && n->text->content == text;
    n->text->content = text;
    aqua_free(text);
    if (same) return;
    aqua_release(n->rt, n->measure);
    n->measure = 0;
    n->text->lines.clear();
    YGNodeSetMeasureFunc(n->yg, measure_text);
    YGNodeSetNodeType(n->yg, YGNodeTypeText);
    YGNodeMarkDirty(n->yg);
}
static void node_get_text(aqua_call *call) {
    Node *n = self_node(call);
    if (n && n->text && YGNodeHasMeasureFunc(n->yg) && !n->measure) aqua_return_text(call, n->text->content.data(), n->text->content.size());
    else if (n) aqua_return_null(call);
}

/* The laid-out lines of a text node, relative to its box: [{text, x, y, width, height, baseline}]. */
static void node_text_lines(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value list = aqua_array_new(rt, 0);
    if (!n->text || !YGNodeHasMeasureFunc(n->yg) || n->measure) { aqua_return(call, list); return; }
    float left = YGNodeLayoutGetPadding(n->yg, YGEdgeLeft) + YGNodeLayoutGetBorder(n->yg, YGEdgeLeft);
    float top = YGNodeLayoutGetPadding(n->yg, YGEdgeTop) + YGNodeLayoutGetBorder(n->yg, YGEdgeTop);
    float inner = YGNodeLayoutGetWidth(n->yg) - left - YGNodeLayoutGetPadding(n->yg, YGEdgeRight) - YGNodeLayoutGetBorder(n->yg, YGEdgeRight);
    layout_text(n, inner, !std::isnan(inner));
    Text &t = *n->text;
    float lh = line_height(t);
    for (size_t i = 0; i < t.lines.size(); i++) {
        const TextLine &line = t.lines[i];
        float x = left + (t.resolved.align == ALIGN_CENTER ? (inner - line.width) / 2 : t.resolved.align == ALIGN_END ? inner - line.width : 0);
        float y = top + (float)i * lh + (lh - t.ascent - t.descent) / 2;
        aqua_value h = aqua_hash_new(rt, 6);
        aqua_hash_set_field(rt, h, "text", aqua_text(rt, line.text.data(), line.text.size()));
        aqua_hash_set_field(rt, h, "x", aqua_double(x));
        aqua_hash_set_field(rt, h, "y", aqua_double(y));
        aqua_hash_set_field(rt, h, "width", aqua_double(line.width));
        aqua_hash_set_field(rt, h, "height", aqua_double(t.ascent + t.descent));
        aqua_hash_set_field(rt, h, "baseline", aqua_double(y + t.ascent));
        aqua_array_push(rt, list, h);
    }
    aqua_return(call, list);
}

static void module_set_measurer(aqua_call *call) {
    auto *state = static_cast<State *>(aqua_state(call));
    aqua_release(state->rt, state->measurer);
    state->measurer = aqua_has_arg(call, 0) ? aqua_retain(state->rt, aqua_arg(call, 0)) : 0;
    state->cache.clear();
}
static void module_clear_cache(aqua_call *call) { static_cast<State *>(aqua_state(call))->cache.clear(); }

/* fontFamily, fontSize, fontWeight and fontStyle of an optional style hash. */
static bool style_of(aqua_call *call, uint32_t index, TextStyle *style) {
    if (aqua_argc(call) <= index || aqua_is_nullish(aqua_arg(call, index))) return true;
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value h = aqua_arg(call, index), v;
    if (h.type != AQUA_HASH && h.type != AQUA_STRUCT) { aqua_fail(call, "Saturn.MeasureText: the style must be a hash / 樣式必須是雜湊。"); return false; }
    if (aqua_hash_field(rt, h, "fontFamily", &v) && v.type == AQUA_STRING) { char *s = aqua_text_dup(rt, v, nullptr); style->family = s; aqua_free(s); }
    if (aqua_hash_field(rt, h, "fontSize", &v) && aqua_is_numeric(v)) style->size = (float)aqua_number_of(v);
    if (aqua_hash_field(rt, h, "fontWeight", &v) && aqua_is_numeric(v)) style->weight = (int)aqua_number_of(v);
    if (aqua_hash_field(rt, h, "fontStyle", &v) && v.type == AQUA_STRING) { char *s = aqua_text_dup(rt, v, nullptr); style->italic = !strcmp(s, "italic"); aqua_free(s); }
    if (aqua_hash_field(rt, h, "lineHeight", &v) && aqua_is_numeric(v)) style->line_height = (float)aqua_number_of(v);
    return true;
}
static void module_measure_text(aqua_call *call) {
    auto *state = static_cast<State *>(aqua_state(call));
    TextStyle style;
    if (!style_of(call, 1, &style)) return;
    aqua_str text = aqua_arg_str(call, 0);
    state->error.clear();
    Metrics m = measure_run(state, std::string_view(text.data, text.length), style);
    if (!state->error.empty()) { aqua_fail(call, "%s", state->error.c_str()); state->error.clear(); return; }
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value h = aqua_hash_new(rt, 3);
    aqua_hash_set_field(rt, h, "width", aqua_double(m.width));
    aqua_hash_set_field(rt, h, "ascent", aqua_double(m.ascent));
    aqua_hash_set_field(rt, h, "descent", aqua_double(m.descent));
    aqua_return(call, h);
}

void define_text(aqua_class *module, aqua_class *node) {
    aqua_define_function(module, "SetTextMeasurer|設定文字測量", module_set_measurer, "|f");
    aqua_define_function(module, "MeasureText|測量文字", module_measure_text, "s|v");
    aqua_define_function(module, "ClearTextCache|清除文字快取", module_clear_cache, "");
    aqua_define_function(node, "SetText|設定文字", node_set_text, "v");
    aqua_define_function(node, "GetText|取得文字", node_get_text, "");
    aqua_define_function(node, "GetTextLines|取得文字行", node_text_lines, "");
}

}  // namespace saturn
