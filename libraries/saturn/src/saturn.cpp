/* Saturn / 土星: the library entry, Saturn.Config and the Yoga half of Saturn.Node (README.md, "Node trees").
 *
 * A node object owns one YGNode whose context points back at it. A node that has a parent keeps its own object alive
 * (`attached`), so a tree lives as long as the script reaches its root; finalizing a root detaches and frees it, and its
 * children follow once nothing else reaches them. Yoga's fatal assertions are checked here first and reported as
 * ordinary errors, so no script can abort the runtime. */
#include "saturn.h"

#include <cctype>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace saturn {

aqua_class *node_class, *config_class, *value_class;

/* ---- Enumerations (constants.def) ---- */

struct EnumEntry { const char *type, *constant; int value; const char *css; };
static const EnumEntry enum_entries[] = {
#define VALUE(type, constant, value, css) {#type, #constant, (int)(value), css},
#include "constants.def"
#undef VALUE
};

static bool same_name(const char *a, const char *b) {
    for (; *a && *b; a++, b++) {
        char x = *a == '_' ? '-' : (char)std::tolower((unsigned char)*a);
        char y = *b == '_' ? '-' : (char)std::tolower((unsigned char)*b);
        if (x != y) return false;
    }
    return *a == *b;
}

bool parse_enum(const char *type, aqua_value v, aqua_runtime *rt, int *out) {
    if (aqua_is_number(v)) {
        if (v.number != std::floor(v.number)) return false;
        for (const EnumEntry &e : enum_entries)
            if (!strcmp(e.type, type) && e.value == (int)v.number) { *out = e.value; return true; }
        return false;
    }
    if (v.type != AQUA_STRING) return false;
    char *text = aqua_text_dup(rt, v, nullptr);
    bool found = false;
    /* A CSS name ("space-between", also "space_between") or the constant's name with or without its type prefix. */
    size_t prefix = strlen(type) - 2;
    for (const EnumEntry &e : enum_entries) {
        if (strcmp(e.type, type)) continue;
        if (same_name(text, e.css) || same_name(text, e.constant) || same_name(text, e.constant + prefix)) {
            *out = e.value; found = true; break;
        }
    }
    aqua_free(text);
    return found;
}

const char *enum_name(const char *type, int value) {
    for (const EnumEntry &e : enum_entries)
        if (!strcmp(e.type, type) && e.value == value) return e.css;
    return "unknown";
}

/* ---- Lengths: numbers are points; strings "12", "50%", "auto", "max-content", "fit-content", "stretch" ---- */

bool parse_length(aqua_runtime *rt, aqua_value v, Length *out) {
    if (aqua_is_nullish(v)) { *out = {YGUnitUndefined, YGUndefined}; return true; }
    if (aqua_is_numeric(v)) {
        double n = aqua_number_of(v);
        *out = std::isnan(n) ? Length{YGUnitUndefined, YGUndefined} : Length{YGUnitPoint, (float)n};
        return true;
    }
    if (v.type != AQUA_STRING) return false;
    char *text = aqua_text_dup(rt, v, nullptr);
    std::string s(text);
    aqua_free(text);
    while (!s.empty() && s.back() == ' ') s.pop_back();
    while (!s.empty() && s.front() == ' ') s.erase(0, 1);
    static const struct { const char *name; YGUnit unit; } keywords[] = {{"auto", YGUnitAuto}, {"max-content", YGUnitMaxContent},
        {"fit-content", YGUnitFitContent}, {"stretch", YGUnitStretch}, {"undefined", YGUnitUndefined}};
    for (auto &k : keywords)
        if (same_name(s.c_str(), k.name)) { *out = {k.unit, k.unit == YGUnitUndefined ? YGUndefined : 0}; return true; }
    YGUnit unit = YGUnitPoint;
    if (s.size() > 1 && s.back() == '%') { unit = YGUnitPercent; s.pop_back(); }
    else if (s.size() > 2 && s.compare(s.size() - 2, 2, "px") == 0) s.resize(s.size() - 2);
    char *end = nullptr;
    double n = std::strtod(s.c_str(), &end);
    if (s.empty() || *end || !std::isfinite(n)) return false;
    *out = {unit, (float)n};
    return true;
}

/* ---- Saturn.Value: a YGValue (value + unit) ---- */

struct ValueData { float value; int unit; };
static void value_finalize(aqua_runtime *, void *data) { delete static_cast<ValueData *>(data); }
static void value_value(aqua_call *call) { aqua_return_double(call, static_cast<ValueData *>(aqua_self(call))->value); }
static void value_unit(aqua_call *call) { aqua_return_int(call, static_cast<ValueData *>(aqua_self(call))->unit); }
static void value_unit_name(aqua_call *call) { aqua_return_cstring(call, enum_name("YGUnit", static_cast<ValueData *>(aqua_self(call))->unit)); }
static void return_value(aqua_call *call, YGValue v) {
    aqua_return(call, aqua_object_new(aqua_call_runtime(call), value_class, new ValueData{v.value, (int)v.unit}));
}

/* ---- Per-runtime state ---- */

static void *state_create(aqua_runtime *rt) { auto *s = new State; s->rt = rt; return s; }
static void state_destroy(aqua_runtime *rt, void *data) {
    auto *s = static_cast<State *>(data);
    aqua_release(rt, s->measurer);
    delete s;
}

/* Reports the first error a script callback raised during a Yoga call. */
static bool report_callback_error(aqua_call *call, State *state) {
    if (state->error.empty()) return false;
    aqua_fail(call, "%s", state->error.c_str());
    state->error.clear();
    return true;
}
static void remember_error(State *state, aqua_runtime *rt, aqua_value error) {
    if (!state->error.empty()) return;
    char *shown = aqua_display(rt, error);
    const char *text = shown ? shown : "Saturn: a layout callback failed.";
    while (!strncmp(text, "ERROR: ", 7)) text += 7;
    state->error = text;
    aqua_free(shown);
}

/* ---- Saturn.Config ---- */

static Config *self_config(aqua_call *call) { return static_cast<Config *>(aqua_self(call)); }
static void config_finalize(aqua_runtime *rt, void *data) {
    auto *c = static_cast<Config *>(data);
    aqua_release(rt, c->logger);
    YGConfigFree(c->yg);
    delete c;
}
static int config_log(YGConfigConstRef config, YGNodeConstRef node, YGLogLevel level, const char *format, va_list args) {
    auto *c = static_cast<Config *>(YGConfigGetContext(config));
    char message[1024];
    va_list copy;
    va_copy(copy, args);
    int length = vsnprintf(message, sizeof message, format, copy);
    va_end(copy);
    if (!c || !c->logger) { aqua_write_error(c ? c->rt : nullptr, message, strlen(message)); return length; }
    Node *n = node ? node_of(node) : nullptr;
    aqua_value arguments[3] = {aqua_int(level), aqua_cstring(c->rt, message), n ? n->self : aqua_null()};
    aqua_value result;
    if (!aqua_invoke(c->rt, aqua_ref_value(c->rt, c->logger), arguments, 3, &result)) remember_error(c->state, c->rt, result);
    return length;
}
static void config_new(aqua_call *call) {
    auto *c = new Config;
    c->rt = aqua_call_runtime(call);
    c->state = static_cast<State *>(aqua_state(call));
    c->yg = YGConfigNew();
    YGConfigSetContext(c->yg, c);
    aqua_return(call, aqua_object_new(c->rt, config_class, c));
}
static void config_set_web_defaults(aqua_call *call) { YGConfigSetUseWebDefaults(self_config(call)->yg, aqua_arg_bool(call, 0)); }
static void config_get_web_defaults(aqua_call *call) { aqua_return_bool(call, YGConfigGetUseWebDefaults(self_config(call)->yg)); }
static void config_set_scale(aqua_call *call) {
    double scale = aqua_arg_number(call, 0);
    if (!(scale >= 0) || !std::isfinite(scale)) { aqua_fail(call, "Saturn.Config.SetPointScaleFactor: the scale must be 0 or more / 縮放比例不能為負。"); return; }
    YGConfigSetPointScaleFactor(self_config(call)->yg, (float)scale);
}
static void config_get_scale(aqua_call *call) { aqua_return_double(call, YGConfigGetPointScaleFactor(self_config(call)->yg)); }
static void config_set_errata(aqua_call *call) {
    int errata;
    if (!parse_enum("YGErrata", aqua_arg(call, 0), aqua_call_runtime(call), &errata)) {
        /* Errata are flags: any combination of the defined bits is valid. */
        aqua_value v = aqua_arg(call, 0);
        if (v.type != AQUA_INT) { aqua_fail(call, "Saturn.Config.SetErrata: expects an Errata constant or a sum of them."); return; }
        errata = (int)v.number;
    }
    YGConfigSetErrata(self_config(call)->yg, (YGErrata)errata);
}
static void config_get_errata(aqua_call *call) { aqua_return_int(call, (int)YGConfigGetErrata(self_config(call)->yg)); }
static void config_set_experimental(aqua_call *call) {
    int feature;
    if (!parse_enum("YGExperimentalFeature", aqua_arg(call, 0), aqua_call_runtime(call), &feature)) {
        aqua_fail(call, "Saturn.Config.SetExperimentalFeatureEnabled: unknown feature."); return;
    }
    YGConfigSetExperimentalFeatureEnabled(self_config(call)->yg, (YGExperimentalFeature)feature, aqua_arg_bool(call, 1));
}
static void config_is_experimental(aqua_call *call) {
    int feature;
    if (!parse_enum("YGExperimentalFeature", aqua_arg(call, 0), aqua_call_runtime(call), &feature)) {
        aqua_fail(call, "Saturn.Config.IsExperimentalFeatureEnabled: unknown feature."); return;
    }
    aqua_return_bool(call, YGConfigIsExperimentalFeatureEnabled(self_config(call)->yg, (YGExperimentalFeature)feature));
}
static void config_set_logger(aqua_call *call) {
    Config *c = self_config(call);
    aqua_release(c->rt, c->logger);
    c->logger = aqua_has_arg(call, 0) ? aqua_retain(c->rt, aqua_arg(call, 0)) : 0;
    YGConfigSetLogger(c->yg, c->logger ? config_log : nullptr);
}

