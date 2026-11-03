/* ImGui: contexts, input, fonts and rendering. A context is one Dear ImGui context; its frame runs from NewFrame to
 * Render, and every ImGui.* widget call in between goes to it. Render returns the frame as a UI draw list (the format
 * the graphics library's drawUI and every UI library share): ImGui's meshes with their clip rectangles, and the
 * create/update/destroy requests of its font atlas textures (ImGuiBackendFlags_RendererHasTextures), so any renderer
 * draws it without knowing ImGui. Input comes from Interaction events (Feed) or the Add* calls. */
#include "binding.h"
#include <aqua/window.h>
#include "imgui_internal.h"

#include <algorithm>
#include <cfloat>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace aqua_imgui {

aqua_class *context_class, *drawlist_class;

/* ---- Assertions: the first failure of a call becomes its error ---- */

static thread_local std::string failed_assert;

}  // namespace aqua_imgui

void aqua_imgui_assert(const char *expression, const char *file, int line) {
    if (!aqua_imgui::failed_assert.empty()) return;
    const char *name = strrchr(file, '/');
    const char *other = strrchr(file, '\\');
    if (other > name) name = other;
    char message[512];
    snprintf(message, sizeof message, "ImGui: %s (%s:%d) / ImGui 內部檢查失敗。", expression, name ? name + 1 : file, line);
    aqua_imgui::failed_assert = message;
}

namespace aqua_imgui {

bool report_assert(aqua_call *call) {
    if (failed_assert.empty()) return false;
    aqua_fail(call, "%s", failed_assert.c_str());
    failed_assert.clear();
    return true;
}

State *state_of(aqua_call *call) { return static_cast<State *>(aqua_state(call)); }

Context *frame_of(aqua_call *call) {
    State *s = state_of(call);
    if (!s->current || !s->current->in_frame) {
        aqua_fail(call, "ImGui: call widgets between context.NewFrame() and context.Render() / 請在 NewFrame 與 Render 之間呼叫。");
        return nullptr;
    }
    ImGui::SetCurrentContext(s->current->imgui);
    failed_assert.clear();
    return s->current;
}

/* ---- Arguments ---- */

bool has(aqua_call *call, uint32_t index) { return aqua_argc(call) > index && !aqua_is_nullish(aqua_arg(call, index)); }

const char *text_arg(aqua_call *call, uint32_t index) {
    if (!has(call, index)) return "";
    aqua_value v = aqua_arg(call, index);
    if (v.type == AQUA_STRING) return aqua_str_of(call, v).data;
    char *shown = aqua_display(aqua_call_runtime(call), v);
    size_t length = shown ? strlen(shown) : 0;
    char *copy = static_cast<char *>(aqua_scratch(call, length + 1));
    memcpy(copy, shown ? shown : "", length + 1);
    aqua_free(shown);
    return copy;
}
int int_arg(aqua_call *call, uint32_t index, int fallback) {
    return has(call, index) && aqua_is_numeric(aqua_arg(call, index)) ? (int)aqua_number_of(aqua_arg(call, index)) : fallback;
}
float float_arg(aqua_call *call, uint32_t index, float fallback) {
    return has(call, index) && aqua_is_numeric(aqua_arg(call, index)) ? (float)aqua_number_of(aqua_arg(call, index)) : fallback;
}
bool bool_arg(aqua_call *call, uint32_t index, bool fallback) {
    return has(call, index) && aqua_arg(call, index).type == AQUA_BOOL ? aqua_arg(call, index).number != 0 : fallback;
}
ImVec2 vec2_arg(aqua_call *call, uint32_t index, ImVec2 fallback) {
    if (!has(call, index)) return fallback;
    float xy[2];
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value v = aqua_arg(call, index);
    if (v.type == AQUA_ARRAY && aqua_array_length(rt, v) == 2 && aqua_array_read_f32(rt, v, 0, 2, xy)) return {xy[0], xy[1]};
    return fallback;
}

static int hex(char c) { return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; }

bool color_arg(aqua_call *call, uint32_t index, ImVec4 *out) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value v = aqua_arg(call, index);
    if (v.type == AQUA_ARRAY) {
        uint32_t n = aqua_array_length(rt, v);
        float c[4] = {0, 0, 0, 1};
        if ((n == 3 || n == 4) && aqua_array_read_f32(rt, v, 0, n, c)) { *out = {c[0], c[1], c[2], c[3]}; return true; }
    } else if (v.type == AQUA_STRING) {
        const char *s = aqua_str_of(call, v).data;
        size_t n = strlen(s);
        if (s[0] == '#' && (n == 7 || n == 9)) {
            int d[8];
            for (size_t i = 1; i < n; i++) if ((d[i - 1] = hex(s[i])) < 0) return false;
            *out = {(d[0] * 16 + d[1]) / 255.0f, (d[2] * 16 + d[3]) / 255.0f, (d[4] * 16 + d[5]) / 255.0f, n == 9 ? (d[6] * 16 + d[7]) / 255.0f : 1.0f};
            return true;
        }
    } else if (aqua_is_numeric(v)) {
        uint32_t argb = (uint32_t)(int64_t)aqua_number_of(v);
        *out = {((argb >> 16) & 255) / 255.0f, ((argb >> 8) & 255) / 255.0f, (argb & 255) / 255.0f, (argb >> 24) / 255.0f};
        return true;
    }
    return false;
}
ImU32 u32_color_arg(aqua_call *call, uint32_t index, ImU32 fallback) {
    ImVec4 c;
    if (!has(call, index)) return fallback;
    /* Draw-list colors also take [r, g, b(, a)] with 0..255 components, as Processing's. */
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value v = aqua_arg(call, index);
    if (v.type == AQUA_ARRAY) {
        float rgba[4] = {0, 0, 0, 255};
        uint32_t n = aqua_array_length(rt, v);
        if ((n == 3 || n == 4) && aqua_array_read_f32(rt, v, 0, n, rgba)) {
            bool unit = rgba[0] <= 1 && rgba[1] <= 1 && rgba[2] <= 1 && (n == 3 || rgba[3] <= 1);
            float scale = unit ? 255.0f : 1.0f;
            return IM_COL32((int)(rgba[0] * scale), (int)(rgba[1] * scale), (int)(rgba[2] * scale), n == 4 ? (int)(rgba[3] * scale) : 255);
        }
    }
    if (!color_arg(call, index, &c)) { aqua_fail(call, "ImGui: unsupported color / 不支援的顏色。"); return fallback; }
    return ImGui::ColorConvertFloat4ToU32(c);
}
ImTextureRef texture_arg(aqua_call *call, uint32_t index) {
    State *s = state_of(call);
    Context *c = s->current;
    aqua_value v = aqua_arg(call, index);
    if (aqua_is_numeric(v)) return ImTextureRef((ImTextureID)(int64_t)aqua_number_of(v));   /* a renderer texture id */
    c->images.push_back(aqua_retain(c->rt, v));
    return ImTextureRef(IMAGE_BIT | (ImTextureID)(c->images.size() - 1));
}
aqua_value vec2_value(aqua_runtime *rt, ImVec2 v) {
    double xy[2] = {v.x, v.y};
    return aqua_array_from_f64(rt, xy, 2, AQUA_DOUBLE);
}
aqua_value floats_value(aqua_runtime *rt, const float *values, int count) {
    double wide[4];
    for (int i = 0; i < count; i++) wide[i] = values[i];
    return aqua_array_from_f64(rt, wide, (uint32_t)count, AQUA_DOUBLE);
}

