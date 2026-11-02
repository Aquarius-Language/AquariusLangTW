/* ImGui: the immediate-mode API, named as in Dear ImGui. Aquarius has no pointers, so a widget that edits a value takes
 * the current value and returns the new one (`speed = ImGui.SliderFloat("Speed", speed, 0.0d, 10.0d);`), and
 * ImGui.IsItemEdited() tells whether it changed, as in ImGui itself. Windows, headers, tab items and modals that take
 * an `open` flag report the flag after the call through ImGui.LastOpen(). Text arguments are shown as they are (no
 * printf formatting); formats of sliders and drags are ImGui's own. Every call needs the frame of the current context
 * (context.NewFrame .. context.Render). */
#include "binding.h"
#include "imgui_internal.h"

#include <cfloat>
#include <cstring>
#include <vector>

namespace aqua_imgui {

#define FRAME Context *c = frame_of(call); if (!c) return; (void)c
#define RETURN_BOOL(e) do { bool result_ = (e); if (!report_assert(call)) aqua_return_bool(call, result_); } while (0)
#define RETURN_DOUBLE(e) do { double result_ = (e); if (!report_assert(call)) aqua_return_double(call, result_); } while (0)
#define RETURN_INT(e) do { int result_ = (e); if (!report_assert(call)) aqua_return_int(call, result_); } while (0)
#define RETURN_VALUE(e) do { aqua_value result_ = (e); if (!report_assert(call)) aqua_return(call, result_); } while (0)
#define DO(statement) do { statement; report_assert(call); } while (0)

static float num(aqua_call *call, uint32_t i) { return (float)aqua_arg_number(call, i); }
static const char *format_arg(aqua_call *call, uint32_t i, const char *fallback) { return has(call, i) ? text_arg(call, i) : fallback; }
static aqua_runtime *rt_of(aqua_call *call) { return aqua_call_runtime(call); }

/* `open` arguments: a Boolean shows a close button; the flag after the call is ImGui.LastOpen(). */
static bool *open_arg(aqua_call *call, uint32_t i, bool *storage) {
    if (!has(call, i) || aqua_arg(call, i).type != AQUA_BOOL) return nullptr;
    *storage = aqua_arg(call, i).number != 0;
    return storage;
}

static bool read_floats(aqua_call *call, uint32_t i, float *out, int count) {
    aqua_runtime *rt = rt_of(call);
    aqua_value v = aqua_arg(call, i);
    if (v.type != AQUA_ARRAY || aqua_array_length(rt, v) != (uint32_t)count || !aqua_array_read_f32(rt, v, 0, (uint32_t)count, out)) {
        aqua_fail(call, "ImGui: expects an array of %d numbers / 需要 %d 個數字的陣列。", count, count);
        return false;
    }
    return true;
}
static bool read_ints(aqua_call *call, uint32_t i, int *out, int count) {
    aqua_runtime *rt = rt_of(call);
    aqua_value v = aqua_arg(call, i);
    if (v.type != AQUA_ARRAY || aqua_array_length(rt, v) != (uint32_t)count || !aqua_array_read_i32(rt, v, 0, (uint32_t)count, out)) {
        aqua_fail(call, "ImGui: expects an array of %d integers / 需要 %d 個整數的陣列。", count, count);
        return false;
    }
    return true;
}
static aqua_value ints_value(aqua_runtime *rt, const int *values, int count) { return aqua_array_from_i32(rt, values, (uint32_t)count); }

/* Items of combos and list boxes: an array of texts. */
static std::vector<const char *> items_of(aqua_call *call, uint32_t i) {
    std::vector<const char *> items;
    aqua_runtime *rt = rt_of(call);
    aqua_value list = aqua_arg(call, i);
    for (uint32_t k = 0, n = aqua_array_length(rt, list); k < n; k++) {
        aqua_value item = aqua_array_get(rt, list, k);
        items.push_back(item.type == AQUA_STRING ? aqua_str_of(call, item).data : "");
    }
    return items;
}

/* ---- Windows ---- */

static void Begin(aqua_call *call) {
    FRAME;
    bool open;
    bool *p = open_arg(call, 1, &open);
    bool shown = ImGui::Begin(text_arg(call, 0), p, int_arg(call, 2, 0));
    c->last_open = p ? open : true;
    RETURN_BOOL(shown);
}
static void End(aqua_call *call) { FRAME; DO(ImGui::End()); }
static void BeginChild(aqua_call *call) {
    FRAME;
    RETURN_BOOL(ImGui::BeginChild(text_arg(call, 0), ImVec2(float_arg(call, 1, 0), float_arg(call, 2, 0)), int_arg(call, 3, 0), int_arg(call, 4, 0)));
}
static void EndChild(aqua_call *call) { FRAME; DO(ImGui::EndChild()); }
static void LastOpen(aqua_call *call) { FRAME; aqua_return_bool(call, c->last_open); }
static void SetNextWindowPos(aqua_call *call) {
    FRAME; DO(ImGui::SetNextWindowPos(ImVec2(num(call, 0), num(call, 1)), int_arg(call, 2, 0), ImVec2(float_arg(call, 3, 0), float_arg(call, 4, 0))));
}
static void SetNextWindowSize(aqua_call *call) { FRAME; DO(ImGui::SetNextWindowSize(ImVec2(num(call, 0), num(call, 1)), int_arg(call, 2, 0))); }
static void SetNextWindowSizeConstraints(aqua_call *call) {
    FRAME; DO(ImGui::SetNextWindowSizeConstraints(ImVec2(num(call, 0), num(call, 1)), ImVec2(float_arg(call, 2, FLT_MAX), float_arg(call, 3, FLT_MAX))));
}
static void SetNextWindowContentSize(aqua_call *call) { FRAME; DO(ImGui::SetNextWindowContentSize(ImVec2(num(call, 0), num(call, 1)))); }
static void SetNextWindowCollapsed(aqua_call *call) { FRAME; DO(ImGui::SetNextWindowCollapsed(aqua_arg_bool(call, 0), int_arg(call, 1, 0))); }
static void SetNextWindowFocus(aqua_call *call) { FRAME; DO(ImGui::SetNextWindowFocus()); }
static void SetNextWindowScroll(aqua_call *call) { FRAME; DO(ImGui::SetNextWindowScroll(ImVec2(num(call, 0), num(call, 1)))); }
static void SetNextWindowBgAlpha(aqua_call *call) { FRAME; DO(ImGui::SetNextWindowBgAlpha(num(call, 0))); }
static void SetWindowFocus(aqua_call *call) { FRAME; DO(ImGui::SetWindowFocus(text_arg(call, 0))); }
static void IsWindowAppearing(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsWindowAppearing()); }
static void IsWindowCollapsed(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsWindowCollapsed()); }
static void IsWindowFocused(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsWindowFocused(int_arg(call, 0, 0))); }
static void IsWindowHovered(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsWindowHovered(int_arg(call, 0, 0))); }
static void GetWindowPos(aqua_call *call) { FRAME; RETURN_VALUE(vec2_value(rt_of(call), ImGui::GetWindowPos())); }
static void GetWindowSize(aqua_call *call) { FRAME; RETURN_VALUE(vec2_value(rt_of(call), ImGui::GetWindowSize())); }
static void GetWindowWidth(aqua_call *call) { FRAME; RETURN_DOUBLE(ImGui::GetWindowWidth()); }
static void GetWindowHeight(aqua_call *call) { FRAME; RETURN_DOUBLE(ImGui::GetWindowHeight()); }
static void GetContentRegionAvail(aqua_call *call) { FRAME; RETURN_VALUE(vec2_value(rt_of(call), ImGui::GetContentRegionAvail())); }
static void GetCursorPos(aqua_call *call) { FRAME; RETURN_VALUE(vec2_value(rt_of(call), ImGui::GetCursorPos())); }
static void GetCursorScreenPos(aqua_call *call) { FRAME; RETURN_VALUE(vec2_value(rt_of(call), ImGui::GetCursorScreenPos())); }
static void GetCursorStartPos(aqua_call *call) { FRAME; RETURN_VALUE(vec2_value(rt_of(call), ImGui::GetCursorStartPos())); }
static void SetCursorPos(aqua_call *call) { FRAME; DO(ImGui::SetCursorPos(ImVec2(num(call, 0), num(call, 1)))); }
static void SetCursorPosX(aqua_call *call) { FRAME; DO(ImGui::SetCursorPosX(num(call, 0))); }
static void SetCursorPosY(aqua_call *call) { FRAME; DO(ImGui::SetCursorPosY(num(call, 0))); }
static void SetCursorScreenPos(aqua_call *call) { FRAME; DO(ImGui::SetCursorScreenPos(ImVec2(num(call, 0), num(call, 1)))); }
static void GetScrollX(aqua_call *call) { FRAME; RETURN_DOUBLE(ImGui::GetScrollX()); }
static void GetScrollY(aqua_call *call) { FRAME; RETURN_DOUBLE(ImGui::GetScrollY()); }
static void GetScrollMaxX(aqua_call *call) { FRAME; RETURN_DOUBLE(ImGui::GetScrollMaxX()); }
static void GetScrollMaxY(aqua_call *call) { FRAME; RETURN_DOUBLE(ImGui::GetScrollMaxY()); }
static void SetScrollX(aqua_call *call) { FRAME; DO(ImGui::SetScrollX(num(call, 0))); }
static void SetScrollY(aqua_call *call) { FRAME; DO(ImGui::SetScrollY(num(call, 0))); }
static void SetScrollHereX(aqua_call *call) { FRAME; DO(ImGui::SetScrollHereX(float_arg(call, 0, 0.5f))); }
static void SetScrollHereY(aqua_call *call) { FRAME; DO(ImGui::SetScrollHereY(float_arg(call, 0, 0.5f))); }

