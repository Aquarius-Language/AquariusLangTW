/* ImGui: ImDrawList objects for custom drawing inside ImGui windows (GetWindowDrawList, GetForegroundDrawList,
 * GetBackgroundDrawList). An object is valid during the frame that produced it. Colors are [r, g, b(, a)] with 0..255
 * (or 0..1) components, "#rrggbb(aa)" or 0xAARRGGBB; points are flat [x0, y0, x1, y1, ...] arrays. */
#include "binding.h"
#include "imgui_internal.h"

#include <vector>

namespace aqua_imgui {

struct DrawListObject { Context *context; int frame; ImDrawList *list; };

static void drawlist_finalize(aqua_runtime *, void *data) { delete static_cast<DrawListObject *>(data); }

static ImDrawList *self_list(aqua_call *call) {
    auto *d = static_cast<DrawListObject *>(aqua_self(call));
    State *s = state_of(call);
    bool live = s->current == d->context && d->context->imgui && d->context->in_frame && d->context->frame == d->frame;
    if (!live) { aqua_fail(call, "ImGui.DrawList: a draw list is valid only during the frame that returned it / 繪製清單只在同一畫格有效。"); return nullptr; }
    ImGui::SetCurrentContext(d->context->imgui);
    return d->list;
}
static float num(aqua_call *call, uint32_t i) { return (float)aqua_arg_number(call, i); }
static ImVec2 at(aqua_call *call, uint32_t i) { return ImVec2(num(call, i), num(call, i + 1)); }
static std::vector<ImVec2> points_arg(aqua_call *call, uint32_t i) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value list = aqua_arg(call, i);
    uint32_t n = aqua_array_length(rt, list);
    std::vector<float> flat(n);
    std::vector<ImVec2> points;
    if (n % 2 || (n && !aqua_array_read_f32(rt, list, 0, n, flat.data()))) { aqua_fail(call, "ImGui.DrawList: points are [x0, y0, x1, y1, ...]."); return points; }
    for (uint32_t k = 0; k < n; k += 2) points.push_back(ImVec2(flat[k], flat[k + 1]));
    return points;
}