/* ---- The draw list built by Render (libraries/saturn/README.md, "The UI draw list") ---- */

enum { OP_CLIP_PUSH = 6, OP_CLIP_POP = 7, OP_TRANSFORM_PUSH = 8, OP_TRANSFORM_POP = 9, OP_MESH = 10, OP_TEXTURE = 11 };

/* The frame's draw list being built. Its values are retained: embed functions run script code, which may collect. */
struct Builder {
    aqua_runtime *rt;
    std::vector<double> commands, vertices;
    std::vector<int32_t> indices;
    std::vector<aqua_ref> images;
    aqua_ref spliced = 0;
    explicit Builder(aqua_runtime *runtime) : rt(runtime) {}
    Builder(const Builder &) = delete;
    ~Builder() { for (aqua_ref ref : images) aqua_release(rt, ref); aqua_release(rt, spliced); }
    void op(int code, std::initializer_list<double> args) { commands.push_back(code); commands.insert(commands.end(), args); }
    int image(aqua_value v) { images.push_back(aqua_retain(rt, v)); return (int)images.size() - 1; }
    aqua_value build() {
        aqua_value h = aqua_hash_new(rt, 6);
        aqua_hash_set_field(rt, h, "version", aqua_int(1));
        aqua_hash_set_field(rt, h, "commands", aqua_array_from_f64(rt, commands.data(), (uint32_t)commands.size(), AQUA_FLOAT));
        aqua_hash_set_field(rt, h, "strings", aqua_array_new(rt, 0));
        aqua_value list = aqua_array_new(rt, (uint32_t)images.size());
        for (size_t i = 0; i < images.size(); i++) aqua_array_set(rt, list, (uint32_t)i, aqua_ref_value(rt, images[i]));
        aqua_hash_set_field(rt, h, "images", list);
        aqua_hash_set_field(rt, h, "vertices", aqua_array_from_f64(rt, vertices.data(), (uint32_t)vertices.size(), AQUA_FLOAT));
        aqua_hash_set_field(rt, h, "indices", aqua_array_from_i32(rt, indices.data(), (uint32_t)indices.size()));
        return h;
    }
};

static aqua_value pixel_bytes(aqua_runtime *rt, ImTextureData *t, int x, int y, int w, int h) {
    std::vector<uint8_t> bytes((size_t)w * (size_t)h * 4);
    for (int row = 0; row < h; row++) {
        const uint8_t *from = static_cast<const uint8_t *>(t->GetPixelsAt(x, y + row));
        if (t->Format == ImTextureFormat_RGBA32) memcpy(bytes.data() + (size_t)row * (size_t)w * 4, from, (size_t)w * 4);
        else for (int i = 0; i < w; i++) {   /* Alpha8: white with alpha */
            uint8_t *to = bytes.data() + ((size_t)row * (size_t)w + (size_t)i) * 4;
            to[0] = to[1] = to[2] = 255;
            to[3] = from[i];
        }
    }
    return aqua_array_from_u8(rt, bytes.data(), (uint32_t)bytes.size());
}

static void textures(Builder &b, State *s, ImDrawData *data) {
    if (!data->Textures) return;
    for (ImTextureData *t : *data->Textures) {
        if (t->Status == ImTextureStatus_WantCreate) {
            int id = s->next_texture++;
            t->SetTexID((ImTextureID)id);
            b.op(OP_TEXTURE, {(double)id, (double)t->Width, (double)t->Height, (double)b.image(pixel_bytes(b.rt, t, 0, 0, t->Width, t->Height)),
                0, 0, (double)t->Width, (double)t->Height});
            t->SetStatus(ImTextureStatus_OK);
        } else if (t->Status == ImTextureStatus_WantUpdates) {
            for (const ImTextureRect &r : t->Updates)
                b.op(OP_TEXTURE, {(double)t->TexID, (double)t->Width, (double)t->Height, (double)b.image(pixel_bytes(b.rt, t, r.x, r.y, r.w, r.h)),
                    (double)r.x, (double)r.y, (double)r.w, (double)r.h});
            t->SetStatus(ImTextureStatus_OK);
        } else if (t->Status == ImTextureStatus_WantDestroy && t->UnusedFrames > 0) {
            b.op(OP_TEXTURE, {(double)t->TexID, 0, 0, -1, 0, 0, 0, 0});
            t->SetTexID(ImTextureID_Invalid);
            t->SetStatus(ImTextureStatus_Destroyed);
        }
    }
}