/* ---- Saturn.Node: lifecycle and tree ---- */

static const char freed_message[] = "Saturn.Node: the node was freed / 節點已釋放。";

Node *arg_node(aqua_call *call, uint32_t index) {
    auto *n = static_cast<Node *>(aqua_arg_object(call, index, node_class));
    if (n && !n->yg) { aqua_fail(call, freed_message); return nullptr; }
    return n;
}
static Node *self_node(aqua_call *call) {
    auto *n = static_cast<Node *>(aqua_self(call));
    if (!n->yg) { aqua_fail(call, freed_message); return nullptr; }
    return n;
}

static void attach(Node *child) { if (!child->attached) child->attached = aqua_retain(child->rt, child->self); }
static void detach(Node *child) { aqua_release(child->rt, child->attached); child->attached = 0; }

static void release_refs(Node *n) {
    for (aqua_ref *ref : {&n->config, &n->context, &n->measure, &n->baseline, &n->dirtied, &n->min_content, &n->painter}) {
        aqua_release(n->rt, *ref);
        *ref = 0;
    }
    if (n->visual) { aqua_release(n->rt, n->visual->image); n->visual->image = 0; }
}

/* Frees a node's YGNode (Yoga detaches it from its parent and its children from it). */
static void free_node(Node *n) {
    if (!n->yg) return;
    std::vector<Node *> children;
    for (size_t i = 0, count = YGNodeGetChildCount(n->yg); i < count; i++) children.push_back(node_of(YGNodeGetChild(n->yg, i)));
    YGNodeFree(n->yg);
    n->yg = nullptr;
    for (Node *child : children) detach(child);
    detach(n);
    release_refs(n);
}
static void free_recursive(Node *n) {
    if (!n->yg) return;
    while (YGNodeGetChildCount(n->yg) > 0) {
        Node *child = node_of(YGNodeGetChild(n->yg, 0));
        YGNodeRemoveChild(n->yg, child->yg);
        free_recursive(child);
        detach(child);
    }
    free_node(n);
}

static void node_finalize(aqua_runtime *, void *data) {
    auto *n = static_cast<Node *>(data);
    free_node(n);
    delete n;
}

static aqua_value make_node(aqua_runtime *rt, State *state, YGNodeRef yg, aqua_ref config) {
    auto *n = new Node;
    n->rt = rt; n->state = state; n->yg = yg; n->config = config;
    YGNodeSetContext(yg, n);
    n->self = aqua_object_new(rt, node_class, n);
    if (n->self.type == AQUA_ERROR) { YGNodeSetContext(yg, nullptr); n->yg = nullptr; aqua_release(rt, config); delete n; YGNodeFree(yg); }
    return n->self;
}

static void node_new(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    YGNodeRef yg;
    aqua_ref config = 0;
    if (aqua_has_arg(call, 0)) {
        auto *c = static_cast<Config *>(aqua_arg_object(call, 0, config_class));
        if (!c) return;
        yg = YGNodeNewWithConfig(c->yg);
        config = aqua_retain(rt, aqua_arg(call, 0));
    } else {
        yg = YGNodeNew();
    }
    aqua_return(call, make_node(rt, static_cast<State *>(aqua_state(call)), yg, config));
}

static void copy_extras(Node *from, Node *to) {
    aqua_runtime *rt = from->rt;
    auto keep = [rt](aqua_ref ref) { return ref ? aqua_retain(rt, aqua_ref_value(rt, ref)) : (aqua_ref)0; };
    to->context = keep(from->context); to->measure = keep(from->measure); to->baseline = keep(from->baseline);
    to->dirtied = keep(from->dirtied); to->min_content = keep(from->min_content); to->painter = keep(from->painter);
    if (from->visual) { to->visual = std::make_unique<Visual>(*from->visual); to->visual->image = keep(from->visual->image); }
    if (from->text) to->text = std::make_unique<Text>(*from->text);
}

/* Clone copies the whole subtree (each clone owns copies of the children), so trees never share nodes. */
static aqua_value clone_tree(Node *n) {
    YGNodeRef yg = YGNodeClone(n->yg);
    YGNodeRemoveAllChildren(yg);   /* the copy's child list still names n's children; they stay n's */
    aqua_ref config = n->config ? aqua_retain(n->rt, aqua_ref_value(n->rt, n->config)) : 0;
    aqua_value copy = make_node(n->rt, n->state, yg, config);
    if (copy.type == AQUA_ERROR) return copy;
    auto *c = static_cast<Node *>(aqua_object_data(n->rt, copy, node_class));
    copy_extras(n, c);
    for (size_t i = 0, count = YGNodeGetChildCount(n->yg); i < count; i++) {
        aqua_value child = clone_tree(node_of(YGNodeGetChild(n->yg, i)));
        if (child.type == AQUA_ERROR) return child;
        auto *cc = static_cast<Node *>(aqua_object_data(n->rt, child, node_class));
        YGNodeInsertChild(yg, cc->yg, i);
        attach(cc);
    }
    return copy;
}
static void node_clone(aqua_call *call) { if (Node *n = self_node(call)) aqua_return(call, clone_tree(n)); }
static void node_free(aqua_call *call) { if (Node *n = self_node(call)) free_node(n); }
static void node_free_recursive(aqua_call *call) { if (Node *n = self_node(call)) free_recursive(n); }
static void node_is_freed(aqua_call *call) { aqua_return_bool(call, static_cast<Node *>(aqua_self(call))->yg == nullptr); }
static void node_reset(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    if (YGNodeGetChildCount(n->yg) > 0 || YGNodeGetOwner(n->yg)) {
        aqua_fail(call, "Saturn.Node.Reset: only a node without parent and children can be reset / 只能重設沒有父子節點的節點。");
        return;
    }
    YGNodeReset(n->yg);
    YGNodeSetContext(n->yg, n);
    aqua_ref config = n->config;
    n->config = 0;
    release_refs(n);
    n->config = config;
    n->visual.reset();
    n->text.reset();
}

static bool is_ancestor(Node *candidate, Node *of) {
    for (YGNodeRef at = of->yg; at; at = YGNodeGetOwner(at))
        if (at == candidate->yg) return true;
    return false;
}

static bool check_insert(aqua_call *call, Node *parent, Node *child, const char *what) {
    if (YGNodeGetOwner(child->yg)) { aqua_fail(call, "Saturn.Node.%s: the child already has a parent; remove it first / 子節點已有父節點。", what); return false; }
    if (YGNodeHasMeasureFunc(parent->yg)) { aqua_fail(call, "Saturn.Node.%s: a node with a measure function or text cannot have children / 有測量函式的節點不能有子節點。", what); return false; }
    if (is_ancestor(child, parent)) { aqua_fail(call, "Saturn.Node.%s: a node cannot contain itself or its ancestor / 節點不能包含自己或祖先。", what); return false; }
    return true;
}

