/* Saturn / 土星: painting a laid-out node tree into a UI draw list, hit testing, absolute boxes, Saturn.Painter (draw
 * lists built by hand or by a node's paint callback) and Saturn.Combine. The draw list format is in README.md. */
#include "saturn.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace saturn {

/* ---- Draw lists ---- */

void DrawList::clear() {
    for (aqua_ref ref : images) aqua_release(rt, ref);
    images.clear();
    commands.clear(); vertices.clear(); indices.clear(); strings.clear(); string_index.clear();
}
int DrawList::text(std::string_view s) {
    auto found = string_index.find(std::string(s));
    if (found != string_index.end()) return found->second;
    int index = (int)strings.size();
    strings.emplace_back(s);
    string_index.emplace(std::string(s), index);
    return index;
}
int DrawList::image(aqua_value v) {
    images.push_back(aqua_retain(rt, v));
    return (int)images.size() - 1;
}

static aqua_value float_array(aqua_runtime *rt, const std::vector<float> &values) {
    std::vector<double> wide(values.begin(), values.end());
    return aqua_array_from_f64(rt, wide.data(), (uint32_t)wide.size(), AQUA_FLOAT);
}

aqua_value DrawList::build() const {
    aqua_value h = aqua_hash_new(rt, 6);
    aqua_hash_set_field(rt, h, "version", aqua_int(1));
    aqua_hash_set_field(rt, h, "commands", float_array(rt, commands));
    aqua_value texts = aqua_array_new(rt, (uint32_t)strings.size());
    for (size_t i = 0; i < strings.size(); i++) aqua_array_set(rt, texts, (uint32_t)i, aqua_text(rt, strings[i].data(), strings[i].size()));
    aqua_hash_set_field(rt, h, "strings", texts);
    aqua_value pictures = aqua_array_new(rt, (uint32_t)images.size());
    for (size_t i = 0; i < images.size(); i++) aqua_array_set(rt, pictures, (uint32_t)i, aqua_ref_value(rt, images[i]));
    aqua_hash_set_field(rt, h, "images", pictures);
    aqua_hash_set_field(rt, h, "vertices", float_array(rt, vertices));
    aqua_hash_set_field(rt, h, "indices", aqua_array_from_i32(rt, indices.data(), (uint32_t)indices.size()));
    return h;
}

bool DrawList::append(aqua_value list, float dx, float dy) {
    aqua_value commands_value, strings_value, images_value, vertices_value, indices_value;
    if (list.type != AQUA_HASH || !aqua_hash_field(rt, list, "commands", &commands_value) || commands_value.type != AQUA_ARRAY) return false;
    uint32_t count = aqua_array_length(rt, commands_value);
    std::vector<float> in(count);
    if (count && !aqua_array_read_f32(rt, commands_value, 0, count, in.data())) return false;
    std::vector<int> string_map, image_map;
    if (aqua_hash_field(rt, list, "strings", &strings_value) && strings_value.type == AQUA_ARRAY) {
        for (uint32_t i = 0, c = aqua_array_length(rt, strings_value); i < c; i++) {
            aqua_value s = aqua_array_get(rt, strings_value, i);
            char *t = s.type == AQUA_STRING ? aqua_text_dup(rt, s, nullptr) : nullptr;
            string_map.push_back(text(t ? t : ""));
            aqua_free(t);
        }
    }
    if (aqua_hash_field(rt, list, "images", &images_value) && images_value.type == AQUA_ARRAY)
        for (uint32_t i = 0, c = aqua_array_length(rt, images_value); i < c; i++) image_map.push_back(image(aqua_array_get(rt, images_value, i)));
    size_t vertex_base = vertices.size() / 8, index_base = indices.size();
    if (aqua_hash_field(rt, list, "vertices", &vertices_value) && vertices_value.type == AQUA_ARRAY) {
        uint32_t c = aqua_array_length(rt, vertices_value);
        vertices.resize(vertices.size() + c);
        if (c && !aqua_array_read_f32(rt, vertices_value, 0, c, vertices.data() + vertices.size() - c)) return false;
    }
    if (aqua_hash_field(rt, list, "indices", &indices_value) && indices_value.type == AQUA_ARRAY) {
        uint32_t c = aqua_array_length(rt, indices_value);
        indices.resize(indices.size() + c);
        if (c && !aqua_array_read_i32(rt, indices_value, 0, c, indices.data() + indices.size() - c)) return false;
    }
    auto remap = [](const std::vector<int> &map, float index) { int i = (int)index; return i >= 0 && i < (int)map.size() ? (float)map[(size_t)i] : -1.0f; };
    bool moved = dx != 0 || dy != 0;
    if (moved) op(OP_TRANSFORM_PUSH, {1, 0, 0, 1, dx, dy});
    for (size_t at = 0; at < in.size();) {
        int code = (int)in[at], size = op_size(code);
        if (size < 0 || at + 1 + (size_t)size > in.size()) return false;
        size_t start = commands.size();
        commands.insert(commands.end(), in.begin() + (long)at, in.begin() + (long)(at + 1 + (size_t)size));
        float *args = commands.data() + start + 1;
        switch (code) {
        case OP_TEXT: args[4] = remap(string_map, args[4]); if (args[5] >= 0) args[5] = remap(string_map, args[5]); break;
        case OP_IMAGE: args[4] = remap(image_map, args[4]); break;
        case OP_TEXTURE: if (args[3] >= 0) args[3] = remap(image_map, args[3]); break;
        case OP_MESH: args[0] += (float)vertex_base; args[2] += (float)index_base; break;
        default: break;
        }
        at += 1 + (size_t)size;
    }
    if (moved) op(OP_TRANSFORM_POP, {});
    return true;
}

