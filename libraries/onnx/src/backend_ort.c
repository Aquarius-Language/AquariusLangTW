/* The engine: ONNX Runtime's C API. It reports this target's ranked execution devices (devices.def), probed once per
 * process, and opens sessions for exact plans; ranking, fallback and validation are policy.c's and tensor.c's.
 *   - Native targets load the official prebuilt release at first use (src/platform). Its build decides which built-in
 *     providers exist (the CUDA 13 build adds CUDA and TensorRT; the Android package NNAPI, XNNPACK and WebGPU). The
 *     web links ONNX Runtime compiled from source into the side module (cmake/onnxruntime-web.cmake): CPU only.
 *   - Plugin providers (WebGPU through Dawn, OpenVINO) are ABI-stable libraries found beside the program and
 *     registered with RegisterExecutionProviderLibrary; their devices come from ONNX Runtime's device discovery.
 *   - Accelerators are verified by building and running a one-node convolution (the probe model), so a missing
 *     driver or cuDNN is reported before an application model is opened.
 *
 * 推論引擎：ONNX Runtime C 介面、各平台裝置排序與探測、工作階段與推論。 */
#include "onnx.h"
#include "platform.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef enum device_use { USE_AUTOMATIC, USE_EXPLICIT, USE_DEVICE } device_use;
typedef enum strategy {
    STRATEGY_CPU, STRATEGY_CUDA, STRATEGY_TENSORRT, STRATEGY_WEBGPU_PLUGIN, STRATEGY_OPENVINO, STRATEGY_WEBGPU,
    STRATEGY_NNAPI, STRATEGY_XNNPACK, STRATEGY_UNAVAILABLE
} strategy;
typedef struct device_row { const char *id; onnx_provider provider; onnx_kind kind; device_use use; strategy strategy; } device_row;

static const device_row rows[] = {
#define DEVICE(id, provider, kind, use, strategy) {id, ONNX_##provider, ONNX_KIND_##kind, USE_##use, STRATEGY_##strategy},
#include "devices.def"
#undef DEVICE
};
enum { ROW_COUNT = sizeof rows / sizeof *rows };

/* Y = Conv(X, W = 2) over float32[1,1,1,1]: ONNX IR 8, opset 13, 129 bytes (the old OnnxProbeModel). A convolution
   reaches the vendor DNN library on every GPU/NPU provider. */
static const uint8_t probe_model[] = {
    8,8,18,14,97,113,117,97,114,105,117,115,45,112,114,111,98,101,58,103,10,15,10,1,88,10,1,87,18,1,89,34,4,67,111,110,
    118,18,5,112,114,111,98,101,42,19,8,1,8,1,8,1,8,1,16,1,66,1,87,74,4,0,0,0,64,90,27,10,1,88,18,22,10,20,8,1,18,16,10,
    2,8,1,10,2,8,1,10,2,8,1,10,2,8,1,98,27,10,1,89,18,22,10,20,8,1,18,16,10,2,8,1,10,2,8,1,10,2,8,1,10,2,8,1,66,4,10,0,
    16,13,
};

#define CUDA_OPT_IN "此執行環境未包含 CUDA 提供者（預設不含）。請在 aqua.toml 加入 [onnx] cuda = true，或以 --onnx-cuda true 建置執行檔；開發時以 -DAQUA_ONNX_CUDA=ON 建置執行環境。"

static struct {
    bool attempted, probed;
    char failure[640];
    const OrtApi *api;
    OrtEnv *env;
    OrtMemoryInfo *memory;
    OrtAllocator *allocator;
    char **built;            /* providers compiled into the loaded ONNX Runtime */
    int built_count;
    onnx_device devices[ROW_COUNT];
    uint32_t device_count;
} engine;
#define ORT (engine.api)

struct onnx_session {
    OrtSession *session;
    onnx_plan plan;
    onnx_provider chain[ONNX_PROVIDER_COUNT];
    uint32_t chain_count;
    onnx_value_info *inputs, *outputs;
    uint32_t input_count, output_count;
};

/* True for success; otherwise the status' described message (and its code) and the status released. */
static bool succeeded(OrtStatus *status, char *message, size_t size, OrtErrorCode *code) {
    if (!status) return true;
    if (code) *code = ORT->GetErrorCode(status);
    if (message) onnx_describe(ORT->GetErrorMessage(status), message, size);
    ORT->ReleaseStatus(status);
    return false;
}

static bool built(const char *name) {
    for (int i = 0; i < engine.built_count; i++) if (strcmp(engine.built[i], name) == 0) return true;
    return false;
}

