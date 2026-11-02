/* Saturn / 土星: node.SetStyle (CSS-named layout and visual properties in one hash), colors, and the visual setters.
 * Layout keys go to saturn.cpp (set_layout_style); this file keeps the visual and text ones (README.md, "Styles"). */
#include "saturn.h"

#include <cctype>
#include <cmath>
#include <cstdlib>
#include <cstring>

namespace saturn {

/* ---- Colors: 0xAARRGGBB integers (Processing's color()), "#rgb[a]", "#rrggbb[aa]", "rgb(...)", "rgba(...)", names,
   [r, g, b(, a)] with 0..255 components, or a hash {r, g, b, a} ---- */

static const struct { const char *name; uint32_t argb; } named_colors[] = {
    {"transparent", 0x00000000}, {"black", 0xff000000}, {"white", 0xffffffff}, {"red", 0xffff0000}, {"green", 0xff008000},
    {"lime", 0xff00ff00}, {"blue", 0xff0000ff}, {"yellow", 0xffffff00}, {"cyan", 0xff00ffff}, {"magenta", 0xffff00ff},
    {"gray", 0xff808080}, {"grey", 0xff808080}, {"silver", 0xffc0c0c0}, {"orange", 0xffffa500}, {"purple", 0xff800080},
    {"pink", 0xffffc0cb}, {"brown", 0xffa52a2a}, {"navy", 0xff000080}, {"teal", 0xff008080}, {"olive", 0xff808000},
    {"maroon", 0xff800000}, {"aqua", 0xff00ffff}, {"fuchsia", 0xffff00ff}, {"gold", 0xffffd700}, {"indigo", 0xff4b0082},
    {"violet", 0xffee82ee}, {"coral", 0xffff7f50}, {"salmon", 0xfffa8072}, {"crimson", 0xffdc143c}, {"skyblue", 0xff87ceeb},
};

static Color from_argb(uint32_t argb) {
    return {(float)((argb >> 16) & 255), (float)((argb >> 8) & 255), (float)(argb & 255), (float)(argb >> 24)};
}

static int hex_digit(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static bool parse_color_text(const char *s, Color *out) {
    while (*s == ' ') s++;
    if (*s == '#') {
        s++;
        size_t n = strlen(s);
        int d[8];
        for (size_t i = 0; i < n && i < 8; i++) if ((d[i] = hex_digit(s[i])) < 0) return false;
        if (n == 3 || n == 4) {
            *out = {(float)(d[0] * 17), (float)(d[1] * 17), (float)(d[2] * 17), n == 4 ? (float)(d[3] * 17) : 255.0f};
            return true;
        }
        if (n == 6 || n == 8) {
            *out = {(float)(d[0] * 16 + d[1]), (float)(d[2] * 16 + d[3]), (float)(d[4] * 16 + d[5]), n == 8 ? (float)(d[6] * 16 + d[7]) : 255.0f};
            return true;
        }
        return false;
    }
    if (!strncmp(s, "rgb", 3)) {
        const char *p = strchr(s, '(');
        if (!p) return false;
        float v[4] = {0, 0, 0, 1};
        int count = 0;
        for (p++; count < 4; count++) {
            char *end;
            v[count] = strtof(p, &end);
            if (end == p) break;
            p = end;
            while (*p == ' ' || *p == ',' || *p == '/') p++;
        }
        if (count < 3) return false;
        *out = {v[0], v[1], v[2], count == 4 ? v[3] * 255.0f : 255.0f};
        return true;
    }
    for (auto &c : named_colors) {
        size_t i = 0;
        for (; c.name[i] && std::tolower((unsigned char)s[i]) == c.name[i]; i++) {}
        if (!c.name[i] && !s[i]) { *out = from_argb(c.argb); return true; }
    }
    return false;
}

bool parse_color(aqua_runtime *rt, aqua_value v, Color *out) {
    if (aqua_is_nullish(v)) { *out = {}; return true; }
    if (v.type == AQUA_INT || v.type == AQUA_LONG) { *out = from_argb((uint32_t)(int64_t)aqua_number_of(v)); return true; }
    if (aqua_is_number(v)) { *out = from_argb((uint32_t)(int64_t)v.number); return true; }
    if (v.type == AQUA_STRING) {
        char *text = aqua_text_dup(rt, v, nullptr);
        bool ok = parse_color_text(text, out);
        aqua_free(text);
        return ok;
    }
    float c[4] = {0, 0, 0, 255};
    if (v.type == AQUA_ARRAY) {
        uint32_t n = aqua_array_length(rt, v);
        if (n < 3 || n > 4) return false;
        for (uint32_t i = 0; i < n; i++) {
            aqua_value e = aqua_array_get(rt, v, i);
            if (!aqua_is_numeric(e)) return false;
            c[i] = (float)aqua_number_of(e);
        }
    } else if (v.type == AQUA_HASH || v.type == AQUA_STRUCT) {
        const char *keys[4] = {"r|red", "g|green", "b|blue", "a|alpha"};
        for (int i = 0; i < 4; i++) {
            aqua_value e;
            if (aqua_hash_field(rt, v, keys[i], &e) && aqua_is_numeric(e)) c[i] = (float)aqua_number_of(e);
            else if (i < 3) return false;
        }
    } else return false;
    *out = {c[0], c[1], c[2], c[3]};
    return true;
}

/* ---- Visual keys ---- */

static bool fail_style(aqua_call *call, std::string_view key) {
    aqua_fail(call, "Saturn.Node.SetStyle: unsupported value for %.*s / 不支援的樣式值。", (int)key.size(), key.data());
    return false;
}
static bool number_of(aqua_value v, float *out) {
    if (!aqua_is_numeric(v)) return false;
    *out = (float)aqua_number_of(v);
    return true;
}
static std::string text_of(aqua_runtime *rt, aqua_value v) {
    if (v.type != AQUA_STRING) return {};
    char *text = aqua_text_dup(rt, v, nullptr);
    std::string s(text);
    aqua_free(text);
    return s;
}

static bool parse_shadow(aqua_runtime *rt, aqua_value v, Shadow *out) {
    if (v.type != AQUA_HASH && v.type != AQUA_STRUCT) return false;
    aqua_value e;
    Shadow s;
    s.color = {0, 0, 0, 64};
    if (aqua_hash_field(rt, v, "x|offsetX", &e) && !number_of(e, &s.x)) return false;
    if (aqua_hash_field(rt, v, "y|offsetY", &e) && !number_of(e, &s.y)) return false;
    if (aqua_hash_field(rt, v, "blur", &e) && !number_of(e, &s.blur)) return false;
    if (aqua_hash_field(rt, v, "spread", &e) && !number_of(e, &s.spread)) return false;
    if (aqua_hash_field(rt, v, "color", &e) && !parse_color(rt, e, &s.color)) return false;
    *out = s;
    return true;
}

static void text_changed(Node *n) {
    if (n->text && YGNodeHasMeasureFunc(n->yg) && !n->measure) {
        n->text->lines.clear();
        YGNodeMarkDirty(n->yg);
    }
}

static TextStyle &text_style(Node *n) {
    if (!n->text) n->text = std::make_unique<Text>();
    return n->text->style;
}

static const struct { const char *name; int fit; } fits[] = {
    {"fill", FIT_FILL}, {"contain", FIT_CONTAIN}, {"cover", FIT_COVER}, {"none", FIT_NONE}, {"scale-down", FIT_SCALE_DOWN}};
static const struct { const char *name; int align; } aligns[] = {
    {"left", ALIGN_START}, {"start", ALIGN_START}, {"center", ALIGN_CENTER}, {"right", ALIGN_END}, {"end", ALIGN_END}, {"justify", ALIGN_JUSTIFY}};
static const struct { const char *name; int index; } sides[] = {{"Left", 0}, {"Top", 1}, {"Right", 2}, {"Bottom", 3}};
static const struct { const char *name; int index; } corners[] = {{"TopLeft", 0}, {"TopRight", 1}, {"BottomRight", 2}, {"BottomLeft", 3}};

/* Applies one visual or text key; false after failing the call. *handled is false for unknown keys. */
static bool set_visual(aqua_call *call, Node *n, std::string_view key, aqua_value v, bool *handled) {
    aqua_runtime *rt = aqua_call_runtime(call);
    *handled = true;
    float number;
    Color color;
    if (key == "background" || key == "backgroundColor") {
        if ((v.type == AQUA_HASH || v.type == AQUA_STRUCT) && key == "background") {
            aqua_value from, to, angle;
            if (!aqua_hash_field(rt, v, "from", &from) || !aqua_hash_field(rt, v, "to", &to)) return fail_style(call, key);
            Visual &look = n->look();
            if (!parse_color(rt, from, &look.background) || !parse_color(rt, to, &look.gradient_to)) return fail_style(call, key);
            look.gradient = true;
            look.gradient_angle = aqua_hash_field(rt, v, "angle", &angle) && aqua_is_numeric(angle) ? (float)aqua_number_of(angle) : 180;
            return true;
        }
        if (!parse_color(rt, v, &color)) return fail_style(call, key);
        n->look().background = color;
        n->look().gradient = false;
        return true;
    }
    if (key == "borderColor") {
        if (v.type == AQUA_ARRAY && aqua_array_length(rt, v) == 4) {   /* top right bottom left, as in CSS */
            static const int order[4] = {1, 2, 3, 0};
            for (uint32_t i = 0; i < 4; i++) if (!parse_color(rt, aqua_array_get(rt, v, i), &n->look().border[order[i]])) return fail_style(call, key);
            return true;
        }
        if (!parse_color(rt, v, &color)) return fail_style(call, key);
        for (Color &c : n->look().border) c = color;
        return true;
    }
    for (auto &s : sides) {
        std::string name = std::string("border") + s.name + "Color";
        if (key == name) {
            if (!parse_color(rt, v, &n->look().border[s.index])) return fail_style(call, key);
            return true;
        }
    }
    if (key == "border") {   /* a width, or {"width", "color"} */
        aqua_value width = v, c;
        if (v.type == AQUA_HASH || v.type == AQUA_STRUCT) {
            if (aqua_hash_field(rt, v, "color", &c)) {
                if (!parse_color(rt, c, &color)) return fail_style(call, key);
                for (Color &side : n->look().border) side = color;
            }
            if (!aqua_hash_field(rt, v, "width", &width)) return true;
        }
        if (!number_of(width, &number)) return fail_style(call, key);
        YGNodeStyleSetBorder(n->yg, YGEdgeAll, number);
        return true;
    }
    if (key == "borderRadius") {
        Visual &look = n->look();
        if (v.type == AQUA_ARRAY && aqua_array_length(rt, v) == 4) {
            for (uint32_t i = 0; i < 4; i++) if (!number_of(aqua_array_get(rt, v, i), &look.radius[i])) return fail_style(call, key);
            return true;
        }
        if (!number_of(v, &number)) return fail_style(call, key);
        for (float &r : look.radius) r = number;
        return true;
    }
    for (auto &c : corners) {
        std::string name = std::string("border") + c.name + "Radius";
        if (key == name) {
            if (!number_of(v, &n->look().radius[c.index])) return fail_style(call, key);
            return true;
        }
    }
    if (key == "boxShadow" || key == "shadow") {
        Visual &look = n->look();
        look.shadows.clear();
        if (aqua_is_nullish(v)) return true;
        Shadow s;
        if (v.type == AQUA_ARRAY) {
            for (uint32_t i = 0, c = aqua_array_length(rt, v); i < c; i++) {
                if (!parse_shadow(rt, aqua_array_get(rt, v, i), &s)) return fail_style(call, key);
                look.shadows.push_back(s);
            }
            return true;
        }
        if (!parse_shadow(rt, v, &s)) return fail_style(call, key);
        look.shadows.push_back(s);
        return true;
    }
    if (key == "opacity") {
        if (!number_of(v, &number)) return fail_style(call, key);
        n->look().opacity = number < 0 ? 0 : number > 1 ? 1 : number;
        return true;
    }
    if (key == "visibility") { n->look().hidden = text_of(rt, v) == "hidden"; return true; }
    if (key == "visible") { if (v.type != AQUA_BOOL) return fail_style(call, key); n->look().hidden = v.number == 0; return true; }
    if (key == "pointerEvents") { n->look().pointer_none = text_of(rt, v) == "none"; return true; }
    if (key == "zIndex") { if (!number_of(v, &number)) return fail_style(call, key); n->look().z_index = (int)number; return true; }
    if (key == "cursor") { n->look().cursor = text_of(rt, v); return true; }
    if (key == "translateX") return number_of(v, &n->look().translate_x) || fail_style(call, key);
    if (key == "translateY") return number_of(v, &n->look().translate_y) || fail_style(call, key);
    if (key == "rotate") return number_of(v, &n->look().rotate) || fail_style(call, key);
    if (key == "scaleX") return number_of(v, &n->look().scale_x) || fail_style(call, key);
    if (key == "scaleY") return number_of(v, &n->look().scale_y) || fail_style(call, key);
    if (key == "scale") {
        if (!number_of(v, &number)) return fail_style(call, key);
        n->look().scale_x = n->look().scale_y = number;
        return true;
    }
    if (key == "transform") {   /* {"translateX", "translateY", "scale", "scaleX", "scaleY", "rotate"} */
        if (v.type != AQUA_HASH && v.type != AQUA_STRUCT) return fail_style(call, key);
        Visual &look = n->look();
        look.translate_x = look.translate_y = look.rotate = 0;
        look.scale_x = look.scale_y = 1;
        for (uint32_t i = 0, c = aqua_hash_count(rt, v); i < c; i++) {
            aqua_value k, e;
            aqua_hash_entry(rt, v, i, &k, &e);
            bool inner;
            std::string name = text_of(rt, k);
            if (name != "translateX" && name != "translateY" && name != "scale" && name != "scaleX" && name != "scaleY" && name != "rotate")
                return fail_style(call, key);
            if (!set_visual(call, n, name, e, &inner)) return false;
        }
        return true;
    }
    if (key == "transformOrigin") {   /* [x, y] as fractions of the box */
        if (v.type != AQUA_ARRAY || aqua_array_length(rt, v) != 2 ||
            !number_of(aqua_array_get(rt, v, 0), &n->look().origin_x) || !number_of(aqua_array_get(rt, v, 1), &n->look().origin_y))
            return fail_style(call, key);
        return true;
    }
    if (key == "image") {
        Visual &look = n->look();
        aqua_release(n->rt, look.image);
        look.image = aqua_is_nullish(v) ? 0 : aqua_retain(n->rt, v);
        update_image_size(n);
        return true;
    }
    if (key == "objectFit") {
        std::string name = text_of(rt, v);
        for (auto &f : fits) if (name == f.name) { n->look().fit = f.fit; return true; }
        return fail_style(call, key);
    }
    if (key == "tint") return parse_color(rt, v, &n->look().tint) || fail_style(call, key);
    if (key == "scrollX") return number_of(v, &n->look().scroll_x) || fail_style(call, key);
    if (key == "scrollY") return number_of(v, &n->look().scroll_y) || fail_style(call, key);

    /* Text */
    if (key == "color") { if (!parse_color(rt, v, &text_style(n).color)) return fail_style(call, key); return true; }
    if (key == "fontFamily") { text_style(n).family = text_of(rt, v); text_changed(n); return true; }
    if (key == "fontSize") {
        if (!number_of(v, &number) || !(number > 0)) return fail_style(call, key);
        text_style(n).size = number;
        text_changed(n);
        return true;
    }
    if (key == "fontWeight") {
        std::string name = text_of(rt, v);
        int weight = name == "bold" ? 700 : name == "normal" ? 400 : name == "light" ? 300 : 0;
        if (!weight && number_of(v, &number)) weight = (int)number;
        if (weight < 1 || weight > 1000) return fail_style(call, key);
        text_style(n).weight = weight;
        text_changed(n);
        return true;
    }
    if (key == "fontStyle") { text_style(n).italic = text_of(rt, v) == "italic"; text_changed(n); return true; }
    if (key == "lineHeight") {
        if (!number_of(v, &number) || !(number > 0)) return fail_style(call, key);
        text_style(n).line_height = number;
        text_changed(n);
        return true;
    }
    if (key == "textAlign") {
        std::string name = text_of(rt, v);
        for (auto &a : aligns) if (name == a.name) { text_style(n).align = a.align; return true; }
        return fail_style(call, key);
    }
    if (key == "maxLines") {
        if (!number_of(v, &number) && !aqua_is_nullish(v)) return fail_style(call, key);
        text_style(n).max_lines = aqua_is_nullish(v) ? 0 : (int)number;
        text_changed(n);
        return true;
    }
    if (key == "textOverflow") { text_style(n).ellipsis = text_of(rt, v) == "ellipsis"; text_changed(n); return true; }
    if (key == "whiteSpace") { text_style(n).nowrap = text_of(rt, v) == "nowrap"; text_changed(n); return true; }
    *handled = false;
    return true;
}

bool apply_style(aqua_call *call, Node *n, aqua_value style) {
    aqua_runtime *rt = aqua_call_runtime(call);
    if (style.type != AQUA_HASH && style.type != AQUA_STRUCT) { aqua_fail(call, "Saturn.Node.SetStyle: expects a hash of style properties / 需要樣式雜湊。"); return false; }
    for (uint32_t i = 0, count = aqua_hash_count(rt, style); i < count; i++) {
        aqua_value key, value;
        aqua_hash_entry(rt, style, i, &key, &value);
        if (key.type != AQUA_STRING) { aqua_fail(call, "Saturn.Node.SetStyle: keys are property names / 鍵必須是屬性名稱。"); return false; }
        /* Struct fields left out are null: they leave the property as it is. */
        if (style.type == AQUA_STRUCT && aqua_is_nullish(value)) continue;
        std::string name = text_of(rt, key);
        bool handled;
        if (!set_layout_style(call, n, name, value, &handled)) return false;
        if (handled) continue;
        if (!set_visual(call, n, name, value, &handled)) return false;
        if (!handled) { aqua_fail(call, "Saturn.Node.SetStyle: unknown property %s / 未知的樣式屬性。", name.c_str()); return false; }
    }
    return true;
}

static Node *self_node(aqua_call *call) {
    auto *n = static_cast<Node *>(aqua_self(call));
    if (!n->yg) { aqua_fail(call, "Saturn.Node: the node was freed / 節點已釋放。"); return nullptr; }
    return n;
}
static void node_set_style(aqua_call *call) { if (Node *n = self_node(call)) apply_style(call, n, aqua_arg(call, 0)); }
static void node_set_scroll(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    n->look().scroll_x = (float)aqua_arg_number(call, 0);
    n->look().scroll_y = (float)aqua_arg_number(call, 1);
}
static void node_scroll_x(aqua_call *call) { Node *n = self_node(call); if (n) aqua_return_double(call, n->visual ? n->visual->scroll_x : 0); }
static void node_scroll_y(aqua_call *call) { Node *n = self_node(call); if (n) aqua_return_double(call, n->visual ? n->visual->scroll_y : 0); }
static void node_cursor(aqua_call *call) {
    Node *n = self_node(call);
    if (n && n->visual && !n->visual->cursor.empty()) aqua_return_cstring(call, n->visual->cursor.c_str()); else if (n) aqua_return_null(call);
}
static void node_set_image(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    bool handled;
    set_visual(call, n, "image", aqua_arg(call, 0), &handled);
    if (aqua_argc(call) > 2) {
        n->look().image_width = (float)aqua_arg_number(call, 1);
        n->look().image_height = (float)aqua_arg_number(call, 2);
    }
}

void define_style(aqua_class *node) {
    aqua_define_function(node, "SetStyle|設定樣式", node_set_style, "v");
    aqua_define_function(node, "SetScrollOffset|設定捲動位置", node_set_scroll, "nn");
    aqua_define_function(node, "GetScrollX|取得水平捲動", node_scroll_x, "");
    aqua_define_function(node, "GetScrollY|取得垂直捲動", node_scroll_y, "");
    aqua_define_function(node, "GetCursor|取得游標", node_cursor, "");
    aqua_define_function(node, "SetImage|設定圖片", node_set_image, "v|nn");
}

}  // namespace saturn
