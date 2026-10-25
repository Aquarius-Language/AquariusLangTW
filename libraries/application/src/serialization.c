/* Serialization|序列化: JSON text, AQD1 binary documents, compression and ZIP archives. */
#include "application.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

/* ---- AQD1 ---- */

bool app_document_encode(aqua_runtime *rt, aqua_value value, uint32_t schema, app_bytes *out, app_error *e) {
    memset(out, 0, sizeof *out);
    if (schema == 0) return app_error_set(e, APP_INVALID_DATA, "Schema version starts at 1.");
    app_bytes json;
    if (!app_json_write(rt, value, &json, e)) return false;
    if (!app_check_bytes((uint64_t)json.length + 16, e)) { app_bytes_free(&json); return false; }
    app_bytes_reserve(out, json.length + 16);
    uint8_t header[16] = {'A', 'Q', 'D', '1'};
    app_put32le(header + 4, schema);
    app_put32le(header + 8, (uint32_t)json.length);
    app_put32le(header + 12, app_crc32(0, json.data, json.length));
    app_bytes_append(out, header, 16);
    app_bytes_append(out, json.data, json.length);
    app_bytes_free(&json);
    return true;
}

bool app_document_decode(aqua_runtime *rt, const uint8_t *data, size_t length, uint32_t *schema, aqua_value *value, app_error *e) {
    if (!app_check_bytes(length, e)) return false;
    if (length < 16 || memcmp(data, "AQD1", 4) != 0) return app_error_set(e, APP_INVALID_DATA, "Invalid document header or version.");
    *schema = app_get32le(data + 4);
    if (*schema == 0 || app_get32le(data + 8) != length - 16 || app_get32le(data + 12) != app_crc32(0, data + 16, length - 16))
        return app_error_set(e, APP_INVALID_DATA, "Invalid document length, schema or checksum.");
    if (!app_utf8_valid(data + 16, length - 16)) return app_error_set(e, APP_INVALID_DATA, "The document's JSON is not valid UTF-8.");
    return app_json_parse(rt, data + 16, length - 16, value, e);
}

/* A whole number argument as uint32 (schemas). */
static bool schema_of(aqua_value v, uint32_t *out, app_error *e) {
    if (!aqua_is_number(v) || !isfinite(v.number)) return app_error_set(e, APP_INVALID_DATA, "Expected a finite number.");
    if (v.number != floor(v.number)) return app_error_set(e, APP_INVALID_DATA, "Expected an integer.");
    if (v.number < 0 || v.number > 4294967295.0) return app_error_set(e, APP_INVALID_DATA, "Arithmetic operation resulted in an overflow.");
    *out = (uint32_t)v.number;
    return true;
}

/* ---- Module functions ---- */

void app_serialization_Json(aqua_call *call) {
    app_error e = {0};
    app_bytes json;
    if (!app_json_write(aqua_call_runtime(call), aqua_arg(call, 0), &json, &e)) { app_fail(call, &e); return; }
    aqua_return_text(call, (const char *)json.data, json.length);
    app_bytes_free(&json);
}

void app_serialization_ParseJson(aqua_call *call) {
    app_error e = {0};
    aqua_str text = aqua_arg_str(call, 0);
    aqua_value value;
    if (!app_json_parse(aqua_call_runtime(call), (const uint8_t *)text.data, text.length, &value, &e)) { app_fail(call, &e); return; }
    aqua_return(call, value);
}

void app_serialization_Binary(aqua_call *call) {
    app_error e = {0};
    uint32_t schema;
    app_bytes out;
    if (!schema_of(aqua_arg(call, 1), &schema, &e) || !app_document_encode(aqua_call_runtime(call), aqua_arg(call, 0), schema, &out, &e)) {
        app_fail(call, &e);
        return;
    }
    aqua_return(call, app_bytes_value(aqua_call_runtime(call), out.data, out.length));
    app_bytes_free(&out);
}

static bool parse_binary(aqua_runtime *rt, aqua_value bytes_value, uint32_t *schema, aqua_value *value, app_error *e) {
    app_bytes bytes;
    if (!app_value_bytes(rt, bytes_value, &bytes, e)) return false;
    bool ok = app_document_decode(rt, bytes.data, bytes.length, schema, value, e);
    app_bytes_free(&bytes);
    return ok;
}

void app_serialization_ParseBinary(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    app_error e = {0};
    uint32_t schema;
    aqua_value value;
    if (!parse_binary(rt, aqua_arg(call, 0), &schema, &value, &e)) { app_fail(call, &e); return; }
    aqua_value record = aqua_hash_new(rt, 2);
    app_set(rt, record, "schema", app_number(schema));
    app_set(rt, record, "value", value);
    aqua_return(call, app_record(rt, record));
}