#define LIST ImDrawList *l = self_list(call); if (!l) return
static void AddLine(aqua_call *call) { LIST; l->AddLine(at(call, 0), at(call, 2), u32_color_arg(call, 4, 0), float_arg(call, 5, 1)); report_assert(call); }
static void AddRect(aqua_call *call) {
    LIST; l->AddRect(at(call, 0), at(call, 2), u32_color_arg(call, 4, 0), float_arg(call, 5, 0), float_arg(call, 6, 1), int_arg(call, 7, 0)); report_assert(call);
}
static void AddRectFilled(aqua_call *call) { LIST; l->AddRectFilled(at(call, 0), at(call, 2), u32_color_arg(call, 4, 0), float_arg(call, 5, 0), int_arg(call, 6, 0)); report_assert(call); }
static void AddRectFilledMultiColor(aqua_call *call) {
    LIST;
    l->AddRectFilledMultiColor(at(call, 0), at(call, 2), u32_color_arg(call, 4, 0), u32_color_arg(call, 5, 0), u32_color_arg(call, 6, 0), u32_color_arg(call, 7, 0));
    report_assert(call);
}
static void AddQuad(aqua_call *call) { LIST; l->AddQuad(at(call, 0), at(call, 2), at(call, 4), at(call, 6), u32_color_arg(call, 8, 0), float_arg(call, 9, 1)); report_assert(call); }
static void AddQuadFilled(aqua_call *call) { LIST; l->AddQuadFilled(at(call, 0), at(call, 2), at(call, 4), at(call, 6), u32_color_arg(call, 8, 0)); report_assert(call); }
static void AddTriangle(aqua_call *call) { LIST; l->AddTriangle(at(call, 0), at(call, 2), at(call, 4), u32_color_arg(call, 6, 0), float_arg(call, 7, 1)); report_assert(call); }
static void AddTriangleFilled(aqua_call *call) { LIST; l->AddTriangleFilled(at(call, 0), at(call, 2), at(call, 4), u32_color_arg(call, 6, 0)); report_assert(call); }
static void AddCircle(aqua_call *call) { LIST; l->AddCircle(at(call, 0), num(call, 2), u32_color_arg(call, 3, 0), int_arg(call, 4, 0), float_arg(call, 5, 1)); report_assert(call); }
static void AddCircleFilled(aqua_call *call) { LIST; l->AddCircleFilled(at(call, 0), num(call, 2), u32_color_arg(call, 3, 0), int_arg(call, 4, 0)); report_assert(call); }
static void AddNgon(aqua_call *call) { LIST; l->AddNgon(at(call, 0), num(call, 2), u32_color_arg(call, 3, 0), aqua_arg_int(call, 4), float_arg(call, 5, 1)); report_assert(call); }
static void AddNgonFilled(aqua_call *call) { LIST; l->AddNgonFilled(at(call, 0), num(call, 2), u32_color_arg(call, 3, 0), aqua_arg_int(call, 4)); report_assert(call); }
static void AddEllipse(aqua_call *call) {
    LIST; l->AddEllipse(at(call, 0), at(call, 2), u32_color_arg(call, 4, 0), float_arg(call, 5, 0), int_arg(call, 6, 0), float_arg(call, 7, 1)); report_assert(call);
}
static void AddEllipseFilled(aqua_call *call) { LIST; l->AddEllipseFilled(at(call, 0), at(call, 2), u32_color_arg(call, 4, 0), float_arg(call, 5, 0), int_arg(call, 6, 0)); report_assert(call); }
/* AddText(x, y, color, text | size): at the current font, or at `size` pixels. */
static void AddText(aqua_call *call) {
    LIST;
    const char *text = text_arg(call, 3);
    if (has(call, 4)) l->AddText(nullptr, num(call, 4), at(call, 0), u32_color_arg(call, 2, 0), text);
    else l->AddText(at(call, 0), u32_color_arg(call, 2, 0), text);
    report_assert(call);
}
static void AddPolyline(aqua_call *call) {
    LIST;
    std::vector<ImVec2> p = points_arg(call, 0);
    if (!aqua_failed(call)) l->AddPolyline(p.data(), (int)p.size(), u32_color_arg(call, 1, 0), float_arg(call, 2, 1), int_arg(call, 3, 0));
    report_assert(call);
}
static void AddConvexPolyFilled(aqua_call *call) {
    LIST;
    std::vector<ImVec2> p = points_arg(call, 0);
    if (!aqua_failed(call)) l->AddConvexPolyFilled(p.data(), (int)p.size(), u32_color_arg(call, 1, 0));
    report_assert(call);
}
static void AddConcavePolyFilled(aqua_call *call) {
    LIST;
    std::vector<ImVec2> p = points_arg(call, 0);
    if (!aqua_failed(call)) l->AddConcavePolyFilled(p.data(), (int)p.size(), u32_color_arg(call, 1, 0));
    report_assert(call);
}
static void AddBezierCubic(aqua_call *call) {
    LIST; l->AddBezierCubic(at(call, 0), at(call, 2), at(call, 4), at(call, 6), u32_color_arg(call, 8, 0), float_arg(call, 9, 1), int_arg(call, 10, 0)); report_assert(call);
}
static void AddBezierQuadratic(aqua_call *call) {
    LIST; l->AddBezierQuadratic(at(call, 0), at(call, 2), at(call, 4), u32_color_arg(call, 6, 0), float_arg(call, 7, 1), int_arg(call, 8, 0)); report_assert(call);
}
static void AddImage(aqua_call *call) {
    LIST;
    ImTextureRef texture = texture_arg(call, 0);
    l->AddImage(texture, at(call, 1), at(call, 3), vec2_arg(call, 5, ImVec2(0, 0)), vec2_arg(call, 6, ImVec2(1, 1)), u32_color_arg(call, 7, IM_COL32_WHITE));
    report_assert(call);
}
static void AddImageRounded(aqua_call *call) {
    LIST;
    ImTextureRef texture = texture_arg(call, 0);
    l->AddImageRounded(texture, at(call, 1), at(call, 3), ImVec2(0, 0), ImVec2(1, 1), u32_color_arg(call, 6, IM_COL32_WHITE), num(call, 5), int_arg(call, 7, 0));
    report_assert(call);
}
static void PushClipRect(aqua_call *call) { LIST; l->PushClipRect(at(call, 0), at(call, 2), bool_arg(call, 4, false)); report_assert(call); }
static void PopClipRect(aqua_call *call) { LIST; l->PopClipRect(); report_assert(call); }
static void PathClear(aqua_call *call) { LIST; l->PathClear(); }
static void PathLineTo(aqua_call *call) { LIST; l->PathLineTo(at(call, 0)); }
static void PathArcTo(aqua_call *call) { LIST; l->PathArcTo(at(call, 0), num(call, 2), num(call, 3), num(call, 4), int_arg(call, 5, 0)); }
static void PathBezierCubicCurveTo(aqua_call *call) { LIST; l->PathBezierCubicCurveTo(at(call, 0), at(call, 2), at(call, 4), int_arg(call, 6, 0)); }
static void PathStroke(aqua_call *call) { LIST; l->PathStroke(u32_color_arg(call, 0, 0), float_arg(call, 1, 1), int_arg(call, 2, 0)); report_assert(call); }
static void PathFillConvex(aqua_call *call) { LIST; l->PathFillConvex(u32_color_arg(call, 0, 0)); report_assert(call); }
static void ChannelsSplit(aqua_call *call) { LIST; l->ChannelsSplit(aqua_arg_int(call, 0)); report_assert(call); }
static void ChannelsSetCurrent(aqua_call *call) { LIST; l->ChannelsSetCurrent(aqua_arg_int(call, 0)); report_assert(call); }
static void ChannelsMerge(aqua_call *call) { LIST; l->ChannelsMerge(); report_assert(call); }
#undef LIST