/* ---- Saturn.Painter ---- */

static aqua_class *painter_class;
struct Painter { DrawList *list; bool owned; };

static void painter_finalize(aqua_runtime *, void *data) {
    auto *p = static_cast<Painter *>(data);
    if (p->owned) delete p->list;
    delete p;
}
static DrawList *self_list(aqua_call *call) {
    auto *p = static_cast<Painter *>(aqua_self(call));
    if (!p->list) aqua_fail(call, "Saturn.Painter: a node's painter is only valid during its paint callback / 繪製器只在繪製回呼中有效。");
    return p->list;
}
static float num(aqua_call *call, uint32_t i) { return (float)aqua_arg_number(call, i); }
static bool arg_color(aqua_call *call, uint32_t i, Color *c) {
    if (!parse_color(aqua_call_runtime(call), aqua_arg(call, i), c)) { aqua_fail(call, "Saturn.Painter: unsupported color / 不支援的顏色。"); return false; }
    return true;
}
/* An optional radius argument: one number, or [top-left, top-right, bottom-right, bottom-left]. */
static bool arg_radii(aqua_call *call, uint32_t i, float r[4]) {
    r[0] = r[1] = r[2] = r[3] = 0;
    if (aqua_argc(call) <= i || aqua_is_nullish(aqua_arg(call, i))) return true;
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value v = aqua_arg(call, i);
    if (aqua_is_numeric(v)) { r[0] = r[1] = r[2] = r[3] = (float)aqua_number_of(v); return true; }
    if (v.type == AQUA_ARRAY && aqua_array_length(rt, v) == 4 && aqua_array_read_f32(rt, v, 0, 4, r)) return true;
    aqua_fail(call, "Saturn.Painter: a radius is a number or [tl, tr, br, bl] / 圓角是數字或四個數字。");
    return false;
}

static void painter_rect(aqua_call *call) {
    DrawList *l = self_list(call); Color c; float r[4];
    if (!l || !arg_color(call, 4, &c) || !arg_radii(call, 5, r)) return;
    l->op(OP_RECT, {num(call, 0), num(call, 1), num(call, 2), num(call, 3), r[0], r[1], r[2], r[3], c.r, c.g, c.b, c.a});
}
static void painter_circle(aqua_call *call) {
    DrawList *l = self_list(call); Color c;
    if (!l || !arg_color(call, 3, &c)) return;
    float x = num(call, 0), y = num(call, 1), radius = num(call, 2);
    l->op(OP_RECT, {x - radius, y - radius, 2 * radius, 2 * radius, radius, radius, radius, radius, c.r, c.g, c.b, c.a});
}
static void painter_border(aqua_call *call) {
    DrawList *l = self_list(call); Color c; float r[4];
    if (!l || !arg_color(call, 5, &c) || !arg_radii(call, 6, r)) return;
    float w = num(call, 4);
    l->op(OP_BORDER, {num(call, 0), num(call, 1), num(call, 2), num(call, 3), r[0], r[1], r[2], r[3], w, w, w, w, c.r, c.g, c.b, c.a});
}
static void painter_line(aqua_call *call) {
    DrawList *l = self_list(call); Color c;
    if (!l || !arg_color(call, 5, &c)) return;
    l->op(OP_LINE, {num(call, 0), num(call, 1), num(call, 2), num(call, 3), num(call, 4), c.r, c.g, c.b, c.a});
}
static void painter_shadow(aqua_call *call) {
    DrawList *l = self_list(call); Color c; float r[4];
    if (!l || !arg_color(call, 5, &c) || !arg_radii(call, 6, r)) return;
    l->op(OP_SHADOW, {num(call, 0), num(call, 1), num(call, 2), num(call, 3), r[0], r[1], r[2], r[3], num(call, 4), 0, c.r, c.g, c.b, c.a});
}
static void painter_gradient(aqua_call *call) {
    /* Gradient(x, y, w, h, from, to, x1, y1, x2, y2 | radius): the colors run along the line (x1, y1) -> (x2, y2). */
    DrawList *l = self_list(call); Color a, b; float r[4];
    if (!l || !arg_color(call, 4, &a) || !arg_color(call, 5, &b) || !arg_radii(call, 10, r)) return;
    l->op(OP_GRADIENT, {num(call, 0), num(call, 1), num(call, 2), num(call, 3), r[0], r[1], r[2], r[3], num(call, 6), num(call, 7), num(call, 8), num(call, 9),
        a.r, a.g, a.b, a.a, b.r, b.g, b.b, b.a});
}
/* Text(text, x, y | style): the top-left of the line box is (x, y); style has color, fontFamily, fontSize, fontWeight,
   fontStyle. Returns the width. */
