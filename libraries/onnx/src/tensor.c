/* Tensors: owned CPU snapshots of the five element types of the contract (float32, float64, int32, uint8, bool), their
 * limits, conversions from and to script values and the validation of a run. GPU memory never crosses this contract;
 * engines read inputs in place and outputs are copied out of engine memory once.
 *
 * 張量：五種元素型別的 CPU 快照、上限、與星泉值互轉及推論輸入檢查。 */
#include "onnx.h"
#include <aqua/image.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

static const struct { const char *name; size_t size; } types[] = {
    [ONNX_FLOAT32] = {"float32", 4}, [ONNX_FLOAT64] = {"float64", 8}, [ONNX_INT32] = {"int32", 4},
    [ONNX_UINT8] = {"uint8", 1}, [ONNX_BOOL] = {"bool", 1}, [ONNX_TYPE_UNSUPPORTED] = {"unsupported", 0},
};

const char *onnx_type_name(onnx_type type) { return types[type].name; }
size_t onnx_type_size(onnx_type type) { return types[type].size; }

bool onnx_type_parse(const char *name, onnx_type *type) {
    for (int i = 0; i < ONNX_TYPE_UNSUPPORTED; i++) {
        if (strcmp(name, types[i].name) == 0) { *type = (onnx_type)i; return true; }
    }
    return false;
}

bool onnx_tensor_init(onnx_tensor *tensor, onnx_type type, const int64_t *shape, uint32_t rank, void *data, onnx_error *error) {
    memset(tensor, 0, sizeof *tensor);
    if (rank > ONNX_MAX_RANK) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "張量維度必須是非負整數，最多十六個維度。");
    for (uint32_t i = 0; i < rank; i++) {
        if (shape[i] < 0 || shape[i] > ONNX_MAX_ELEMENTS) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "張量維度必須是非負整數，最多十六個維度。");
        tensor->shape[i] = shape[i];
    }
    int64_t count = 1;
    for (uint32_t i = 0; i < rank; i++) {
        if (shape[i] != 0 && count > INT64_MAX / shape[i]) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "張量尺寸超過上限。");
        count *= shape[i];
    }
    if (count > ONNX_MAX_ELEMENTS) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "張量資料長度與形狀不符或超過上限。");
    tensor->type = type;
    tensor->rank = rank;
    tensor->count = (size_t)count;
    if (data) {
        tensor->data = data;
        tensor->borrowed = true;
    } else {
        tensor->data = calloc(tensor->count ? tensor->count : 1, onnx_type_size(type));
        if (!tensor->data) return onnx_fail(error, ONNX_ENGINE, "記憶體不足。");
    }
    return true;
}

void onnx_tensor_free(onnx_tensor *tensor) {
    if (!tensor->borrowed) free(tensor->data);
    tensor->data = NULL;
}

/* The checks and messages of the old binding, in its order: type text, shape, then the data for that type. */
bool onnx_tensor_from_values(aqua_runtime *rt, aqua_value type_value, aqua_value shape_value, aqua_value data_value,
                             onnx_tensor *tensor, onnx_error *error) {
    memset(tensor, 0, sizeof *tensor);
    if (type_value.type != AQUA_STRING) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "預期字串參數。");
    if (shape_value.type != AQUA_ARRAY) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "預期陣列參數。");
    uint32_t rank = aqua_array_length(rt, shape_value);
    double *dimensions = malloc((rank ? rank : 1) * sizeof *dimensions);
    if (!aqua_array_read_f64(rt, shape_value, 0, rank, dimensions)) {
        free(dimensions);
        return onnx_fail(error, ONNX_INVALID_ARGUMENT, "預期數字陣列。");
    }
    int64_t shape[ONNX_MAX_RANK];
    for (uint32_t i = 0; i < rank; i++) {
        double d = dimensions[i];
        if (!isfinite(d) || d != trunc(d) || d < 0 || d > ONNX_MAX_ELEMENTS) {
            free(dimensions);
            return onnx_fail(error, ONNX_INVALID_ARGUMENT, "張量維度必須是非負整數。");
        }
        if (i < ONNX_MAX_RANK) shape[i] = (int64_t)d;
    }
    free(dimensions);
    char *type_name = aqua_text_dup(rt, type_value, NULL);
    onnx_type type;
    bool known = onnx_type_parse(type_name, &type);
    aqua_free(type_name);

    /* An image object supplies its RGBA bytes directly for a uint8 tensor (an addition to the old contract). */
    aqua_image *image = aqua_image_of(rt, data_value);
    if (image) {
        if (!known || type != ONNX_UINT8) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "影像只能建立無號整數8（uint8）張量。");
        size_t bytes = (size_t)image->width * (size_t)image->height * 4;
        if (!onnx_tensor_init(tensor, type, shape, rank, NULL, error)) return false;
        if (tensor->count != bytes) {
            onnx_tensor_free(tensor);
            return onnx_fail(error, ONNX_INVALID_ARGUMENT, "張量資料長度與形狀不符或超過上限。");
        }
        memcpy(tensor->data, image->rgba, bytes);
        return true;
    }
    if (data_value.type != AQUA_ARRAY) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "預期陣列參數。");
    uint32_t length = aqua_array_length(rt, data_value);
    if (length > ONNX_MAX_ELEMENTS) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "張量資料超過上限。");
    if (known && type == ONNX_BOOL) {
        for (uint32_t i = 0; i < length; i++) {
            if (aqua_array_get(rt, data_value, i).type != AQUA_BOOL) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "布林張量必須使用布林值。");
        }
    }
    double *numbers = NULL;
    if (!known || type != ONNX_BOOL) {
        numbers = malloc((length ? length : 1) * sizeof *numbers);
        if (!numbers) return onnx_fail(error, ONNX_ENGINE, "記憶體不足。");
        if (!aqua_array_read_f64(rt, data_value, 0, length, numbers)) {
            free(numbers);
            return onnx_fail(error, ONNX_INVALID_ARGUMENT, "預期數字陣列。");
        }
        bool valid = known;
        for (uint32_t i = 0; i < length; i++) {
            double n = numbers[i];
            if (!isfinite(n)) {
                free(numbers);
                return onnx_fail(error, ONNX_INVALID_ARGUMENT, "輸入張量必須使用有限數值。");
            }
            if (type == ONNX_FLOAT32 && !isfinite((float)n)) valid = false;
            if (type == ONNX_INT32 && (n != trunc(n) || n < INT32_MIN || n > INT32_MAX)) valid = false;
            if (type == ONNX_UINT8 && (n != trunc(n) || n < 0 || n > 255)) valid = false;
        }
        if (!valid) {
            free(numbers);
            return onnx_fail(error, ONNX_INVALID_ARGUMENT, "張量型別不支援或數值超出型別範圍。");
        }
    }
    if (!onnx_tensor_init(tensor, type, shape, rank, NULL, error)) { free(numbers); return false; }
    if (tensor->count != length) {
        free(numbers);
        onnx_tensor_free(tensor);
        return onnx_fail(error, ONNX_INVALID_ARGUMENT, "張量資料長度與形狀不符或超過上限。");
    }
    for (uint32_t i = 0; i < length; i++) {
        switch (type) {
        case ONNX_FLOAT32: ((float *)tensor->data)[i] = (float)numbers[i]; break;
        case ONNX_FLOAT64: ((double *)tensor->data)[i] = numbers[i]; break;
        case ONNX_INT32: ((int32_t *)tensor->data)[i] = (int32_t)numbers[i]; break;
        case ONNX_UINT8: ((uint8_t *)tensor->data)[i] = (uint8_t)numbers[i]; break;
        case ONNX_BOOL: ((uint8_t *)tensor->data)[i] = aqua_array_get(rt, data_value, i).number != 0; break;
        default: break;
        }
    }
    free(numbers);
    return true;
}