static bool load_engine(void) {
    if (engine.attempted) return ORT != NULL;
    engine.attempted = true;
    const OrtApiBase *base = onnx_platform_runtime(engine.failure, sizeof engine.failure);
    if (!base) return false;
    const OrtApi *api = base->GetApi(ORT_API_VERSION);
    if (!api) {
        snprintf(engine.failure, sizeof engine.failure, "ONNX Runtime %s 版本過舊：需要 %s 或更新版本。", base->GetVersionString(), ONNX_RUNTIME_VERSION);
        return false;
    }
    engine.api = api;
    char message[512];
    if (!succeeded(ORT->CreateEnv(ORT_LOGGING_LEVEL_WARNING, "aquarius", &engine.env), message, sizeof message, NULL) ||
        !succeeded(ORT->CreateCpuMemoryInfo(OrtArenaAllocator, OrtMemTypeDefault, &engine.memory), message, sizeof message, NULL) ||
        !succeeded(ORT->GetAllocatorWithDefaultOptions(&engine.allocator), message, sizeof message, NULL) ||
        !succeeded(ORT->GetAvailableProviders(&engine.built, &engine.built_count), message, sizeof message, NULL)) {
        snprintf(engine.failure, sizeof engine.failure, "%s", message);
        engine.api = NULL;
        return false;
    }
    return true;
}

/* ---- Plugin execution-provider libraries (desktop) ------------------------------------------------------------ */
#if defined(AQUA_DESKTOP)
typedef struct plugin_device { const OrtEpDevice *device; onnx_kind kind; bool software, discrete; char name[192]; } plugin_device;
typedef struct plugin {
    onnx_provider provider;
    const char *registration, *file, *match;
    bool attempted;
    char failure[640];
    plugin_device devices[8];
    uint32_t count;
} plugin;
static plugin plugins[] = {
    {ONNX_WebGPU, "aquarius_webgpu", ONNX_LIBRARY_FILE("onnxruntime_providers_webgpu"), "WebGpu", false, "", {{0}}, 0},
    {ONNX_OpenVINO, "aquarius_openvino", ONNX_LIBRARY_FILE("onnxruntime_providers_openvino_plugin"), "OpenVINO", false, "", {{0}}, 0},
};

static bool contains_ignoring_case(const char *text, const char *part) {
    size_t n = strlen(part);
    for (; *text; text++) {
        size_t i = 0;
        while (i < n && text[i] && (text[i] | 32) == (part[i] | 32)) i++;
        if (i == n) return true;
    }
    return false;
}

/* The program's folders, then each folder of AQUARIUS_ONNX_PROVIDER_PATH; each directly or in onnx-providers/. */
static bool locate(const char *file, char *path, size_t size) {
    char *directories[2] = {onnx_platform_binary_directory(), onnx_platform_executable_directory()};
    bool found = false;
    for (int i = 0; i < 2 && !found; i++) {
        if (!directories[i]) continue;
        snprintf(path, size, "%s/%s", directories[i], file);
        found = onnx_platform_file_exists(path);
        if (!found) {
            snprintf(path, size, "%s/onnx-providers/%s", directories[i], file);
            found = onnx_platform_file_exists(path);
        }
    }
    free(directories[0]); free(directories[1]);
    const char *extra = getenv("AQUARIUS_ONNX_PROVIDER_PATH");
    while (!found && extra && *extra) {
        const char *end = strchr(extra, ONNX_PATH_SEPARATOR);
        size_t length = end ? (size_t)(end - extra) : strlen(extra);
        while (length && *extra == ' ') { extra++; length--; }
        while (length && extra[length - 1] == ' ') length--;
        if (length) {
            snprintf(path, size, "%.*s/%s", (int)length, extra, file);
            found = onnx_platform_file_exists(path);
            if (!found) {
                snprintf(path, size, "%.*s/onnx-providers/%s", (int)length, extra, file);
                found = onnx_platform_file_exists(path);
            }
        }
        extra = end ? end + 1 : NULL;
    }
    return found;
}

/* Registers the library once per process and keeps the devices its factories report. */
static void ensure_plugin(plugin *p) {
    if (p->attempted) return;
    p->attempted = true;
    char path[4096];
    if (!locate(p->file, path, sizeof path)) {
        snprintf(p->failure, sizeof p->failure, "未找到 %s 外掛程式庫；將其放在程式旁或 AQUARIUS_ONNX_PROVIDER_PATH 指定的資料夾。", onnx_provider_id(p->provider));
        return;
    }
    ORTCHAR_T *native = onnx_platform_path(path);
    bool registered = succeeded(ORT->RegisterExecutionProviderLibrary(engine.env, p->registration, native), p->failure, sizeof p->failure, NULL);
    free(native);
    if (!registered) return;
    const OrtEpDevice *const *list = NULL;
    size_t count = 0;
    if (!succeeded(ORT->GetEpDevices(engine.env, &list, &count), p->failure, sizeof p->failure, NULL)) return;
    for (size_t i = 0; i < count && p->count < sizeof p->devices / sizeof *p->devices; i++) {
        const char *ep = ORT->EpDevice_EpName(list[i]);
        if (!ep || !contains_ignoring_case(ep, p->match)) continue;
        const OrtHardwareDevice *hardware = ORT->EpDevice_Device(list[i]);
        OrtHardwareDeviceType type = ORT->HardwareDevice_Type(hardware);
        plugin_device *d = &p->devices[p->count++];
        d->device = list[i];
        d->kind = type == OrtHardwareDeviceType_GPU ? ONNX_KIND_GPU : type == OrtHardwareDeviceType_NPU ? ONNX_KIND_NPU : ONNX_KIND_CPU;
        const OrtKeyValuePairs *metadata = ORT->HardwareDevice_Metadata(hardware);
        const char *description = metadata ? ORT->GetKeyValue(metadata, "Description") : NULL;
        char fallback[96];
        if (!description || !description[0]) {
            const char *vendor = ORT->HardwareDevice_Vendor(hardware);
            snprintf(fallback, sizeof fallback, "%s 0x%04x", vendor ? vendor : "", (unsigned)ORT->HardwareDevice_DeviceId(hardware));
            description = fallback;
        }
        /* Mesa llvmpipe/lavapipe (0x10005), Microsoft Basic Render/WARP (0x1414) and SwiftShader are CPU rasterizers. */
        uint32_t vendor_id = ORT->HardwareDevice_VendorId(hardware);
        d->software = d->kind == ONNX_KIND_GPU && (vendor_id == 0x10005 || vendor_id == 0x1414 || contains_ignoring_case(description, "llvmpipe") ||
            contains_ignoring_case(description, "SwiftShader") || contains_ignoring_case(description, "Basic Render"));
        const char *discrete = metadata ? ORT->GetKeyValue(metadata, "Discrete") : NULL;
        d->discrete = discrete && strcmp(discrete, "1") == 0;
        snprintf(d->name, sizeof d->name, "%s (%s)", description, ep);
        const char *diagnostics = getenv("AQUARIUS_ONNX_DIAGNOSTICS");
        if (diagnostics && strcmp(diagnostics, "1") == 0)
            fprintf(stderr, "[onnx] %s device: %s, vendor 0x%x%s%s\n", ep, d->name, (unsigned)vendor_id, d->discrete ? ", discrete" : "", d->software ? ", software" : "");
    }
    if (p->count == 0) snprintf(p->failure, sizeof p->failure, "%s 外掛已載入，但沒有回報可用裝置（請確認驅動程式）。", onnx_provider_id(p->provider));
}