static void painter_text(aqua_call *call) {
    DrawList *l = self_list(call);
    if (!l) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    auto *state = static_cast<State *>(aqua_state(call));
    TextStyle style;
    if (aqua_argc(call) > 3 && !aqua_is_nullish(aqua_arg(call, 3))) {
        aqua_value h = aqua_arg(call, 3), v;
        if (h.type != AQUA_HASH && h.type != AQUA_STRUCT) { aqua_fail(call, "Saturn.Painter.Text: the style must be a hash / 樣式必須是雜湊。"); return; }
        if (aqua_hash_field(rt, h, "color", &v) && !parse_color(rt, v, &style.color)) { aqua_fail(call, "Saturn.Painter.Text: unsupported color."); return; }
        if (aqua_hash_field(rt, h, "fontFamily", &v) && v.type == AQUA_STRING) { char *s = aqua_text_dup(rt, v, nullptr); style.family = s; aqua_free(s); }
        if (aqua_hash_field(rt, h, "fontSize", &v) && aqua_is_numeric(v)) style.size = (float)aqua_number_of(v);
        if (aqua_hash_field(rt, h, "fontWeight", &v) && aqua_is_numeric(v)) style.weight = (int)aqua_number_of(v);
        if (aqua_hash_field(rt, h, "fontStyle", &v) && v.type == AQUA_STRING) { char *s = aqua_text_dup(rt, v, nullptr); style.italic = !strcmp(s, "italic"); aqua_free(s); }
    }
    aqua_str text = aqua_arg_str(call, 0);
    std::string_view s(text.data, text.length);
    state->error.clear();
    Metrics m = measure_run(state, s, style);
    if (!state->error.empty()) { aqua_fail(call, "%s", state->error.c_str()); state->error.clear(); return; }
    Color c = style.color;
    l->op(OP_TEXT, {num(call, 1), num(call, 2), m.width, m.ascent + m.descent, (float)l->text(s), style.family.empty() ? -1.0f : (float)l->text(style.family),
        style.size, (float)style.weight, style.italic ? 1.0f : 0.0f, m.ascent, c.r, c.g, c.b, c.a});
    aqua_return_double(call, m.width);
}
static void painter_image(aqua_call *call) {
    /* Image(image, x, y, w, h | tint, [u0, v0, u1, v1], radius) */
    DrawList *l = self_list(call);
    if (!l) return;
    Color tint{255, 255, 255, 255};
    float uv[4] = {0, 0, 1, 1}, r[4];
    aqua_runtime *rt = aqua_call_runtime(call);
    if (aqua_argc(call) > 5 && !aqua_is_nullish(aqua_arg(call, 5)) && !arg_color(call, 5, &tint)) return;
    if (aqua_argc(call) > 6 && !aqua_is_nullish(aqua_arg(call, 6)) &&
        (aqua_arg(call, 6).type != AQUA_ARRAY || aqua_array_length(rt, aqua_arg(call, 6)) != 4 || !aqua_array_read_f32(rt, aqua_arg(call, 6), 0, 4, uv))) {
        aqua_fail(call, "Saturn.Painter.Image: uv is [u0, v0, u1, v1] / 紋理座標是四個數字。"); return;
    }
    if (!arg_radii(call, 7, r)) return;
    l->op(OP_IMAGE, {num(call, 1), num(call, 2), num(call, 3), num(call, 4), (float)l->image(aqua_arg(call, 0)), uv[0], uv[1], uv[2], uv[3],
        r[0], r[1], r[2], r[3], tint.r, tint.g, tint.b, tint.a});
}
static void painter_push_clip(aqua_call *call) {
    DrawList *l = self_list(call); float r[4];
    if (l && arg_radii(call, 4, r)) l->op(OP_CLIP_PUSH, {num(call, 0), num(call, 1), num(call, 2), num(call, 3), r[0], r[1], r[2], r[3]});
}
static void painter_pop_clip(aqua_call *call) { if (DrawList *l = self_list(call)) l->op(OP_CLIP_POP, {}); }
static void painter_push_transform(aqua_call *call) {
    if (DrawList *l = self_list(call)) l->op(OP_TRANSFORM_PUSH, {num(call, 0), num(call, 1), num(call, 2), num(call, 3), num(call, 4), num(call, 5)});
}
static void painter_translate(aqua_call *call) { if (DrawList *l = self_list(call)) l->op(OP_TRANSFORM_PUSH, {1, 0, 0, 1, num(call, 0), num(call, 1)}); }
static void painter_pop_transform(aqua_call *call) { if (DrawList *l = self_list(call)) l->op(OP_TRANSFORM_POP, {}); }
static void painter_push_opacity(aqua_call *call) { if (DrawList *l = self_list(call)) l->op(OP_OPACITY_PUSH, {std::clamp(num(call, 0), 0.0f, 1.0f)}); }
static void painter_pop_opacity(aqua_call *call) { if (DrawList *l = self_list(call)) l->op(OP_OPACITY_POP, {}); }
/* Mesh(vertices, indices | texture): vertices x y u v r g b a per vertex (colors 0..255), indices into those
   vertices; texture is a Texture id (-1 or none: untextured). */