/* Splices a draw list returned by the embed function (see Embed in widgets.cpp) at its place in the frame. */
static bool splice(aqua_call *call, Builder &b, aqua_value list) {
    aqua_runtime *rt = b.rt;
    aqua_value commands, images, vertices, indices;
    if (list.type != AQUA_HASH || !aqua_hash_field(rt, list, "commands", &commands) || commands.type != AQUA_ARRAY) {
        aqua_fail(call, "ImGui.Render: the embed function must return a UI draw list / 嵌入函式必須回傳繪製清單。");
        return false;
    }
    /* Strings, images and meshes are renumbered after this frame's own. */
    uint32_t count = aqua_array_length(rt, commands);
    std::vector<float> in(count);
    if (count && !aqua_array_read_f32(rt, commands, 0, count, in.data())) return false;
    static const int sizes[] = {0, 12, 16, 14, 14, 17, 8, 0, 6, 0, 5, 8, 9, 20, 1, 0};
    size_t image_base = b.images.size(), vertex_base = b.vertices.size() / 8, index_base = b.indices.size();
    if (aqua_hash_field(rt, list, "images", &images) && images.type == AQUA_ARRAY)
        for (uint32_t i = 0, n = aqua_array_length(rt, images); i < n; i++) b.image(aqua_array_get(rt, images, i));
    if (aqua_hash_field(rt, list, "vertices", &vertices) && vertices.type == AQUA_ARRAY) {
        uint32_t n = aqua_array_length(rt, vertices);
        std::vector<double> v(n);
        if (n && !aqua_array_read_f64(rt, vertices, 0, n, v.data())) return false;
        b.vertices.insert(b.vertices.end(), v.begin(), v.end());
    }
    if (aqua_hash_field(rt, list, "indices", &indices) && indices.type == AQUA_ARRAY) {
        uint32_t n = aqua_array_length(rt, indices);
        std::vector<int32_t> v(n);
        if (n && !aqua_array_read_i32(rt, indices, 0, n, v.data())) return false;
        b.indices.insert(b.indices.end(), v.begin(), v.end());
    }
    aqua_value strings;
    bool has_strings = aqua_hash_field(rt, list, "strings", &strings) && strings.type == AQUA_ARRAY && aqua_array_length(rt, strings) > 0;
    if (has_strings) {
        /* Text needs the strings: the spliced list keeps them by moving into the frame's (empty) string table. */
        if (b.spliced) { aqua_fail(call, "ImGui.Render: only one embedded list with text per frame is supported; combine them first."); return false; }
        b.spliced = aqua_retain(rt, strings);
    }
    for (size_t at = 0; at < in.size();) {
        int code = (int)in[at];
        if (code <= 0 || code > 15 || at + 1 + (size_t)sizes[code] > in.size()) return false;
        size_t start = b.commands.size();
        b.commands.insert(b.commands.end(), in.begin() + (long)at, in.begin() + (long)(at + 1 + (size_t)sizes[code]));
        double *a = b.commands.data() + start + 1;
        if (code == 5) a[4] += (double)image_base;
        if (code == 11 && a[3] >= 0) a[3] += (double)image_base;
        if (code == 10) { a[0] += (double)vertex_base; a[2] += (double)index_base; if (a[4] <= -2) a[4] -= (double)image_base; }
        at += 1 + (size_t)sizes[code];
    }
    return true;
}

void embed_callback(const ImDrawList *, const ImDrawCmd *) {}

/* ---- Context objects ---- */

static void context_finalize(aqua_runtime *rt, void *data) {
    auto *c = static_cast<Context *>(data);
    if (c->imgui) {
        if (c->state && c->state->current == c) c->state->current = nullptr;
        ImGuiContext *previous = ImGui::GetCurrentContext();
        ImGui::DestroyContext(c->imgui);
        if (previous != c->imgui) ImGui::SetCurrentContext(previous);
    }
    for (aqua_ref ref : c->images) aqua_release(rt, ref);
    for (auto &p : c->payloads) aqua_release(rt, p.second);
    aqua_release(rt, c->clipboard_get);
    aqua_release(rt, c->clipboard_set);
    delete c;
}

static Context *self_context(aqua_call *call) {
    auto *c = static_cast<Context *>(aqua_self(call));
    if (!c->imgui) { aqua_fail(call, "ImGui: the context was destroyed / 情境已銷毀。"); return nullptr; }
    ImGui::SetCurrentContext(c->imgui);
    failed_assert.clear();
    return c;
}

static void apply_conventions(aqua_runtime *rt, aqua_value conventions);

/* The clipboard: the handlers SetClipboardHandlers gave, else the system's (the window system's plain text), so copy
   and paste in text fields reach other applications without any setup. */
static const char *clipboard_get(ImGuiContext *imgui) {
    auto *c = static_cast<Context *>(imgui->IO.UserData);
    if (c->clipboard_get) {
        aqua_value result;
        if (aqua_invoke(c->rt, aqua_ref_value(c->rt, c->clipboard_get), nullptr, 0, &result) && result.type == AQUA_STRING) {
            char *text = aqua_text_dup(c->rt, result, nullptr);
            c->clipboard = text;
            aqua_free(text);
        }
    } else if (char *text = aqua_clipboard_text(c->rt)) {
        c->clipboard = text;
        aqua_free(text);
    }
    return c->clipboard.c_str();
}
static void clipboard_set(ImGuiContext *imgui, const char *text) {
    auto *c = static_cast<Context *>(imgui->IO.UserData);
    c->clipboard = text;
    if (c->clipboard_set) {
        aqua_value argument = aqua_cstring(c->rt, text), result;
        aqua_invoke(c->rt, aqua_ref_value(c->rt, c->clipboard_set), &argument, 1, &result);
    } else {
        aqua_clipboard_set_text(c->rt, text);
    }
}
static void ime_data(ImGuiContext *imgui, ImGuiViewport *, ImGuiPlatformImeData *data) {
    auto *c = static_cast<Context *>(imgui->IO.UserData);
    c->ime_visible = data->WantVisible;
    c->ime_x = data->InputPos.x;
    c->ime_y = data->InputPos.y;
    c->ime_height = data->InputLineHeight;
}

