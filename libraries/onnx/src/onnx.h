/* onnx.h — declarations shared by the onnx library's sources; nothing here is visible to other libraries.
 *
 * The library keeps the layering of the old ONNX contract (version 2):
 *   policy.c       the provider catalog (providers.def), selection parsing, ranking and fallback; platform independent
 *   tensor.c       tensors, their limits and conversions, and the validation of a run
 *   backend_ort.c  the engine: ONNX Runtime's C API and each target's ranked execution devices (devices.def)
 *   onnx.c         the ONNX|推論引擎|ONNXRuntime module; mediapipe.c and pose.c the MediaPipe|姿態追蹤 module
 * Every target runs the same policy and binding; only backend_ort.c's device table and src/platform differ.
 *
 * 推論引擎函式庫的內部宣告：後端目錄、選擇策略、張量與推論引擎介面。 */
#ifndef ONNX_INTERNAL_H
#define ONNX_INTERNAL_H
#include <aqua/aqua.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* ---- Limits (the old contract's, except 2 GiB models instead of 256 MiB) ------------------------------------- */
#define ONNX_MAX_ELEMENTS (16u * 1024u * 1024u)    /* elements of one tensor */
#define ONNX_MAX_MODEL_BYTES 0x7FFFFFFFu           /* protobuf's 2 GiB limit */
#define ONNX_MAX_SESSIONS 8                        /* live ONNX sessions of one runtime */
#define ONNX_MAX_RANK 16
#define ONNX_MAX_WEIGHT_FILES 64                   /* external weight files of one model */
#define ONNX_MAX_DEVICES 16                        /* rows of devices.def */

/* ---- Failures ------------------------------------------------------------------------------------------------- */
typedef enum onnx_status { ONNX_OK, ONNX_INVALID_ARGUMENT, ONNX_UNSUPPORTED, ONNX_CLOSED, ONNX_ENGINE } onnx_status;
typedef struct onnx_error { onnx_status status; char message[1536]; } onnx_error;
/* Records a failure (printf format, UTF-8) and returns false. */
bool onnx_fail(onnx_error *error, onnx_status status, const char *format, ...);
/* The first line of an engine message without ONNX Runtime's status preamble, at most 400 characters. */
void onnx_describe(const char *message, char *out, size_t size);

/* ---- Providers and devices ------------------------------------------------------------------------------------ */
typedef enum onnx_provider {
#define PROVIDER(id, chinese) ONNX_##id,
#include "providers.def"
#undef PROVIDER
    ONNX_PROVIDER_COUNT
} onnx_provider;
typedef enum onnx_kind { ONNX_KIND_CPU, ONNX_KIND_GPU, ONNX_KIND_NPU } onnx_kind;

const char *onnx_provider_id(onnx_provider provider);      /* "CPU", "CUDA", ... (the language-visible value) */
const char *onnx_provider_chinese(onnx_provider provider); /* "中央處理器", ... */
const char *onnx_kind_name(onnx_kind kind);                /* "CPU", "GPU", "NPU" */
bool onnx_provider_parse(const char *text, onnx_provider *provider);

/* One execution provider on one hardware class of this machine (ORT's OrtEpDevice). Unavailable devices stay listed
   with a reason; `automatic` false marks devices used only on explicit request. */
typedef struct onnx_device {
    const char *id;
    onnx_provider provider;
    onnx_kind kind;
    bool available, automatic;
    char name[192];
    char reason[640];   /* empty when there is none */
} onnx_device;

/* What a script asked for: a device preference (falls back) or an explicit, ordered provider list (strict). */
typedef enum onnx_preference { ONNX_EXPLICIT, ONNX_PREFER_AUTO, ONNX_PREFER_GPU, ONNX_PREFER_NPU } onnx_preference;
typedef struct onnx_selection {
    onnx_preference preference;
    uint32_t count;
    onnx_provider providers[ONNX_PROVIDER_COUNT];
} onnx_selection;
extern const onnx_selection onnx_auto;
/* Parses one provider or preference name, or an ordered list of provider names (bilingual, ids case-insensitive). */
bool onnx_selection_parse(const char *const *texts, uint32_t count, onnx_selection *selection, onnx_error *error);

/* A concrete, ordered device chain for one session; ONNX Runtime appends its CPU provider implicitly. */
typedef struct onnx_plan { uint32_t count; const onnx_device *devices[ONNX_PROVIDER_COUNT]; } onnx_plan;
/* Ranked candidate plans for a selection (the old OnnxPlanner). */
bool onnx_plan_sessions(const onnx_device *devices, uint32_t device_count, const onnx_selection *selection,
                        onnx_plan *plans, uint32_t *plan_count, onnx_error *error);

/* ---- Tensors -------------------------------------------------------------------------------------------------- */
typedef enum onnx_type { ONNX_FLOAT32, ONNX_FLOAT64, ONNX_INT32, ONNX_UINT8, ONNX_BOOL, ONNX_TYPE_UNSUPPORTED } onnx_type;
const char *onnx_type_name(onnx_type type); /* "float32", "float64", "int32", "uint8", "bool", "unsupported" */
bool onnx_type_parse(const char *name, onnx_type *type);
size_t onnx_type_size(onnx_type type);