aqua_value onnx_tensor_values(aqua_runtime *rt, const onnx_tensor *tensor) {
    uint32_t count = (uint32_t)tensor->count;
    switch (tensor->type) {
    case ONNX_FLOAT32: {
        double *values = malloc((count ? count : 1) * sizeof *values);
        for (uint32_t i = 0; i < count; i++) values[i] = ((const float *)tensor->data)[i];
        aqua_value array = aqua_array_from_f64(rt, values, count, AQUA_DOUBLE);
        free(values);
        return array;
    }
    case ONNX_FLOAT64: return aqua_array_from_f64(rt, tensor->data, count, AQUA_DOUBLE);
    case ONNX_INT32: return aqua_array_from_i32(rt, tensor->data, count);
    case ONNX_UINT8: return aqua_array_from_u8(rt, tensor->data, count);
    case ONNX_BOOL: {
        aqua_value array = aqua_array_new(rt, count);
        for (uint32_t i = 0; i < count; i++) aqua_array_set(rt, array, i, aqua_bool(((const uint8_t *)tensor->data)[i] != 0));
        return array;
    }
    default: return aqua_array_new(rt, 0);
    }
}

bool onnx_validate_run(const onnx_value_info *inputs, uint32_t input_count, const onnx_value_info *outputs,
                       uint32_t output_count, const char *const *names, const onnx_tensor *const *tensors, uint32_t count,
                       const char *const *requested, uint32_t requested_count, onnx_error *error) {
    /* `names` are already distinct, so equal counts and every model input present make a one-to-one match. */
    bool complete = count == input_count;
    for (uint32_t m = 0; complete && m < input_count; m++) {
        bool found = false;
        for (uint32_t i = 0; i < count; i++) found |= strcmp(names[i], inputs[m].name) == 0;
        complete = found;
    }
    if (!complete) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "必須提供每個模型輸入且不得包含未知名稱。");
    bool outputs_valid = requested_count > 0;
    for (uint32_t r = 0; outputs_valid && r < requested_count; r++) {
        for (uint32_t s = r + 1; s < requested_count; s++) outputs_valid &= strcmp(requested[r], requested[s]) != 0;
        bool known = false;
        for (uint32_t o = 0; o < output_count; o++) known |= strcmp(requested[r], outputs[o].name) == 0;
        outputs_valid &= known;
    }
    if (!outputs_valid) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "輸出名稱必須存在、不得重複且至少指定一個。");
    for (uint32_t m = 0; m < input_count; m++) {
        const onnx_tensor *tensor = NULL;
        for (uint32_t i = 0; i < count; i++) if (strcmp(names[i], inputs[m].name) == 0) tensor = tensors[i];
        bool matches = tensor->type == inputs[m].type && tensor->rank == inputs[m].rank;
        for (uint32_t d = 0; matches && d < inputs[m].rank; d++) matches = inputs[m].shape[d] < 0 || inputs[m].shape[d] == tensor->shape[d];
        if (!matches) return onnx_fail(error, ONNX_INVALID_ARGUMENT, "輸入「%s」的型別或形狀與模型不符。", inputs[m].name);
    }
    return true;
}