static plugin *plugin_of(strategy s) { return s == STRATEGY_WEBGPU_PLUGIN ? &plugins[0] : &plugins[1]; }

/* A hardware device of the plugin for the class: a discrete GPU first (the browser's "high-performance" adapter), then
   any real hardware, and a software rasterizer last. */
static const plugin_device *plugin_device_of(plugin *p, onnx_kind kind) {
    ensure_plugin(p);
    const plugin_device *hardware = NULL, *software = NULL;
    for (uint32_t i = 0; i < p->count; i++) {
        const plugin_device *d = &p->devices[i];
        if (d->kind != kind) continue;
        if (!d->software && d->discrete) return d;
        if (!d->software && !hardware) hardware = d;
        if (d->software && !software) software = d;
    }
    return hardware ? hardware : software;
}
#endif

/* ---- Provider strategies -------------------------------------------------------------------------------------- */
static uint32_t mobile_threads(void) {
    uint32_t half = onnx_platform_processor_count() / 2;
    return half < 1 ? 1 : half > 4 ? 4 : half;
}

static const char *cuda_device(void) {
    const char *id = getenv("AQUARIUS_ONNX_CUDA_DEVICE");
    if (!id || !id[0]) return "0";
    for (const char *p = id; *p; p++) if (*p < '0' || *p > '9') return "0";
    return id;
}

static void display_name(const device_row *row, char *name, size_t size) {
    switch (row->strategy) {
    case STRATEGY_CPU:
#if defined(AQUA_OS_ANDROID)
        snprintf(name, size, "ONNX Runtime CPU (%u threads)", mobile_threads());
#elif defined(AQUA_OS_WEB)
        snprintf(name, size, "WebAssembly SIMD（1 執行緒）");
#else
        snprintf(name, size, "ONNX Runtime CPU (%s, %u threads)",
#if defined(__aarch64__)
                 "Arm64",
#else
                 "X64",
#endif
                 onnx_platform_processor_count());
#endif
        break;
    case STRATEGY_CUDA: snprintf(name, size, "NVIDIA CUDA GPU %s", cuda_device()); break;
    case STRATEGY_TENSORRT: snprintf(name, size, "NVIDIA TensorRT GPU %s", cuda_device()); break;
    case STRATEGY_WEBGPU: snprintf(name, size, "Vulkan GPU (WebGPU/Dawn)"); break;
    case STRATEGY_NNAPI: snprintf(name, size, "Android NNAPI accelerators"); break;
    case STRATEGY_XNNPACK: snprintf(name, size, "XNNPACK (%u threads)", mobile_threads()); break;
    default: snprintf(name, size, "%s %s", onnx_provider_id(row->provider), onnx_kind_name(row->kind)); break;
    }
}

/* Session-level tuning: accelerated sessions keep CPU fallback nodes on one thread, leaving cores for capture and
   drawing; XNNPACK owns its own pool; Android's CPU provider uses half the cores up to four; the web has no threads,
   so ONNX Runtime must never start a pool there. */
static uint32_t intra_op_threads(const device_row *row) {
#if defined(AQUA_OS_WEB)
    (void)row;
    return 1;
#else
    if (row->strategy == STRATEGY_XNNPACK) return 1;
#if defined(AQUA_OS_ANDROID)
    if (row->strategy == STRATEGY_CPU) return mobile_threads();
#endif
    return row->kind != ONNX_KIND_CPU ? 1 : 0; /* 0: ONNX Runtime's default */
#endif
}

