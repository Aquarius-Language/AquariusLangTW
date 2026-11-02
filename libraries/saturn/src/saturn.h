/* Saturn / 土星: Yoga layout trees for AquariusLang, with the visual properties, text layout, painting into a UI draw
 * list and hit testing that make a plain node tree a user interface (README.md). Shared declarations of the library's
 * C++ sources; everything here stays private to the library. */
#pragma once
#include <aqua/aqua.h>
#include <yoga/Yoga.h>

#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace saturn {

/* ---- The UI draw list (version 1), shared with the graphics library and every renderer (README.md) ---- */
enum Op : int {
    OP_RECT = 1,          /* x y w h  tl tr br bl  r g b a */
    OP_BORDER = 2,        /* x y w h  tl tr br bl  left top right bottom  r g b a */
    OP_SHADOW = 3,        /* x y w h  tl tr br bl  blur spread  r g b a */
    OP_TEXT = 4,          /* x y w h  string font size weight italic ascent  r g b a */
    OP_IMAGE = 5,         /* x y w h  image u0 v0 u1 v1  tl tr br bl  r g b a */
    OP_CLIP_PUSH = 6,     /* x y w h  tl tr br bl */
    OP_CLIP_POP = 7,      /* */
    OP_TRANSFORM_PUSH = 8,/* a b c d e f   (x' = a x + c y + e, y' = b x + d y + f) */
    OP_TRANSFORM_POP = 9, /* */
    OP_MESH = 10,         /* firstVertex vertexCount firstIndex indexCount texture */
    OP_TEXTURE = 11,      /* texture width height image x y w h   (image -1 frees the texture) */
    OP_LINE = 12,         /* x1 y1 x2 y2 width  r g b a */
    OP_GRADIENT = 13,     /* x y w h  tl tr br bl  x1 y1 x2 y2  r g b a  r g b a */
    OP_OPACITY_PUSH = 14, /* alpha (0..1, multiplies everything until the matching pop) */
    OP_OPACITY_POP = 15,  /* */
};

struct Color {
    float r = 0, g = 0, b = 0, a = 0;
    bool visible() const { return a > 0; }
};

/* The number of floats after each op code (OP_TEXT: x y w h string font size weight italic ascent r g b a = 14). */
constexpr int op_size(int op) {
    constexpr int sizes[] = {0, 12, 16, 14, 14, 17, 8, 0, 6, 0, 5, 8, 9, 20, 1, 0};
    return op > 0 && op < (int)(sizeof sizes / sizeof *sizes) ? sizes[op] : -1;
}

/* A draw list being built. Images are retained until the list is destroyed. */
struct DrawList {
    aqua_runtime *rt;
    std::vector<float> commands, vertices;
    std::vector<int32_t> indices;
    std::vector<std::string> strings;
    std::vector<aqua_ref> images;
    std::unordered_map<std::string, int> string_index;
    explicit DrawList(aqua_runtime *runtime) : rt(runtime) {}
    DrawList(const DrawList &) = delete;
    ~DrawList() { clear(); }
    void clear();
    int text(std::string_view s);
    int image(aqua_value v);
    void op(Op code, std::initializer_list<float> args) { commands.push_back((float)code); commands.insert(commands.end(), args); }
    bool append(aqua_value list, float dx, float dy);   /* splices a built draw list; false when it is malformed */
    aqua_value build() const;   /* {"version": 1, "commands", "strings", "images", "vertices", "indices"} */
};

/* ---- Visual properties of a node ---- */
enum Fit : int { FIT_FILL, FIT_CONTAIN, FIT_COVER, FIT_NONE, FIT_SCALE_DOWN };
enum TextAlign : int { ALIGN_START, ALIGN_CENTER, ALIGN_END, ALIGN_JUSTIFY };

struct Shadow { float x = 0, y = 0, blur = 0, spread = 0; Color color; };