static void painter_mesh(aqua_call *call) {
    DrawList *l = self_list(call);
    if (!l) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    uint32_t vc = aqua_array_length(rt, aqua_arg(call, 0)), ic = aqua_array_length(rt, aqua_arg(call, 1));
    if (vc % 8) { aqua_fail(call, "Saturn.Painter.Mesh: vertices hold 8 numbers each (x y u v r g b a) / 每個頂點 8 個數字。"); return; }
    size_t first_vertex = l->vertices.size() / 8, first_index = l->indices.size();
    l->vertices.resize(l->vertices.size() + vc);
    l->indices.resize(l->indices.size() + ic);
    if ((vc && !aqua_array_read_f32(rt, aqua_arg(call, 0), 0, vc, l->vertices.data() + first_vertex * 8)) ||
        (ic && !aqua_array_read_i32(rt, aqua_arg(call, 1), 0, ic, l->indices.data() + first_index))) {
        l->vertices.resize(first_vertex * 8); l->indices.resize(first_index);
        aqua_fail(call, "Saturn.Painter.Mesh: vertices and indices must be numbers / 頂點與索引必須是數字。"); return;
    }
    for (uint32_t i = 0; i < ic; i++)
        if (l->indices[first_index + i] < 0 || (uint32_t)l->indices[first_index + i] >= vc / 8) {
            l->vertices.resize(first_vertex * 8); l->indices.resize(first_index);
            aqua_fail(call, "Saturn.Painter.Mesh: index %u is outside the vertices / 索引超出頂點範圍。", i); return;
        }
    float texture = aqua_argc(call) > 2 && aqua_is_numeric(aqua_arg(call, 2)) ? num(call, 2) : -1;
    l->op(OP_MESH, {(float)first_vertex, (float)(vc / 8), (float)first_index, (float)ic, texture});
}
/* Texture(id, width, height, rgbaBytes | x, y, w, h) creates or updates a renderer texture; FreeTexture(id). */
static void painter_texture(aqua_call *call) {
    DrawList *l = self_list(call);
    if (!l) return;
    float x = 0, y = 0, w = num(call, 1), h = num(call, 2);
    if (aqua_argc(call) > 7) { x = num(call, 4); y = num(call, 5); w = num(call, 6); h = num(call, 7); }
    l->op(OP_TEXTURE, {num(call, 0), num(call, 1), num(call, 2), (float)l->image(aqua_arg(call, 3)), x, y, w, h});
}
static void painter_free_texture(aqua_call *call) { if (DrawList *l = self_list(call)) l->op(OP_TEXTURE, {num(call, 0), 0, 0, -1, 0, 0, 0, 0}); }
static void painter_append(aqua_call *call) {
    DrawList *l = self_list(call);
    if (!l) return;
    float dx = aqua_argc(call) > 2 ? num(call, 1) : 0, dy = aqua_argc(call) > 2 ? num(call, 2) : 0;
    if (!l->append(aqua_arg(call, 0), dx, dy)) aqua_fail(call, "Saturn.Painter.Append: not a UI draw list / 不是繪製清單。");
}
static void painter_build(aqua_call *call) { if (DrawList *l = self_list(call)) aqua_return(call, l->build()); }
static void painter_clear(aqua_call *call) { if (DrawList *l = self_list(call)) l->clear(); }
static void painter_count(aqua_call *call) {
    DrawList *l = self_list(call);
    if (!l) return;
    int count = 0;
    for (size_t at = 0; at < l->commands.size(); at += 1 + (size_t)op_size((int)l->commands[at])) count++;
    aqua_return_int(call, count);
}
static void module_painter(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_return(call, aqua_object_new(rt, painter_class, new Painter{new DrawList(rt), true}));
}
static void module_combine(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    DrawList list(rt);
    aqua_value lists = aqua_arg(call, 0);
    for (uint32_t i = 0, c = aqua_array_length(rt, lists); i < c; i++) {
        aqua_value item = aqua_array_get(rt, lists, i);
        if (aqua_is_nullish(item)) continue;
        if (!list.append(item, 0, 0)) { aqua_fail(call, "Saturn.Combine: element %u is not a UI draw list / 第 %u 個元素不是繪製清單。", i, i); return; }
    }
    aqua_return(call, list.build());
}

/* ---- Geometry ---- */

struct Matrix {
    float a = 1, b = 0, c = 0, d = 1, e = 0, f = 0;
    Matrix operator*(const Matrix &m) const {
        return {a * m.a + c * m.b, b * m.a + d * m.b, a * m.c + c * m.d, b * m.c + d * m.d, a * m.e + c * m.f + e, b * m.e + d * m.f + f};
    }
    bool invert(Matrix *out) const {
        float det = a * d - b * c;
        if (std::fabs(det) < 1e-12f) return false;
        *out = {d / det, -b / det, -c / det, a / det, (c * f - d * e) / det, (b * e - a * f) / det};
        return true;
    }
    void apply(float *x, float *y) const { float px = *x; *x = a * px + c * *y + e; *y = b * px + d * *y + f; }
};

