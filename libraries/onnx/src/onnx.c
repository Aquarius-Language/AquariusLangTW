/* The ONNX|推論引擎|ONNXRuntime module: provider, preference and type constants, device listing, sessions and tensors.
 * Names, aliases, argument counts, results and error messages are the old binding's: a failure stops the script with
 * "推論引擎.<function>：<reason>".
 *
 * 推論引擎模組：常數、後端清單、工作階段與張量；錯誤訊息沿用舊版。 */
#include "onnx.h"
#include "platform.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define PREFIX "推論引擎"

static aqua_class *session_class, *tensor_class, *device_class, *info_class;

struct session_object {
    onnx_state *owner;        /* NULL once the runtime's state is gone */
    onnx_session *session;    /* NULL once disposed */
    onnx_provider backend;
    onnx_kind kind;
    onnx_provider chain[ONNX_PROVIDER_COUNT];
    uint32_t chain_count;
    session_object *next;
};

typedef struct tensor_object { onnx_tensor tensor; char *name; } tensor_object;

static void fail(aqua_call *call, const char *function, const onnx_error *error) {
    aqua_fail(call, PREFIX ".%s：%s", function, error->message);
}

/* ---- Values shared with mediapipe.c ------------------------------------------------------------------------ */
bool onnx_selection_of(aqua_runtime *rt, aqua_value value, onnx_selection *selection, onnx_error *error) {
    if (value.type == AQUA_STRING) {
        char *text = aqua_text_dup(rt, value, NULL);
        const char *texts[1] = {text};
        bool ok = onnx_selection_parse(texts, 1, selection, error);
        aqua_free(text);
        return ok;
    }
    if (value.type != AQUA_ARRAY) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "推論後端必須是字串或字串陣列。");
    uint32_t count = aqua_array_length(rt, value);
    char **texts = calloc(count ? count : 1, sizeof *texts);
    bool ok = texts != NULL;
    for (uint32_t i = 0; ok && i < count; i++) {
        aqua_value item = aqua_array_get(rt, value, i);
        if (item.type != AQUA_STRING) ok = onnx_fail(error, ONNX_INVALID_ARGUMENT, "預期字串參數。");
        else texts[i] = aqua_text_dup(rt, item, NULL);
    }
    if (ok) ok = onnx_selection_parse((const char *const *)texts, count, selection, error);
    for (uint32_t i = 0; texts && i < count; i++) aqua_free(texts[i]);
    free(texts);
    return ok;
}

static bool same_prefix(const char *path, const char *root, size_t length) {
#if defined(AQUA_OS_WINDOWS)
    for (size_t i = 0; i < length; i++) {
        char a = path[i] >= 'A' && path[i] <= 'Z' ? (char)(path[i] + 32) : path[i];
        char b = root[i] >= 'A' && root[i] <= 'Z' ? (char)(root[i] + 32) : root[i];
        if (a != b) return false;
    }
    return true;
#else
    return strncmp(path, root, length) == 0;
#endif
}