/* ---- Docking ---- */

static void DockSpaceOverViewport(aqua_call *call) {
    FRAME;
    ImGuiID id = has(call, 0) ? ImGui::GetID(text_arg(call, 0)) : 0;
    RETURN_INT((int)ImGui::DockSpaceOverViewport(id, nullptr, int_arg(call, 1, 0)));
}
static void DockSpace(aqua_call *call) {
    FRAME;
    RETURN_INT((int)ImGui::DockSpace(ImGui::GetID(text_arg(call, 0)), ImVec2(float_arg(call, 1, 0), float_arg(call, 2, 0)), int_arg(call, 3, 0)));
}
static void SetNextWindowDockID(aqua_call *call) { FRAME; DO(ImGui::SetNextWindowDockID((ImGuiID)(uint32_t)aqua_arg_number(call, 0), int_arg(call, 1, 0))); }
static void GetID(aqua_call *call) { FRAME; RETURN_INT((int)ImGui::GetID(text_arg(call, 0))); }

/* ---- Layout and ids ---- */

static void Separator(aqua_call *call) { FRAME; DO(ImGui::Separator()); }
static void SeparatorText(aqua_call *call) { FRAME; DO(ImGui::SeparatorText(text_arg(call, 0))); }
static void SameLine(aqua_call *call) { FRAME; DO(ImGui::SameLine(float_arg(call, 0, 0), float_arg(call, 1, -1))); }
static void NewLine(aqua_call *call) { FRAME; DO(ImGui::NewLine()); }
static void Spacing(aqua_call *call) { FRAME; DO(ImGui::Spacing()); }
static void Dummy(aqua_call *call) { FRAME; DO(ImGui::Dummy(ImVec2(num(call, 0), num(call, 1)))); }
static void Indent(aqua_call *call) { FRAME; DO(ImGui::Indent(float_arg(call, 0, 0))); }
static void Unindent(aqua_call *call) { FRAME; DO(ImGui::Unindent(float_arg(call, 0, 0))); }
static void BeginGroup(aqua_call *call) { FRAME; DO(ImGui::BeginGroup()); }
static void EndGroup(aqua_call *call) { FRAME; DO(ImGui::EndGroup()); }
static void AlignTextToFramePadding(aqua_call *call) { FRAME; DO(ImGui::AlignTextToFramePadding()); }
static void GetTextLineHeight(aqua_call *call) { FRAME; RETURN_DOUBLE(ImGui::GetTextLineHeight()); }
static void GetTextLineHeightWithSpacing(aqua_call *call) { FRAME; RETURN_DOUBLE(ImGui::GetTextLineHeightWithSpacing()); }
static void GetFrameHeight(aqua_call *call) { FRAME; RETURN_DOUBLE(ImGui::GetFrameHeight()); }
static void GetFrameHeightWithSpacing(aqua_call *call) { FRAME; RETURN_DOUBLE(ImGui::GetFrameHeightWithSpacing()); }
static void PushItemWidth(aqua_call *call) { FRAME; DO(ImGui::PushItemWidth(num(call, 0))); }
static void PopItemWidth(aqua_call *call) { FRAME; DO(ImGui::PopItemWidth()); }
static void SetNextItemWidth(aqua_call *call) { FRAME; DO(ImGui::SetNextItemWidth(num(call, 0))); }
static void CalcItemWidth(aqua_call *call) { FRAME; RETURN_DOUBLE(ImGui::CalcItemWidth()); }
static void PushTextWrapPos(aqua_call *call) { FRAME; DO(ImGui::PushTextWrapPos(float_arg(call, 0, 0))); }
static void PopTextWrapPos(aqua_call *call) { FRAME; DO(ImGui::PopTextWrapPos()); }
static void PushID(aqua_call *call) {
    FRAME;
    aqua_value v = aqua_arg(call, 0);
    if (v.type == AQUA_INT) DO(ImGui::PushID((int)v.number)); else DO(ImGui::PushID(text_arg(call, 0)));
}
static void PopID(aqua_call *call) { FRAME; DO(ImGui::PopID()); }
static void CalcTextSize(aqua_call *call) {
    FRAME; RETURN_VALUE(vec2_value(rt_of(call), ImGui::CalcTextSize(text_arg(call, 0), nullptr, bool_arg(call, 1, false), float_arg(call, 2, -1))));
}
static void BeginDisabled(aqua_call *call) { FRAME; DO(ImGui::BeginDisabled(bool_arg(call, 0, true))); }
static void EndDisabled(aqua_call *call) { FRAME; DO(ImGui::EndDisabled()); }

/* Embed(id, width, height): reserves an item whose pixels come from another UI library: context.Render(embed) calls
   embed(id, x, y, width, height) for its draw list and splices it here, clipped to the item. */
static void Embed(aqua_call *call) {
    FRAME;
    ImVec2 size(num(call, 1), num(call, 2));
    ImVec2 min = ImGui::GetCursorScreenPos();
    ImGui::InvisibleButton(text_arg(call, 0), ImVec2(size.x > 0 ? size.x : 1, size.y > 0 ? size.y : 1));
    c->embeds.push_back({text_arg(call, 0), min, ImVec2(min.x + size.x, min.y + size.y)});
    ImGui::GetWindowDrawList()->AddCallback(embed_callback, (void *)(intptr_t)(c->embeds.size() - 1));
    RETURN_BOOL(ImGui::IsItemHovered());
}

/* ---- Text ---- */