/* A node's transform in the coordinates its box was laid out in, about its transform origin. */
static Matrix node_matrix(const Visual &v, float x, float y, float w, float h) {
    float ox = x + v.origin_x * w, oy = y + v.origin_y * h, angle = v.rotate * 3.14159265358979f / 180.0f;
    float cs = std::cos(angle), sn = std::sin(angle);
    Matrix to{1, 0, 0, 1, ox + v.translate_x, oy + v.translate_y}, rotate{cs, sn, -sn, cs, 0, 0}, scale{v.scale_x, 0, 0, v.scale_y, 0, 0},
        back{1, 0, 0, 1, -ox, -oy};
    return to * rotate * scale * back;
}

static void children_in_order(Node *n, std::vector<Node *> &out) {
    out.clear();
    for (size_t i = 0, count = YGNodeGetChildCount(n->yg); i < count; i++) out.push_back(node_of(YGNodeGetChild(n->yg, i)));
    std::stable_sort(out.begin(), out.end(), [](Node *x, Node *y) {
        return (x->visual ? x->visual->z_index : 0) < (y->visual ? y->visual->z_index : 0);
    });
}

static void clamp_radii(const float in[4], float w, float h, float out[4]) {
    float limit = std::max(0.0f, std::min(w, h) / 2);
    for (int i = 0; i < 4; i++) out[i] = std::clamp(in[i], 0.0f, limit);
}

/* ---- Painting ---- */

struct Painting { aqua_call *call; DrawList &list; State *state; bool failed = false; };

static void paint_image(DrawList &list, Visual &v, aqua_value image, float x, float y, float w, float h, const float radii[4]) {
    float u0 = 0, v0 = 0, u1 = 1, v1 = 1, dx = x, dy = y, dw = w, dh = h;
    float iw = v.image_width, ih = v.image_height;
    if (iw > 0 && ih > 0 && v.fit != FIT_FILL && w > 0 && h > 0) {
        float contain = std::min(w / iw, h / ih), cover = std::max(w / iw, h / ih);
        float scale = v.fit == FIT_CONTAIN ? contain : v.fit == FIT_COVER ? cover : v.fit == FIT_NONE ? 1 : std::min(1.0f, contain);
        float sw = iw * scale, sh = ih * scale;
        /* Centered; the part outside the box is cut through the texture coordinates. */
        dx = x + (w - sw) / 2; dy = y + (h - sh) / 2; dw = sw; dh = sh;
        if (dw > w) { float cut = (dw - w) / dw / 2; u0 = cut; u1 = 1 - cut; dx = x; dw = w; }
        if (dh > h) { float cut = (dh - h) / dh / 2; v0 = cut; v1 = 1 - cut; dy = y; dh = h; }
    }
    bool whole = dx == x && dy == y && dw == w && dh == h;
    list.op(OP_IMAGE, {dx, dy, dw, dh, (float)list.image(image), u0, v0, u1, v1, whole ? radii[0] : 0, whole ? radii[1] : 0,
        whole ? radii[2] : 0, whole ? radii[3] : 0, v.tint.r, v.tint.g, v.tint.b, v.tint.a});
}

static void paint_text(Painting &p, Node *n, float x, float y, float w) {
    Text &t = *n->text;
    float bl = YGNodeLayoutGetBorder(n->yg, YGEdgeLeft) + YGNodeLayoutGetPadding(n->yg, YGEdgeLeft);
    float bt = YGNodeLayoutGetBorder(n->yg, YGEdgeTop) + YGNodeLayoutGetPadding(n->yg, YGEdgeTop);
    float inner = w - bl - YGNodeLayoutGetBorder(n->yg, YGEdgeRight) - YGNodeLayoutGetPadding(n->yg, YGEdgeRight);
    layout_text(n, inner, true);
    const TextStyle &style = t.resolved;
    float lh = style.size * style.line_height;
    int font = style.family.empty() ? -1 : p.list.text(style.family);
    const Color &c = style.color;
    for (size_t i = 0; i < t.lines.size(); i++) {
        const TextLine &line = t.lines[i];
        if (line.text.empty()) continue;
        float lx = x + bl + (style.align == ALIGN_CENTER ? (inner - line.width) / 2 : style.align == ALIGN_END ? inner - line.width : 0);
        float ly = y + bt + (float)i * lh + (lh - t.ascent - t.descent) / 2;
        p.list.op(OP_TEXT, {lx, ly, line.width, t.ascent + t.descent, (float)p.list.text(line.text), (float)font, style.size,
            (float)style.weight, style.italic ? 1.0f : 0.0f, t.ascent, c.r, c.g, c.b, c.a});
    }
}