uint8_t *onnx_model_bytes(aqua_call *call, aqua_value model, size_t *length, bool *owned, onnx_error *error) {
    aqua_runtime *rt = aqua_call_runtime(call);
    *owned = false;
    if (model.type == AQUA_ARRAY) {
        /* The model's bytes themselves (an addition to the old contract, which took paths only). */
        uint32_t count = aqua_array_length(rt, model);
        if (count > ONNX_MAX_MODEL_BYTES) { onnx_fail(error, ONNX_INVALID_ARGUMENT, "模型大小超過上限。"); return NULL; }
        uint8_t *bytes = malloc(count ? count : 1);
        if (!bytes || !aqua_array_read_u8(rt, model, 0, count, bytes)) {
            free(bytes);
            onnx_fail(error, ONNX_INVALID_ARGUMENT, "模型位元組必須是 0 到 255 的整數。");
            return NULL;
        }
        *length = count;
        *owned = true;
        return bytes;
    }
    if (model.type != AQUA_STRING) { onnx_fail(error, ONNX_INVALID_ARGUMENT, "預期字串參數。"); return NULL; }
    aqua_str text = aqua_str_of(call, model);
    char *path = aqua_scratch(call, text.length + 1);
    memcpy(path, text.data, text.length + 1);
    for (char *p = path; *p; p++) if (*p == '\\') *p = '/';
    /* A packaged asset: by its package path, or by an absolute path into the package (目前工作目錄 + "/模型.onnx"). */
    const char *root = aqua_application_directory(rt);
    size_t root_length = strlen(root);
    const char *relative = path;
    if (root_length && strlen(path) > root_length && path[root_length] == '/' && same_prefix(path, root, root_length)) relative = path + root_length + 1;
    else if (!root_length && path[0] == '/') relative = path + 1; /* no application directory (web): "/x" is the package's */
    size_t size = 0;
    const uint8_t *asset = aqua_asset(rt, relative, &size);
    if (asset) {
        if (size > ONNX_MAX_MODEL_BYTES) { onnx_fail(error, ONNX_INVALID_ARGUMENT, "模型大小超過上限。"); return NULL; }
        *length = size;
        return (uint8_t *)asset;
    }
#if !defined(AQUA_OS_WEB)
    /* Otherwise a file, as the old desktop host read unpackaged models. */
    bool too_large = false;
    uint8_t *bytes = onnx_platform_read_file(path, ONNX_MAX_MODEL_BYTES, length, &too_large);
    if (too_large) { onnx_fail(error, ONNX_INVALID_ARGUMENT, "模型大小超過上限。"); return NULL; }
    if (bytes) { *owned = true; return bytes; }
#endif
    onnx_fail(error, ONNX_INVALID_ARGUMENT, "找不到模型檔案「%s」。", text.data);
    return NULL;
}

/* ---- Records -------------------------------------------------------------------------------------------------- */
static aqua_value string_array(aqua_runtime *rt, const onnx_provider *providers, uint32_t count) {
    aqua_value array = aqua_array_new(rt, count);
    for (uint32_t i = 0; i < count; i++) aqua_array_set(rt, array, i, aqua_cstring(rt, onnx_provider_id(providers[i])));
    return array;
}

/* Dimensions as integers; dynamic axes are -1. */
static aqua_value shape_array(aqua_runtime *rt, const int64_t *shape, uint32_t rank) {
    aqua_value array = aqua_array_new(rt, rank);
    for (uint32_t i = 0; i < rank; i++) aqua_array_set(rt, array, i, aqua_int((int32_t)shape[i]));
    return array;
}

static void device_finalize(aqua_runtime *rt, void *data) { (void)rt; free(data); }
static const onnx_device *device_self(aqua_call *call) { return aqua_self(call); }
static void device_provider(aqua_call *call) { aqua_return_cstring(call, onnx_provider_id(device_self(call)->provider)); }
static void device_kind(aqua_call *call) { aqua_return_cstring(call, onnx_kind_name(device_self(call)->kind)); }
static void device_name(aqua_call *call) { aqua_return_cstring(call, device_self(call)->name); }
static void device_available(aqua_call *call) { aqua_return_bool(call, device_self(call)->available); }
static void device_automatic(aqua_call *call) { aqua_return_bool(call, device_self(call)->available && device_self(call)->automatic); }
static void device_reason(aqua_call *call) { aqua_return_cstring(call, device_self(call)->reason); }

static void info_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    onnx_value_info *info = data;
    free(info->name); free(info->shape); free(info);
}
static const onnx_value_info *info_self(aqua_call *call) { return aqua_self(call); }
static void info_name(aqua_call *call) { aqua_return_cstring(call, info_self(call)->name); }
static void info_type(aqua_call *call) { aqua_return_cstring(call, onnx_type_name(info_self(call)->type)); }
static void info_shape(aqua_call *call) { aqua_return(call, shape_array(aqua_call_runtime(call), info_self(call)->shape, info_self(call)->rank)); }

static aqua_value info_array(aqua_runtime *rt, const onnx_value_info *infos, uint32_t count) {
    aqua_value array = aqua_array_new(rt, count);
    for (uint32_t i = 0; i < count; i++) {
        onnx_value_info *copy = calloc(1, sizeof *copy);
        copy->name = strdup(infos[i].name);
        copy->type = infos[i].type;
        copy->rank = infos[i].rank;
        copy->shape = calloc(infos[i].rank ? infos[i].rank : 1, sizeof *copy->shape);
        memcpy(copy->shape, infos[i].shape, infos[i].rank * sizeof *copy->shape);
        aqua_array_set(rt, array, i, aqua_object_new(rt, info_class, copy));
    }
    return array;
}