static void Text(aqua_call *call) { FRAME; DO(ImGui::TextUnformatted(text_arg(call, 0))); }
static void TextColored(aqua_call *call) {
    FRAME;
    ImVec4 color;
    if (!color_arg(call, 0, &color)) { aqua_fail(call, "ImGui.TextColored: unsupported color."); return; }
    DO(ImGui::TextColored(color, "%s", text_arg(call, 1)));
}
static void TextDisabled(aqua_call *call) { FRAME; DO(ImGui::TextDisabled("%s", text_arg(call, 0))); }
static void TextWrapped(aqua_call *call) { FRAME; DO(ImGui::TextWrapped("%s", text_arg(call, 0))); }
static void LabelText(aqua_call *call) { FRAME; DO(ImGui::LabelText(text_arg(call, 0), "%s", text_arg(call, 1))); }
static void BulletText(aqua_call *call) { FRAME; DO(ImGui::BulletText("%s", text_arg(call, 0))); }
static void TextLink(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::TextLink(text_arg(call, 0))); }
static void Bullet(aqua_call *call) { FRAME; DO(ImGui::Bullet()); }

/* ---- Buttons and toggles ---- */

static void Button(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::Button(text_arg(call, 0), ImVec2(float_arg(call, 1, 0), float_arg(call, 2, 0)))); }
static void SmallButton(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::SmallButton(text_arg(call, 0))); }
static void InvisibleButton(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::InvisibleButton(text_arg(call, 0), ImVec2(num(call, 1), num(call, 2)), int_arg(call, 3, 0))); }
static void ArrowButton(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::ArrowButton(text_arg(call, 0), (ImGuiDir)aqua_arg_int(call, 1))); }
static void Checkbox(aqua_call *call) {
    FRAME;
    bool v = aqua_arg(call, 1).type == AQUA_BOOL && aqua_arg(call, 1).number != 0;
    ImGui::Checkbox(text_arg(call, 0), &v);
    RETURN_BOOL(v);
}
/* RadioButton(label, active) -> clicked; RadioButton(label, current, value) -> the new current value. */
static void RadioButton(aqua_call *call) {
    FRAME;
    if (aqua_argc(call) > 2) {
        int current = aqua_arg_int(call, 1);
        ImGui::RadioButton(text_arg(call, 0), &current, aqua_arg_int(call, 2));
        RETURN_INT(current);
        return;
    }
    RETURN_BOOL(ImGui::RadioButton(text_arg(call, 0), bool_arg(call, 1, false)));
}
static void ProgressBar(aqua_call *call) {
    FRAME; DO(ImGui::ProgressBar(num(call, 0), ImVec2(float_arg(call, 1, -FLT_MIN), float_arg(call, 2, 0)), has(call, 3) ? text_arg(call, 3) : nullptr));
}
static void Image(aqua_call *call) {
    FRAME;
    ImTextureRef texture = texture_arg(call, 0);
    DO(ImGui::Image(texture, ImVec2(num(call, 1), num(call, 2)), vec2_arg(call, 3, ImVec2(0, 0)), vec2_arg(call, 4, ImVec2(1, 1))));
}
static void ImageButton(aqua_call *call) {
    FRAME;
    ImTextureRef texture = texture_arg(call, 1);
    ImVec4 background(0, 0, 0, 0), tint(1, 1, 1, 1);
    if (has(call, 6)) color_arg(call, 6, &background);
    if (has(call, 7)) color_arg(call, 7, &tint);
    RETURN_BOOL(ImGui::ImageButton(text_arg(call, 0), texture, ImVec2(num(call, 2), num(call, 3)), vec2_arg(call, 4, ImVec2(0, 0)),
                                   vec2_arg(call, 5, ImVec2(1, 1)), background, tint));
}

/* ---- Combos, list boxes, selectables ---- */

static void Combo(aqua_call *call) {
    FRAME;
    int current = aqua_arg_int(call, 1);
    std::vector<const char *> items = items_of(call, 2);
    ImGui::Combo(text_arg(call, 0), &current, items.data(), (int)items.size(), int_arg(call, 3, -1));
    RETURN_INT(current);
}
static void BeginCombo(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::BeginCombo(text_arg(call, 0), text_arg(call, 1), int_arg(call, 2, 0))); }
static void EndCombo(aqua_call *call) { FRAME; DO(ImGui::EndCombo()); }
static void ListBox(aqua_call *call) {
    FRAME;
    int current = aqua_arg_int(call, 1);
    std::vector<const char *> items = items_of(call, 2);
    ImGui::ListBox(text_arg(call, 0), &current, items.data(), (int)items.size(), int_arg(call, 3, -1));
    RETURN_INT(current);
}
static void BeginListBox(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::BeginListBox(text_arg(call, 0), ImVec2(float_arg(call, 1, 0), float_arg(call, 2, 0)))); }
static void EndListBox(aqua_call *call) { FRAME; DO(ImGui::EndListBox()); }
static void Selectable(aqua_call *call) {
    FRAME; RETURN_BOOL(ImGui::Selectable(text_arg(call, 0), bool_arg(call, 1, false), int_arg(call, 2, 0), ImVec2(float_arg(call, 3, 0), float_arg(call, 4, 0))));
}

/* ---- Drags, sliders and inputs: each returns the new value ---- */

#define FLOAT_EDITOR(Name, call_expression) static void Name(aqua_call *call) { \
        FRAME; float v = num(call, 1); call_expression; RETURN_DOUBLE(v); }
#define FLOATS_EDITOR(Name, N, call_expression) static void Name(aqua_call *call) { \
        FRAME; float v[N]; if (!read_floats(call, 1, v, N)) return; call_expression; RETURN_VALUE(floats_value(rt_of(call), v, N)); }
#define INT_EDITOR(Name, call_expression) static void Name(aqua_call *call) { \
        FRAME; int v = aqua_arg_int(call, 1); call_expression; RETURN_INT(v); }
#define INTS_EDITOR(Name, N, call_expression) static void Name(aqua_call *call) { \
        FRAME; int v[N]; if (!read_ints(call, 1, v, N)) return; call_expression; RETURN_VALUE(ints_value(rt_of(call), v, N)); }