/* ImGui.CreateContext(| options): options {"docking": true, "keyboard": true, "theme": "dark" | "light" | "classic"}. */
static void module_create_context(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    State *s = state_of(call);
    auto *c = new Context;
    c->rt = rt;
    c->state = s;
    ImGuiContext *previous = ImGui::GetCurrentContext();
    c->imgui = ImGui::CreateContext();
    ImGui::SetCurrentContext(c->imgui);
    ImGuiIO &io = ImGui::GetIO();
    io.UserData = c;
    io.IniFilename = nullptr;
    io.LogFilename = nullptr;
    io.BackendRendererName = "aquarius-ui-draw-list";
    io.BackendPlatformName = "aquarius";
    io.BackendFlags |= ImGuiBackendFlags_RendererHasTextures | ImGuiBackendFlags_RendererHasVtxOffset | ImGuiBackendFlags_HasMouseCursors;
    io.ConfigErrorRecoveryEnableAssert = false;
    io.ConfigErrorRecoveryEnableDebugLog = false;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard | ImGuiConfigFlags_DockingEnable;
    ImGuiPlatformIO &platform = ImGui::GetPlatformIO();
    platform.Platform_GetClipboardTextFn = clipboard_get;
    platform.Platform_SetClipboardTextFn = clipboard_set;
    platform.Platform_SetImeDataFn = ime_data;
    ImGui::StyleColorsDark();
    if (has(call, 0)) {
        aqua_value options = aqua_arg(call, 0), v;
        if (aqua_hash_field(rt, options, "docking", &v) && v.type == AQUA_BOOL && v.number == 0) io.ConfigFlags &= ~ImGuiConfigFlags_DockingEnable;
        if (aqua_hash_field(rt, options, "keyboard", &v) && v.type == AQUA_BOOL && v.number == 0) io.ConfigFlags &= ~ImGuiConfigFlags_NavEnableKeyboard;
        if (aqua_hash_field(rt, options, "conventions", &v) && !aqua_is_nullish(v)) apply_conventions(rt, v);
        if (aqua_hash_field(rt, options, "theme", &v) && v.type == AQUA_STRING) {
            char *theme = aqua_text_dup(rt, v, nullptr);
            if (!strcmp(theme, "light")) ImGui::StyleColorsLight(); else if (!strcmp(theme, "classic")) ImGui::StyleColorsClassic();
            aqua_free(theme);
        }
    }
    c->self = aqua_object_new(rt, context_class, c);
    if (c->self.type == AQUA_ERROR) { ImGui::DestroyContext(c->imgui); c->imgui = nullptr; delete c; ImGui::SetCurrentContext(previous); aqua_return(call, c->self); return; }
    if (!s->current) s->current = c;
    aqua_return(call, c->self);
}

static void ctx_new_frame(aqua_call *call) {
    Context *c = self_context(call);
    if (!c) return;
    ImGuiIO &io = ImGui::GetIO();
    if (c->in_frame) ImGui::EndFrame();
    io.DisplaySize = ImVec2(std::max(1.0f, (float)aqua_arg_number(call, 0)), std::max(1.0f, (float)aqua_arg_number(call, 1)));
    float dt = float_arg(call, 2, 1.0f / 60.0f);
    io.DeltaTime = dt > 0 ? dt : 1.0f / 60.0f;
    float scale = float_arg(call, 3, 1.0f);
    io.DisplayFramebufferScale = ImVec2(scale, scale);
    for (aqua_ref ref : c->images) aqua_release(c->rt, ref);
    c->images.clear();
    c->embeds.clear();
    ImGui::NewFrame();
    c->in_frame = true;
    c->frame++;
    c->state->current = c;
    report_assert(call);
}
static void ctx_end_frame(aqua_call *call) {
    Context *c = self_context(call);
    if (!c || !c->in_frame) return;
    ImGui::EndFrame();
    c->in_frame = false;
    report_assert(call);
}

/* Render(| embed): the frame as a UI draw list. `embed(id, x, y, width, height)` returns the draw list for each
   ImGui.Embed(id, ...) item (a Saturn surface, a Clay layout, ...), spliced at its place and clipped to it. */
static void ctx_render(aqua_call *call) {
    Context *c = self_context(call);
    if (!c) return;
    if (!c->in_frame) { aqua_fail(call, "ImGui.Context.Render: call NewFrame first / 請先呼叫 NewFrame。"); return; }
    ImGui::Render();
    c->in_frame = false;
    if (report_assert(call)) return;
    aqua_runtime *rt = c->rt;
    ImDrawData *data = ImGui::GetDrawData();
    Builder b(rt);
    textures(b, c->state, data);
    ImVec2 origin = data->DisplayPos;
    aqua_value embed = has(call, 0) ? aqua_arg(call, 0) : aqua_null();
    for (ImDrawList *list : data->CmdLists) {
        size_t vertex_base = b.vertices.size() / 8, index_base = b.indices.size();
        for (const ImDrawVert &v : list->VtxBuffer) {
            ImU32 col = v.col;
            b.vertices.insert(b.vertices.end(), {(double)(v.pos.x - origin.x), (double)(v.pos.y - origin.y), (double)v.uv.x, (double)v.uv.y,
                (double)(col & 255), (double)((col >> 8) & 255), (double)((col >> 16) & 255), (double)(col >> 24)});
        }
        for (ImDrawIdx i : list->IdxBuffer) b.indices.push_back((int32_t)i);
        for (const ImDrawCmd &cmd : list->CmdBuffer) {
            if (cmd.UserCallback) {
                if (cmd.UserCallback != embed_callback || embed.type == AQUA_NULL) continue;
                size_t index = (size_t)(intptr_t)cmd.UserCallbackData;
                if (index >= c->embeds.size()) continue;
                const Embed &item = c->embeds[index];
                double x = item.min.x - origin.x, y = item.min.y - origin.y, w = item.max.x - item.min.x, h = item.max.y - item.min.y;
                ImVec4 r = cmd.ClipRect;
                double cx = std::max((double)r.x - origin.x, x), cy = std::max((double)r.y - origin.y, y);
                double cw = std::min((double)r.z - origin.x, x + w) - cx, ch = std::min((double)r.w - origin.y, y + h) - cy;
                if (cw <= 0 || ch <= 0) continue;
                aqua_value arguments[5] = {aqua_cstring(rt, item.id.c_str()), aqua_double(x), aqua_double(y), aqua_double(w), aqua_double(h)}, result;
                if (!aqua_invoke(rt, embed, arguments, 5, &result)) {
                    char *shown = aqua_display(rt, result);
                    const char *text = shown ? shown : "ImGui.Render: the embed function failed.";
                    while (!strncmp(text, "ERROR: ", 7)) text += 7;
                    aqua_fail(call, "%s", text);
                    aqua_free(shown);
                    return;
                }
                if (aqua_is_nullish(result)) continue;
                b.op(OP_CLIP_PUSH, {cx, cy, cw, ch, 0, 0, 0, 0});
                b.op(OP_TRANSFORM_PUSH, {1, 0, 0, 1, x, y});
                if (!splice(call, b, result)) { if (!aqua_failed(call)) aqua_fail(call, "ImGui.Render: malformed embedded draw list."); return; }
                b.op(OP_TRANSFORM_POP, {});
                b.op(OP_CLIP_POP, {});
                continue;
            }
            ImVec4 r = cmd.ClipRect;
            double x = r.x - origin.x, y = r.y - origin.y, w = r.z - r.x, h = r.w - r.y;
            if (w <= 0 || h <= 0 || cmd.ElemCount == 0) continue;
            ImTextureID texture = cmd.GetTexID();
            double t = -1;
            if (texture & IMAGE_BIT) {
                size_t image = (size_t)(texture & ~IMAGE_BIT);
                if (image < c->images.size()) t = -2.0 - b.image(aqua_ref_value(rt, c->images[image]));
            } else if (texture != ImTextureID_Invalid) t = (double)texture;
            b.op(OP_CLIP_PUSH, {x, y, w, h, 0, 0, 0, 0});
            b.op(OP_MESH, {(double)(vertex_base + cmd.VtxOffset), (double)((size_t)list->VtxBuffer.Size - cmd.VtxOffset),
                (double)(index_base + cmd.IdxOffset), (double)cmd.ElemCount, t});
            b.op(OP_CLIP_POP, {});
        }
    }
    aqua_value result = b.build();
    if (b.spliced) aqua_hash_set_field(rt, result, "strings", aqua_ref_value(rt, b.spliced));
    aqua_return(call, result);
}