static void node_insert_child(aqua_call *call) {
    Node *n = self_node(call), *child = n ? arg_node(call, 0) : nullptr;
    if (!child) return;
    size_t count = YGNodeGetChildCount(n->yg);
    size_t index = count;
    if (aqua_argc(call) > 1) {
        int32_t i = aqua_arg_int(call, 1);
        if (i < 0 || (size_t)i > count) { aqua_fail(call, "Saturn.Node.InsertChild: index %d is outside 0..%zu / 索引超出範圍。", i, count); return; }
        index = (size_t)i;
    }
    if (!check_insert(call, n, child, "InsertChild")) return;
    YGNodeInsertChild(n->yg, child->yg, index);
    attach(child);
}
static void node_swap_child(aqua_call *call) {
    Node *n = self_node(call), *child = n ? arg_node(call, 0) : nullptr;
    if (!child) return;
    int32_t index = aqua_arg_int(call, 1);
    size_t count = YGNodeGetChildCount(n->yg);
    if (index < 0 || (size_t)index >= count) { aqua_fail(call, "Saturn.Node.SwapChild: index %d is outside 0..%zu / 索引超出範圍。", index, count ? count - 1 : 0); return; }
    if (!check_insert(call, n, child, "SwapChild")) return;
    Node *old = node_of(YGNodeGetChild(n->yg, (size_t)index));
    YGNodeRemoveChild(n->yg, old->yg);
    detach(old);
    YGNodeInsertChild(n->yg, child->yg, (size_t)index);
    attach(child);
}
static void node_remove_child(aqua_call *call) {
    Node *n = self_node(call), *child = n ? arg_node(call, 0) : nullptr;
    if (!child || YGNodeGetOwner(child->yg) != n->yg) return;
    YGNodeRemoveChild(n->yg, child->yg);
    detach(child);
}
static void remove_all(Node *n) {
    std::vector<Node *> children;
    for (size_t i = 0, count = YGNodeGetChildCount(n->yg); i < count; i++) children.push_back(node_of(YGNodeGetChild(n->yg, i)));
    YGNodeRemoveAllChildren(n->yg);
    for (Node *child : children) detach(child);
}
static void node_remove_all(aqua_call *call) { if (Node *n = self_node(call)) remove_all(n); }
static void node_set_children(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value list = aqua_arg(call, 0);
    uint32_t count = aqua_array_length(rt, list);
    std::vector<Node *> children;
    for (uint32_t i = 0; i < count; i++) {
        auto *child = static_cast<Node *>(aqua_object_data(rt, aqua_array_get(rt, list, i), node_class));
        if (!child || !child->yg) { aqua_fail(call, "Saturn.Node.SetChildren: element %u is not a live Saturn.Node / 第 %u 個元素不是節點。", i, i); return; }
        for (Node *seen : children) if (seen == child) { aqua_fail(call, "Saturn.Node.SetChildren: a node appears twice / 節點重複。"); return; }
        if (YGNodeGetOwner(child->yg) != n->yg && !check_insert(call, n, child, "SetChildren")) return;
        if (YGNodeGetOwner(child->yg) == n->yg) continue;
        children.push_back(child);
    }
    if (count && YGNodeHasMeasureFunc(n->yg)) { check_insert(call, n, node_of(YGNodeGetChild(n->yg, 0)), "SetChildren"); return; }
    remove_all(n);
    for (uint32_t i = 0; i < count; i++) {
        auto *child = static_cast<Node *>(aqua_object_data(rt, aqua_array_get(rt, list, i), node_class));
        YGNodeInsertChild(n->yg, child->yg, i);
        attach(child);
    }
}
static void node_get_child(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    int32_t index = aqua_arg_int(call, 0);
    if (index < 0 || (size_t)index >= YGNodeGetChildCount(n->yg)) { aqua_return_null(call); return; }
    aqua_return(call, node_of(YGNodeGetChild(n->yg, (size_t)index))->self);
}
static void node_child_count(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_int(call, (int32_t)YGNodeGetChildCount(n->yg)); }
static void node_children(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    size_t count = YGNodeGetChildCount(n->yg);
    aqua_value list = aqua_array_new(rt, (uint32_t)count);
    for (size_t i = 0; i < count; i++) aqua_array_set(rt, list, (uint32_t)i, node_of(YGNodeGetChild(n->yg, i))->self);
    aqua_return(call, list);
}
static void node_get_owner(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    YGNodeRef owner = YGNodeGetOwner(n->yg);
    if (owner) aqua_return(call, node_of(owner)->self); else aqua_return_null(call);
}
static void node_get_config(aqua_call *call) {
    Node *n = self_node(call);
    if (n && n->config) aqua_return(call, aqua_ref_value(n->rt, n->config)); else aqua_return_null(call);
}
static void node_set_config(aqua_call *call) {
    Node *n = self_node(call);
    auto *c = n ? static_cast<Config *>(aqua_arg_object(call, 0, config_class)) : nullptr;
    if (!c) return;
    YGNodeSetConfig(n->yg, c->yg);
    aqua_release(n->rt, n->config);
    n->config = aqua_retain(n->rt, aqua_arg(call, 0));
}

/* ---- Layout ---- */

static void node_calculate_layout(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    auto number = [call](uint32_t i) -> float {
        if (aqua_argc(call) <= i || aqua_is_nullish(aqua_arg(call, i))) return YGUndefined;
        return (float)aqua_arg_number(call, i);
    };
    int direction = YGDirectionLTR;
    if (aqua_argc(call) > 2 && !aqua_is_nullish(aqua_arg(call, 2)) &&
        !parse_enum("YGDirection", aqua_arg(call, 2), aqua_call_runtime(call), &direction)) {
        aqua_fail(call, "Saturn.Node.CalculateLayout: unknown direction / 未知的方向。"); return;
    }
    n->state->error.clear();
    YGNodeCalculateLayout(n->yg, number(0), number(1), (YGDirection)direction);
    report_callback_error(call, n->state);
}

#define LAYOUT_GETTER(name, function) \
    static void layout_##name(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_double(call, function(n->yg)); }
LAYOUT_GETTER(left, YGNodeLayoutGetLeft)
LAYOUT_GETTER(top, YGNodeLayoutGetTop)
LAYOUT_GETTER(right, YGNodeLayoutGetRight)
LAYOUT_GETTER(bottom, YGNodeLayoutGetBottom)
LAYOUT_GETTER(width, YGNodeLayoutGetWidth)
LAYOUT_GETTER(height, YGNodeLayoutGetHeight)
LAYOUT_GETTER(raw_width, YGNodeLayoutGetRawWidth)
LAYOUT_GETTER(raw_height, YGNodeLayoutGetRawHeight)
#undef LAYOUT_GETTER
static void layout_direction(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_int(call, YGNodeLayoutGetDirection(n->yg)); }
static void layout_overflow(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_bool(call, YGNodeLayoutGetHadOverflow(n->yg)); }

static bool arg_edge(aqua_call *call, uint32_t index, YGEdge *edge) {
    int e;
    if (!parse_enum("YGEdge", aqua_arg(call, index), aqua_call_runtime(call), &e)) { aqua_fail(call, "Saturn.Node: unknown edge / 未知的邊。"); return false; }
    *edge = (YGEdge)e;
    return true;
}
/* Yoga's layout edge getters accept only physical and start/end edges. */
static bool arg_layout_edge(aqua_call *call, YGEdge *edge) {
    if (!arg_edge(call, 0, edge)) return false;
    if (*edge > YGEdgeEnd) { aqua_fail(call, "Saturn.Node: computed layouts have left, top, right, bottom, start and end edges / 計算結果只有實際的邊。"); return false; }
    return true;
}
#define LAYOUT_EDGE_GETTER(name, function) static void layout_##name(aqua_call *call) { \
        Node *n = self_node(call); YGEdge edge; if (n && arg_layout_edge(call, &edge)) aqua_return_double(call, function(n->yg, edge)); }
LAYOUT_EDGE_GETTER(margin, YGNodeLayoutGetMargin)
LAYOUT_EDGE_GETTER(border, YGNodeLayoutGetBorder)
LAYOUT_EDGE_GETTER(padding, YGNodeLayoutGetPadding)
#undef LAYOUT_EDGE_GETTER

/* The computed box relative to the parent, as Yoga's JS binding returns it: {left, top, right, bottom, width, height}. */
static void layout_record(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value h = aqua_hash_new(rt, 6);
    aqua_hash_set_field(rt, h, "left", aqua_double(YGNodeLayoutGetLeft(n->yg)));
    aqua_hash_set_field(rt, h, "top", aqua_double(YGNodeLayoutGetTop(n->yg)));
    aqua_hash_set_field(rt, h, "right", aqua_double(YGNodeLayoutGetRight(n->yg)));
    aqua_hash_set_field(rt, h, "bottom", aqua_double(YGNodeLayoutGetBottom(n->yg)));
    aqua_hash_set_field(rt, h, "width", aqua_double(YGNodeLayoutGetWidth(n->yg)));
    aqua_hash_set_field(rt, h, "height", aqua_double(YGNodeLayoutGetHeight(n->yg)));
    aqua_return(call, h);
}

static void node_has_new_layout(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_bool(call, YGNodeGetHasNewLayout(n->yg)); }
static void node_set_has_new_layout(aqua_call *call) { if (Node *n = self_node(call)) YGNodeSetHasNewLayout(n->yg, aqua_arg_bool(call, 0)); }
static void node_is_dirty(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_bool(call, YGNodeIsDirty(n->yg)); }
static void node_mark_dirty(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    if (!YGNodeHasMeasureFunc(n->yg)) { aqua_fail(call, "Saturn.Node.MarkDirty: only leaf nodes with a measure function (or text) mark themselves dirty / 只有具測量函式的葉節點可標記為需重算。"); return; }
    YGNodeMarkDirty(n->yg);
}

/* ---- Callbacks: measure, min-content measure, baseline, dirtied ---- */