#define DRAG_ARGS float_arg(call, 2, 1.0f), float_arg(call, 3, 0), float_arg(call, 4, 0), format_arg(call, 5, "%.3f"), int_arg(call, 6, 0)
#define DRAG_INT_ARGS float_arg(call, 2, 1.0f), int_arg(call, 3, 0), int_arg(call, 4, 0), format_arg(call, 5, "%d"), int_arg(call, 6, 0)
#define SLIDER_ARGS num(call, 2), num(call, 3), format_arg(call, 4, "%.3f"), int_arg(call, 5, 0)
#define SLIDER_INT_ARGS aqua_arg_int(call, 2), aqua_arg_int(call, 3), format_arg(call, 4, "%d"), int_arg(call, 5, 0)
FLOAT_EDITOR(DragFloat, ImGui::DragFloat(text_arg(call, 0), &v, DRAG_ARGS))
FLOATS_EDITOR(DragFloat2, 2, ImGui::DragFloat2(text_arg(call, 0), v, DRAG_ARGS))
FLOATS_EDITOR(DragFloat3, 3, ImGui::DragFloat3(text_arg(call, 0), v, DRAG_ARGS))
FLOATS_EDITOR(DragFloat4, 4, ImGui::DragFloat4(text_arg(call, 0), v, DRAG_ARGS))
INT_EDITOR(DragInt, ImGui::DragInt(text_arg(call, 0), &v, DRAG_INT_ARGS))
INTS_EDITOR(DragInt2, 2, ImGui::DragInt2(text_arg(call, 0), v, DRAG_INT_ARGS))
INTS_EDITOR(DragInt3, 3, ImGui::DragInt3(text_arg(call, 0), v, DRAG_INT_ARGS))
INTS_EDITOR(DragInt4, 4, ImGui::DragInt4(text_arg(call, 0), v, DRAG_INT_ARGS))
FLOAT_EDITOR(SliderFloat, ImGui::SliderFloat(text_arg(call, 0), &v, SLIDER_ARGS))
FLOATS_EDITOR(SliderFloat2, 2, ImGui::SliderFloat2(text_arg(call, 0), v, SLIDER_ARGS))
FLOATS_EDITOR(SliderFloat3, 3, ImGui::SliderFloat3(text_arg(call, 0), v, SLIDER_ARGS))
FLOATS_EDITOR(SliderFloat4, 4, ImGui::SliderFloat4(text_arg(call, 0), v, SLIDER_ARGS))
INT_EDITOR(SliderInt, ImGui::SliderInt(text_arg(call, 0), &v, SLIDER_INT_ARGS))
INTS_EDITOR(SliderInt2, 2, ImGui::SliderInt2(text_arg(call, 0), v, SLIDER_INT_ARGS))
INTS_EDITOR(SliderInt3, 3, ImGui::SliderInt3(text_arg(call, 0), v, SLIDER_INT_ARGS))
INTS_EDITOR(SliderInt4, 4, ImGui::SliderInt4(text_arg(call, 0), v, SLIDER_INT_ARGS))
FLOAT_EDITOR(SliderAngle, ImGui::SliderAngle(text_arg(call, 0), &v, float_arg(call, 2, -360), float_arg(call, 3, 360), format_arg(call, 4, "%.0f deg"), int_arg(call, 5, 0)))
static void VSliderFloat(aqua_call *call) {
    FRAME;
    float v = num(call, 3);
    ImGui::VSliderFloat(text_arg(call, 0), ImVec2(num(call, 1), num(call, 2)), &v, num(call, 4), num(call, 5), format_arg(call, 6, "%.3f"), int_arg(call, 7, 0));
    RETURN_DOUBLE(v);
}
#define INPUT_ARGS float_arg(call, 2, 0), float_arg(call, 3, 0), format_arg(call, 4, "%.3f"), int_arg(call, 5, 0)
FLOAT_EDITOR(InputFloat, ImGui::InputFloat(text_arg(call, 0), &v, INPUT_ARGS))
FLOATS_EDITOR(InputFloat2, 2, ImGui::InputFloat2(text_arg(call, 0), v, format_arg(call, 2, "%.3f"), int_arg(call, 3, 0)))
FLOATS_EDITOR(InputFloat3, 3, ImGui::InputFloat3(text_arg(call, 0), v, format_arg(call, 2, "%.3f"), int_arg(call, 3, 0)))
FLOATS_EDITOR(InputFloat4, 4, ImGui::InputFloat4(text_arg(call, 0), v, format_arg(call, 2, "%.3f"), int_arg(call, 3, 0)))
INT_EDITOR(InputInt, ImGui::InputInt(text_arg(call, 0), &v, int_arg(call, 2, 1), int_arg(call, 3, 100), int_arg(call, 4, 0)))
INTS_EDITOR(InputInt2, 2, ImGui::InputInt2(text_arg(call, 0), v, int_arg(call, 2, 0)))
INTS_EDITOR(InputInt3, 3, ImGui::InputInt3(text_arg(call, 0), v, int_arg(call, 2, 0)))
INTS_EDITOR(InputInt4, 4, ImGui::InputInt4(text_arg(call, 0), v, int_arg(call, 2, 0)))
static void InputDouble(aqua_call *call) {
    FRAME;
    double v = aqua_arg_number(call, 1);
    ImGui::InputDouble(text_arg(call, 0), &v, has(call, 2) ? aqua_arg_number(call, 2) : 0, has(call, 3) ? aqua_arg_number(call, 3) : 0,
                       format_arg(call, 4, "%.6f"), int_arg(call, 5, 0));
    RETURN_DOUBLE(v);
}

/* Text fields edit a buffer sized to the text plus `capacity` more bytes (default 1024). */
static std::vector<char> text_buffer(aqua_call *call, uint32_t index, uint32_t capacity_index) {
    const char *text = text_arg(call, index);
    size_t length = strlen(text), extra = (size_t)int_arg(call, capacity_index, 1024);
    std::vector<char> buffer(length + extra + 1, 0);
    memcpy(buffer.data(), text, length);
    return buffer;
}
static void InputText(aqua_call *call) {
    FRAME;
    std::vector<char> buffer = text_buffer(call, 1, 3);
    ImGui::InputText(text_arg(call, 0), buffer.data(), buffer.size(), int_arg(call, 2, 0));
    if (!report_assert(call)) aqua_return_cstring(call, buffer.data());
}
static void InputTextMultiline(aqua_call *call) {
    FRAME;
    std::vector<char> buffer = text_buffer(call, 1, 5);
    ImGui::InputTextMultiline(text_arg(call, 0), buffer.data(), buffer.size(), ImVec2(float_arg(call, 2, 0), float_arg(call, 3, 0)), int_arg(call, 4, 0));
    if (!report_assert(call)) aqua_return_cstring(call, buffer.data());
}
static void InputTextWithHint(aqua_call *call) {
    FRAME;
    std::vector<char> buffer = text_buffer(call, 2, 4);
    ImGui::InputTextWithHint(text_arg(call, 0), text_arg(call, 1), buffer.data(), buffer.size(), int_arg(call, 3, 0));
    if (!report_assert(call)) aqua_return_cstring(call, buffer.data());
}

/* ---- Colors: [r, g, b(, a)] with 0..1 components ---- */

FLOATS_EDITOR(ColorEdit3, 3, ImGui::ColorEdit3(text_arg(call, 0), v, int_arg(call, 2, 0)))
FLOATS_EDITOR(ColorEdit4, 4, ImGui::ColorEdit4(text_arg(call, 0), v, int_arg(call, 2, 0)))
FLOATS_EDITOR(ColorPicker3, 3, ImGui::ColorPicker3(text_arg(call, 0), v, int_arg(call, 2, 0)))
FLOATS_EDITOR(ColorPicker4, 4, ImGui::ColorPicker4(text_arg(call, 0), v, int_arg(call, 2, 0)))
static void ColorButton(aqua_call *call) {
    FRAME;
    ImVec4 color;
    if (!color_arg(call, 1, &color)) { aqua_fail(call, "ImGui.ColorButton: unsupported color."); return; }
    RETURN_BOOL(ImGui::ColorButton(text_arg(call, 0), color, int_arg(call, 2, 0), ImVec2(float_arg(call, 3, 0), float_arg(call, 4, 0))));
}

/* ---- Trees, headers, tabs ---- */

static void TreeNode(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::TreeNodeEx(text_arg(call, 0), int_arg(call, 1, 0))); }
static void TreePush(aqua_call *call) { FRAME; DO(ImGui::TreePush(text_arg(call, 0))); }
static void TreePop(aqua_call *call) { FRAME; DO(ImGui::TreePop()); }
static void SetNextItemOpen(aqua_call *call) { FRAME; DO(ImGui::SetNextItemOpen(aqua_arg_bool(call, 0), int_arg(call, 1, 0))); }
static void CollapsingHeader(aqua_call *call) {
    FRAME;
    bool open;
    bool *p = open_arg(call, 1, &open);
    bool shown = p ? ImGui::CollapsingHeader(text_arg(call, 0), p, int_arg(call, 2, 0)) : ImGui::CollapsingHeader(text_arg(call, 0), int_arg(call, 2, 0));
    c->last_open = p ? open : true;
    RETURN_BOOL(shown);
}
static void BeginTabBar(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::BeginTabBar(text_arg(call, 0), int_arg(call, 1, 0))); }
static void EndTabBar(aqua_call *call) { FRAME; DO(ImGui::EndTabBar()); }
static void BeginTabItem(aqua_call *call) {
    FRAME;
    bool open;
    bool *p = open_arg(call, 1, &open);
    bool shown = ImGui::BeginTabItem(text_arg(call, 0), p, int_arg(call, 2, 0));
    c->last_open = p ? open : true;
    RETURN_BOOL(shown);
}
static void EndTabItem(aqua_call *call) { FRAME; DO(ImGui::EndTabItem()); }
static void TabItemButton(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::TabItemButton(text_arg(call, 0), int_arg(call, 1, 0))); }
static void SetTabItemClosed(aqua_call *call) { FRAME; DO(ImGui::SetTabItemClosed(text_arg(call, 0))); }