static void ctx_set_current(aqua_call *call) { if (Context *c = self_context(call)) c->state->current = c; }
static void ctx_destroy(aqua_call *call) {
    auto *c = static_cast<Context *>(aqua_self(call));
    if (!c->imgui) return;
    if (c->state->current == c) c->state->current = nullptr;
    ImGui::DestroyContext(c->imgui);
    c->imgui = nullptr;
    c->in_frame = false;
}

/* ---- Input ---- */

static const struct { int glfw; ImGuiKey key; } keys[] = {
    {32, ImGuiKey_Space}, {39, ImGuiKey_Apostrophe}, {44, ImGuiKey_Comma}, {45, ImGuiKey_Minus}, {46, ImGuiKey_Period},
    {47, ImGuiKey_Slash}, {59, ImGuiKey_Semicolon}, {61, ImGuiKey_Equal}, {91, ImGuiKey_LeftBracket}, {92, ImGuiKey_Backslash},
    {93, ImGuiKey_RightBracket}, {96, ImGuiKey_GraveAccent}, {256, ImGuiKey_Escape}, {257, ImGuiKey_Enter}, {258, ImGuiKey_Tab},
    {259, ImGuiKey_Backspace}, {260, ImGuiKey_Insert}, {261, ImGuiKey_Delete}, {262, ImGuiKey_RightArrow}, {263, ImGuiKey_LeftArrow},
    {264, ImGuiKey_DownArrow}, {265, ImGuiKey_UpArrow}, {266, ImGuiKey_PageUp}, {267, ImGuiKey_PageDown}, {268, ImGuiKey_Home},
    {269, ImGuiKey_End}, {280, ImGuiKey_CapsLock}, {281, ImGuiKey_ScrollLock}, {282, ImGuiKey_NumLock}, {283, ImGuiKey_PrintScreen},
    {284, ImGuiKey_Pause}, {330, ImGuiKey_KeypadDecimal}, {331, ImGuiKey_KeypadDivide}, {332, ImGuiKey_KeypadMultiply},
    {333, ImGuiKey_KeypadSubtract}, {334, ImGuiKey_KeypadAdd}, {335, ImGuiKey_KeypadEnter}, {336, ImGuiKey_KeypadEqual},
    {340, ImGuiKey_LeftShift}, {341, ImGuiKey_LeftCtrl}, {342, ImGuiKey_LeftAlt}, {343, ImGuiKey_LeftSuper},
    {344, ImGuiKey_RightShift}, {345, ImGuiKey_RightCtrl}, {346, ImGuiKey_RightAlt}, {347, ImGuiKey_RightSuper}, {348, ImGuiKey_Menu},
};
/* A GLFW key code (aqua/window.h, Processing's keyCode, Interaction's key) as an ImGuiKey. */
static ImGuiKey key_of_glfw(int code) {
    if (code >= 48 && code <= 57) return (ImGuiKey)(ImGuiKey_0 + (code - 48));
    if (code >= 65 && code <= 90) return (ImGuiKey)(ImGuiKey_A + (code - 65));
    if (code >= 290 && code <= 301) return (ImGuiKey)(ImGuiKey_F1 + (code - 290));
    if (code >= 320 && code <= 329) return (ImGuiKey)(ImGuiKey_Keypad0 + (code - 320));
    for (auto &k : keys) if (k.glfw == code) return k.key;
    return ImGuiKey_None;
}

static void ctx_add_mouse_pos(aqua_call *call) { if (self_context(call)) ImGui::GetIO().AddMousePosEvent((float)aqua_arg_number(call, 0), (float)aqua_arg_number(call, 1)); }
static void ctx_add_mouse_button(aqua_call *call) {
    int button = aqua_arg_int(call, 0);
    if (button < 0 || button >= ImGuiMouseButton_COUNT) { aqua_fail(call, "ImGui.Context.AddMouseButton: buttons are 0..4."); return; }
    if (self_context(call)) ImGui::GetIO().AddMouseButtonEvent(button, aqua_arg_bool(call, 1));
}
static void ctx_add_mouse_wheel(aqua_call *call) { if (self_context(call)) ImGui::GetIO().AddMouseWheelEvent((float)aqua_arg_number(call, 0), (float)aqua_arg_number(call, 1)); }
static void ctx_add_key(aqua_call *call) {
    if (!self_context(call)) return;
    int key = aqua_arg_int(call, 0);
    if (key < ImGuiKey_NamedKey_BEGIN || key >= ImGuiKey_NamedKey_END) {
        if ((key & ImGuiMod_Mask_) == 0) { aqua_fail(call, "ImGui.Context.AddKey: expects an ImGui.Key_* or ImGui.Mod_* constant."); return; }
    }
    ImGui::GetIO().AddKeyEvent((ImGuiKey)key, aqua_arg_bool(call, 1));
}
static void apply_mods(int mods) {
    ImGuiIO &io = ImGui::GetIO();
    io.AddKeyEvent(ImGuiMod_Shift, (mods & 1) != 0);
    io.AddKeyEvent(ImGuiMod_Ctrl, (mods & 2) != 0);
    io.AddKeyEvent(ImGuiMod_Alt, (mods & 4) != 0);
    io.AddKeyEvent(ImGuiMod_Super, (mods & 8) != 0);
}
static void ctx_add_glfw_key(aqua_call *call) {
    if (!self_context(call)) return;
    if (has(call, 2)) apply_mods(aqua_arg_int(call, 2));
    ImGuiKey key = key_of_glfw(aqua_arg_int(call, 0));
    if (key != ImGuiKey_None) ImGui::GetIO().AddKeyEvent(key, aqua_arg_bool(call, 1));
}
static void ctx_add_text(aqua_call *call) { if (self_context(call)) ImGui::GetIO().AddInputCharactersUTF8(aqua_arg_str(call, 0).data); }
static void ctx_add_focus(aqua_call *call) { if (self_context(call)) ImGui::GetIO().AddFocusEvent(aqua_arg_bool(call, 0)); }