static bool configure(OrtSessionOptions *options, const device_row *row, char *message, size_t size) {
    uint32_t threads = intra_op_threads(row);
    return !threads || succeeded(ORT->SetIntraOpNumThreads(options, (int)threads), message, size, NULL);
}

static bool append_cuda(OrtSessionOptions *options, char *message, size_t size) {
    onnx_platform_prepare_cuda();
    OrtCUDAProviderOptionsV2 *cuda = NULL;
    if (!succeeded(ORT->CreateCUDAProviderOptions(&cuda), message, size, NULL)) return false;
    /* Grow the arena by exact requests (no doubling next to the application's own GPU work); exhaustive cuDNN
       convolution search with the maximum workspace. */
    const char *keys[] = {"device_id", "arena_extend_strategy", "cudnn_conv_algo_search", "cudnn_conv_use_max_workspace", "do_copy_in_default_stream"};
    const char *values[] = {cuda_device(), "kSameAsRequested", "EXHAUSTIVE", "1", "1"};
    bool ok = succeeded(ORT->UpdateCUDAProviderOptions(cuda, keys, values, 5), message, size, NULL) &&
              succeeded(ORT->SessionOptionsAppendExecutionProvider_CUDA_V2(options, cuda), message, size, NULL);
    ORT->ReleaseCUDAProviderOptions(cuda);
    return ok;
}

/* TensorRT with FP16 and persistent engine and timing caches; CUDA follows as its companion. */
static bool append_tensorrt(OrtSessionOptions *options, char *message, size_t size) {
    char cache[4096];
    const char *custom = getenv("AQUARIUS_ONNX_CACHE");
    char *data = custom && custom[0] ? NULL : onnx_platform_data_directory();
    if (custom && custom[0]) snprintf(cache, sizeof cache, "%s/tensorrt", custom);
    else snprintf(cache, sizeof cache, "%s/AquariusLang/onnx/tensorrt", data ? data : ".");
    free(data);
    onnx_platform_make_directories(cache);
    OrtTensorRTProviderOptionsV2 *trt = NULL;
    if (!succeeded(ORT->CreateTensorRTProviderOptions(&trt), message, size, NULL)) return false;
    const char *keys[] = {"device_id", "trt_fp16_enable", "trt_engine_cache_enable", "trt_engine_cache_path", "trt_timing_cache_enable", "trt_timing_cache_path"};
    const char *values[] = {cuda_device(), "1", "1", cache, "1", cache};
    bool ok = succeeded(ORT->UpdateTensorRTProviderOptions(trt, keys, values, 6), message, size, NULL) &&
              succeeded(ORT->SessionOptionsAppendExecutionProvider_TensorRT_V2(options, trt), message, size, NULL);
    ORT->ReleaseTensorRTProviderOptions(trt);
    return ok && append_cuda(options, message, size);
}

static bool append(OrtSessionOptions *options, const device_row *row, char *message, size_t size) {
    switch (row->strategy) {
    case STRATEGY_CPU: return true; /* always the implicit last provider */
    case STRATEGY_CUDA: return append_cuda(options, message, size);
    case STRATEGY_TENSORRT: return append_tensorrt(options, message, size);
#if defined(AQUA_DESKTOP)
    case STRATEGY_WEBGPU_PLUGIN:
    case STRATEGY_OPENVINO: {
        const plugin_device *device = plugin_device_of(plugin_of(row->strategy), row->kind);
        if (!device) {
            snprintf(message, size, "找不到 %s 的 %s 裝置。", onnx_provider_id(row->provider), onnx_kind_name(row->kind));
            return false;
        }
        return succeeded(ORT->SessionOptionsAppendExecutionProvider_V2(options, engine.env, &device->device, 1, NULL, NULL, 0), message, size, NULL);
    }
#endif
    case STRATEGY_WEBGPU: return succeeded(ORT->SessionOptionsAppendExecutionProvider(options, "WebGPU", NULL, NULL, 0), message, size, NULL);
    case STRATEGY_XNNPACK: {
        char threads[16];
        snprintf(threads, sizeof threads, "%u", mobile_threads());
        const char *keys[] = {"intra_op_num_threads"}, *values[] = {threads};
        return succeeded(ORT->SessionOptionsAppendExecutionProvider(options, "XNNPACK", keys, values, 1), message, size, NULL);
    }
    case STRATEGY_NNAPI: {
        /* Accelerators only (NNAPI_FLAG_CPU_DISABLED): nodes NNAPI would run on its reference CPU go to ORT's faster
           CPU kernels. The NNAPI factory is an exported function of the Android package, not part of OrtApi. */
        OrtStatus *(ORT_API_CALL *nnapi)(OrtSessionOptions *, uint32_t) =
            (OrtStatus *(ORT_API_CALL *)(OrtSessionOptions *, uint32_t))onnx_platform_symbol("OrtSessionOptionsAppendExecutionProvider_Nnapi");
        if (!nnapi) { snprintf(message, size, "此 ONNX Runtime 建置不含 NNAPI。"); return false; }
        return succeeded(nnapi(options, 0x004), message, size, NULL);
    }
    default:
        snprintf(message, size, "此平台不支援 %s 推論後端。", onnx_provider_id(row->provider));
        return false;
    }
}