/* ---- Tables ---- */

static void BeginTable(aqua_call *call) {
    FRAME;
    int columns = aqua_arg_int(call, 1);
    if (columns < 1 || columns > 512) { aqua_fail(call, "ImGui.BeginTable: 1..512 columns."); return; }
    RETURN_BOOL(ImGui::BeginTable(text_arg(call, 0), columns, int_arg(call, 2, 0), ImVec2(float_arg(call, 3, 0), float_arg(call, 4, 0)), float_arg(call, 5, 0)));
}
static void EndTable(aqua_call *call) { FRAME; DO(ImGui::EndTable()); }
static void TableNextRow(aqua_call *call) { FRAME; DO(ImGui::TableNextRow(int_arg(call, 0, 0), float_arg(call, 1, 0))); }
static void TableNextColumn(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::TableNextColumn()); }
static void TableSetColumnIndex(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::TableSetColumnIndex(aqua_arg_int(call, 0))); }
static void TableSetupColumn(aqua_call *call) { FRAME; DO(ImGui::TableSetupColumn(text_arg(call, 0), int_arg(call, 1, 0), float_arg(call, 2, 0))); }
static void TableSetupScrollFreeze(aqua_call *call) { FRAME; DO(ImGui::TableSetupScrollFreeze(aqua_arg_int(call, 0), aqua_arg_int(call, 1))); }
static void TableHeadersRow(aqua_call *call) { FRAME; DO(ImGui::TableHeadersRow()); }
static void TableHeader(aqua_call *call) { FRAME; DO(ImGui::TableHeader(text_arg(call, 0))); }
static void TableGetColumnCount(aqua_call *call) { FRAME; RETURN_INT(ImGui::TableGetColumnCount()); }
static void TableGetColumnIndex(aqua_call *call) { FRAME; RETURN_INT(ImGui::TableGetColumnIndex()); }
static void TableGetRowIndex(aqua_call *call) { FRAME; RETURN_INT(ImGui::TableGetRowIndex()); }
static void TableSetBgColor(aqua_call *call) { FRAME; ImU32 color = u32_color_arg(call, 1, 0); DO(ImGui::TableSetBgColor(aqua_arg_int(call, 0), color, int_arg(call, 2, -1))); }

/* ---- Menus, popups, tooltips ---- */

static void BeginMenuBar(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::BeginMenuBar()); }
static void EndMenuBar(aqua_call *call) { FRAME; DO(ImGui::EndMenuBar()); }
static void BeginMainMenuBar(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::BeginMainMenuBar()); }
static void EndMainMenuBar(aqua_call *call) { FRAME; DO(ImGui::EndMainMenuBar()); }
static void BeginMenu(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::BeginMenu(text_arg(call, 0), bool_arg(call, 1, true))); }
static void EndMenu(aqua_call *call) { FRAME; DO(ImGui::EndMenu()); }
static void MenuItem(aqua_call *call) {
    FRAME; RETURN_BOOL(ImGui::MenuItem(text_arg(call, 0), has(call, 1) ? text_arg(call, 1) : nullptr, bool_arg(call, 2, false), bool_arg(call, 3, true)));
}
static void OpenPopup(aqua_call *call) { FRAME; DO(ImGui::OpenPopup(text_arg(call, 0), int_arg(call, 1, 0))); }
static void BeginPopup(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::BeginPopup(text_arg(call, 0), int_arg(call, 1, 0))); }
static void BeginPopupModal(aqua_call *call) {
    FRAME;
    bool open;
    bool *p = open_arg(call, 1, &open);
    bool shown = ImGui::BeginPopupModal(text_arg(call, 0), p, int_arg(call, 2, 0));
    c->last_open = p ? open : true;
    RETURN_BOOL(shown);
}
static void EndPopup(aqua_call *call) { FRAME; DO(ImGui::EndPopup()); }
static void CloseCurrentPopup(aqua_call *call) { FRAME; DO(ImGui::CloseCurrentPopup()); }
static void BeginPopupContextItem(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::BeginPopupContextItem(has(call, 0) ? text_arg(call, 0) : nullptr, int_arg(call, 1, 1))); }
static void BeginPopupContextWindow(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::BeginPopupContextWindow(has(call, 0) ? text_arg(call, 0) : nullptr, int_arg(call, 1, 1))); }
static void BeginPopupContextVoid(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::BeginPopupContextVoid(has(call, 0) ? text_arg(call, 0) : nullptr, int_arg(call, 1, 1))); }
static void IsPopupOpen(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsPopupOpen(text_arg(call, 0), int_arg(call, 1, 0))); }
static void BeginTooltip(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::BeginTooltip()); }
static void EndTooltip(aqua_call *call) { FRAME; DO(ImGui::EndTooltip()); }
static void SetTooltip(aqua_call *call) { FRAME; DO(ImGui::SetTooltip("%s", text_arg(call, 0))); }
static void BeginItemTooltip(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::BeginItemTooltip()); }
static void SetItemTooltip(aqua_call *call) { FRAME; DO(ImGui::SetItemTooltip("%s", text_arg(call, 0))); }

/* ---- Plots ---- */

static void plot(aqua_call *call, bool histogram) {
    FRAME;
    aqua_runtime *rt = rt_of(call);
    aqua_value list = aqua_arg(call, 1);
    std::vector<float> values(aqua_array_length(rt, list));
    if (!values.empty() && !aqua_array_read_f32(rt, list, 0, (uint32_t)values.size(), values.data())) { aqua_fail(call, "ImGui: plot values must be numbers."); return; }
    const char *overlay = has(call, 3) ? text_arg(call, 3) : nullptr;
    ImVec2 size(float_arg(call, 6, 0), float_arg(call, 7, 0));
    if (histogram) ImGui::PlotHistogram(text_arg(call, 0), values.data(), (int)values.size(), int_arg(call, 2, 0), overlay, float_arg(call, 4, FLT_MAX), float_arg(call, 5, FLT_MAX), size);
    else ImGui::PlotLines(text_arg(call, 0), values.data(), (int)values.size(), int_arg(call, 2, 0), overlay, float_arg(call, 4, FLT_MAX), float_arg(call, 5, FLT_MAX), size);
    report_assert(call);
}
static void PlotLines(aqua_call *call) { plot(call, false); }
static void PlotHistogram(aqua_call *call) { plot(call, true); }

/* ---- Item and input queries ---- */