/* A field of an event: an Interaction record, a Processing-style hash or a struct. */
static aqua_value field(aqua_runtime *rt, aqua_value event, const char *name) {
    aqua_value v = aqua_null();
    if (event.type == AQUA_NATIVE) v = aqua_object_member(rt, event, name);
    else if (event.type == AQUA_HASH || event.type == AQUA_STRUCT) aqua_hash_field(rt, event, name, &v);
    return v;
}
static double number(aqua_value v, double fallback) { return aqua_is_numeric(v) ? aqua_number_of(v) : fallback; }

/* Feed(event): Interaction events (press, release, move, drag..., scroll, key, text, focus) or raw window events
   ({"type": "pointer" | "button" | "scroll" | "key" | "text", ...}). Returns whether ImGui wants it (the pointer over
   its windows, a focused text field), so mixed interfaces pass the rest on. */
static void ctx_feed(aqua_call *call) {
    Context *c = self_context(call);
    if (!c) return;
    aqua_runtime *rt = c->rt;
    ImGuiIO &io = ImGui::GetIO();
    aqua_value event = aqua_arg(call, 0);
    aqua_value type_value = field(rt, event, "type");
    if (type_value.type != AQUA_STRING) { aqua_return_bool(call, false); return; }
    char *type = aqua_text_dup(rt, type_value, nullptr);
    std::string t = type;
    aqua_free(type);
    double x = number(field(rt, event, "x"), NAN), y = number(field(rt, event, "y"), NAN);
    bool pointer = t == "move" || t == "press" || t == "release" || t == "drag" || t == "dragStart" || t == "dragEnd" || t == "click" ||
                   t == "pointer" || t == "button" || t == "longPress" || t == "contextMenu";
    if (pointer && !std::isnan(x) && !std::isnan(y)) io.AddMousePosEvent((float)x, (float)y);
    /* The first Interaction event brings the platform's conventions (double-click time, caret blinking, Mac editing). */
    if (!c->conventions_known && event.type == AQUA_NATIVE) {
        aqua_value conventions = aqua_object_member(rt, event, "conventions");
        if (!aqua_is_nullish(conventions) && conventions.type != AQUA_ERROR) apply_conventions(rt, conventions);
        c->conventions_known = true;
    }
    aqua_value mods = field(rt, event, "modifiers");
    if (aqua_is_nullish(mods)) mods = field(rt, event, "mods");
    if (aqua_is_numeric(mods)) apply_mods((int)aqua_number_of(mods));
    bool wanted = false;
    if (t == "press" || t == "release" || t == "button") {
        int button = (int)number(field(rt, event, "button"), 0);
        bool down = t == "press" || (t == "button" && number(field(rt, event, "action"), 0) != 0);
        if (button >= 0 && button < ImGuiMouseButton_COUNT) io.AddMouseButtonEvent(button, down);
        wanted = io.WantCaptureMouse;
    } else if (t == "scroll") {
        /* Interaction scrolls in logical pixels (positive dy: content moves up); ImGui counts wheel lines. */
        double dx = number(field(rt, event, "dx"), 0), dy = number(field(rt, event, "dy"), 0);
        bool raw = !aqua_is_nullish(field(rt, event, "action")) || aqua_is_nullish(field(rt, event, "dx"));
        if (raw) io.AddMouseWheelEvent((float)number(field(rt, event, "x"), 0), (float)number(field(rt, event, "y"), 0));
        else io.AddMouseWheelEvent((float)(-dx / 40.0), (float)(-dy / 40.0));
        wanted = io.WantCaptureMouse;
    } else if (pointer) {
        wanted = io.WantCaptureMouse;
    } else if (t == "key") {
        /* Interaction names the action ("press", "repeat", "release"); raw window events number it (0 is release). */
        aqua_value action = field(rt, event, "action");
        bool down = true;
        if (action.type == AQUA_STRING) { char *a = aqua_text_dup(rt, action, nullptr); down = strcmp(a, "release") != 0; aqua_free(a); }
        else down = number(action, 1) != 0;
        ImGuiKey key = key_of_glfw((int)number(field(rt, event, "key"), -1));
        if (key != ImGuiKey_None) io.AddKeyEvent(key, down);
        wanted = io.WantCaptureKeyboard;
    } else if (t == "text") {
        aqua_value text = field(rt, event, "text");
        if (text.type == AQUA_STRING) { char *s = aqua_text_dup(rt, text, nullptr); io.AddInputCharactersUTF8(s); aqua_free(s); }
        wanted = io.WantTextInput;
    } else if (t == "focus") {
        aqua_value state = field(rt, event, "state");
        io.AddFocusEvent(state.type == AQUA_BOOL ? state.number != 0 : true);
    } else if (t == "leave") {
        io.AddMousePosEvent(-FLT_MAX, -FLT_MAX);
    }
    aqua_return_bool(call, wanted);
}

/* HitTest(x, y): whether a window, popup or menu of the last frame covers the point (layered interfaces route the
   pointer with it before feeding anything). */