/* ---- Tensors -------------------------------------------------------------------------------------------------- */
static void tensor_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    tensor_object *t = data;
    onnx_tensor_free(&t->tensor);
    free(t->name);
    free(t);
}

static aqua_value tensor_value(aqua_runtime *rt, onnx_tensor tensor, const char *name) {
    tensor_object *t = calloc(1, sizeof *t);
    t->tensor = tensor;
    t->name = strdup(name);
    return aqua_object_new(rt, tensor_class, t);
}

static tensor_object *tensor_self(aqua_call *call) { return aqua_self(call); }
static void tensor_name(aqua_call *call) { aqua_return_cstring(call, tensor_self(call)->name); }
static void tensor_type(aqua_call *call) { aqua_return_cstring(call, onnx_type_name(tensor_self(call)->tensor.type)); }
static void tensor_shape(aqua_call *call) {
    const onnx_tensor *t = &tensor_self(call)->tensor;
    aqua_return(call, shape_array(aqua_call_runtime(call), t->shape, t->rank));
}
static void tensor_data(aqua_call *call) { aqua_return(call, onnx_tensor_values(aqua_call_runtime(call), &tensor_self(call)->tensor)); }

/* ---- Sessions ------------------------------------------------------------------------------------------------- */
static void unlink_session(session_object *s) {
    if (!s->owner) return;
    for (session_object **p = &s->owner->sessions; *p; p = &(*p)->next) {
        if (*p == s) { *p = s->next; s->owner->session_count--; break; }
    }
    s->owner = NULL;
}

static void session_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    session_object *s = data;
    unlink_session(s);
    backend_close(s->session);
    free(s);
}

void onnx_close_sessions(onnx_state *state) {
    while (state->sessions) {
        session_object *s = state->sessions;
        state->sessions = s->next;
        backend_close(s->session);
        s->session = NULL;
        s->owner = NULL;
    }
    state->session_count = 0;
}

static session_object *session_self(aqua_call *call) { return aqua_self(call); }
static bool live(aqua_call *call, const char *function) {
    if (session_self(call)->session) return true;
    aqua_fail(call, PREFIX ".%s：推論工作階段已關閉。", function);
    return false;
}
static void session_backend(aqua_call *call) { aqua_return_cstring(call, onnx_provider_id(session_self(call)->backend)); }
static void session_device(aqua_call *call) { aqua_return_cstring(call, onnx_kind_name(session_self(call)->kind)); }
static void session_providers(aqua_call *call) {
    session_object *s = session_self(call);
    aqua_return(call, string_array(aqua_call_runtime(call), s->chain, s->chain_count));
}

static void session_inputs(aqua_call *call) {
    if (!live(call, "取得輸入資訊")) return;
    uint32_t count;
    const onnx_value_info *infos = backend_inputs(session_self(call)->session, &count);
    aqua_return(call, info_array(aqua_call_runtime(call), infos, count));
}

static void session_outputs(aqua_call *call) {
    if (!live(call, "取得輸出資訊")) return;
    uint32_t count;
    const onnx_value_info *infos = backend_outputs(session_self(call)->session, &count);
    aqua_return(call, info_array(aqua_call_runtime(call), infos, count));
}

static void session_dispose(aqua_call *call) {
    session_object *s = session_self(call);
    unlink_session(s);
    backend_close(s->session);
    s->session = NULL;
}

/* UTF-8 copies (call scratch) of an array of strings, or NULL after failing with the old messages. */
static const char **texts_of(aqua_call *call, aqua_value array, uint32_t *count, const char *function) {
    aqua_runtime *rt = aqua_call_runtime(call);
    if (array.type != AQUA_ARRAY) { aqua_fail(call, PREFIX ".%s：預期陣列參數。", function); return NULL; }
    *count = aqua_array_length(rt, array);
    const char **texts = aqua_scratch(call, (*count + 1) * sizeof *texts);
    for (uint32_t i = 0; i < *count; i++) {
        aqua_value item = aqua_array_get(rt, array, i);
        if (item.type != AQUA_STRING) { aqua_fail(call, PREFIX ".%s：預期字串參數。", function); return NULL; }
        texts[i] = aqua_str_of(call, item).data;
    }
    return texts;
}

