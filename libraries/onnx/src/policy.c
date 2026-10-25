/* The provider catalog, selection parsing, device ranking and session fallback: the same policy on every target, so a
 * script selects providers identically everywhere (the old OnnxProviders, OnnxSelection, OnnxPlanner and
 * OnnxSessionFactory). The engine only lists ranked devices and opens exact plans (backend_ort.c).
 *
 * 後端目錄、選擇解析、裝置排序與工作階段備援策略：所有平台相同。 */
#include "onnx.h"
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

static const struct { const char *id, *chinese; } catalog[ONNX_PROVIDER_COUNT] = {
#define PROVIDER(id, chinese) {#id, chinese},
#include "providers.def"
#undef PROVIDER
};

static const struct { const char *english, *chinese; onnx_preference preference; } preferences[] = {
    {"Auto", "自動", ONNX_PREFER_AUTO}, {"GPU", "圖形處理器", ONNX_PREFER_GPU}, {"NPU", "神經處理器", ONNX_PREFER_NPU},
};

const onnx_selection onnx_auto = {ONNX_PREFER_AUTO, 0, {0}};

bool onnx_fail(onnx_error *error, onnx_status status, const char *format, ...) {
    va_list arguments;
    va_start(arguments, format);
    error->status = status;
    vsnprintf(error->message, sizeof error->message, format, arguments);
    va_end(arguments);
    return false;
}

void onnx_describe(const char *message, char *out, size_t size) {
    if (!message) message = "";
    /* Drop a "... [ONNXRuntimeError] : 1 : FAIL : " or "[ErrorCode:Fail] " preamble, then keep the first line. */
    const char *marker = strstr(message, "[ONNXRuntimeError] : ");
    if (marker) {
        const char *rest = marker;
        for (int separators = 0; separators < 3 && rest; separators++) {
            rest = strstr(rest, " : ");
            if (rest) rest += 3;
        }
        if (rest) message = rest;
    } else if (strncmp(message, "[ErrorCode:", 11) == 0 && strchr(message, ']')) {
        message = strchr(message, ']') + 1;
    }
    while (*message == ' ') message++;
    size_t length = strcspn(message, "\r\n");
    while (length > 0 && message[length - 1] == ' ') length--;
    /* At most 400 characters (code points), then an ellipsis. */
    size_t bytes = 0, characters = 0;
    while (bytes < length && characters < 400) {
        bytes++;
        while (bytes < length && ((unsigned char)message[bytes] & 0xC0) == 0x80) bytes++;
        characters++;
    }
    snprintf(out, size, "%.*s%s", (int)bytes, message, bytes < length ? "…" : "");
}

const char *onnx_provider_id(onnx_provider provider) { return catalog[provider].id; }
const char *onnx_provider_chinese(onnx_provider provider) { return catalog[provider].chinese; }
const char *onnx_kind_name(onnx_kind kind) { return kind == ONNX_KIND_GPU ? "GPU" : kind == ONNX_KIND_NPU ? "NPU" : "CPU"; }

/* Provider ids and English names are ASCII and compared like .NET's OrdinalIgnoreCase. */
static bool equal_ignoring_case(const char *a, const char *b) {
    for (; *a && *b; a++, b++) {
        char x = *a >= 'A' && *a <= 'Z' ? (char)(*a + 32) : *a, y = *b >= 'A' && *b <= 'Z' ? (char)(*b + 32) : *b;
        if (x != y) return false;
    }
    return *a == *b;
}

bool onnx_provider_parse(const char *text, onnx_provider *provider) {
    for (int i = 0; i < ONNX_PROVIDER_COUNT; i++) {
        if (equal_ignoring_case(text, catalog[i].id) || strcmp(text, catalog[i].chinese) == 0) {
            *provider = (onnx_provider)i;
            return true;
        }
    }
    return false;
}

bool onnx_selection_parse(const char *const *texts, uint32_t count, onnx_selection *selection, onnx_error *error) {
    memset(selection, 0, sizeof *selection);
    if (count == 1) {
        for (size_t i = 0; i < sizeof preferences / sizeof *preferences; i++) {
            if (equal_ignoring_case(texts[0], preferences[i].english) || strcmp(texts[0], preferences[i].chinese) == 0) {
                selection->preference = preferences[i].preference;
                return true;
            }
        }
    }
    if (count == 0) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "推論後端清單不得為空。");
    selection->preference = ONNX_EXPLICIT;
    bool repeated = false;
    for (uint32_t i = 0; i < count; i++) {
        onnx_provider provider;
        if (!onnx_provider_parse(texts[i], &provider)) {
            char known[256] = "";
            for (int p = 0; p < ONNX_PROVIDER_COUNT; p++) {
                if (p) strncat(known, "、", sizeof known - strlen(known) - 1);
                strncat(known, catalog[p].id, sizeof known - strlen(known) - 1);
            }
            return onnx_fail(error, ONNX_INVALID_ARGUMENT, "未知的推論後端「%s」。可用：自動、圖形處理器、神經處理器或 %s。", texts[i], known);
        }
        for (uint32_t j = 0; j < selection->count; j++) repeated |= selection->providers[j] == provider;
        if (!repeated) selection->providers[selection->count++] = provider;
    }
    if (repeated) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "推論後端清單不得重複。");
    return true;
}