/* Full graph optimization, sequential execution and no idle spinning (camera and UI work keep their CPU time). */
static OrtSessionOptions *create_options(char *message, size_t size) {
    OrtSessionOptions *options = NULL;
    if (!succeeded(ORT->CreateSessionOptions(&options), message, size, NULL)) return NULL;
    if (succeeded(ORT->SetSessionGraphOptimizationLevel(options, ORT_ENABLE_ALL), message, size, NULL) &&
        succeeded(ORT->SetSessionExecutionMode(options, ORT_SEQUENTIAL), message, size, NULL) &&
        succeeded(ORT->AddSessionConfigEntry(options, "session.intra_op.allow_spinning", "0"), message, size, NULL) &&
        succeeded(ORT->AddSessionConfigEntry(options, "session.inter_op.allow_spinning", "0"), message, size, NULL)) return options;
    ORT->ReleaseSessionOptions(options);
    return NULL;
}

/* Builds and runs the probe model through one provider and checks the numeric result. */
static bool run_probe(const device_row *row, char *message, size_t size) {
    OrtSessionOptions *options = create_options(message, size);
    if (!options) return false;
    OrtSession *session = NULL;
    OrtValue *input = NULL, *output = NULL;
    float x = 1.5f;
    int64_t shape[4] = {1, 1, 1, 1};
    const char *in = "X", *out = "Y";
    bool ok = configure(options, row, message, size) && append(options, row, message, size) &&
        succeeded(ORT->CreateSessionFromArray(engine.env, probe_model, sizeof probe_model, options, &session), message, size, NULL) &&
        succeeded(ORT->CreateTensorWithDataAsOrtValue(engine.memory, &x, sizeof x, shape, 4, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &input), message, size, NULL) &&
        succeeded(ORT->Run(session, NULL, &in, (const OrtValue *const *)&input, 1, &out, 1, &output), message, size, NULL);
    if (ok) {
        float *y = NULL;
        ok = succeeded(ORT->GetTensorMutableData(output, (void **)&y), message, size, NULL);
        if (ok && fabsf(*y - x * 2) > 1e-3f) {
            snprintf(message, size, "%s 探測模型結果錯誤（%g）。", onnx_provider_id(row->provider), (double)*y);
            ok = false;
        }
    }
    if (output) ORT->ReleaseValue(output);
    if (input) ORT->ReleaseValue(input);
    if (session) ORT->ReleaseSession(session);
    ORT->ReleaseSessionOptions(options);
    return ok;
}

static void probe(onnx_device *device, const device_row *row) {
    bool automatic = row->use != USE_EXPLICIT;
    char *reason = device->reason;
    size_t size = sizeof device->reason;
    display_name(row, device->name, sizeof device->name);
    switch (row->strategy) {
    case STRATEGY_CPU: device->available = true; break;
    case STRATEGY_CUDA:
        if (!built("CUDAExecutionProvider")) snprintf(reason, size, "%s", CUDA_OPT_IN);
        else device->available = run_probe(row, reason, size);
        break;
    case STRATEGY_TENSORRT: {
        /* Loading the provider proves the TensorRT libraries exist; building a probe engine would cost seconds. */
        const onnx_device *cuda = NULL;
        for (uint32_t i = 0; i < engine.device_count; i++) if (rows[i].strategy == STRATEGY_CUDA) cuda = &engine.devices[i];
        if (!built("TensorrtExecutionProvider")) snprintf(reason, size, "%s", CUDA_OPT_IN);
        else if (!cuda || !cuda->available) snprintf(reason, size, "需要可用的 CUDA：%s", cuda ? cuda->reason : "");
        else {
            OrtSessionOptions *options = create_options(reason, size);
            device->available = options && append(options, row, reason, size);
            if (options) ORT->ReleaseSessionOptions(options);
        }
        break;
    }
#if defined(AQUA_DESKTOP)
    case STRATEGY_WEBGPU_PLUGIN:
    case STRATEGY_OPENVINO: {
        plugin *p = plugin_of(row->strategy);
        const plugin_device *found = plugin_device_of(p, row->kind);
        if (p->failure[0]) snprintf(reason, size, "%s", p->failure);
        else if (!found) snprintf(reason, size, "%s 未回報可用的 %s 裝置。", onnx_provider_id(row->provider), onnx_kind_name(row->kind));
        else {
            device->available = true;
            snprintf(device->name, sizeof device->name, "%s", found->name);
            if (found->software) {
                snprintf(reason, size, "軟體模擬的圖形介面卡，僅在明確指定時使用。");
                automatic = false;
            }
        }
        break;
    }
#endif
    case STRATEGY_WEBGPU:
    case STRATEGY_NNAPI:
    case STRATEGY_XNNPACK: {
        const char *name = row->strategy == STRATEGY_WEBGPU ? "WebGpuExecutionProvider"
            : row->strategy == STRATEGY_NNAPI ? "NnapiExecutionProvider" : "XnnpackExecutionProvider";
        if (!built(name)) snprintf(reason, size, "此 ONNX Runtime 建置不含 %s。", onnx_provider_id(row->provider));
        else device->available = run_probe(row, reason, size);
        break;
    }
    case STRATEGY_UNAVAILABLE:
        /* Web: ONNX Runtime is compiled from source without its WebGPU and WebNN providers (README, "Web"). */
        snprintf(reason, size, "%s", row->provider == ONNX_WebGPU
            ? "網頁版推論引擎目前只提供中央處理器（WebGPU 推論須在執行中同步等待 GPU 讀回，網頁無法如此等待）。"
            : "網頁版推論引擎不含 WebNN。");
        break;
    default: snprintf(reason, size, "此平台不支援 %s 推論後端。", onnx_provider_id(row->provider)); break;
    }
    if (device->available && row->use == USE_DEVICE && onnx_platform_emulator()) {
        snprintf(reason, size, "Android 模擬器上僅在明確指定時使用。");
        automatic = false;
    }
    if (!device->available && !reason[0]) snprintf(reason, size, "未提供原因");
    device->automatic = device->available && automatic;
}