static void session_run(aqua_call *call) {
    static const char *const function = "推論";
    if (!live(call, function)) return;
    aqua_runtime *rt = aqua_call_runtime(call);
    session_object *s = session_self(call);
    uint32_t name_count, output_count;
    const char **names = texts_of(call, aqua_arg(call, 0), &name_count, function);
    if (!names) return;
    aqua_value values = aqua_arg(call, 1);
    if (values.type != AQUA_ARRAY) { aqua_fail(call, PREFIX ".%s：預期陣列參數。", function); return; }
    const char **outputs = texts_of(call, aqua_arg(call, 2), &output_count, function);
    if (!outputs) return;
    bool distinct = name_count == aqua_array_length(rt, values);
    for (uint32_t i = 0; distinct && i < name_count; i++)
        for (uint32_t j = i + 1; j < name_count; j++) distinct &= strcmp(names[i], names[j]) != 0;
    if (!distinct) { aqua_fail(call, PREFIX ".%s：輸入名稱與張量數量必須相同，且名稱不得重複。", function); return; }
    const onnx_tensor **tensors = aqua_scratch(call, (name_count + 1) * sizeof *tensors);
    for (uint32_t i = 0; i < name_count; i++) {
        tensor_object *t = aqua_object_data(rt, aqua_array_get(rt, values, i), tensor_class);
        if (!t) { aqua_fail(call, PREFIX ".%s：必須使用同一執行環境建立的張量。", function); return; }
        tensors[i] = &t->tensor;
    }
    onnx_error error = {0};
    uint32_t input_count, model_outputs;
    const onnx_value_info *inputs = backend_inputs(s->session, &input_count);
    const onnx_value_info *output_infos = backend_outputs(s->session, &model_outputs);
    if (!onnx_validate_run(inputs, input_count, output_infos, model_outputs, names, tensors, name_count, outputs, output_count, &error)) {
        fail(call, function, &error);
        return;
    }
    onnx_tensor *results = aqua_scratch(call, (output_count + 1) * sizeof *results);
    if (!backend_run(s->session, names, tensors, name_count, outputs, results, output_count, &error)) {
        fail(call, function, &error);
        return;
    }
    aqua_value array = aqua_array_new(rt, output_count);
    for (uint32_t i = 0; i < output_count; i++) aqua_array_set(rt, array, i, tensor_value(rt, results[i], outputs[i]));
    aqua_return(call, array);
}

/* ---- Module functions ----------------------------------------------------------------------------------------- */
static void is_supported(aqua_call *call) {
    onnx_selection selection;
    onnx_error error = {0};
    bool supported = false;
    if (!onnx_selection_of(aqua_call_runtime(call), aqua_arg(call, 0), &selection, &error) ||
        !onnx_selection_supported(&selection, &supported, &error)) {
        fail(call, "是否支援", &error);
        return;
    }
    aqua_return_bool(call, supported);
}

static void get_providers(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    uint32_t count;
    const onnx_device *devices = backend_devices(&count);
    aqua_value array = aqua_array_new(rt, count);
    for (uint32_t i = 0; i < count; i++) {
        onnx_device *copy = malloc(sizeof *copy);
        *copy = devices[i];
        aqua_array_set(rt, array, i, aqua_object_new(rt, device_class, copy));
    }
    aqua_return(call, array);
}

static void create_tensor(aqua_call *call) {
    onnx_tensor tensor;
    onnx_error error = {0};
    aqua_runtime *rt = aqua_call_runtime(call);
    if (!onnx_tensor_from_values(rt, aqua_arg(call, 0), aqua_arg(call, 1), aqua_arg(call, 2), &tensor, &error)) {
        fail(call, "建立張量", &error);
        return;
    }
    aqua_return(call, tensor_value(rt, tensor, ""));
}