static void paint_node(Painting &p, Node *n, float ox, float oy) {
    if (p.failed || YGNodeStyleGetDisplay(n->yg) == YGDisplayNone) return;
    Visual *v = n->visual.get();
    if (v && v->hidden) return;
    std::vector<Node *> children;
    if (YGNodeStyleGetDisplay(n->yg) == YGDisplayContents) {
        /* No box of its own: its children are laid out in its parent. */
        children_in_order(n, children);
        for (Node *child : children) paint_node(p, child, ox, oy);
        return;
    }
    float x = ox + YGNodeLayoutGetLeft(n->yg), y = oy + YGNodeLayoutGetTop(n->yg);
    float w = YGNodeLayoutGetWidth(n->yg), h = YGNodeLayoutGetHeight(n->yg);
    DrawList &list = p.list;
    Visual shown;
    if (v) { shown = shown_visual(n, &p.state->animating); v = &shown; }
    bool transformed = v && v->transformed(), faded = v && v->opacity < 1;
    if (transformed) {
        Matrix m = node_matrix(*v, x, y, w, h);
        list.op(OP_TRANSFORM_PUSH, {m.a, m.b, m.c, m.d, m.e, m.f});
    }
    if (faded) list.op(OP_OPACITY_PUSH, {v->opacity});
    float radii[4] = {0, 0, 0, 0};
    float border[4] = {YGNodeLayoutGetBorder(n->yg, YGEdgeLeft), YGNodeLayoutGetBorder(n->yg, YGEdgeTop),
        YGNodeLayoutGetBorder(n->yg, YGEdgeRight), YGNodeLayoutGetBorder(n->yg, YGEdgeBottom)};
    if (v) {
        clamp_radii(v->radius, w, h, radii);
        for (const Shadow &s : v->shadows) {
            if (!s.color.visible()) continue;
            list.op(OP_SHADOW, {x + s.x - s.spread, y + s.y - s.spread, w + 2 * s.spread, h + 2 * s.spread, radii[0] + s.spread,
                radii[1] + s.spread, radii[2] + s.spread, radii[3] + s.spread, s.blur, s.spread, s.color.r, s.color.g, s.color.b, s.color.a});
        }
        if (v->gradient) {
            float angle = v->gradient_angle * 3.14159265358979f / 180.0f, dx = std::sin(angle), dy = -std::cos(angle);
            float length = std::fabs(w * dx) + std::fabs(h * dy), cx = x + w / 2, cy = y + h / 2;
            const Color &a = v->background, &b = v->gradient_to;
            list.op(OP_GRADIENT, {x, y, w, h, radii[0], radii[1], radii[2], radii[3], cx - dx * length / 2, cy - dy * length / 2,
                cx + dx * length / 2, cy + dy * length / 2, a.r, a.g, a.b, a.a, b.r, b.g, b.b, b.a});
        } else if (v->background.visible()) {
            const Color &c = v->background;
            list.op(OP_RECT, {x, y, w, h, radii[0], radii[1], radii[2], radii[3], c.r, c.g, c.b, c.a});
        }
        if (v->image) {
            float ix = x + border[0] + YGNodeLayoutGetPadding(n->yg, YGEdgeLeft), iy = y + border[1] + YGNodeLayoutGetPadding(n->yg, YGEdgeTop);
            float iw = w - (ix - x) - border[2] - YGNodeLayoutGetPadding(n->yg, YGEdgeRight);
            float ih = h - (iy - y) - border[3] - YGNodeLayoutGetPadding(n->yg, YGEdgeBottom);
            paint_image(list, *v, aqua_ref_value(n->rt, v->image), ix, iy, iw, ih, radii);
        }
        bool any = border[0] > 0 || border[1] > 0 || border[2] > 0 || border[3] > 0;
        bool uniform = true;
        for (int i = 1; i < 4; i++) {
            const Color &a = v->border[0], &b = v->border[i];
            if (a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a) uniform = false;
        }
        if (any && uniform && v->border[0].visible()) {
            const Color &c = v->border[0];
            list.op(OP_BORDER, {x, y, w, h, radii[0], radii[1], radii[2], radii[3], border[0], border[1], border[2], border[3], c.r, c.g, c.b, c.a});
        } else if (any) {
            for (int side = 0; side < 4; side++) {
                const Color &c = v->border[side];
                if (border[side] <= 0 || !c.visible()) continue;
                float only[4] = {0, 0, 0, 0};
                only[side] = border[side];
                list.op(OP_BORDER, {x, y, w, h, radii[0], radii[1], radii[2], radii[3], only[0], only[1], only[2], only[3], c.r, c.g, c.b, c.a});
            }
        }
    }
    if (is_text(n)) paint_text(p, n, x, y, w);
    if (n->painter) {
        /* The node's paint callback draws in its own coordinates through a painter valid only during the call. */
        auto *painter = new Painter{&list, false};
        aqua_value object = aqua_object_new(n->rt, painter_class, painter);
        if (object.type == AQUA_ERROR) { delete painter; p.failed = true; aqua_fail(p.call, "Saturn: too many objects."); return; }
        list.op(OP_TRANSFORM_PUSH, {1, 0, 0, 1, x, y});
        aqua_value arguments[3] = {object, aqua_double(w), aqua_double(h)}, result;
        bool ok = aqua_invoke(n->rt, aqua_ref_value(n->rt, n->painter), arguments, 3, &result);
        painter->list = nullptr;
        list.op(OP_TRANSFORM_POP, {});
        if (!ok) {
            char *shown = aqua_display(n->rt, result);
            const char *text = shown ? shown : "Saturn: a paint callback failed.";
            while (!strncmp(text, "ERROR: ", 7)) text += 7;
            aqua_fail(p.call, "%s", text);
            aqua_free(shown);
            p.failed = true;
            return;
        }
    }
    children_in_order(n, children);
    if (!children.empty()) {
        bool clip = YGNodeStyleGetOverflow(n->yg) != YGOverflowVisible;
        if (clip) {
            float inner[4];
            for (int i = 0; i < 4; i++) inner[i] = std::max(0.0f, radii[i] - std::max(border[i], border[(i + 1) % 4]));
            list.op(OP_CLIP_PUSH, {x + border[0], y + border[1], w - border[0] - border[2], h - border[1] - border[3], inner[0], inner[1], inner[2], inner[3]});
        }
        float sx = v ? v->scroll_x : 0, sy = v ? v->scroll_y : 0;
        for (Node *child : children) paint_node(p, child, x - sx, y - sy);
        if (clip) list.op(OP_CLIP_POP, {});
    }
    if (faded) list.op(OP_OPACITY_POP, {});
    if (transformed) list.op(OP_TRANSFORM_POP, {});
}