static const char *reason_of(const onnx_device *device) { return device->reason[0] ? device->reason : "未提供原因"; }

bool onnx_plan_sessions(const onnx_device *devices, uint32_t device_count, const onnx_selection *selection,
                        onnx_plan *plans, uint32_t *plan_count, onnx_error *error) {
    *plan_count = 0;
    if (selection->preference == ONNX_EXPLICIT) {
        /* Strict: the first available device of each provider in host rank order, in the requested order. */
        onnx_plan plan = {0};
        for (uint32_t i = 0; i < selection->count; i++) {
            onnx_provider provider = selection->providers[i];
            const onnx_device *first = NULL, *chosen = NULL;
            for (uint32_t d = 0; d < device_count; d++) {
                if (devices[d].provider != provider) continue;
                if (!first) first = &devices[d];
                if (devices[d].available) { chosen = &devices[d]; break; }
            }
            if (!first) return onnx_fail(error, ONNX_UNSUPPORTED, "此平台未提供 %s 推論後端。", onnx_provider_id(provider));
            if (!chosen) return onnx_fail(error, ONNX_UNSUPPORTED, "推論後端 %s 目前無法使用：%s", onnx_provider_id(provider), reason_of(first));
            plan.devices[plan.count++] = chosen;
        }
        plans[0] = plan;
        *plan_count = 1;
        return true;
    }
    /* Preferences: every available automatic device of the requested class, one per provider in rank order. */
    const onnx_device *candidates[ONNX_PROVIDER_COUNT];
    uint32_t count = 0;
    for (uint32_t d = 0; d < device_count; d++) {
        const onnx_device *device = &devices[d];
        if (!device->available || !device->automatic) continue;
        if (selection->preference == ONNX_PREFER_GPU && device->kind != ONNX_KIND_GPU) continue;
        if (selection->preference == ONNX_PREFER_NPU && device->kind != ONNX_KIND_NPU) continue;
        bool seen = false;
        for (uint32_t c = 0; c < count; c++) seen |= candidates[c]->provider == device->provider;
        if (!seen) candidates[count++] = device;
    }
    if (selection->preference == ONNX_PREFER_AUTO) {
        /* CPU always closes the Auto chain, even when a host ranked it elsewhere. */
        const onnx_device *cpu = NULL;
        for (uint32_t c = 0; c < count; c++) {
            if (candidates[c]->provider != ONNX_CPU) continue;
            cpu = candidates[c];
            memmove(&candidates[c], &candidates[c + 1], (count - c - 1) * sizeof *candidates);
            count--;
            break;
        }
        for (uint32_t d = 0; !cpu && d < device_count; d++)
            if (devices[d].provider == ONNX_CPU && devices[d].available) cpu = &devices[d];
        if (cpu) candidates[count++] = cpu;
    }
    if (count == 0) {
        return onnx_fail(error, ONNX_UNSUPPORTED, "%s", selection->preference == ONNX_PREFER_GPU ? "找不到可用的圖形處理器（GPU）推論後端。"
            : selection->preference == ONNX_PREFER_NPU ? "找不到可用的神經處理器（NPU）推論後端。" : "此平台沒有可用的推論後端。");
    }
    for (uint32_t c = 0; c < count; c++) {
        plans[c].count = 1;
        plans[c].devices[0] = candidates[c];
    }
    *plan_count = count;
    return true;
}

bool onnx_selection_supported(const onnx_selection *selection, bool *supported, onnx_error *error) {
    uint32_t device_count, plan_count;
    const onnx_device *devices = backend_devices(&device_count);
    onnx_plan plans[ONNX_PROVIDER_COUNT];
    *supported = onnx_plan_sessions(devices, device_count, selection, plans, &plan_count, error) && plan_count > 0;
    return *supported || error->status == ONNX_UNSUPPORTED;
}

onnx_session *onnx_session_open(const uint8_t *model, size_t length, const onnx_selection *selection, onnx_error *error) {
    if (length < 1 || length > ONNX_MAX_MODEL_BYTES) {
        onnx_fail(error, ONNX_INVALID_ARGUMENT, "模型必須是非空的 ONNX 資料，最多二百五十六 MiB。");
        return NULL;
    }
    uint32_t device_count, plan_count;
    const onnx_device *devices = backend_devices(&device_count);
    onnx_plan plans[ONNX_PROVIDER_COUNT];
    if (!onnx_plan_sessions(devices, device_count, selection, plans, &plan_count, error)) return NULL;
    char failures[sizeof error->message] = "";
    uint32_t failed = 0;
    for (uint32_t i = 0; i < plan_count; i++) {
        onnx_session *session = backend_open(model, length, &plans[i], error);
        if (session) return session;
        /* Explicit lists never become a different session; preferences skip providers that fail to initialize. */
        if (selection->preference == ONNX_EXPLICIT || (error->status != ONNX_ENGINE && error->status != ONNX_UNSUPPORTED)) return NULL;
        size_t used = strlen(failures);
        snprintf(failures + used, sizeof failures - used, "%s%s（%s）", failed ? "；" : "",
                 onnx_provider_id(plans[i].devices[0]->provider), error->message);
        failed++;
    }
    if (failed > 1) onnx_fail(error, ONNX_ENGINE, "所有候選推論後端都無法初始化：%s", failures);
    return NULL;
}