static void get_list(aqua_call *call, int which) {
    Context *c = frame_of(call);
    if (!c) return;
    ImDrawList *list = which == 0 ? ImGui::GetWindowDrawList() : which == 1 ? ImGui::GetForegroundDrawList() : ImGui::GetBackgroundDrawList();
    if (report_assert(call)) return;
    aqua_return(call, aqua_object_new(c->rt, drawlist_class, new DrawListObject{c, c->frame, list}));
}
static void GetWindowDrawList(aqua_call *call) { get_list(call, 0); }
static void GetForegroundDrawList(aqua_call *call) { get_list(call, 1); }
static void GetBackgroundDrawList(aqua_call *call) { get_list(call, 2); }

void define_drawlist(aqua_library *library, aqua_class *module) {
    drawlist_class = aqua_define_class(library, "ImGui.DrawList", drawlist_finalize);
    aqua_define_function(module, "GetWindowDrawList", GetWindowDrawList, "");
    aqua_define_function(module, "GetForegroundDrawList", GetForegroundDrawList, "");
    aqua_define_function(module, "GetBackgroundDrawList", GetBackgroundDrawList, "");
    struct Entry { const char *names; aqua_function function; const char *signature; };
    static const Entry entries[] = {
#define E(name, signature) {#name, name, signature}
        E(AddLine, "nnnnv|n"), E(AddRect, "nnnnv|nni"), E(AddRectFilled, "nnnnv|ni"), E(AddRectFilledMultiColor, "nnnnvvvv"),
        E(AddQuad, "nnnnnnnnv|n"), E(AddQuadFilled, "nnnnnnnnv"), E(AddTriangle, "nnnnnnv|n"), E(AddTriangleFilled, "nnnnnnv"),
        E(AddCircle, "nnnv|in"), E(AddCircleFilled, "nnnv|i"), E(AddNgon, "nnnvi|n"), E(AddNgonFilled, "nnnvi"),
        E(AddEllipse, "nnnnv|nin"), E(AddEllipseFilled, "nnnnv|ni"), E(AddText, "nnvv|n"), E(AddPolyline, "av|ni"),
        E(AddConvexPolyFilled, "av"), E(AddConcavePolyFilled, "av"), E(AddBezierCubic, "nnnnnnnnv|ni"), E(AddBezierQuadratic, "nnnnnnv|ni"),
        E(AddImage, "vnnnn|vvv"), E(AddImageRounded, "vnnnnn|vi"), E(PushClipRect, "nnnn|b"), E(PopClipRect, ""), E(PathClear, ""),
        E(PathLineTo, "nn"), E(PathArcTo, "nnnnn|i"), E(PathBezierCubicCurveTo, "nnnnnn|i"), E(PathStroke, "v|ni"), E(PathFillConvex, "v"),
        E(ChannelsSplit, "i"), E(ChannelsSetCurrent, "i"), E(ChannelsMerge, ""),
#undef E
    };
    for (const Entry &e : entries) aqua_define_function(drawlist_class, e.names, e.function, e.signature);
}

}  // namespace aqua_imgui