static void create_session(aqua_call *call) {
    static const char *const function = "建立工作階段";
    aqua_runtime *rt = aqua_call_runtime(call);
    onnx_state *state = aqua_state(call);
    onnx_selection selection = onnx_auto;
    onnx_error error = {0};
    if (aqua_has_arg(call, 1) && !onnx_selection_of(rt, aqua_arg(call, 1), &selection, &error)) { fail(call, function, &error); return; }
    aqua_value model = aqua_arg(call, 0);
    if (model.type != AQUA_STRING && model.type != AQUA_ARRAY) { aqua_fail(call, PREFIX ".%s：預期字串參數。", function); return; }
    if (state->session_count >= ONNX_MAX_SESSIONS) { aqua_fail(call, PREFIX ".%s：最多只能同時建立八個推論工作階段。", function); return; }
    size_t length = 0;
    bool owned = false;
    uint8_t *bytes = onnx_model_bytes(call, model, &length, &owned, &error);
    if (!bytes) { fail(call, function, &error); return; }
    onnx_session *session = onnx_session_open(bytes, length, &selection, &error);
    if (owned) free(bytes);
    if (!session) { fail(call, function, &error); return; }
    session_object *s = calloc(1, sizeof *s);
    s->owner = state;
    s->session = session;
    s->backend = backend_plan(session)->devices[0]->provider;
    s->kind = backend_plan(session)->devices[0]->kind;
    s->chain_count = backend_chain(session, s->chain);
    s->next = state->sessions;
    state->sessions = s;
    state->session_count++;
    aqua_return(call, aqua_object_new(rt, session_class, s));
}

void onnx_module(aqua_library *library) {
    aqua_class *module = aqua_define_module(library, "ONNX|推論引擎|ONNXRuntime");
    char names[96];
    for (int p = 0; p < ONNX_PROVIDER_COUNT; p++) {
        snprintf(names, sizeof names, "%s|%s", onnx_provider_id((onnx_provider)p), onnx_provider_chinese((onnx_provider)p));
        aqua_define_string(module, names, onnx_provider_id((onnx_provider)p));
    }
    aqua_define_string(module, "Auto|自動", "Auto");
    aqua_define_string(module, "GPU|圖形處理器", "GPU");
    aqua_define_string(module, "NPU|神經處理器", "NPU");
    aqua_define_string(module, "Float32|浮點32", "float32");
    aqua_define_string(module, "Float64|浮點64", "float64");
    aqua_define_string(module, "Int32|整數32", "int32");
    aqua_define_string(module, "UInt8|無號整數8", "uint8");
    aqua_define_string(module, "Bool|布林", "bool");
    aqua_define_function(module, "IsSupported|是否支援", is_supported, "v");
    aqua_define_function(module, "GetProviders|取得後端清單", get_providers, "");
    aqua_define_function(module, "CreateSession|建立工作階段", create_session, "v|v");
    aqua_define_function(module, "CreateTensor|建立張量", create_tensor, "vvv");

    session_class = aqua_define_class(library, "ONNX.Session", session_finalize);
    aqua_define_property(session_class, "Backend|後端", session_backend);
    aqua_define_property(session_class, "Device|裝置", session_device);
    aqua_define_property(session_class, "Providers|後端清單", session_providers);
    aqua_define_function(session_class, "GetInputs|取得輸入資訊", session_inputs, "");
    aqua_define_function(session_class, "GetOutputs|取得輸出資訊", session_outputs, "");
    aqua_define_function(session_class, "Run|推論", session_run, "vvv");
    aqua_define_function(session_class, "Dispose|釋放", session_dispose, "");

    tensor_class = aqua_define_class(library, "ONNX.Tensor", tensor_finalize);
    aqua_define_property(tensor_class, "Name|名稱", tensor_name);
    aqua_define_function(tensor_class, "GetData|取得資料", tensor_data, "");
    aqua_define_function(tensor_class, "GetShape|取得形狀", tensor_shape, "");
    aqua_define_function(tensor_class, "GetType|取得型別", tensor_type, "");

    device_class = aqua_define_class(library, "ONNX.Device", device_finalize);
    aqua_define_property(device_class, "Provider|後端", device_provider);
    aqua_define_property(device_class, "Device|裝置", device_kind);
    aqua_define_property(device_class, "Name|名稱", device_name);
    aqua_define_property(device_class, "Available|可用", device_available);
    aqua_define_property(device_class, "Automatic|自動選用", device_automatic);
    aqua_define_property(device_class, "Reason|原因", device_reason);

    info_class = aqua_define_class(library, "ONNX.ValueInfo", info_finalize);
    aqua_define_property(info_class, "Name|名稱", info_name);
    aqua_define_property(info_class, "Type|型別", info_type);
    aqua_define_property(info_class, "Shape|形狀", info_shape);
}