static bool read_size(aqua_runtime *rt, aqua_value v, YGSize *out) {
    aqua_value w, h;
    if (v.type == AQUA_ARRAY && aqua_array_length(rt, v) >= 2) { w = aqua_array_get(rt, v, 0); h = aqua_array_get(rt, v, 1); }
    else if ((v.type == AQUA_HASH || v.type == AQUA_STRUCT) && aqua_hash_field(rt, v, "width|寬度", &w) && aqua_hash_field(rt, v, "height|高度", &h)) {}
    else if (v.type == AQUA_NATIVE) { w = aqua_object_member(rt, v, "width"); h = aqua_object_member(rt, v, "height"); }
    else return false;
    if (!aqua_is_numeric(w) || !aqua_is_numeric(h)) return false;
    *out = {(float)aqua_number_of(w), (float)aqua_number_of(h)};
    return true;
}

static YGSize call_measure(Node *n, aqua_ref function, float width, YGMeasureMode wm, float height, YGMeasureMode hm) {
    aqua_value arguments[4] = {aqua_double(width), aqua_int(wm), aqua_double(height), aqua_int(hm)};
    aqua_value result;
    YGSize size{0, 0};
    if (!aqua_invoke(n->rt, aqua_ref_value(n->rt, function), arguments, 4, &result)) remember_error(n->state, n->rt, result);
    else if (!read_size(n->rt, result, &size) && n->state->error.empty())
        n->state->error = "Saturn: a measure function must return [width, height] or a value with width and height / 測量函式必須回傳 [寬, 高]。";
    return size;
}
static YGSize measure_callback(YGNodeConstRef node, float width, YGMeasureMode wm, float height, YGMeasureMode hm) {
    Node *n = node_of(node);
    return call_measure(n, n->measure, width, wm, height, hm);
}
static YGSize min_content_callback(YGNodeConstRef node, float width, YGMeasureMode wm, float height, YGMeasureMode hm) {
    Node *n = node_of(node);
    return call_measure(n, n->min_content, width, wm, height, hm);
}
static float baseline_callback(YGNodeConstRef node, float width, float height) {
    Node *n = node_of(node);
    aqua_value arguments[2] = {aqua_double(width), aqua_double(height)}, result;
    if (!aqua_invoke(n->rt, aqua_ref_value(n->rt, n->baseline), arguments, 2, &result)) { remember_error(n->state, n->rt, result); return 0; }
    return aqua_is_numeric(result) ? (float)aqua_number_of(result) : 0;
}
static void dirtied_callback(YGNodeConstRef node) {
    Node *n = node_of(node);
    aqua_value argument = n->self, result;
    if (!aqua_invoke(n->rt, aqua_ref_value(n->rt, n->dirtied), &argument, 1, &result)) remember_error(n->state, n->rt, result);
}

static bool replace_callback(aqua_call *call, Node *n, aqua_ref *slot) {
    aqua_release(n->rt, *slot);
    *slot = aqua_has_arg(call, 0) ? aqua_retain(n->rt, aqua_arg(call, 0)) : 0;
    return *slot != 0;
}
static void node_set_measure(aqua_call *call) {
    Node *n = self_node(call);
    if (!n) return;
    if (aqua_has_arg(call, 0) && YGNodeGetChildCount(n->yg) > 0) { aqua_fail(call, "Saturn.Node.SetMeasureFunc: a node with children cannot have a measure function / 有子節點的節點不能設定測量函式。"); return; }
    YGNodeSetMeasureFunc(n->yg, replace_callback(call, n, &n->measure) ? measure_callback : nullptr);
}
static void node_set_min_content(aqua_call *call) {
    if (Node *n = self_node(call)) YGNodeSetMinContentMeasureFunc(n->yg, replace_callback(call, n, &n->min_content) ? min_content_callback : nullptr);
}
static void node_set_baseline(aqua_call *call) {
    if (Node *n = self_node(call)) YGNodeSetBaselineFunc(n->yg, replace_callback(call, n, &n->baseline) ? baseline_callback : nullptr);
}
static void node_set_dirtied(aqua_call *call) {
    if (Node *n = self_node(call)) YGNodeSetDirtiedFunc(n->yg, replace_callback(call, n, &n->dirtied) ? dirtied_callback : nullptr);
}
static void node_has_measure(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_bool(call, YGNodeHasMeasureFunc(n->yg)); }
static void node_has_baseline(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_bool(call, YGNodeHasBaselineFunc(n->yg)); }
static void node_has_min_content(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_bool(call, YGNodeHasMinContentMeasureFunc(n->yg)); }
static void node_set_min_content_width(aqua_call *call) { if (Node *n = self_node(call)) YGNodeSetMinContentWidth(n->yg, (float)aqua_arg_number(call, 0)); }
static void node_set_min_content_height(aqua_call *call) { if (Node *n = self_node(call)) YGNodeSetMinContentHeight(n->yg, (float)aqua_arg_number(call, 0)); }
static void node_get_min_content_width(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_double(call, YGNodeGetMinContentWidth(n->yg)); }
static void node_get_min_content_height(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_double(call, YGNodeGetMinContentHeight(n->yg)); }

static void node_set_context(aqua_call *call) { if (Node *n = self_node(call)) replace_callback(call, n, &n->context); }
static void node_get_context(aqua_call *call) {
    Node *n = self_node(call);
    if (n && n->context) aqua_return(call, aqua_ref_value(n->rt, n->context)); else aqua_return_null(call);
}
static void node_set_reference_baseline(aqua_call *call) { if (Node *n = self_node(call)) YGNodeSetIsReferenceBaseline(n->yg, aqua_arg_bool(call, 0)); }
static void node_is_reference_baseline(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_bool(call, YGNodeIsReferenceBaseline(n->yg)); }
static void node_set_node_type(aqua_call *call) {
    Node *n = self_node(call);
    int type;
    if (n && parse_enum("YGNodeType", aqua_arg(call, 0), aqua_call_runtime(call), &type)) YGNodeSetNodeType(n->yg, (YGNodeType)type);
    else if (n) aqua_fail(call, "Saturn.Node.SetNodeType: unknown node type / 未知的節點型別。");
}
static void node_get_node_type(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_int(call, YGNodeGetNodeType(n->yg)); }
static void node_set_containing_block(aqua_call *call) { if (Node *n = self_node(call)) YGNodeSetAlwaysFormsContainingBlock(n->yg, aqua_arg_bool(call, 0)); }
static void node_get_containing_block(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_bool(call, YGNodeGetAlwaysFormsContainingBlock(n->yg)); }
static void node_copy_style(aqua_call *call) {
    Node *n = self_node(call), *from = n ? arg_node(call, 0) : nullptr;
    if (from) YGNodeCopyStyle(n->yg, from->yg);
}

/* ---- Style properties (properties.def) ---- */

static bool fail_value(aqua_call *call, const char *method) {
    char *shown = aqua_display(aqua_call_runtime(call), aqua_arg(call, aqua_argc(call) - 1));
    aqua_fail(call, "Saturn.Node.%s: unsupported value %s / 不支援的值。", method, shown ? shown : "");
    aqua_free(shown);
    return false;
}