/* An owned CPU snapshot (or, with `borrowed`, a view the caller keeps alive). bool elements are one byte 0/1. */
typedef struct onnx_tensor {
    onnx_type type;
    uint32_t rank;
    int64_t shape[ONNX_MAX_RANK];
    size_t count;
    void *data;
    bool borrowed;
} onnx_tensor;
/* Validates the shape and allocates zeroed data for it (or borrows `data` when given). */
bool onnx_tensor_init(onnx_tensor *tensor, onnx_type type, const int64_t *shape, uint32_t rank, void *data, onnx_error *error);
void onnx_tensor_free(onnx_tensor *tensor);
/* A tensor from script values (CreateTensor), and its elements as a new script array (GetData). */
bool onnx_tensor_from_values(aqua_runtime *rt, aqua_value type, aqua_value shape, aqua_value data, onnx_tensor *tensor, onnx_error *error);
aqua_value onnx_tensor_values(aqua_runtime *rt, const onnx_tensor *tensor);

/* Model input or output metadata. Negative dimensions are dynamic; non-tensor values have type "unsupported". */
typedef struct onnx_value_info { char *name; onnx_type type; uint32_t rank; int64_t *shape; } onnx_value_info;
/* Every model input exactly once with matching type, rank and static dimensions; known, unique, nonempty outputs. */
bool onnx_validate_run(const onnx_value_info *inputs, uint32_t input_count, const onnx_value_info *outputs,
                       uint32_t output_count, const char *const *names, const onnx_tensor *const *tensors, uint32_t count,
                       const char *const *requested, uint32_t requested_count, onnx_error *error);

/* ---- Sessions ------------------------------------------------------------------------------------------------- */
/* A model to open: its .onnx bytes and the weight files of a model saved with external data (a small .onnx plus
   separate weight files, how models over protobuf's 2 GiB limit are stored). A model read from a file names its folder,
   where the engine reads the weight files itself; a packaged model brings its packaged weight files in memory. */
typedef struct onnx_weight_file { char *name; const uint8_t *bytes; size_t length; } onnx_weight_file;
typedef struct onnx_model {
    const uint8_t *bytes;
    size_t length;
    bool owned;               /* bytes are freed with the model */
    char *folder;             /* weight files' folder (UTF-8), or NULL */
    onnx_weight_file *files;  /* packaged weight files, borrowed from the package */
    uint32_t file_count;
} onnx_model;
void onnx_model_free(onnx_model *model);
typedef struct onnx_session onnx_session;
/* Opens a session for a selection: explicit lists are strict, preferences try ranked candidates and move on only
   when a provider fails to initialize on this machine (the old OnnxSessionFactory). */
onnx_session *onnx_session_open(const onnx_model *model, const onnx_selection *selection, onnx_error *error);
/* Whether the selection yields a plan on this machine now; false only for "unsupported" failures. */
bool onnx_selection_supported(const onnx_selection *selection, bool *supported, onnx_error *error);

/* ---- The engine (backend_ort.c) ------------------------------------------------------------------------------- */
/* Every device of this target, best first, probed once per process. */
const onnx_device *backend_devices(uint32_t *count);
/* Opens exactly this plan; never substitutes another provider. */
onnx_session *backend_open(const onnx_model *model, const onnx_plan *plan, onnx_error *error);
void backend_close(onnx_session *session);
const onnx_plan *backend_plan(const onnx_session *session);
/* The provider chain handed to the engine: the plan, companions (CUDA behind TensorRT) and the CPU fallback. */
uint32_t backend_chain(const onnx_session *session, onnx_provider chain[ONNX_PROVIDER_COUNT]);
const onnx_value_info *backend_inputs(const onnx_session *session, uint32_t *count);
const onnx_value_info *backend_outputs(const onnx_session *session, uint32_t *count);
/* Runs validated inputs; `outputs` receive owned tensors in the requested order. */
bool backend_run(onnx_session *session, const char *const *input_names, const onnx_tensor *const *inputs,
                 uint32_t input_count, const char *const *output_names, onnx_tensor *outputs, uint32_t output_count,
                 onnx_error *error);

/* ---- Per-runtime state, shared by both modules (library.c) ----------------------------------------------------- */
typedef struct session_object session_object; /* onnx.c */
typedef struct pose_tracker pose_tracker;     /* mediapipe.c */
typedef struct onnx_state {
    session_object *sessions;  /* live ONNX sessions, at most ONNX_MAX_SESSIONS */
    uint32_t session_count;
    pose_tracker *trackers;    /* live pose trackers, at most four */
    uint32_t tracker_count;
} onnx_state;
/* Release everything still open when the runtime ends. */
void onnx_close_sessions(onnx_state *state);
void mediapipe_close_trackers(onnx_state *state);

/* ---- Shared by the two modules (onnx.c) ------------------------------------------------------------------------- */
/* A provider/preference string or an ordered array of provider strings. */
bool onnx_selection_of(aqua_runtime *rt, aqua_value value, onnx_selection *selection, onnx_error *error);
/* The model for a path (a packaged asset, or a file on native targets) or a byte array; release it with
   onnx_model_free. */
bool onnx_model_read(aqua_call *call, aqua_value value, onnx_model *model, onnx_error *error);

void onnx_module(aqua_library *library);      /* onnx.c */
void mediapipe_module(aqua_library *library); /* mediapipe.c */
#endif