const onnx_device *backend_devices(uint32_t *count) {
    onnx_platform_lock();
    if (!engine.probed) {
        engine.probed = true;
        if (!load_engine()) {
            onnx_device *cpu = &engine.devices[0];
            cpu->id = "CPU";
            cpu->provider = ONNX_CPU;
            cpu->kind = ONNX_KIND_CPU;
            snprintf(cpu->name, sizeof cpu->name, "ONNX Runtime");
            snprintf(cpu->reason, sizeof cpu->reason, "%s", engine.failure);
            engine.device_count = 1;
        } else {
            for (uint32_t i = 0; i < ROW_COUNT; i++) {
                onnx_device *device = &engine.devices[i];
                device->id = rows[i].id;
                device->provider = rows[i].provider;
                device->kind = rows[i].kind;
                engine.device_count = i; /* rows probed so far, for TensorRT's look at CUDA */
                probe(device, &rows[i]);
            }
            engine.device_count = ROW_COUNT;
        }
        const char *diagnostics = getenv("AQUARIUS_ONNX_DIAGNOSTICS");
        if (diagnostics && strcmp(diagnostics, "1") == 0) {
            for (uint32_t i = 0; i < engine.device_count; i++) {
                const onnx_device *d = &engine.devices[i];
                fprintf(stderr, "[onnx] %-14s %-11s auto=%-5s %s %s\n", d->id, d->available ? "available" : "unavailable",
                        d->automatic ? "true" : "false", d->name, d->reason);
            }
        }
    }
    onnx_platform_unlock();
    *count = engine.device_count;
    return engine.devices;
}

/* ---- Sessions ------------------------------------------------------------------------------------------------- */
static onnx_type type_of(ONNXTensorElementDataType type) {
    switch (type) {
    case ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT: return ONNX_FLOAT32;
    case ONNX_TENSOR_ELEMENT_DATA_TYPE_DOUBLE: return ONNX_FLOAT64;
    case ONNX_TENSOR_ELEMENT_DATA_TYPE_INT32: return ONNX_INT32;
    case ONNX_TENSOR_ELEMENT_DATA_TYPE_UINT8: return ONNX_UINT8;
    case ONNX_TENSOR_ELEMENT_DATA_TYPE_BOOL: return ONNX_BOOL;
    default: return ONNX_TYPE_UNSUPPORTED;
    }
}

static ONNXTensorElementDataType element_of(onnx_type type) {
    static const ONNXTensorElementDataType elements[] = {
        [ONNX_FLOAT32] = ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, [ONNX_FLOAT64] = ONNX_TENSOR_ELEMENT_DATA_TYPE_DOUBLE,
        [ONNX_INT32] = ONNX_TENSOR_ELEMENT_DATA_TYPE_INT32, [ONNX_UINT8] = ONNX_TENSOR_ELEMENT_DATA_TYPE_UINT8,
        [ONNX_BOOL] = ONNX_TENSOR_ELEMENT_DATA_TYPE_BOOL,
    };
    return elements[type];
}

static void free_infos(onnx_value_info *infos, uint32_t count) {
    for (uint32_t i = 0; infos && i < count; i++) { free(infos[i].name); free(infos[i].shape); }
    free(infos);
}