static void IsItemHovered(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsItemHovered(int_arg(call, 0, 0))); }
static void IsItemActive(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsItemActive()); }
static void IsItemFocused(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsItemFocused()); }
static void IsItemClicked(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsItemClicked(int_arg(call, 0, 0))); }
static void IsItemVisible(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsItemVisible()); }
static void IsItemEdited(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsItemEdited()); }
static void IsItemActivated(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsItemActivated()); }
static void IsItemDeactivated(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsItemDeactivated()); }
static void IsItemDeactivatedAfterEdit(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsItemDeactivatedAfterEdit()); }
static void IsItemToggledOpen(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsItemToggledOpen()); }
static void IsAnyItemHovered(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsAnyItemHovered()); }
static void IsAnyItemActive(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsAnyItemActive()); }
static void IsAnyItemFocused(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::IsAnyItemFocused()); }
static void GetItemRectMin(aqua_call *call) { FRAME; RETURN_VALUE(vec2_value(rt_of(call), ImGui::GetItemRectMin())); }
static void GetItemRectMax(aqua_call *call) { FRAME; RETURN_VALUE(vec2_value(rt_of(call), ImGui::GetItemRectMax())); }
static void GetItemRectSize(aqua_call *call) { FRAME; RETURN_VALUE(vec2_value(rt_of(call), ImGui::GetItemRectSize())); }
static void SetItemDefaultFocus(aqua_call *call) { FRAME; DO(ImGui::SetItemDefaultFocus()); }
static void SetKeyboardFocusHere(aqua_call *call) { FRAME; DO(ImGui::SetKeyboardFocusHere(int_arg(call, 0, 0))); }
static void SetNextItemAllowOverlap(aqua_call *call) { FRAME; DO(ImGui::SetNextItemAllowOverlap()); }

static bool key_arg(aqua_call *call, ImGuiKey *key) {
    int k = aqua_arg_int(call, 0);
    if (k < ImGuiKey_NamedKey_BEGIN || k >= ImGuiKey_NamedKey_END) { aqua_fail(call, "ImGui: expects an ImGui.Key_* constant."); return false; }
    *key = (ImGuiKey)k;
    return true;
}
static void IsKeyDown(aqua_call *call) { FRAME; ImGuiKey k; if (key_arg(call, &k)) RETURN_BOOL(ImGui::IsKeyDown(k)); }
static void IsKeyPressed(aqua_call *call) { FRAME; ImGuiKey k; if (key_arg(call, &k)) RETURN_BOOL(ImGui::IsKeyPressed(k, bool_arg(call, 1, true))); }
static void IsKeyReleased(aqua_call *call) { FRAME; ImGuiKey k; if (key_arg(call, &k)) RETURN_BOOL(ImGui::IsKeyReleased(k)); }
static bool button_arg(aqua_call *call, uint32_t i, int *button) {
    *button = int_arg(call, i, 0);
    if (*button < 0 || *button >= ImGuiMouseButton_COUNT) { aqua_fail(call, "ImGui: mouse buttons are 0..4."); return false; }
    return true;
}
static void IsMouseDown(aqua_call *call) { FRAME; int b; if (button_arg(call, 0, &b)) RETURN_BOOL(ImGui::IsMouseDown(b)); }
static void IsMouseClicked(aqua_call *call) { FRAME; int b; if (button_arg(call, 0, &b)) RETURN_BOOL(ImGui::IsMouseClicked(b, bool_arg(call, 1, false))); }
static void IsMouseReleased(aqua_call *call) { FRAME; int b; if (button_arg(call, 0, &b)) RETURN_BOOL(ImGui::IsMouseReleased(b)); }
static void IsMouseDoubleClicked(aqua_call *call) { FRAME; int b; if (button_arg(call, 0, &b)) RETURN_BOOL(ImGui::IsMouseDoubleClicked(b)); }
static void IsMouseDragging(aqua_call *call) { FRAME; int b; if (button_arg(call, 0, &b)) RETURN_BOOL(ImGui::IsMouseDragging(b, float_arg(call, 1, -1))); }
static void GetMousePos(aqua_call *call) { FRAME; RETURN_VALUE(vec2_value(rt_of(call), ImGui::GetMousePos())); }
static void GetMouseDragDelta(aqua_call *call) { FRAME; int b; if (button_arg(call, 0, &b)) RETURN_VALUE(vec2_value(rt_of(call), ImGui::GetMouseDragDelta(b, float_arg(call, 1, -1)))); }
static void ResetMouseDragDelta(aqua_call *call) { FRAME; int b; if (button_arg(call, 0, &b)) DO(ImGui::ResetMouseDragDelta(b)); }
static void SetMouseCursor(aqua_call *call) { FRAME; DO(ImGui::SetMouseCursor(aqua_arg_int(call, 0))); }
static void GetTime(aqua_call *call) { FRAME; RETURN_DOUBLE(ImGui::GetTime()); }
static void GetFrameCount(aqua_call *call) { FRAME; RETURN_INT(ImGui::GetFrameCount()); }

/* ---- Drag and drop: payloads are any Aquarius values ---- */

static void BeginDragDropSource(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::BeginDragDropSource(int_arg(call, 0, 0))); }
static void SetDragDropPayload(aqua_call *call) {
    FRAME;
    std::string type = text_arg(call, 0);
    if (type.size() > 32) { aqua_fail(call, "ImGui.SetDragDropPayload: payload types are at most 32 bytes."); return; }
    auto found = c->payloads.find(type);
    if (found != c->payloads.end()) aqua_release(c->rt, found->second);
    c->payloads[type] = aqua_retain(c->rt, aqua_arg(call, 1));
    int marker = 1;
    RETURN_BOOL(ImGui::SetDragDropPayload(type.c_str(), &marker, sizeof marker, int_arg(call, 2, 0)));
}
static void EndDragDropSource(aqua_call *call) { FRAME; DO(ImGui::EndDragDropSource()); }
static void BeginDragDropTarget(aqua_call *call) { FRAME; RETURN_BOOL(ImGui::BeginDragDropTarget()); }
static void AcceptDragDropPayload(aqua_call *call) {
    FRAME;
    std::string type = text_arg(call, 0);
    const ImGuiPayload *payload = ImGui::AcceptDragDropPayload(type.c_str(), int_arg(call, 1, 0));
    auto found = c->payloads.find(type);
    if (!payload || found == c->payloads.end()) { if (!report_assert(call)) aqua_return_null(call); return; }
    RETURN_VALUE(aqua_ref_value(c->rt, found->second));
}
static void EndDragDropTarget(aqua_call *call) { FRAME; DO(ImGui::EndDragDropTarget()); }

/* ---- Style ---- */