#define ENUM_PROPERTY(N, css, zh, T) \
    static bool set_##N(aqua_call *call, Node *n, aqua_value v) { int e; \
        if (!parse_enum(#T, v, aqua_call_runtime(call), &e)) return fail_value(call, "Set" #N); \
        YGNodeStyleSet##N(n->yg, (T)e); return true; } \
    static void method_set_##N(aqua_call *call) { if (Node *n = self_node(call)) set_##N(call, n, aqua_arg(call, 0)); } \
    static void method_get_##N(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_int(call, (int)YGNodeStyleGet##N(n->yg)); }
#define NUMBER_PROPERTY(N, css, zh) \
    static bool set_##N(aqua_call *call, Node *n, aqua_value v) { \
        if (!aqua_is_numeric(v) && !aqua_is_nullish(v)) return fail_value(call, "Set" #N); \
        YGNodeStyleSet##N(n->yg, aqua_is_nullish(v) ? YGUndefined : (float)aqua_number_of(v)); return true; } \
    static void method_set_##N(aqua_call *call) { if (Node *n = self_node(call)) set_##N(call, n, aqua_arg(call, 0)); } \
    static void method_get_##N(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_double(call, YGNodeStyleGet##N(n->yg)); }
#define SIZE_COMMON(N) \
    static void method_set_##N##Percent(aqua_call *call) { if (Node *n = self_node(call)) YGNodeStyleSet##N##Percent(n->yg, (float)aqua_arg_number(call, 0)); } \
    static void method_set_##N##MaxContent(aqua_call *call) { if (Node *n = self_node(call)) YGNodeStyleSet##N##MaxContent(n->yg); } \
    static void method_set_##N##FitContent(aqua_call *call) { if (Node *n = self_node(call)) YGNodeStyleSet##N##FitContent(n->yg); } \
    static void method_set_##N##Stretch(aqua_call *call) { if (Node *n = self_node(call)) YGNodeStyleSet##N##Stretch(n->yg); } \
    static void method_get_##N(aqua_call *call) { if (Node *n = self_node(call)) return_value(call, YGNodeStyleGet##N(n->yg)); } \
    static void method_set_##N(aqua_call *call) { if (Node *n = self_node(call)) set_##N(call, n, aqua_arg(call, 0)); }
#define SIZE_PROPERTY(N, css, zh) \
    static bool set_##N(aqua_call *call, Node *n, aqua_value v) { Length l; \
        if (!parse_length(aqua_call_runtime(call), v, &l)) return fail_value(call, "Set" #N); \
        switch (l.unit) { \
        case YGUnitPoint: YGNodeStyleSet##N(n->yg, l.value); break; \
        case YGUnitPercent: YGNodeStyleSet##N##Percent(n->yg, l.value); break; \
        case YGUnitAuto: YGNodeStyleSet##N##Auto(n->yg); break; \
        case YGUnitMaxContent: YGNodeStyleSet##N##MaxContent(n->yg); break; \
        case YGUnitFitContent: YGNodeStyleSet##N##FitContent(n->yg); break; \
        case YGUnitStretch: YGNodeStyleSet##N##Stretch(n->yg); break; \
        default: YGNodeStyleSet##N(n->yg, YGUndefined); } \
        return true; } \
    static void method_set_##N##Auto(aqua_call *call) { if (Node *n = self_node(call)) YGNodeStyleSet##N##Auto(n->yg); } \
    SIZE_COMMON(N)
#define BOUND_PROPERTY(N, css, zh) \
    static bool set_##N(aqua_call *call, Node *n, aqua_value v) { Length l; \
        if (!parse_length(aqua_call_runtime(call), v, &l) || l.unit == YGUnitAuto) return fail_value(call, "Set" #N); \
        switch (l.unit) { \
        case YGUnitPoint: YGNodeStyleSet##N(n->yg, l.value); break; \
        case YGUnitPercent: YGNodeStyleSet##N##Percent(n->yg, l.value); break; \
        case YGUnitMaxContent: YGNodeStyleSet##N##MaxContent(n->yg); break; \
        case YGUnitFitContent: YGNodeStyleSet##N##FitContent(n->yg); break; \
        case YGUnitStretch: YGNodeStyleSet##N##Stretch(n->yg); break; \
        default: YGNodeStyleSet##N(n->yg, YGUndefined); } \
        return true; } \
    SIZE_COMMON(N)
#define EDGE_COMMON(N) \
    static void method_set_##N(aqua_call *call) { Node *n = self_node(call); YGEdge e; \
        if (n && arg_edge(call, 0, &e)) set_##N(call, n, e, aqua_arg(call, 1)); } \
    static void method_set_##N##Percent(aqua_call *call) { Node *n = self_node(call); YGEdge e; \
        if (n && arg_edge(call, 0, &e)) YGNodeStyleSet##N##Percent(n->yg, e, (float)aqua_arg_number(call, 1)); } \
    static void method_get_##N(aqua_call *call) { Node *n = self_node(call); YGEdge e; \
        if (n && arg_edge(call, 0, &e)) return_value(call, YGNodeStyleGet##N(n->yg, e)); }
#define EDGE_PROPERTY(N, css, zh) \
    static bool set_##N(aqua_call *call, Node *n, YGEdge e, aqua_value v) { Length l; \
        if (!parse_length(aqua_call_runtime(call), v, &l) || l.unit > YGUnitAuto) return fail_value(call, "Set" #N); \
        if (l.unit == YGUnitPercent) YGNodeStyleSet##N##Percent(n->yg, e, l.value); \
        else if (l.unit == YGUnitAuto) YGNodeStyleSet##N##Auto(n->yg, e); \
        else YGNodeStyleSet##N(n->yg, e, l.value); \
        return true; } \
    static void method_set_##N##Auto(aqua_call *call) { Node *n = self_node(call); YGEdge e; \
        if (n && arg_edge(call, 0, &e)) YGNodeStyleSet##N##Auto(n->yg, e); } \
    EDGE_COMMON(N)
#define PADDING_PROPERTY(N, css, zh) \
    static bool set_##N(aqua_call *call, Node *n, YGEdge e, aqua_value v) { Length l; \
        if (!parse_length(aqua_call_runtime(call), v, &l) || l.unit > YGUnitPercent) return fail_value(call, "Set" #N); \
        if (l.unit == YGUnitPercent) YGNodeStyleSet##N##Percent(n->yg, e, l.value); \
        else YGNodeStyleSet##N(n->yg, e, l.value); \
        return true; } \
    EDGE_COMMON(N)
#define GRID_LINE_PROPERTY(N, css, zh) \
    static void method_set_##N(aqua_call *call) { if (Node *n = self_node(call)) YGNodeStyleSet##N(n->yg, aqua_arg_int(call, 0)); } \
    static void method_set_##N##Auto(aqua_call *call) { if (Node *n = self_node(call)) YGNodeStyleSet##N##Auto(n->yg); } \
    static void method_set_##N##Span(aqua_call *call) { if (Node *n = self_node(call)) YGNodeStyleSet##N##Span(n->yg, aqua_arg_int(call, 0)); } \
    static void method_get_##N(aqua_call *call) { if (Node *n = self_node(call)) aqua_return_int(call, YGNodeStyleGet##N(n->yg)); }
#define GRID_TRACKS_PROPERTY(N, P, css, zh) \
    static void method_set_##P##Count(aqua_call *call) { Node *n = self_node(call); int32_t c = aqua_arg_int(call, 0); \
        if (n && c >= 0) YGNodeStyleSet##P##Count(n->yg, (size_t)c); else if (n) aqua_fail(call, "Saturn.Node.Set" #P "Count: negative count."); } \
    static void method_set_##N(aqua_call *call) { Node *n = self_node(call); int t; int32_t i = aqua_arg_int(call, 0); \
        if (!n) return; \
        if (i < 0 || !parse_enum("YGGridTrackType", aqua_arg(call, 1), aqua_call_runtime(call), &t)) { fail_value(call, "Set" #N); return; } \
        YGNodeStyleSet##N(n->yg, (size_t)i, (YGGridTrackType)t, (float)aqua_arg_number(call, 2)); } \
    static void method_set_##N##MinMax(aqua_call *call) { Node *n = self_node(call); int t1, t2; int32_t i = aqua_arg_int(call, 0); \
        if (!n) return; aqua_runtime *rt = aqua_call_runtime(call); \
        if (i < 0 || !parse_enum("YGGridTrackType", aqua_arg(call, 1), rt, &t1) || !parse_enum("YGGridTrackType", aqua_arg(call, 3), rt, &t2)) { fail_value(call, "Set" #N "MinMax"); return; } \
        YGNodeStyleSet##N##MinMax(n->yg, (size_t)i, (YGGridTrackType)t1, (float)aqua_arg_number(call, 2), (YGGridTrackType)t2, (float)aqua_arg_number(call, 4)); }
#include "properties.def"
#undef ENUM_PROPERTY
#undef NUMBER_PROPERTY
#undef SIZE_PROPERTY
#undef BOUND_PROPERTY
#undef EDGE_PROPERTY
#undef PADDING_PROPERTY
#undef GRID_LINE_PROPERTY
#undef GRID_TRACKS_PROPERTY
#undef SIZE_COMMON
#undef EDGE_COMMON

static void method_set_border(aqua_call *call) {
    Node *n = self_node(call); YGEdge e;
    if (n && arg_edge(call, 0, &e)) YGNodeStyleSetBorder(n->yg, e, (float)aqua_arg_number(call, 1));
}
static void method_get_border(aqua_call *call) {
    Node *n = self_node(call); YGEdge e;
    if (n && arg_edge(call, 0, &e)) aqua_return_double(call, YGNodeStyleGetBorder(n->yg, e));
}
static bool arg_gutter(aqua_call *call, YGGutter *gutter) {
    int g;
    if (!parse_enum("YGGutter", aqua_arg(call, 0), aqua_call_runtime(call), &g)) { aqua_fail(call, "Saturn.Node: unknown gutter / 未知的間距方向。"); return false; }
    *gutter = (YGGutter)g;
    return true;
}
static bool set_gap(aqua_call *call, Node *n, YGGutter gutter, aqua_value v) {
    Length l;
    if (!parse_length(aqua_call_runtime(call), v, &l) || l.unit > YGUnitPercent) return fail_value(call, "SetGap");
    if (l.unit == YGUnitPercent) YGNodeStyleSetGapPercent(n->yg, gutter, l.value); else YGNodeStyleSetGap(n->yg, gutter, l.value);
    return true;
}
static void method_set_gap(aqua_call *call) { Node *n = self_node(call); YGGutter g; if (n && arg_gutter(call, &g)) set_gap(call, n, g, aqua_arg(call, 1)); }
static void method_set_gap_percent(aqua_call *call) {
    Node *n = self_node(call); YGGutter g;
    if (n && arg_gutter(call, &g)) YGNodeStyleSetGapPercent(n->yg, g, (float)aqua_arg_number(call, 1));
}
static void method_get_gap(aqua_call *call) { Node *n = self_node(call); YGGutter g; if (n && arg_gutter(call, &g)) return_value(call, YGNodeStyleGetGap(n->yg, g)); }

static void define_properties(aqua_class *node) {
#define ENUM_PROPERTY(N, css, zh, T) \
    aqua_define_function(node, "Set" #N "|設定" zh, method_set_##N, "v"); \
    aqua_define_function(node, "Get" #N "|取得" zh, method_get_##N, "");
#define NUMBER_PROPERTY(N, css, zh) \
    aqua_define_function(node, "Set" #N "|設定" zh, method_set_##N, "v"); \
    aqua_define_function(node, "Get" #N "|取得" zh, method_get_##N, "");
#define SIZE_COMMON(N, zh) \
    aqua_define_function(node, "Set" #N "|設定" zh, method_set_##N, "v"); \
    aqua_define_function(node, "Set" #N "Percent|設定" zh "百分比", method_set_##N##Percent, "n"); \
    aqua_define_function(node, "Set" #N "MaxContent|設定" zh "為最大內容", method_set_##N##MaxContent, ""); \
    aqua_define_function(node, "Set" #N "FitContent|設定" zh "為適應內容", method_set_##N##FitContent, ""); \
    aqua_define_function(node, "Set" #N "Stretch|設定" zh "為延展", method_set_##N##Stretch, ""); \
    aqua_define_function(node, "Get" #N "|取得" zh, method_get_##N, "");
#define SIZE_PROPERTY(N, css, zh) SIZE_COMMON(N, zh) \
    aqua_define_function(node, "Set" #N "Auto|設定" zh "為自動", method_set_##N##Auto, "");
#define BOUND_PROPERTY(N, css, zh) SIZE_COMMON(N, zh)
#define EDGE_COMMON(N, zh) \
    aqua_define_function(node, "Set" #N "|設定" zh, method_set_##N, "vv"); \
    aqua_define_function(node, "Set" #N "Percent|設定" zh "百分比", method_set_##N##Percent, "vn"); \
    aqua_define_function(node, "Get" #N "|取得" zh, method_get_##N, "v");
#define EDGE_PROPERTY(N, css, zh) EDGE_COMMON(N, zh) \
    aqua_define_function(node, "Set" #N "Auto|設定" zh "為自動", method_set_##N##Auto, "v");
#define PADDING_PROPERTY(N, css, zh) EDGE_COMMON(N, zh)
#define GRID_LINE_PROPERTY(N, css, zh) \
    aqua_define_function(node, "Set" #N "|設定" zh, method_set_##N, "i"); \
    aqua_define_function(node, "Set" #N "Auto|設定" zh "為自動", method_set_##N##Auto, ""); \
    aqua_define_function(node, "Set" #N "Span|設定" zh "跨越", method_set_##N##Span, "i"); \
    aqua_define_function(node, "Get" #N "|取得" zh, method_get_##N, "");
#define GRID_TRACKS_PROPERTY(N, P, css, zh) \
    aqua_define_function(node, "Set" #P "Count|設定" zh "數量", method_set_##P##Count, "i"); \
    aqua_define_function(node, "Set" #N "|設定" zh, method_set_##N, "ivn"); \
    aqua_define_function(node, "Set" #N "MinMax|設定" zh "範圍", method_set_##N##MinMax, "ivnvn");
#include "properties.def"
#undef ENUM_PROPERTY
#undef NUMBER_PROPERTY
#undef SIZE_PROPERTY
#undef BOUND_PROPERTY
#undef EDGE_PROPERTY
#undef PADDING_PROPERTY
#undef GRID_LINE_PROPERTY
#undef GRID_TRACKS_PROPERTY
#undef SIZE_COMMON
#undef EDGE_COMMON
    aqua_define_function(node, "SetBorder|設定框線", method_set_border, "vn");
    aqua_define_function(node, "GetBorder|取得框線", method_get_border, "v");
    aqua_define_function(node, "SetGap|設定間距", method_set_gap, "vv");
    aqua_define_function(node, "SetGapPercent|設定間距百分比", method_set_gap_percent, "vn");
    aqua_define_function(node, "GetGap|取得間距", method_get_gap, "v");
}

/* ---- Layout keys of SetStyle (CSS names; style.cpp handles the visual ones) ---- */

typedef bool (*value_setter)(aqua_call *, Node *, aqua_value);
typedef bool (*edge_setter)(aqua_call *, Node *, YGEdge, aqua_value);
static bool set_border_width(aqua_call *call, Node *n, YGEdge e, aqua_value v) {
    if (!aqua_is_numeric(v) && !aqua_is_nullish(v)) return fail_value(call, "SetStyle border width");
    YGNodeStyleSetBorder(n->yg, e, aqua_is_nullish(v) ? YGUndefined : (float)aqua_number_of(v));
    return true;
}

static const struct { const char *css; value_setter set; } value_keys[] = {
#define ENUM_PROPERTY(N, css, zh, T) {css, set_##N},
#define NUMBER_PROPERTY(N, css, zh) {css, set_##N},
#define SIZE_PROPERTY(N, css, zh) {css, set_##N},
#define BOUND_PROPERTY(N, css, zh) {css, set_##N},
#define EDGE_PROPERTY(N, css, zh)
#define PADDING_PROPERTY(N, css, zh)
#define GRID_LINE_PROPERTY(N, css, zh)
#define GRID_TRACKS_PROPERTY(N, P, css, zh)
#include "properties.def"
#undef ENUM_PROPERTY
#undef NUMBER_PROPERTY
#undef SIZE_PROPERTY
#undef BOUND_PROPERTY
#undef EDGE_PROPERTY
#undef PADDING_PROPERTY
#undef GRID_LINE_PROPERTY
#undef GRID_TRACKS_PROPERTY
};
/* Edge families: margin, marginTop, marginX, ...; positions use inset, insetTop, ... and the bare top/left/... keys. */
static const struct { const char *prefix, *suffix; edge_setter set; } edge_keys[] = {
    {"margin", "", set_Margin}, {"padding", "", set_Padding}, {"inset", "", set_Position}, {"border", "Width", set_border_width},
};
static const struct { const char *name; YGEdge edge; } edge_names[] = {
    {"", YGEdgeAll}, {"Left", YGEdgeLeft}, {"Top", YGEdgeTop}, {"Right", YGEdgeRight}, {"Bottom", YGEdgeBottom},
    {"Start", YGEdgeStart}, {"End", YGEdgeEnd}, {"Horizontal", YGEdgeHorizontal}, {"Vertical", YGEdgeVertical},
    {"X", YGEdgeHorizontal}, {"Y", YGEdgeVertical},
};

/* The CSS shorthand forms of an all-edges value: [all], [vertical, horizontal], [top, horizontal, bottom] or
   [top, right, bottom, left]. */
static bool set_edges(aqua_call *call, Node *n, edge_setter set, aqua_value v) {
    aqua_runtime *rt = aqua_call_runtime(call);
    if (v.type != AQUA_ARRAY) return set(call, n, YGEdgeAll, v);
    uint32_t count = aqua_array_length(rt, v);
    auto at = [&](uint32_t i) { return aqua_array_get(rt, v, i); };
    switch (count) {
    case 1: return set(call, n, YGEdgeAll, at(0));
    case 2: return set(call, n, YGEdgeVertical, at(0)) && set(call, n, YGEdgeHorizontal, at(1));
    case 3: return set(call, n, YGEdgeTop, at(0)) && set(call, n, YGEdgeHorizontal, at(1)) && set(call, n, YGEdgeBottom, at(2));
    case 4: return set(call, n, YGEdgeTop, at(0)) && set(call, n, YGEdgeRight, at(1)) && set(call, n, YGEdgeBottom, at(2)) && set(call, n, YGEdgeLeft, at(3));
    default: return fail_value(call, "SetStyle edges");
    }
}

/* A grid line: an integer, "auto", "span 2" (or a hash {"span": 2}). */
static bool parse_grid_line(aqua_runtime *rt, aqua_value v, int *kind, int *value) {
    if (aqua_is_nullish(v)) { *kind = 0; return true; }
    if (aqua_is_number(v)) { *kind = 1; *value = (int)v.number; return true; }
    aqua_value span;
    if (v.type == AQUA_HASH && aqua_hash_field(rt, v, "span", &span) && aqua_is_number(span)) { *kind = 2; *value = (int)span.number; return true; }
    if (v.type != AQUA_STRING) return false;
    char *text = aqua_text_dup(rt, v, nullptr);
    std::string s(text);
    aqua_free(text);
    if (same_name(s.c_str(), "auto")) { *kind = 0; return true; }
    *kind = 1;
    const char *p = s.c_str();
    while (*p == ' ') p++;
    if (!strncmp(p, "span", 4)) { *kind = 2; p += 4; }
    char *end;
    long n = strtol(p, &end, 10);
    while (*end == ' ') end++;
    if (end == p || *end) return false;
    *value = (int)n;
    return true;
}
#define GRID_LINE_SETTER(N) static bool set_##N(aqua_call *call, Node *n, aqua_value v) { int kind, value = 0; \
        if (!parse_grid_line(aqua_call_runtime(call), v, &kind, &value)) return fail_value(call, "SetStyle " #N); \
        if (kind == 0) YGNodeStyleSet##N##Auto(n->yg); else if (kind == 1) YGNodeStyleSet##N(n->yg, value); \
        else YGNodeStyleSet##N##Span(n->yg, value); \
        return true; }
GRID_LINE_SETTER(GridColumnStart)
GRID_LINE_SETTER(GridColumnEnd)
GRID_LINE_SETTER(GridRowStart)
GRID_LINE_SETTER(GridRowEnd)
#undef GRID_LINE_SETTER
/* gridColumn / gridRow: "1 / 3", "span 2", [start, end] or a single line. */
static bool set_grid_span(aqua_call *call, Node *n, aqua_value v, value_setter start, value_setter end) {
    aqua_runtime *rt = aqua_call_runtime(call);
    if (v.type == AQUA_ARRAY && aqua_array_length(rt, v) == 2) return start(call, n, aqua_array_get(rt, v, 0)) && end(call, n, aqua_array_get(rt, v, 1));
    if (v.type == AQUA_STRING) {
        char *text = aqua_text_dup(rt, v, nullptr);
        std::string s(text);
        aqua_free(text);
        size_t slash = s.find('/');
        if (slash != std::string::npos) {
            std::string a = s.substr(0, slash), b = s.substr(slash + 1);
            return start(call, n, aqua_text(rt, a.data(), a.size())) && end(call, n, aqua_text(rt, b.data(), b.size()));
        }
    }
    return start(call, n, v);
}

/* Grid tracks: an array of track sizes, or a string of them separated by spaces. A track is a number (points), "50%",
   "auto", "2fr" or "minmax(min, max)" (also ["minmax", min, max]). */
struct Track { YGGridTrackType type; float value; };
static bool parse_track(const std::string &text, Track *out) {
    std::string s = text;
    while (!s.empty() && s.front() == ' ') s.erase(0, 1);
    while (!s.empty() && s.back() == ' ') s.pop_back();
    if (same_name(s.c_str(), "auto")) { *out = {YGGridTrackTypeAuto, 0}; return true; }
    YGGridTrackType type = YGGridTrackTypePoints;
    if (s.size() > 2 && s.compare(s.size() - 2, 2, "fr") == 0) { type = YGGridTrackTypeFr; s.resize(s.size() - 2); }
    else if (s.size() > 1 && s.back() == '%') { type = YGGridTrackTypePercent; s.pop_back(); }
    else if (s.size() > 2 && s.compare(s.size() - 2, 2, "px") == 0) s.resize(s.size() - 2);
    char *end;
    double n = strtod(s.c_str(), &end);
    if (s.empty() || *end || !std::isfinite(n)) return false;
    *out = {type, (float)n};
    return true;
}
static bool track_of(aqua_runtime *rt, aqua_value v, Track *out) {
    if (aqua_is_numeric(v)) { *out = {YGGridTrackTypePoints, (float)aqua_number_of(v)}; return true; }
    if (v.type != AQUA_STRING) return false;
    char *text = aqua_text_dup(rt, v, nullptr);
    bool ok = parse_track(text, out);
    aqua_free(text);
    return ok;
}
typedef void (*track_count)(YGNodeRef, size_t);
typedef void (*track_set)(YGNodeRef, size_t, YGGridTrackType, float);
typedef void (*track_minmax)(YGNodeRef, size_t, YGGridTrackType, float, YGGridTrackType, float);
static bool set_tracks(aqua_call *call, Node *n, aqua_value v, track_count count, track_set set, track_minmax minmax) {
    aqua_runtime *rt = aqua_call_runtime(call);
    /* Split a string into tracks, keeping minmax(...) together. */
    std::vector<std::string> parts;
    std::vector<aqua_value> items;
    if (v.type == AQUA_STRING) {
        char *text = aqua_text_dup(rt, v, nullptr);
        std::string s(text), part;
        aqua_free(text);
        int depth = 0;
        for (char c : s) {
            if (c == '(') depth++;
            if (c == ')') depth--;
            if (c == ' ' && depth == 0) { if (!part.empty()) parts.push_back(part); part.clear(); } else part += c;
        }
        if (!part.empty()) parts.push_back(part);
    } else if (v.type == AQUA_ARRAY) {
        for (uint32_t i = 0, c = aqua_array_length(rt, v); i < c; i++) items.push_back(aqua_array_get(rt, v, i));
    } else if (!aqua_is_nullish(v)) return fail_value(call, "SetStyle grid tracks");
    size_t total = v.type == AQUA_STRING ? parts.size() : items.size();
    count(n->yg, total);
    for (size_t i = 0; i < total; i++) {
        Track a{}, b{};
        bool ok;
        if (v.type == AQUA_STRING) {
            const std::string &p = parts[i];
            if (!p.compare(0, 7, "minmax(") && p.back() == ')') {
                size_t comma = p.find(',');
                ok = comma != std::string::npos && parse_track(p.substr(7, comma - 7), &a) && parse_track(p.substr(comma + 1, p.size() - comma - 2), &b);
                if (ok) minmax(n->yg, i, a.type, a.value, b.type, b.value);
            } else if ((ok = parse_track(p, &a))) set(n->yg, i, a.type, a.value);
        } else if (items[i].type == AQUA_ARRAY && aqua_array_length(rt, items[i]) == 3) {
            ok = track_of(rt, aqua_array_get(rt, items[i], 1), &a) && track_of(rt, aqua_array_get(rt, items[i], 2), &b);
            if (ok) minmax(n->yg, i, a.type, a.value, b.type, b.value);
        } else if ((ok = track_of(rt, items[i], &a))) set(n->yg, i, a.type, a.value);
        if (!ok) return fail_value(call, "SetStyle grid tracks");
    }
    return true;
}

bool set_layout_style(aqua_call *call, Node *n, std::string_view key, aqua_value v, bool *handled) {
    *handled = true;
    for (auto &k : value_keys) if (key == k.css) return k.set(call, n, v);
    if (key == "top" || key == "left" || key == "right" || key == "bottom" || key == "start" || key == "end") {
        std::string name(key);
        name[0] = (char)std::toupper((unsigned char)name[0]);
        for (auto &e : edge_names) if (name == e.name) return set_Position(call, n, e.edge, v);
    }
    for (auto &k : edge_keys) {
        size_t prefix = strlen(k.prefix), suffix = strlen(k.suffix);
        if (key.size() < prefix + suffix || key.compare(0, prefix, k.prefix) || key.compare(key.size() - suffix, suffix, k.suffix)) continue;
        std::string_view middle = key.substr(prefix, key.size() - prefix - suffix);
        for (auto &e : edge_names) {
            if (middle != e.name) continue;
            return e.edge == YGEdgeAll ? set_edges(call, n, k.set, v) : k.set(call, n, e.edge, v);
        }
    }
    aqua_runtime *rt = aqua_call_runtime(call);
    if (key == "gap") {
        if (v.type == AQUA_ARRAY && aqua_array_length(rt, v) == 2)
            return set_gap(call, n, YGGutterRow, aqua_array_get(rt, v, 0)) && set_gap(call, n, YGGutterColumn, aqua_array_get(rt, v, 1));
        return set_gap(call, n, YGGutterAll, v);
    }
    if (key == "rowGap") return set_gap(call, n, YGGutterRow, v);
    if (key == "columnGap") return set_gap(call, n, YGGutterColumn, v);
    if (key == "gridColumnStart") return set_GridColumnStart(call, n, v);
    if (key == "gridColumnEnd") return set_GridColumnEnd(call, n, v);
    if (key == "gridRowStart") return set_GridRowStart(call, n, v);
    if (key == "gridRowEnd") return set_GridRowEnd(call, n, v);
    if (key == "gridColumn") return set_grid_span(call, n, v, set_GridColumnStart, set_GridColumnEnd);
    if (key == "gridRow") return set_grid_span(call, n, v, set_GridRowStart, set_GridRowEnd);
    if (key == "gridTemplateColumns") return set_tracks(call, n, v, YGNodeStyleSetGridTemplateColumnsCount, YGNodeStyleSetGridTemplateColumn, YGNodeStyleSetGridTemplateColumnMinMax);
    if (key == "gridTemplateRows") return set_tracks(call, n, v, YGNodeStyleSetGridTemplateRowsCount, YGNodeStyleSetGridTemplateRow, YGNodeStyleSetGridTemplateRowMinMax);
    if (key == "gridAutoColumns") return set_tracks(call, n, v, YGNodeStyleSetGridAutoColumnsCount, YGNodeStyleSetGridAutoColumn, YGNodeStyleSetGridAutoColumnMinMax);
    if (key == "gridAutoRows") return set_tracks(call, n, v, YGNodeStyleSetGridAutoRowsCount, YGNodeStyleSetGridAutoRow, YGNodeStyleSetGridAutoRowMinMax);
    *handled = false;
    return true;
}

/* ---- Module helpers ---- */

static void module_float_is_undefined(aqua_call *call) { aqua_return_bool(call, YGFloatIsUndefined((float)aqua_arg_number(call, 0))); }
static void module_enum_name(aqua_call *call) {
    /* Saturn.EnumName("Align", Saturn.AlignCenter) -> "center" */
    aqua_str type = aqua_arg_str(call, 0);
    std::string full = std::string("YG") + type.data;
    aqua_return_cstring(call, enum_name(full.c_str(), aqua_arg_int(call, 1)));
}

}  // namespace saturn

/* Contain(callback): runs the callback to completion right here (it may not wait for asynchronous results) and gives
   {Success, Value, Message}: what SaturnUI wraps painters, builders and the input method in, so a failing widget loses
   only its own part. Unlike an asynchronous try, it never pauses: painting from native code can rely on it. */
static void module_contain(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value result = aqua_null();
    bool ok = aqua_invoke(rt, aqua_arg(call, 0), nullptr, 0, &result);
    aqua_value record = aqua_hash_new(rt, 3);
    aqua_hash_set_field(rt, record, "Success", aqua_bool(ok));
    aqua_hash_set_field(rt, record, "Value", ok ? result : aqua_null());
    if (!ok) {
        char *shown = aqua_display(rt, result);
        const char *text = shown ? shown : "failed";
        while (!strncmp(text, "ERROR: ", 7)) text += 7;
        aqua_hash_set_field(rt, record, "Message", aqua_cstring(rt, text));
        aqua_free(shown);
    }
    aqua_return(call, record);
}

using namespace saturn;

AQUA_LIBRARY(saturn) {
    aqua_library_state(library, state_create, state_destroy);
    aqua_class *module = aqua_define_module(library, "Saturn|土星");
    node_class = aqua_define_class(library, "Saturn.Node", node_finalize);
    config_class = aqua_define_class(library, "Saturn.Config", config_finalize);
    value_class = aqua_define_class(library, "Saturn.Value", value_finalize);

    aqua_define_function(module, "Node|NodeNew|節點", node_new, "|o");
    aqua_define_function(module, "Config|ConfigNew|設定檔", config_new, "");
    aqua_define_function(module, "FloatIsUndefined|是否未定義", module_float_is_undefined, "n");
    aqua_define_function(module, "EnumName|列舉名稱", module_enum_name, "si");
    aqua_define_function(module, "Contain|隔離執行", module_contain, "f");
    aqua_define_double(module, "Undefined|未定義", NAN);
#define VALUE(type, constant, value, css) aqua_define_int(module, #constant, (int32_t)(value));
#include "constants.def"
#undef VALUE

    aqua_define_property(value_class, "value|數值", value_value);
    aqua_define_property(value_class, "unit|單位", value_unit);
    aqua_define_property(value_class, "unitName|單位名稱", value_unit_name);

    aqua_define_function(config_class, "SetUseWebDefaults|設定網頁預設", config_set_web_defaults, "b");
    aqua_define_function(config_class, "GetUseWebDefaults|取得網頁預設", config_get_web_defaults, "");
    aqua_define_function(config_class, "SetPointScaleFactor|設定點縮放", config_set_scale, "n");
    aqua_define_function(config_class, "GetPointScaleFactor|取得點縮放", config_get_scale, "");
    aqua_define_function(config_class, "SetErrata|設定相容誤差", config_set_errata, "v");
    aqua_define_function(config_class, "GetErrata|取得相容誤差", config_get_errata, "");
    aqua_define_function(config_class, "SetExperimentalFeatureEnabled|設定實驗功能", config_set_experimental, "vb");
    aqua_define_function(config_class, "IsExperimentalFeatureEnabled|實驗功能已啟用", config_is_experimental, "v");
    aqua_define_function(config_class, "SetLogger|設定記錄器", config_set_logger, "|f");

    aqua_class *node = node_class;
    aqua_define_function(node, "Clone|複製", node_clone, "");
    aqua_define_function(node, "Free|釋放", node_free, "");
    aqua_define_function(node, "FreeRecursive|遞迴釋放", node_free_recursive, "");
    aqua_define_function(node, "IsFreed|已釋放", node_is_freed, "");
    aqua_define_function(node, "Reset|重設", node_reset, "");
    aqua_define_function(node, "CopyStyle|複製樣式", node_copy_style, "o");
    aqua_define_function(node, "InsertChild|插入子節點", node_insert_child, "o|i");
    aqua_define_function(node, "SwapChild|替換子節點", node_swap_child, "oi");
    aqua_define_function(node, "RemoveChild|移除子節點", node_remove_child, "o");
    aqua_define_function(node, "RemoveAllChildren|移除所有子節點", node_remove_all, "");
    aqua_define_function(node, "SetChildren|設定子節點", node_set_children, "a");
    aqua_define_function(node, "GetChild|取得子節點", node_get_child, "i");
    aqua_define_function(node, "GetChildCount|子節點數", node_child_count, "");
    aqua_define_function(node, "GetChildren|取得子節點列表", node_children, "");
    aqua_define_function(node, "GetOwner|GetParent|取得父節點", node_get_owner, "");
    aqua_define_function(node, "GetConfig|取得設定檔", node_get_config, "");
    aqua_define_function(node, "SetConfig|設定設定檔", node_set_config, "o");
    aqua_define_function(node, "CalculateLayout|計算佈局", node_calculate_layout, "|vvv");
    aqua_define_function(node, "GetComputedLeft|計算左", layout_left, "");
    aqua_define_function(node, "GetComputedTop|計算上", layout_top, "");
    aqua_define_function(node, "GetComputedRight|計算右", layout_right, "");
    aqua_define_function(node, "GetComputedBottom|計算下", layout_bottom, "");
    aqua_define_function(node, "GetComputedWidth|計算寬度", layout_width, "");
    aqua_define_function(node, "GetComputedHeight|計算高度", layout_height, "");
    aqua_define_function(node, "GetComputedRawWidth|計算原始寬度", layout_raw_width, "");
    aqua_define_function(node, "GetComputedRawHeight|計算原始高度", layout_raw_height, "");
    aqua_define_function(node, "GetComputedLayout|計算佈局結果", layout_record, "");
    aqua_define_function(node, "GetComputedDirection|計算方向", layout_direction, "");
    aqua_define_function(node, "GetComputedMargin|計算外距", layout_margin, "v");
    aqua_define_function(node, "GetComputedBorder|計算框線", layout_border, "v");
    aqua_define_function(node, "GetComputedPadding|計算內距", layout_padding, "v");
    aqua_define_function(node, "GetHadOverflow|是否溢出", layout_overflow, "");
    aqua_define_function(node, "GetHasNewLayout|有新佈局", node_has_new_layout, "");
    aqua_define_function(node, "SetHasNewLayout|設定有新佈局", node_set_has_new_layout, "b");
    aqua_define_function(node, "IsDirty|需重算", node_is_dirty, "");
    aqua_define_function(node, "MarkDirty|標記重算", node_mark_dirty, "");
    aqua_define_function(node, "SetMeasureFunc|設定測量函式", node_set_measure, "|f");
    aqua_define_function(node, "UnsetMeasureFunc|取消測量函式", node_set_measure, "");
    aqua_define_function(node, "HasMeasureFunc|有測量函式", node_has_measure, "");
    aqua_define_function(node, "SetMinContentMeasureFunc|設定最小內容測量函式", node_set_min_content, "|f");
    aqua_define_function(node, "HasMinContentMeasureFunc|有最小內容測量函式", node_has_min_content, "");
    aqua_define_function(node, "SetMinContentWidth|設定最小內容寬度", node_set_min_content_width, "n");
    aqua_define_function(node, "SetMinContentHeight|設定最小內容高度", node_set_min_content_height, "n");
    aqua_define_function(node, "GetMinContentWidth|取得最小內容寬度", node_get_min_content_width, "");
    aqua_define_function(node, "GetMinContentHeight|取得最小內容高度", node_get_min_content_height, "");
    aqua_define_function(node, "SetBaselineFunc|設定基線函式", node_set_baseline, "|f");
    aqua_define_function(node, "HasBaselineFunc|有基線函式", node_has_baseline, "");
    aqua_define_function(node, "SetDirtiedFunc|設定重算通知", node_set_dirtied, "|f");
    aqua_define_function(node, "SetContext|設定內容", node_set_context, "|v");
    aqua_define_function(node, "GetContext|取得內容", node_get_context, "");
    aqua_define_function(node, "SetIsReferenceBaseline|設定為參考基線", node_set_reference_baseline, "b");
    aqua_define_function(node, "IsReferenceBaseline|是參考基線", node_is_reference_baseline, "");
    aqua_define_function(node, "SetNodeType|設定節點型別", node_set_node_type, "v");
    aqua_define_function(node, "GetNodeType|取得節點型別", node_get_node_type, "");
    aqua_define_function(node, "SetAlwaysFormsContainingBlock|設定形成包含區塊", node_set_containing_block, "b");
    aqua_define_function(node, "GetAlwaysFormsContainingBlock|取得形成包含區塊", node_get_containing_block, "");
    define_properties(node);
    define_style(module, node);
    define_text(module, node);
    define_paint(library, module, node);
}