struct Visual {
    Color background;
    bool gradient = false;
    Color gradient_to;
    float gradient_angle = 180;              /* degrees, CSS convention: 180 runs top to bottom */
    Color border[4];                         /* left top right bottom */
    float radius[4] = {0, 0, 0, 0};          /* top-left top-right bottom-right bottom-left */
    std::vector<Shadow> shadows;
    float opacity = 1;
    bool hidden = false, pointer_none = false;
    int z_index = 0;
    float translate_x = 0, translate_y = 0, scale_x = 1, scale_y = 1, rotate = 0, origin_x = 0.5f, origin_y = 0.5f;
    aqua_ref image = 0;
    int fit = FIT_FILL;
    Color tint{255, 255, 255, 255};
    float image_width = 0, image_height = 0; /* intrinsic size, 0 when unknown */
    std::string cursor;
    float scroll_x = 0, scroll_y = 0;
    bool transformed() const { return translate_x != 0 || translate_y != 0 || scale_x != 1 || scale_y != 1 || rotate != 0; }
};

struct TextStyle {
    std::string family;
    float size = 14, line_height = 1.25f;    /* line_height: a multiple of size */
    int weight = 400, align = ALIGN_START, max_lines = 0;
    bool italic = false, ellipsis = false, nowrap = false;
    Color color{0, 0, 0, 255};
};

struct TextLine { std::string text; float width = 0; };

struct Text {
    std::string content;
    TextStyle style;
    std::vector<TextLine> lines;             /* the latest layout */
    float ascent = 0, descent = 0;
};

struct State;

struct Node {
    YGNodeRef yg = nullptr;
    aqua_runtime *rt = nullptr;
    State *state = nullptr;
    aqua_value self{};                       /* the script object; not retained (the handle lives until finalized) */
    aqua_ref attached = 0;                   /* the node's own object, held while a parent owns it */
    aqua_ref config = 0, context = 0, measure = 0, baseline = 0, dirtied = 0, min_content = 0, painter = 0;
    std::unique_ptr<Visual> visual;
    std::unique_ptr<Text> text;
    Visual &look() { if (!visual) visual = std::make_unique<Visual>(); return *visual; }
};

struct Config {
    YGConfigRef yg = nullptr;
    aqua_runtime *rt = nullptr;
    State *state = nullptr;
    aqua_ref logger = 0;
};

struct Metrics { float width, ascent, descent; };

/* Per-runtime state: the text measurer and its cache, and the first error raised inside a layout callback. */
struct State {
    aqua_runtime *rt = nullptr;
    aqua_ref measurer = 0;
    std::unordered_map<std::string, Metrics> cache;
    std::string error;
};

extern aqua_class *node_class, *config_class, *value_class;

inline Node *node_of(YGNodeConstRef n) { return static_cast<Node *>(YGNodeGetContext(n)); }
/* A text node: SetText gave it content and Saturn's own measure function (a script measure function replaces it). */
inline bool is_text(Node *n) { return n->text && !n->measure && YGNodeHasMeasureFunc(n->yg); }
Node *arg_node(aqua_call *call, uint32_t index);    /* fails the call for a freed node */

/* constants.def lookups: a constant or its CSS name; false when unknown. */
bool parse_enum(const char *type, aqua_value v, aqua_runtime *rt, int *out);
const char *enum_name(const char *type, int value);
bool parse_color(aqua_runtime *rt, aqua_value v, Color *out);

/* A length: numbers are points; strings "12", "12px", "50%", "auto", "max-content", "fit-content", "stretch". */
struct Length { int unit; float value; };
bool parse_length(aqua_runtime *rt, aqua_value v, Length *out);

/* saturn.cpp: one layout key of SetStyle (CSS names); *handled is false for keys that are not layout properties. */
bool set_layout_style(aqua_call *call, Node *node, std::string_view key, aqua_value v, bool *handled);

/* style.cpp */
void define_style(aqua_class *node);
bool apply_style(aqua_call *call, Node *node, aqua_value style);

/* text.cpp */
void define_text(aqua_class *module, aqua_class *node);
Metrics measure_run(State *state, std::string_view text, const TextStyle &style);
void layout_text(Node *node, float width, bool bounded);
YGSize measure_text(YGNodeConstRef node, float width, YGMeasureMode width_mode, float height, YGMeasureMode height_mode);
void update_image_size(Node *node);

/* paint.cpp */
void define_paint(aqua_library *library, aqua_class *module, aqua_class *node);

}  // namespace saturn