static void StyleColorsDark(aqua_call *call) { FRAME; DO(ImGui::StyleColorsDark()); }
static void StyleColorsLight(aqua_call *call) { FRAME; DO(ImGui::StyleColorsLight()); }
static void StyleColorsClassic(aqua_call *call) { FRAME; DO(ImGui::StyleColorsClassic()); }
static bool col_arg(aqua_call *call, int *col) {
    *col = aqua_arg_int(call, 0);
    if (*col < 0 || *col >= ImGuiCol_COUNT) { aqua_fail(call, "ImGui: expects an ImGui.Col_* constant."); return false; }
    return true;
}
static void PushStyleColor(aqua_call *call) {
    FRAME;
    int col;
    ImVec4 color;
    if (!col_arg(call, &col)) return;
    if (!color_arg(call, 1, &color)) { aqua_fail(call, "ImGui.PushStyleColor: unsupported color."); return; }
    DO(ImGui::PushStyleColor(col, color));
}
static void PopStyleColor(aqua_call *call) { FRAME; DO(ImGui::PopStyleColor(int_arg(call, 0, 1))); }
static void PushStyleVar(aqua_call *call) {
    FRAME;
    int var = aqua_arg_int(call, 0);
    if (var < 0 || var >= ImGuiStyleVar_COUNT) { aqua_fail(call, "ImGui.PushStyleVar: expects an ImGui.StyleVar_* constant."); return; }
    if (aqua_arg(call, 1).type == AQUA_ARRAY) DO(ImGui::PushStyleVar(var, vec2_arg(call, 1, ImVec2(0, 0))));
    else DO(ImGui::PushStyleVar(var, num(call, 1)));
}
static void PopStyleVar(aqua_call *call) { FRAME; DO(ImGui::PopStyleVar(int_arg(call, 0, 1))); }
static void GetStyleColor(aqua_call *call) {
    FRAME;
    int col;
    if (!col_arg(call, &col)) return;
    ImVec4 v = ImGui::GetStyle().Colors[col];
    float values[4] = {v.x, v.y, v.z, v.w};
    RETURN_VALUE(floats_value(rt_of(call), values, 4));
}
static void SetStyleColor(aqua_call *call) {
    FRAME;
    int col;
    ImVec4 color;
    if (!col_arg(call, &col)) return;
    if (!color_arg(call, 1, &color)) { aqua_fail(call, "ImGui.SetStyleColor: unsupported color."); return; }
    ImGui::GetStyle().Colors[col] = color;
}
/* SetStyle({"windowRounding": 8, "framePadding": [8, 4], ...}): ImGuiStyle fields by their names, first letter lower. */
static void SetStyle(aqua_call *call) {
    FRAME;
    aqua_runtime *rt = rt_of(call);
    aqua_value h = aqua_arg(call, 0);
    ImGuiStyle &style = ImGui::GetStyle();
    static const struct { const char *name; size_t offset; bool pair; } fields[] = {
#define F(field, name) {name, offsetof(ImGuiStyle, field), false}
#define V(field, name) {name, offsetof(ImGuiStyle, field), true}
        F(Alpha, "alpha"), F(DisabledAlpha, "disabledAlpha"), V(WindowPadding, "windowPadding"), F(WindowRounding, "windowRounding"),
        F(WindowBorderSize, "windowBorderSize"), V(WindowMinSize, "windowMinSize"), V(WindowTitleAlign, "windowTitleAlign"),
        F(ChildRounding, "childRounding"), F(ChildBorderSize, "childBorderSize"), F(PopupRounding, "popupRounding"),
        F(PopupBorderSize, "popupBorderSize"), V(FramePadding, "framePadding"), F(FrameRounding, "frameRounding"),
        F(FrameBorderSize, "frameBorderSize"), V(ItemSpacing, "itemSpacing"), V(ItemInnerSpacing, "itemInnerSpacing"),
        V(CellPadding, "cellPadding"), V(TouchExtraPadding, "touchExtraPadding"), F(IndentSpacing, "indentSpacing"),
        F(ColumnsMinSpacing, "columnsMinSpacing"), F(ScrollbarSize, "scrollbarSize"), F(ScrollbarRounding, "scrollbarRounding"),
        F(GrabMinSize, "grabMinSize"), F(GrabRounding, "grabRounding"), F(TabRounding, "tabRounding"), F(TabBorderSize, "tabBorderSize"),
        V(ButtonTextAlign, "buttonTextAlign"), V(SelectableTextAlign, "selectableTextAlign"), F(FontScaleMain, "fontScale"),
        F(FontSizeBase, "fontSize"), F(DockingSeparatorSize, "dockingSeparatorSize"),
#undef F
#undef V
    };
    for (uint32_t i = 0, n = aqua_hash_count(rt, h); i < n; i++) {
        aqua_value key, value;
        aqua_hash_entry(rt, h, i, &key, &value);
        const char *name = key.type == AQUA_STRING ? aqua_str_of(call, key).data : "";
        bool found = false;
        for (auto &f : fields) {
            if (strcmp(f.name, name)) continue;
            found = true;
            float *target = reinterpret_cast<float *>(reinterpret_cast<char *>(&style) + f.offset);
            if (f.pair) {
                float xy[2];
                if (value.type != AQUA_ARRAY || aqua_array_length(rt, value) != 2 || !aqua_array_read_f32(rt, value, 0, 2, xy)) { aqua_fail(call, "ImGui.SetStyle: %s is [x, y].", name); return; }
                target[0] = xy[0]; target[1] = xy[1];
            } else if (aqua_is_numeric(value)) *target = (float)aqua_number_of(value);
            else { aqua_fail(call, "ImGui.SetStyle: %s is a number.", name); return; }
        }
        if (!found) { aqua_fail(call, "ImGui.SetStyle: unknown style field %s.", name); return; }
    }
}
static void ScaleAllSizes(aqua_call *call) { FRAME; DO(ImGui::GetStyle().ScaleAllSizes(num(call, 0))); }
static void PushFont(aqua_call *call) {
    FRAME;
    int index = aqua_arg_int(call, 0);
    if (index < -1 || index >= (int)c->fonts.size()) { aqua_fail(call, "ImGui.PushFont: no font %d (context.AddFont returns the index).", index); return; }
    DO(ImGui::PushFont(index < 0 ? nullptr : c->fonts[(size_t)index], float_arg(call, 1, 0)));
}
static void PopFont(aqua_call *call) { FRAME; DO(ImGui::PopFont()); }
static void GetFontSize(aqua_call *call) { FRAME; RETURN_DOUBLE(ImGui::GetFontSize()); }

/* ---- Tools ---- */

static void show(aqua_call *call, void (*window)(bool *)) {
    FRAME;
    bool open = bool_arg(call, 0, true);
    window(aqua_argc(call) > 0 ? &open : nullptr);
    RETURN_BOOL(open);
}
static void ShowDemoWindow(aqua_call *call) { show(call, ImGui::ShowDemoWindow); }
static void ShowMetricsWindow(aqua_call *call) { show(call, ImGui::ShowMetricsWindow); }
static void ShowDebugLogWindow(aqua_call *call) { show(call, ImGui::ShowDebugLogWindow); }
static void ShowIDStackToolWindow(aqua_call *call) { show(call, ImGui::ShowIDStackToolWindow); }
static void ShowAboutWindow(aqua_call *call) { show(call, ImGui::ShowAboutWindow); }
static void ShowStyleEditor(aqua_call *call) { FRAME; DO(ImGui::ShowStyleEditor()); }
static void ShowUserGuide(aqua_call *call) { FRAME; DO(ImGui::ShowUserGuide()); }