static void ctx_hit_test(aqua_call *call) {
    Context *c = self_context(call);
    if (!c) return;
    ImVec2 point((float)aqua_arg_number(call, 0), (float)aqua_arg_number(call, 1));
    bool hit = false;
    for (ImGuiWindow *w : c->imgui->Windows) {
        if (!w->WasActive || w->Hidden || (w->Flags & ImGuiWindowFlags_NoMouseInputs)) continue;
        if ((w->Flags & ImGuiWindowFlags_NoBackground) && (w->Flags & ImGuiWindowFlags_NoTitleBar) && w->DockNodeAsHost) continue;
        if (w->Rect().Contains(point)) { hit = true; break; }
    }
    aqua_return_bool(call, hit || c->imgui->ActiveId != 0);
}
static void ctx_want_mouse(aqua_call *call) { if (self_context(call)) aqua_return_bool(call, ImGui::GetIO().WantCaptureMouse); }
static void ctx_want_keyboard(aqua_call *call) { if (self_context(call)) aqua_return_bool(call, ImGui::GetIO().WantCaptureKeyboard); }
static void ctx_want_text(aqua_call *call) { if (self_context(call)) aqua_return_bool(call, ImGui::GetIO().WantTextInput); }
static void ctx_framerate(aqua_call *call) { if (self_context(call)) aqua_return_double(call, ImGui::GetIO().Framerate); }
/* The cursor ImGui asks for, as Interaction's SetCursor names it. */
static void ctx_cursor(aqua_call *call) {
    if (!self_context(call)) return;
    static const char *names[] = {"arrow", "text", "move", "resizeVertical", "resizeHorizontal", "resizeAntiDiagonal", "resizeDiagonal",
        "hand", "wait", "progress", "notAllowed"};
    ImGuiMouseCursor cursor = ImGui::GetMouseCursor();
    if (cursor == ImGuiMouseCursor_None) aqua_return_cstring(call, "none");
    else if (cursor >= 0 && cursor < (int)(sizeof names / sizeof *names)) aqua_return_cstring(call, names[cursor]);
    else aqua_return_cstring(call, "arrow");
}
/* Where an input method's window should open while a text field is active: [x, y, lineHeight], or null. */
static void ctx_ime_rect(aqua_call *call) {
    Context *c = self_context(call);
    if (!c) return;
    if (!c->ime_visible) { aqua_return_null(call); return; }
    double r[3] = {c->ime_x, c->ime_y, c->ime_height};
    aqua_return(call, aqua_array_from_f64(c->rt, r, 3, AQUA_DOUBLE));
}
/* UseConventions(conventions): the platform's rules from Interaction.Conventions() (SaturnUI's ImGuiLayer passes them):
   double-click time, drag threshold, caret blinking (0 turns it off) and, where the shortcut key is Command, the Mac's
   text editing (Option moves by words, Command by lines and to the ends, Command shortcuts). */
static void apply_conventions(aqua_runtime *rt, aqua_value conventions) {
    ImGuiIO &io = ImGui::GetIO();
    aqua_value v = field(rt, conventions, "doubleClickTime");
    if (aqua_is_numeric(v) && aqua_number_of(v) > 0) {
        io.MouseDoubleClickTime = (float)(aqua_number_of(v) / 1000.0);
        io.MouseSingleClickDelay = io.MouseDoubleClickTime * 2;
    }
    v = field(rt, conventions, "dragThreshold");
    if (aqua_is_numeric(v) && aqua_number_of(v) > 0) io.MouseDragThreshold = (float)aqua_number_of(v);
    v = field(rt, conventions, "caretBlinkTime");
    if (aqua_is_numeric(v)) io.ConfigInputTextCursorBlink = aqua_number_of(v) > 0;
    v = field(rt, conventions, "primaryModifier");
    if (v.type == AQUA_STRING) { char *m = aqua_text_dup(rt, v, nullptr); io.ConfigMacOSXBehaviors = strcmp(m, "Cmd") == 0; aqua_free(m); }
}
static void ctx_use_conventions(aqua_call *call) {
    Context *c = self_context(call);
    if (c) { apply_conventions(c->rt, aqua_arg(call, 0)); c->conventions_known = true; }
}

static void ctx_set_clipboard(aqua_call *call) {
    Context *c = self_context(call);
    if (!c) return;
    aqua_release(c->rt, c->clipboard_get);
    aqua_release(c->rt, c->clipboard_set);
    c->clipboard_get = has(call, 0) ? aqua_retain(c->rt, aqua_arg(call, 0)) : 0;
    c->clipboard_set = has(call, 1) ? aqua_retain(c->rt, aqua_arg(call, 1)) : 0;
}

/* ---- Fonts ---- */

/* AddFont(source, size | merge): source is font bytes, a packaged asset path or a file path (TTF, OTF, TTC). With
   merge, its glyphs fill in the previous font (CJK after a Latin font). Returns the font's index for PushFont. */
static void ctx_add_font(aqua_call *call) {
    Context *c = self_context(call);
    if (!c) return;
    if (c->in_frame) { aqua_fail(call, "ImGui.Context.AddFont: add fonts outside NewFrame..Render / 請在畫格外加入字型。"); return; }
    aqua_runtime *rt = c->rt;
    aqua_value source = aqua_arg(call, 0);
    std::vector<uint8_t> bytes;
    if (source.type == AQUA_ARRAY) {
        bytes.resize(aqua_array_length(rt, source));
        if (bytes.empty() || !aqua_array_read_u8(rt, source, 0, (uint32_t)bytes.size(), bytes.data())) { aqua_fail(call, "ImGui.Context.AddFont: font bytes must be integers 0..255."); return; }
    } else if (source.type == AQUA_STRING) {
        const char *path = aqua_arg_str(call, 0).data;
        size_t length = 0;
        const uint8_t *asset = aqua_asset(rt, path, &length);
        if (asset) bytes.assign(asset, asset + length);
        else {
            FILE *f = fopen(path, "rb");
            if (!f) { aqua_fail(call, "ImGui.Context.AddFont: cannot open %s / 無法開啟字型。", path); return; }
            uint8_t chunk[65536];
            size_t n;
            while ((n = fread(chunk, 1, sizeof chunk, f)) > 0) bytes.insert(bytes.end(), chunk, chunk + n);
            fclose(f);
        }
    } else { aqua_fail(call, "ImGui.Context.AddFont: the source is bytes or a path."); return; }
    void *data = ImGui::MemAlloc(bytes.size());
    memcpy(data, bytes.data(), bytes.size());
    ImFontConfig config;
    config.MergeMode = bool_arg(call, 2, false) && !c->fonts.empty();
    config.FontDataOwnedByAtlas = true;
    ImFont *font = ImGui::GetIO().Fonts->AddFontFromMemoryTTF(data, (int)bytes.size(), (float)aqua_arg_number(call, 1), &config);
    if (report_assert(call)) return;
    if (!font) { aqua_fail(call, "ImGui.Context.AddFont: not a font ImGui can read / 無法讀取字型。"); return; }
    if (!config.MergeMode) c->fonts.push_back(font);
    aqua_return_int(call, (int)c->fonts.size() - 1);
}
static void ctx_add_default_font(aqua_call *call) {
    Context *c = self_context(call);
    if (!c) return;
    if (c->in_frame) { aqua_fail(call, "ImGui.Context.AddDefaultFont: add fonts outside NewFrame..Render."); return; }
    ImFontConfig config;
    config.SizePixels = float_arg(call, 0, 13);
    c->fonts.push_back(ImGui::GetIO().Fonts->AddFontDefault(&config));
    aqua_return_int(call, (int)c->fonts.size() - 1);
}