/* Never fails: invalid bytes, checksum failures and wrong input types give {ok: false, schema: 0, value: null, error}. */
void app_serialization_TryParseBinary(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    app_error e = {0};
    uint32_t schema = 0;
    aqua_value value = aqua_null();
    bool ok = parse_binary(rt, aqua_arg(call, 0), &schema, &value, &e);
    aqua_value record = aqua_hash_new(rt, 4);
    app_set(rt, record, "ok", aqua_bool(ok));
    app_set(rt, record, "schema", ok ? app_number(schema) : aqua_int(0));
    app_set_text(rt, record, "error", ok ? "" : e.message);
    app_set(rt, record, "value", ok ? value : aqua_null());
    aqua_return(call, app_record(rt, record));
}

void app_serialization_Compress(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    app_error e = {0};
    app_compression format;
    app_level level = APP_OPTIMAL;
    app_bytes input, output;
    if (!app_compression_of(aqua_arg_str(call, 1).data, &format, &e) ||
        (aqua_has_arg(call, 2) && !app_level_of(aqua_arg_str(call, 2).data, &level, &e)) ||
        !app_value_bytes(rt, aqua_arg(call, 0), &input, &e)) { app_fail(call, &e); return; }
    bool ok = app_compress(input.data, input.length, format, level, &output, &e);
    app_bytes_free(&input);
    if (!ok) { app_fail(call, &e); return; }
    aqua_return(call, app_bytes_value(rt, output.data, output.length));
    app_bytes_free(&output);
}

void app_serialization_Decompress(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    app_error e = {0};
    app_compression format;
    app_bytes input, output;
    if (app_cancelled(call)) return;
    if (!app_compression_of(aqua_arg_str(call, 1).data, &format, &e) || !app_value_bytes(rt, aqua_arg(call, 0), &input, &e)) { app_fail(call, &e); return; }
    bool ok = app_decompress(input.data, input.length, format, APP_MAX_BYTES, &output, &e);
    app_bytes_free(&input);
    if (!ok) { app_fail(call, &e); return; }
    if (app_cancelled(call)) { app_bytes_free(&output); return; }
    aqua_return(call, app_bytes_value(rt, output.data, output.length));
    app_bytes_free(&output);
}

void app_serialization_Archive(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    app_error e = {0};
    aqua_value hash = aqua_arg(call, 0);
    uint32_t count = aqua_hash_count(rt, hash);
    if (count > APP_MAX_ENTRIES) { app_fail_with(call, APP_LIMIT_EXCEEDED, "Too many archive entries."); return; }
    app_entry *entries = calloc(count ? count : 1, sizeof *entries);
    bool ok = true;
    uint64_t total = 0;
    size_t filled = 0;
    for (uint32_t i = 0; i < count && ok; i++) {
        aqua_value key, value;
        aqua_hash_entry(rt, hash, i, &key, &value);
        if (key.type != AQUA_STRING) { ok = app_error_set(&e, APP_INVALID_DATA, "Expected a string."); break; }
        entries[i].name = aqua_text_dup(rt, key, NULL);
        filled = i + 1;
        if (!app_value_bytes(rt, value, &entries[i].data, &e)) { ok = false; break; }
        total += entries[i].data.length;
        if (!app_check_bytes(total, &e)) ok = false;
    }
    app_bytes out = {0};
    if (ok) ok = app_archive_create(entries, count, &out, &e);
    for (size_t i = 0; i < filled; i++) { aqua_free(entries[i].name); entries[i].name = NULL; app_bytes_free(&entries[i].data); }
    free(entries);
    if (!ok) { app_fail(call, &e); return; }
    aqua_return(call, app_bytes_value(rt, out.data, out.length));
    app_bytes_free(&out);
}

void app_serialization_ReadArchive(aqua_call *call) {
    aqua_runtime *rt = aqua_call_runtime(call);
    app_error e = {0};
    app_bytes input;
    app_entry *entries;
    size_t count;
    if (app_cancelled(call)) return;
    if (!app_value_bytes(rt, aqua_arg(call, 0), &input, &e)) { app_fail(call, &e); return; }
    bool ok = app_archive_read(input.data, input.length, &entries, &count, &e);
    app_bytes_free(&input);
    if (!ok) { app_fail(call, &e); return; }
    aqua_value hash = aqua_hash_new(rt, (uint32_t)count);
    for (size_t i = 0; i < count; i++)
        aqua_hash_set(rt, hash, aqua_cstring(rt, entries[i].name), app_bytes_value(rt, entries[i].data.data, entries[i].data.length));
    app_entries_free(entries, count);
    aqua_return(call, hash);
}