/* The names, element types and dimensions of a session's inputs or outputs. */
static bool read_infos(OrtSession *session, bool inputs, onnx_value_info **out, uint32_t *out_count, char *message, size_t size) {
    size_t count = 0;
    if (!succeeded(inputs ? ORT->SessionGetInputCount(session, &count) : ORT->SessionGetOutputCount(session, &count), message, size, NULL)) return false;
    onnx_value_info *infos = calloc(count ? count : 1, sizeof *infos);
    if (!infos) { snprintf(message, size, "記憶體不足。"); return false; }
    for (size_t i = 0; i < count; i++) {
        char *name = NULL;
        OrtTypeInfo *type = NULL;
        if (!succeeded(inputs ? ORT->SessionGetInputName(session, i, engine.allocator, &name) : ORT->SessionGetOutputName(session, i, engine.allocator, &name), message, size, NULL) ||
            !succeeded(inputs ? ORT->SessionGetInputTypeInfo(session, i, &type) : ORT->SessionGetOutputTypeInfo(session, i, &type), message, size, NULL)) {
            if (name) succeeded(ORT->AllocatorFree(engine.allocator, name), NULL, 0, NULL);
            free_infos(infos, (uint32_t)count);
            return false;
        }
        infos[i].name = strdup(name);
        succeeded(ORT->AllocatorFree(engine.allocator, name), NULL, 0, NULL);
        infos[i].type = ONNX_TYPE_UNSUPPORTED;
        const OrtTensorTypeAndShapeInfo *tensor = NULL;
        if (succeeded(ORT->CastTypeInfoToTensorInfo(type, &tensor), NULL, 0, NULL) && tensor) {
            ONNXTensorElementDataType element;
            size_t rank = 0;
            if (succeeded(ORT->GetTensorElementType(tensor, &element), NULL, 0, NULL)) infos[i].type = type_of(element);
            if (succeeded(ORT->GetDimensionsCount(tensor, &rank), NULL, 0, NULL) && rank) {
                infos[i].shape = calloc(rank, sizeof *infos[i].shape);
                if (infos[i].shape && succeeded(ORT->GetDimensions(tensor, infos[i].shape, rank), NULL, 0, NULL)) infos[i].rank = (uint32_t)rank;
            }
        }
        ORT->ReleaseTypeInfo(type);
    }
    *out = infos;
    *out_count = (uint32_t)count;
    return true;
}

static const device_row *row_of(const onnx_device *device) { return &rows[device - engine.devices]; }

/* Where the engine finds the weight files of a model saved with external data: the folder of a model file, or the
   packaged weight files in memory. */
static bool add_weights(OrtSessionOptions *options, const onnx_model *model, char *message, size_t size) {
    if (model->folder && !succeeded(ORT->AddSessionConfigEntry(options, "session.model_external_initializers_file_folder_path",
                                                               model->folder), message, size, NULL)) return false;
    if (!model->file_count) return true;
    ORTCHAR_T *names[ONNX_MAX_WEIGHT_FILES];
    char *buffers[ONNX_MAX_WEIGHT_FILES];
    size_t lengths[ONNX_MAX_WEIGHT_FILES];
    for (uint32_t i = 0; i < model->file_count; i++) {
        names[i] = onnx_platform_path(model->files[i].name);
        buffers[i] = (char *)model->files[i].bytes;
        lengths[i] = model->files[i].length;
    }
    bool ok = succeeded(ORT->AddExternalInitializersFromFilesInMemory(options, (const ORTCHAR_T *const *)names, buffers, lengths,
                                                                      model->file_count), message, size, NULL);
    for (uint32_t i = 0; i < model->file_count; i++) free(names[i]);
    return ok;
}

onnx_session *backend_open(const onnx_model *model, const onnx_plan *plan, onnx_error *error) {
    const char *primary = onnx_provider_id(plan->devices[0]->provider);
    char message[1024] = "";
    OrtSessionOptions *options = create_options(message, sizeof message);
    bool ok = options != NULL && add_weights(options, model, message, sizeof message);
    onnx_platform_lock(); /* plugin device state is process wide */
    for (uint32_t i = 0; ok && i < plan->count; i++) {
        const device_row *row = row_of(plan->devices[i]);
        ok = configure(options, row, message, sizeof message) && append(options, row, message, sizeof message);
    }
    onnx_platform_unlock();
    OrtSession *native = NULL;
    OrtErrorCode code = ORT_FAIL;
    if (ok) {
        ok = succeeded(ORT->CreateSessionFromArray(engine.env, model->bytes, model->length, options, &native), message, sizeof message, &code);
        if (!ok && (code == ORT_INVALID_PROTOBUF || code == ORT_INVALID_GRAPH || code == ORT_NO_MODEL)) {
            ORT->ReleaseSessionOptions(options);
            onnx_fail(error, ONNX_INVALID_ARGUMENT, "ONNX 模型無效或格式不受支援：%s", message);
            return NULL;
        }
    }
    if (options) ORT->ReleaseSessionOptions(options);
    if (!ok) {
        onnx_fail(error, ONNX_ENGINE, "ONNX 模型或推論後端 %s 初始化失敗：%s", primary, message);
        return NULL;
    }
    onnx_session *session = calloc(1, sizeof *session);
    if (!session || !read_infos(native, true, &session->inputs, &session->input_count, message, sizeof message) ||
        !read_infos(native, false, &session->outputs, &session->output_count, message, sizeof message)) {
        if (session) free_infos(session->inputs, session->input_count);
        free(session);
        ORT->ReleaseSession(native);
        onnx_fail(error, ONNX_ENGINE, "ONNX 模型或推論後端 %s 初始化失敗：%s", primary, message);
        return NULL;
    }
    session->session = native;
    session->plan = *plan;
    /* The chain as the engine sees it: requested providers, companions, then the implicit CPU fallback. */
    for (uint32_t i = 0; i <= plan->count; i++) {
        onnx_provider chain[2] = {i < plan->count ? plan->devices[i]->provider : ONNX_CPU, ONNX_PROVIDER_COUNT};
        if (i < plan->count && row_of(plan->devices[i])->strategy == STRATEGY_TENSORRT) chain[1] = ONNX_CUDA;
        for (int c = 0; c < 2 && chain[c] != ONNX_PROVIDER_COUNT; c++) {
            bool seen = false;
            for (uint32_t s = 0; s < session->chain_count; s++) seen |= session->chain[s] == chain[c];
            if (!seen) session->chain[session->chain_count++] = chain[c];
        }
    }
    return session;
}