/* A font file of the system for "sans", "mono" or "cjk" text, or null (the web has none: ship a font asset). */
static void module_system_font(aqua_call *call) {
    const char *kind = aqua_arg_str(call, 0).data;
    const char *const *candidates;
#if defined(AQUA_OS_WINDOWS)
    static const char *const cjk[] = {"C:/Windows/Fonts/msjh.ttc", "C:/Windows/Fonts/msyh.ttc", "C:/Windows/Fonts/meiryo.ttc", nullptr};
    static const char *const sans[] = {"C:/Windows/Fonts/segoeui.ttf", "C:/Windows/Fonts/arial.ttf", nullptr};
    static const char *const mono[] = {"C:/Windows/Fonts/consola.ttf", "C:/Windows/Fonts/cour.ttf", nullptr};
#elif defined(AQUA_OS_ANDROID)
    static const char *const cjk[] = {"/system/fonts/NotoSansCJK-Regular.ttc", "/system/fonts/NotoSansSC-Regular.otf", nullptr};
    static const char *const sans[] = {"/system/fonts/Roboto-Regular.ttf", nullptr};
    static const char *const mono[] = {"/system/fonts/DroidSansMono.ttf", "/system/fonts/CutiveMono.ttf", nullptr};
#elif defined(AQUA_OS_LINUX)
    static const char *const cjk[] = {"/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc", "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
        "/usr/share/fonts/google-noto-cjk/NotoSansCJK-Regular.ttc", "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc", nullptr};
    static const char *const sans[] = {"/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/TTF/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf", nullptr};
    static const char *const mono[] = {"/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", "/usr/share/fonts/TTF/DejaVuSansMono.ttf", nullptr};
#else
    static const char *const cjk[] = {nullptr}, *const sans[] = {nullptr}, *const mono[] = {nullptr};
#endif
    candidates = !strcmp(kind, "cjk") ? cjk : !strcmp(kind, "mono") ? mono : sans;
    for (int i = 0; candidates[i]; i++) {
        FILE *f = fopen(candidates[i], "rb");
        if (f) { fclose(f); aqua_return_cstring(call, candidates[i]); return; }
    }
    aqua_return_null(call);
}

static void module_version(aqua_call *call) { aqua_return_cstring(call, ImGui::GetVersion()); }
static void module_current(aqua_call *call) {
    State *s = state_of(call);
    if (s->current) aqua_return(call, s->current->self); else aqua_return_null(call);
}

static void *state_create(aqua_runtime *rt) { auto *s = new State; s->rt = rt; return s; }
static void state_destroy(aqua_runtime *, void *data) { delete static_cast<State *>(data); }

}  // namespace aqua_imgui

using namespace aqua_imgui;


AQUA_LIBRARY(imgui) {
    aqua_library_state(library, state_create, state_destroy);
    aqua_class *module = aqua_define_module(library, "ImGui");
    context_class = aqua_define_class(library, "ImGui.Context", context_finalize);
    aqua_define_function(module, "CreateContext|建立情境", module_create_context, "|h");
    aqua_define_function(module, "GetCurrentContext|目前情境", module_current, "");
    aqua_define_function(module, "GetVersion|版本", module_version, "");
    aqua_define_function(module, "SystemFontPath|系統字型路徑", module_system_font, "s");
#define CONSTANT(name) aqua_define_int(module, strncmp(#name, "ImGui", 5) == 0 ? &#name[5] : &#name[2], (int32_t)(name));
#include "constants.def"
#undef CONSTANT
    aqua_class *c = context_class;
    aqua_define_function(c, "NewFrame|新畫格", ctx_new_frame, "nn|nn");
    aqua_define_function(c, "EndFrame|結束畫格", ctx_end_frame, "");
    aqua_define_function(c, "Render|算繪", ctx_render, "|f");
    aqua_define_function(c, "SetCurrent|設為目前", ctx_set_current, "");
    aqua_define_function(c, "Destroy|銷毀", ctx_destroy, "");
    aqua_define_function(c, "Feed|輸入事件", ctx_feed, "v");
    aqua_define_function(c, "AddMousePos|滑鼠位置", ctx_add_mouse_pos, "nn");
    aqua_define_function(c, "AddMouseButton|滑鼠按鈕", ctx_add_mouse_button, "ib");
    aqua_define_function(c, "AddMouseWheel|滑鼠滾輪", ctx_add_mouse_wheel, "nn");
    aqua_define_function(c, "AddKey|按鍵", ctx_add_key, "ib");
    aqua_define_function(c, "AddKeyCode|按鍵碼", ctx_add_glfw_key, "ib|i");
    aqua_define_function(c, "AddText|輸入文字", ctx_add_text, "s");
    aqua_define_function(c, "AddFocus|焦點", ctx_add_focus, "b");
    aqua_define_function(c, "AddFont|加入字型", ctx_add_font, "vn|b");
    aqua_define_function(c, "AddDefaultFont|加入預設字型", ctx_add_default_font, "|n");
    aqua_define_function(c, "SetClipboardHandlers|設定剪貼簿", ctx_set_clipboard, "|vv");
    aqua_define_function(c, "UseConventions|使用慣例", ctx_use_conventions, "v");
    aqua_define_function(c, "ImeRect|輸入法位置", ctx_ime_rect, "");
    aqua_define_function(c, "HitTest|命中測試", ctx_hit_test, "nn");
    aqua_define_property(c, "WantCaptureMouse|需要滑鼠", ctx_want_mouse);
    aqua_define_property(c, "WantCaptureKeyboard|需要鍵盤", ctx_want_keyboard);
    aqua_define_property(c, "WantTextInput|需要文字輸入", ctx_want_text);
    aqua_define_property(c, "Framerate|畫格率", ctx_framerate);
    aqua_define_property(c, "MouseCursor|滑鼠游標", ctx_cursor);
    define_widgets(module);
    define_drawlist(library, module);
}