static Node *self_node(aqua_call *call) {
    auto *n = static_cast<Node *>(aqua_self(call));
    if (!n->yg) { aqua_fail(call, "Saturn.Node: the node was freed / 節點已釋放。"); return nullptr; }
    return n;
}

/* node.Paint(| x, y): the tree drawn with its root at (x, y) plus the root's own computed position. */
static void node_paint(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    float ox = aqua_argc(call) > 1 ? num(call, 0) : 0, oy = aqua_argc(call) > 1 ? num(call, 1) : 0;
    DrawList list(aqua_call_runtime(call));
    Painting p{call, list, n->state};
    n->state->error.clear();
    n->state->animating = false;
    paint_node(p, n, ox, oy);
    if (p.failed) return;
    if (!n->state->error.empty()) { aqua_fail(call, "%s", n->state->error.c_str()); n->state->error.clear(); return; }
    aqua_return(call, list.build());
}
static void node_set_painter(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    aqua_release(n->rt, n->painter);
    n->painter = aqua_has_arg(call, 0) ? aqua_retain(n->rt, aqua_arg(call, 0)) : 0;
}

/* ---- Hit testing ---- */

/* The deepest node under (px, py) in the coordinates the tree was painted in; `path` gets the nodes from the root. */
static bool hit(Node *n, float px, float py, float ox, float oy, std::vector<Node *> &path) {
    if (YGNodeStyleGetDisplay(n->yg) == YGDisplayNone) return false;
    Visual *v = n->visual.get();
    if (v && v->hidden) return false;
    std::vector<Node *> children;
    children_in_order(n, children);
    if (YGNodeStyleGetDisplay(n->yg) == YGDisplayContents) {
        for (auto it = children.rbegin(); it != children.rend(); ++it) {
            if (hit(*it, px, py, ox, oy, path)) { path.insert(path.begin(), n); return true; }
        }
        return false;
    }
    float x = ox + YGNodeLayoutGetLeft(n->yg), y = oy + YGNodeLayoutGetTop(n->yg);
    float w = YGNodeLayoutGetWidth(n->yg), h = YGNodeLayoutGetHeight(n->yg);
    if (v && v->transformed()) {
        Matrix inverse;
        if (!node_matrix(*v, x, y, w, h).invert(&inverse)) return false;
        inverse.apply(&px, &py);
    }
    bool inside = px >= x && py >= y && px < x + w && py < y + h;
    bool clip = YGNodeStyleGetOverflow(n->yg) != YGOverflowVisible;
    if (inside || !clip) {
        float sx = v ? v->scroll_x : 0, sy = v ? v->scroll_y : 0;
        for (auto it = children.rbegin(); it != children.rend(); ++it) {
            if (hit(*it, px, py, x - sx, y - sy, path)) { path.insert(path.begin(), n); return true; }
        }
    }
    if (inside && !(v && v->pointer_none)) { path.insert(path.begin(), n); return true; }
    return false;
}
static void node_hit_test(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    std::vector<Node *> path;
    float ox = aqua_argc(call) > 3 ? num(call, 2) : 0, oy = aqua_argc(call) > 3 ? num(call, 3) : 0;
    if (hit(n, num(call, 0), num(call, 1), ox, oy, path)) aqua_return(call, path.back()->self); else aqua_return_null(call);
}
static void node_hit_test_path(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    std::vector<Node *> path;
    float ox = aqua_argc(call) > 3 ? num(call, 2) : 0, oy = aqua_argc(call) > 3 ? num(call, 3) : 0;
    hit(n, num(call, 0), num(call, 1), ox, oy, path);
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value list = aqua_array_new(rt, (uint32_t)path.size());
    for (size_t i = 0; i < path.size(); i++) aqua_array_set(rt, list, (uint32_t)i, path[i]->self);
    aqua_return(call, list);
}