void backend_close(onnx_session *session) {
    if (!session) return;
    ORT->ReleaseSession(session->session);
    free_infos(session->inputs, session->input_count);
    free_infos(session->outputs, session->output_count);
    free(session);
}

const onnx_plan *backend_plan(const onnx_session *session) { return &session->plan; }

uint32_t backend_chain(const onnx_session *session, onnx_provider chain[ONNX_PROVIDER_COUNT]) {
    memcpy(chain, session->chain, session->chain_count * sizeof *chain);
    return session->chain_count;
}

const onnx_value_info *backend_inputs(const onnx_session *session, uint32_t *count) { *count = session->input_count; return session->inputs; }
const onnx_value_info *backend_outputs(const onnx_session *session, uint32_t *count) { *count = session->output_count; return session->outputs; }

bool backend_run(onnx_session *session, const char *const *input_names, const onnx_tensor *const *inputs,
                 uint32_t input_count, const char *const *output_names, onnx_tensor *outputs, uint32_t output_count,
                 onnx_error *error) {
    const char *primary = onnx_provider_id(session->plan.devices[0]->provider);
    OrtValue **values = calloc((size_t)input_count + output_count + 1, sizeof *values);
    if (!values) return onnx_fail(error, ONNX_ENGINE, "記憶體不足。");
    OrtValue **results = values + input_count;
    char message[1024] = "";
    bool ok = true;
    /* Inputs are bound in place (ONNX Runtime never writes them); outputs are allocated by the engine. */
    for (uint32_t i = 0; ok && i < input_count; i++) {
        const onnx_tensor *t = inputs[i];
        ok = succeeded(ORT->CreateTensorWithDataAsOrtValue(engine.memory, t->data, t->count * onnx_type_size(t->type), t->shape,
                                                           t->rank, element_of(t->type), &values[i]), message, sizeof message, NULL);
    }
    if (ok) ok = succeeded(ORT->Run(session->session, NULL, input_names, (const OrtValue *const *)values, input_count,
                                    output_names, output_count, results), message, sizeof message, NULL);
    if (!ok) onnx_fail(error, ONNX_ENGINE, "ONNX 推論失敗（%s）；請確認模型運算子與輸入資料：%s", primary, message);
    uint32_t done = 0;
    for (; ok && done < output_count; done++) {
        /* One copy from engine memory into an owned snapshot (device outputs are already downloaded). */
        int is_tensor = 0;
        OrtTensorTypeAndShapeInfo *info = NULL;
        ONNXTensorElementDataType element = ONNX_TENSOR_ELEMENT_DATA_TYPE_UNDEFINED;
        size_t rank = 0, elements = 0;
        int64_t shape[ONNX_MAX_RANK];
        void *data = NULL;
        if (!succeeded(ORT->IsTensor(results[done], &is_tensor), NULL, 0, NULL) || !is_tensor) {
            ok = onnx_fail(error, ONNX_UNSUPPORTED, "此版本不支援非張量的模型輸出。");
            break;
        }
        bool described = succeeded(ORT->GetTensorTypeAndShape(results[done], &info), message, sizeof message, NULL) &&
            succeeded(ORT->GetTensorElementType(info, &element), NULL, 0, NULL) &&
            succeeded(ORT->GetDimensionsCount(info, &rank), NULL, 0, NULL) && rank <= ONNX_MAX_RANK &&
            succeeded(ORT->GetDimensions(info, shape, rank), NULL, 0, NULL) &&
            succeeded(ORT->GetTensorShapeElementCount(info, &elements), NULL, 0, NULL) && elements <= ONNX_MAX_ELEMENTS;
        if (info) ORT->ReleaseTensorTypeAndShapeInfo(info);
        if (!described) { ok = onnx_fail(error, ONNX_ENGINE, "模型輸出尺寸超過上限或無效。"); break; }
        onnx_type type = type_of(element);
        if (type == ONNX_TYPE_UNSUPPORTED) { ok = onnx_fail(error, ONNX_UNSUPPORTED, "此版本不支援模型輸出的張量型別。"); break; }
        if (!onnx_tensor_init(&outputs[done], type, shape, (uint32_t)rank, NULL, error)) { ok = false; break; }
        if (elements && (!succeeded(ORT->GetTensorMutableData(results[done], &data), message, sizeof message, NULL) || !data)) {
            onnx_tensor_free(&outputs[done]);
            ok = onnx_fail(error, ONNX_ENGINE, "ONNX 推論失敗（%s）；請確認模型運算子與輸入資料：%s", primary, message);
            break;
        }
        if (elements) memcpy(outputs[done].data, data, elements * onnx_type_size(type));
    }
    if (!ok) for (uint32_t i = 0; i < done; i++) onnx_tensor_free(&outputs[i]);
    for (uint32_t i = 0; i < input_count + output_count; i++) if (values[i]) ORT->ReleaseValue(values[i]);
    free(values);
    return ok;
}