void define_widgets(aqua_class *m) {
    struct Entry { const char *names; aqua_function function; const char *signature; };
    static const Entry entries[] = {
#define E(name, signature) {#name, name, signature}
        E(Begin, "v|vi"), E(End, ""), E(BeginChild, "v|nnii"), E(EndChild, ""), E(LastOpen, ""),
        E(SetNextWindowPos, "nn|inn"), E(SetNextWindowSize, "nn|i"), E(SetNextWindowSizeConstraints, "nn|nn"),
        E(SetNextWindowContentSize, "nn"), E(SetNextWindowCollapsed, "b|i"), E(SetNextWindowFocus, ""), E(SetNextWindowScroll, "nn"),
        E(SetNextWindowBgAlpha, "n"), E(SetWindowFocus, "v"), E(IsWindowAppearing, ""), E(IsWindowCollapsed, ""),
        E(IsWindowFocused, "|i"), E(IsWindowHovered, "|i"), E(GetWindowPos, ""), E(GetWindowSize, ""), E(GetWindowWidth, ""),
        E(GetWindowHeight, ""), E(GetContentRegionAvail, ""), E(GetCursorPos, ""), E(GetCursorScreenPos, ""), E(GetCursorStartPos, ""),
        E(SetCursorPos, "nn"), E(SetCursorPosX, "n"), E(SetCursorPosY, "n"), E(SetCursorScreenPos, "nn"), E(GetScrollX, ""),
        E(GetScrollY, ""), E(GetScrollMaxX, ""), E(GetScrollMaxY, ""), E(SetScrollX, "n"), E(SetScrollY, "n"), E(SetScrollHereX, "|n"),
        E(SetScrollHereY, "|n"), E(DockSpaceOverViewport, "|vi"), E(DockSpace, "v|nni"), E(SetNextWindowDockID, "n|i"), E(GetID, "v"),
        E(Separator, ""), E(SeparatorText, "v"), E(SameLine, "|nn"), E(NewLine, ""), E(Spacing, ""), E(Dummy, "nn"), E(Indent, "|n"),
        E(Unindent, "|n"), E(BeginGroup, ""), E(EndGroup, ""), E(AlignTextToFramePadding, ""), E(GetTextLineHeight, ""),
        E(GetTextLineHeightWithSpacing, ""), E(GetFrameHeight, ""), E(GetFrameHeightWithSpacing, ""), E(PushItemWidth, "n"),
        E(PopItemWidth, ""), E(SetNextItemWidth, "n"), E(CalcItemWidth, ""), E(PushTextWrapPos, "|n"), E(PopTextWrapPos, ""),
        E(PushID, "v"), E(PopID, ""), E(CalcTextSize, "v|bn"), E(BeginDisabled, "|b"), E(EndDisabled, ""), E(Embed, "vnn"),
        E(Text, "v"), E(TextColored, "vv"), E(TextDisabled, "v"), E(TextWrapped, "v"), E(LabelText, "vv"), E(BulletText, "v"),
        E(TextLink, "v"), E(Bullet, ""), E(Button, "v|nn"), E(SmallButton, "v"), E(InvisibleButton, "vnn|i"), E(ArrowButton, "vi"),
        E(Checkbox, "vv"), E(RadioButton, "vv|i"), E(ProgressBar, "n|nnv"), E(Image, "vnn|vv"), E(ImageButton, "vvnn|vvvv"),
        E(Combo, "via|i"), E(BeginCombo, "vv|i"), E(EndCombo, ""), E(ListBox, "via|i"), E(BeginListBox, "v|nn"), E(EndListBox, ""),
        E(Selectable, "v|vinn"), E(DragFloat, "vn|nnnvi"), E(DragFloat2, "va|nnnvi"), E(DragFloat3, "va|nnnvi"), E(DragFloat4, "va|nnnvi"),
        E(DragInt, "vn|nnnvi"), E(DragInt2, "va|nnnvi"), E(DragInt3, "va|nnnvi"), E(DragInt4, "va|nnnvi"), E(SliderFloat, "vnnn|vi"),
        E(SliderFloat2, "vann|vi"), E(SliderFloat3, "vann|vi"), E(SliderFloat4, "vann|vi"), E(SliderInt, "vnnn|vi"),
        E(SliderInt2, "vann|vi"), E(SliderInt3, "vann|vi"), E(SliderInt4, "vann|vi"), E(SliderAngle, "vn|nnvi"),
        E(VSliderFloat, "vnnnnn|vi"), E(InputFloat, "vn|nnvi"), E(InputFloat2, "va|vi"), E(InputFloat3, "va|vi"), E(InputFloat4, "va|vi"),
        E(InputInt, "vn|iii"), E(InputInt2, "va|i"), E(InputInt3, "va|i"), E(InputInt4, "va|i"), E(InputDouble, "vn|nnvi"),
        E(InputText, "vv|ii"), E(InputTextMultiline, "vv|nnii"), E(InputTextWithHint, "vvv|ii"), E(ColorEdit3, "va|i"),
        E(ColorEdit4, "va|i"), E(ColorPicker3, "va|i"), E(ColorPicker4, "va|i"), E(ColorButton, "vv|inn"), E(TreeNode, "v|i"),
        E(TreePush, "v"), E(TreePop, ""), E(SetNextItemOpen, "b|i"), E(CollapsingHeader, "v|vi"), E(BeginTabBar, "v|i"),
        E(EndTabBar, ""), E(BeginTabItem, "v|vi"), E(EndTabItem, ""), E(TabItemButton, "v|i"), E(SetTabItemClosed, "v"),
        E(BeginTable, "vi|innn"), E(EndTable, ""), E(TableNextRow, "|in"), E(TableNextColumn, ""), E(TableSetColumnIndex, "i"),
        E(TableSetupColumn, "v|in"), E(TableSetupScrollFreeze, "ii"), E(TableHeadersRow, ""), E(TableHeader, "v"),
        E(TableGetColumnCount, ""), E(TableGetColumnIndex, ""), E(TableGetRowIndex, ""), E(TableSetBgColor, "iv|i"),
        E(BeginMenuBar, ""), E(EndMenuBar, ""), E(BeginMainMenuBar, ""), E(EndMainMenuBar, ""), E(BeginMenu, "v|b"), E(EndMenu, ""),
        E(MenuItem, "v|vbb"), E(OpenPopup, "v|i"), E(BeginPopup, "v|i"), E(BeginPopupModal, "v|vi"), E(EndPopup, ""),
        E(CloseCurrentPopup, ""), E(BeginPopupContextItem, "|vi"), E(BeginPopupContextWindow, "|vi"), E(BeginPopupContextVoid, "|vi"),
        E(IsPopupOpen, "v|i"), E(BeginTooltip, ""), E(EndTooltip, ""), E(SetTooltip, "v"), E(BeginItemTooltip, ""), E(SetItemTooltip, "v"),
        E(PlotLines, "va|ivnnnn"), E(PlotHistogram, "va|ivnnnn"), E(IsItemHovered, "|i"), E(IsItemActive, ""), E(IsItemFocused, ""),
        E(IsItemClicked, "|i"), E(IsItemVisible, ""), E(IsItemEdited, ""), E(IsItemActivated, ""), E(IsItemDeactivated, ""),
        E(IsItemDeactivatedAfterEdit, ""), E(IsItemToggledOpen, ""), E(IsAnyItemHovered, ""), E(IsAnyItemActive, ""),
        E(IsAnyItemFocused, ""), E(GetItemRectMin, ""), E(GetItemRectMax, ""), E(GetItemRectSize, ""), E(SetItemDefaultFocus, ""),
        E(SetKeyboardFocusHere, "|i"), E(SetNextItemAllowOverlap, ""), E(IsKeyDown, "i"), E(IsKeyPressed, "i|b"), E(IsKeyReleased, "i"),
        E(IsMouseDown, "|i"), E(IsMouseClicked, "|ib"), E(IsMouseReleased, "|i"), E(IsMouseDoubleClicked, "|i"), E(IsMouseDragging, "|in"),
        E(GetMousePos, ""), E(GetMouseDragDelta, "|in"), E(ResetMouseDragDelta, "|i"), E(SetMouseCursor, "i"), E(GetTime, ""),
        E(GetFrameCount, ""), E(BeginDragDropSource, "|i"), E(SetDragDropPayload, "vv|i"), E(EndDragDropSource, ""),
        E(BeginDragDropTarget, ""), E(AcceptDragDropPayload, "v|i"), E(EndDragDropTarget, ""), E(StyleColorsDark, ""),
        E(StyleColorsLight, ""), E(StyleColorsClassic, ""), E(PushStyleColor, "iv"), E(PopStyleColor, "|i"), E(PushStyleVar, "iv"),
        E(PopStyleVar, "|i"), E(GetStyleColor, "i"), E(SetStyleColor, "iv"), E(SetStyle, "h"), E(ScaleAllSizes, "n"), E(PushFont, "i|n"),
        E(PopFont, ""), E(GetFontSize, ""), E(ShowDemoWindow, "|b"), E(ShowMetricsWindow, "|b"), E(ShowDebugLogWindow, "|b"),
        E(ShowIDStackToolWindow, "|b"), E(ShowAboutWindow, "|b"), E(ShowStyleEditor, ""), E(ShowUserGuide, ""),
#undef E
    };
    for (const Entry &e : entries) aqua_define_function(m, e.names, e.function, e.signature);
}

}  // namespace aqua_imgui