/* The node's box in its root's coordinates (scroll offsets of its ancestors applied; transforms are not). */
static void node_absolute(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    float x = YGNodeLayoutGetLeft(n->yg), y = YGNodeLayoutGetTop(n->yg);
    for (YGNodeRef at = YGNodeGetOwner(n->yg); at; at = YGNodeGetOwner(at)) {
        Node *owner = node_of(at);
        if (YGNodeStyleGetDisplay(at) == YGDisplayContents) continue;
        x += YGNodeLayoutGetLeft(at) - (owner->visual ? owner->visual->scroll_x : 0);
        y += YGNodeLayoutGetTop(at) - (owner->visual ? owner->visual->scroll_y : 0);
    }
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value h = aqua_hash_new(rt, 4);
    aqua_hash_set_field(rt, h, "x", aqua_double(x));
    aqua_hash_set_field(rt, h, "y", aqua_double(y));
    aqua_hash_set_field(rt, h, "width", aqua_double(YGNodeLayoutGetWidth(n->yg)));
    aqua_hash_set_field(rt, h, "height", aqua_double(YGNodeLayoutGetHeight(n->yg)));
    aqua_return(call, h);
}
/* The extent of the node's children (for scrolling): {width, height} measured from its padding box. */
static void node_content_size(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    float right = 0, bottom = 0;
    for (size_t i = 0, count = YGNodeGetChildCount(n->yg); i < count; i++) {
        YGNodeRef c = YGNodeGetChild(n->yg, i);
        if (YGNodeStyleGetDisplay(c) == YGDisplayNone) continue;
        right = std::max(right, YGNodeLayoutGetLeft(c) + YGNodeLayoutGetWidth(c) + YGNodeLayoutGetMargin(c, YGEdgeRight));
        bottom = std::max(bottom, YGNodeLayoutGetTop(c) + YGNodeLayoutGetHeight(c) + YGNodeLayoutGetMargin(c, YGEdgeBottom));
    }
    right += YGNodeLayoutGetPadding(n->yg, YGEdgeRight) - YGNodeLayoutGetBorder(n->yg, YGEdgeLeft);
    bottom += YGNodeLayoutGetPadding(n->yg, YGEdgeBottom) - YGNodeLayoutGetBorder(n->yg, YGEdgeTop);
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value h = aqua_hash_new(rt, 2);
    aqua_hash_set_field(rt, h, "width", aqua_double(right));
    aqua_hash_set_field(rt, h, "height", aqua_double(bottom));
    aqua_return(call, h);
}

void define_paint(aqua_library *library, aqua_class *module, aqua_class *node) {
    painter_class = aqua_define_class(library, "Saturn.Painter", painter_finalize);
    aqua_class *p = painter_class;
    aqua_define_function(p, "Rect|矩形", painter_rect, "nnnnv|v");
    aqua_define_function(p, "Circle|圓形", painter_circle, "nnnv");
    aqua_define_function(p, "Border|框線", painter_border, "nnnnnv|v");
    aqua_define_function(p, "Line|線段", painter_line, "nnnnnv");
    aqua_define_function(p, "Shadow|陰影", painter_shadow, "nnnnnv|v");
    aqua_define_function(p, "Gradient|漸層", painter_gradient, "nnnnvvnnnn|v");
    aqua_define_function(p, "Text|文字", painter_text, "snn|v");
    aqua_define_function(p, "Image|圖片", painter_image, "vnnnn|vvv");
    aqua_define_function(p, "PushClip|推入裁切", painter_push_clip, "nnnn|v");
    aqua_define_function(p, "PopClip|彈出裁切", painter_pop_clip, "");
    aqua_define_function(p, "PushTransform|推入變換", painter_push_transform, "nnnnnn");
    aqua_define_function(p, "Translate|平移", painter_translate, "nn");
    aqua_define_function(p, "PopTransform|彈出變換", painter_pop_transform, "");
    aqua_define_function(p, "PushOpacity|推入透明度", painter_push_opacity, "n");
    aqua_define_function(p, "PopOpacity|彈出透明度", painter_pop_opacity, "");
    aqua_define_function(p, "Mesh|網格", painter_mesh, "aa|v");
    aqua_define_function(p, "Texture|紋理", painter_texture, "nnna|nnnn");
    aqua_define_function(p, "FreeTexture|釋放紋理", painter_free_texture, "n");
    aqua_define_function(p, "Append|附加", painter_append, "h|nn");
    aqua_define_function(p, "Build|建立", painter_build, "");
    aqua_define_function(p, "Clear|清除", painter_clear, "");
    aqua_define_function(p, "CommandCount|指令數", painter_count, "");
    aqua_define_function(module, "Painter|繪製器", module_painter, "");
    aqua_define_function(module, "Combine|合併繪製清單", module_combine, "a");
    aqua_define_function(node, "Paint|繪製", node_paint, "|nn");
    aqua_define_function(node, "SetPainter|設定繪製函式", node_set_painter, "|f");
    aqua_define_function(node, "HitTest|命中測試", node_hit_test, "nn|nn");
    aqua_define_function(node, "HitTestPath|命中路徑", node_hit_test_path, "nn|nn");
    aqua_define_function(node, "GetAbsoluteLayout|取得絕對佈局", node_absolute, "");
    aqua_define_function(node, "GetContentSize|取得內容大小", node_content_size, "");
}

}  // namespace saturn
