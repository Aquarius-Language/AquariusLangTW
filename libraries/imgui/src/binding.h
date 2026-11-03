/* ImGui: Dear ImGui for AquariusLang. Shared declarations of context.cpp (contexts, input, fonts, rendering into the
 * UI draw list) and widgets.cpp (the immediate-mode API, named as in Dear ImGui). Private to the library. */
#pragma once
#include <aqua/aqua.h>
#include "imgui.h"

#include <string>
#include <unordered_map>
#include <vector>

namespace aqua_imgui {

struct Embed { std::string id; ImVec2 min, max; };   /* an ImGui.Embed() item of the current frame */

struct Context {
    ImGuiContext *imgui = nullptr;
    aqua_runtime *rt = nullptr;
    struct State *state = nullptr;
    aqua_value self{};
    bool in_frame = false, destroyed = false;
    int frame = 0;                              /* counts NewFrame calls (DrawList objects check it) */
    std::vector<aqua_ref> images;               /* ImGui.Image() textures of this frame: ImTextureID IMAGE_BIT | index */
    std::vector<aqua_ref> fonts_data;           /* font bytes kept alive for the atlas */
    std::vector<ImFont *> fonts;
    std::unordered_map<std::string, aqua_ref> payloads;   /* drag and drop values by payload type */
    std::vector<Embed> embeds;                  /* Embed() items of this frame, by callback index */
    aqua_ref clipboard_get = 0, clipboard_set = 0;
    std::string clipboard;
    float ime_x = 0, ime_y = 0, ime_height = 0;
    bool ime_visible = false;
    bool conventions_known = false;   /* UseConventions ran, or the first Interaction event brought them */

    bool last_open = true;
};

struct State {
    aqua_runtime *rt = nullptr;
    Context *current = nullptr;
    int next_texture = 1 << 20;                 /* texture ids ImGui's own textures use in draw lists */
    std::string error;                          /* the first failed IM_ASSERT of the current call */
};

constexpr ImTextureID IMAGE_BIT = (ImTextureID)1 << 62;

extern aqua_class *context_class, *drawlist_class;

State *state_of(aqua_call *call);
/* The current context inside a frame (NewFrame .. Render), or NULL after failing the call. */
Context *frame_of(aqua_call *call);
/* Fails the call with an IM_ASSERT that fired during it; true when one did. */
bool report_assert(aqua_call *call);

/* Arguments */
const char *text_arg(aqua_call *call, uint32_t index);                 /* any value's text, in call scratch */
int int_arg(aqua_call *call, uint32_t index, int fallback);              /* optional integer */
float float_arg(aqua_call *call, uint32_t index, float fallback);        /* optional number */
bool bool_arg(aqua_call *call, uint32_t index, bool fallback);           /* optional Boolean */
bool has(aqua_call *call, uint32_t index);
ImVec2 vec2_arg(aqua_call *call, uint32_t index, ImVec2 fallback);     /* [x, y] */
bool color_arg(aqua_call *call, uint32_t index, ImVec4 *out);          /* [r, g, b(, a)] 0..1, "#rrggbb", 0xAARRGGBB */
ImU32 u32_color_arg(aqua_call *call, uint32_t index, ImU32 fallback);  /* the same, packed for draw lists */
ImTextureRef texture_arg(aqua_call *call, uint32_t index);             /* an image value of any renderer */
aqua_value vec2_value(aqua_runtime *rt, ImVec2 v);
aqua_value floats_value(aqua_runtime *rt, const float *values, int count);

/* The draw callback that marks an Embed() item; Render splices the embedded list there. */
void embed_callback(const ImDrawList *list, const ImDrawCmd *command);

void define_widgets(aqua_class *module);
void define_drawlist(aqua_library *library, aqua_class *module);

}  // namespace aqua_imgui
