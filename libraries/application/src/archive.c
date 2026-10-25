/* ZIP archives of named byte resources (Serialization.Archive / ReadArchive). Names are relative slash paths without
 * empty, "." or ".." components; reading checks entry counts, sizes, duplicates and CRC-32s. Ordinary stored and
 * deflated entries are supported; encrypted, multi-disk and ZIP64 archives fail explicitly. */
#include "application.h"
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <zlib.h>

bool app_archive_name_valid(const char *name, size_t length) {
    if (!length || name[0] == '/') return false;
    size_t start = 0;
    for (size_t i = 0; i <= length; i++) {
        if (i < length && (name[i] == '\\' || name[i] == ':' || name[i] == 0)) return false;
        if (i == length || name[i] == '/') {
            size_t n = i - start;
            if (n == 0 || (n == 1 && name[start] == '.') || (n == 2 && name[start] == '.' && name[start + 1] == '.')) return false;
            start = i + 1;
        }
    }
    return true;
}

void app_entries_free(app_entry *entries, size_t count) {
    for (size_t i = 0; i < count; i++) { free(entries[i].name); app_bytes_free(&entries[i].data); }
    free(entries);
}

/* The current local time in MS-DOS format, as ZIP writers stamp new entries. */
static void dos_time(uint16_t *time_out, uint16_t *date_out) {
    time_t now = time(NULL);
    struct tm parts;
#if defined(_WIN32)
    localtime_s(&parts, &now);
#else
    localtime_r(&now, &parts);
#endif
    int year = parts.tm_year + 1900;
    if (year < 1980) year = 1980;
    *time_out = (uint16_t)(parts.tm_hour << 11 | parts.tm_min << 5 | parts.tm_sec / 2);
    *date_out = (uint16_t)((year - 1980) << 9 | (parts.tm_mon + 1) << 5 | parts.tm_mday);
}

static bool raw_deflate(const uint8_t *data, size_t length, app_bytes *out) {
    memset(out, 0, sizeof *out);
    z_stream z;
    memset(&z, 0, sizeof z);
    if (deflateInit2(&z, 6, Z_DEFLATED, -15, 8, Z_DEFAULT_STRATEGY) != Z_OK) return false;
    if (!app_bytes_reserve(out, deflateBound(&z, (uLong)length) + 16)) { deflateEnd(&z); return false; }
    z.next_in = (Bytef *)data; z.avail_in = (uInt)length;
    z.next_out = out->data; z.avail_out = (uInt)out->capacity;
    int status = deflate(&z, Z_FINISH);
    out->length = z.total_out;
    deflateEnd(&z);
    return status == Z_STREAM_END;
}

bool app_archive_create(const app_entry *entries, size_t count, app_bytes *out, app_error *e) {
    memset(out, 0, sizeof *out);
    if (count > APP_MAX_ENTRIES) return app_error_set(e, APP_LIMIT_EXCEEDED, "Too many archive entries.");
    uint64_t total = 0;
    for (size_t i = 0; i < count; i++) total += entries[i].data.length;
    if (!app_check_bytes(total, e)) return false;
    uint16_t time_value, date_value;
    dos_time(&time_value, &date_value);
    uint32_t *offsets = calloc(count ? count : 1, sizeof *offsets), *crcs = calloc(count ? count : 1, sizeof *crcs);
    uint32_t *sizes = calloc(count ? count : 1, sizeof *sizes);
    uint16_t *methods = calloc(count ? count : 1, sizeof *methods);
    bool ok = true;
    for (size_t i = 0; i < count && ok; i++) {
        const app_entry *entry = &entries[i];
        size_t name_length = strlen(entry->name);
        if (!app_archive_name_valid(entry->name, name_length)) { ok = app_error_set(e, APP_INVALID_DATA, "Invalid archive resource name."); break; }
        app_bytes packed;
        bool deflated = raw_deflate(entry->data.data, entry->data.length, &packed) && packed.length < entry->data.length;
        const uint8_t *payload = deflated ? packed.data : entry->data.data;
        size_t payload_length = deflated ? packed.length : entry->data.length;
        bool utf8 = false;
        for (size_t k = 0; k < name_length; k++) if ((uint8_t)entry->name[k] >= 0x80) utf8 = true;
        offsets[i] = (uint32_t)out->length;
        crcs[i] = app_crc32(0, entry->data.data, entry->data.length);
        sizes[i] = (uint32_t)payload_length;
        methods[i] = (uint16_t)(deflated ? 8 : 0) | (uint16_t)(utf8 ? 0x8000 : 0);
        uint8_t header[30];
        app_put32le(header, 0x04034b50);
        app_put16le(header + 4, deflated ? 20 : 10);
        app_put16le(header + 6, utf8 ? 0x0800 : 0);
        app_put16le(header + 8, deflated ? 8 : 0);
        app_put16le(header + 10, time_value);
        app_put16le(header + 12, date_value);
        app_put32le(header + 14, crcs[i]);
        app_put32le(header + 18, (uint32_t)payload_length);
        app_put32le(header + 22, (uint32_t)entry->data.length);
        app_put16le(header + 26, (uint32_t)name_length);
        app_put16le(header + 28, 0);
        app_bytes_append(out, header, sizeof header);
        app_bytes_append(out, entry->name, name_length);
        app_bytes_append(out, payload, payload_length);
        app_bytes_free(&packed);
        if (out->length > APP_MAX_BYTES) ok = app_check_bytes(out->length, e);
    }
    if (ok) {
        uint32_t directory = (uint32_t)out->length;
        for (size_t i = 0; i < count; i++) {
            const app_entry *entry = &entries[i];
            size_t name_length = strlen(entry->name);
            bool deflated = (methods[i] & 0xFF) == 8, utf8 = (methods[i] & 0x8000) != 0;
            uint8_t header[46];
            app_put32le(header, 0x02014b50);
            app_put16le(header + 4, 20);
            app_put16le(header + 6, deflated ? 20 : 10);
            app_put16le(header + 8, utf8 ? 0x0800 : 0);
            app_put16le(header + 10, deflated ? 8 : 0);
            app_put16le(header + 12, time_value);
            app_put16le(header + 14, date_value);
            app_put32le(header + 16, crcs[i]);
            app_put32le(header + 20, sizes[i]);
            app_put32le(header + 24, (uint32_t)entry->data.length);
            app_put16le(header + 28, (uint32_t)name_length);
            memset(header + 30, 0, 12);
            app_put32le(header + 42, offsets[i]);
            app_bytes_append(out, header, sizeof header);
            app_bytes_append(out, entry->name, name_length);
        }
        uint8_t end[22];
        app_put32le(end, 0x06054b50);
        app_put16le(end + 4, 0);
        app_put16le(end + 6, 0);
        app_put16le(end + 8, (uint32_t)count);
        app_put16le(end + 10, (uint32_t)count);
        app_put32le(end + 12, (uint32_t)out->length - directory);
        app_put32le(end + 16, directory);
        app_put16le(end + 20, 0);
        app_bytes_append(out, end, sizeof end);
        ok = app_check_bytes(out->length, e);
    }
    free(offsets); free(crcs); free(sizes); free(methods);
    if (!ok) app_bytes_free(out);
    return ok;
}

static bool corrupt(app_error *e, const char *message) { return app_error_set(e, APP_INVALID_DATA, "%s", message); }

bool app_archive_read(const uint8_t *data, size_t length, app_entry **entries_out, size_t *count_out, app_error *e) {
    *entries_out = NULL;
    *count_out = 0;
    if (!app_check_bytes(length, e)) return false;
    if (length < 22) return corrupt(e, "Number of entries expected in End Of Central Directory does not correspond to number of entries in Central Directory.");
    size_t end = SIZE_MAX;
    size_t lowest = length > 22 + 65535 ? length - 22 - 65535 : 0;
    for (size_t p = length - 22 + 1; p-- > lowest;) if (app_get32le(data + p) == 0x06054b50) { end = p; break; }
    if (end == SIZE_MAX) return corrupt(e, "End of Central Directory record could not be found.");
    uint32_t disk = app_get16le(data + end + 4), directory_disk = app_get16le(data + end + 6);
    uint32_t disk_entries = app_get16le(data + end + 8), entries = app_get16le(data + end + 10);
    uint32_t directory_size = app_get32le(data + end + 12), directory = app_get32le(data + end + 16);
    if (end >= 20 && app_get32le(data + end - 20) == 0x07064b50) return app_error_set(e, APP_UNSUPPORTED, "ZIP64 archives are not supported.");
    if (entries == 0xFFFF || directory == 0xFFFFFFFFu || directory_size == 0xFFFFFFFFu)
        return app_error_set(e, APP_UNSUPPORTED, "ZIP64 archives are not supported.");
    if (disk != 0 || directory_disk != 0 || disk_entries != entries) return app_error_set(e, APP_UNSUPPORTED, "Multipart archives are not supported.");
    if (entries > APP_MAX_ENTRIES) return app_error_set(e, APP_LIMIT_EXCEEDED, "Too many archive entries.");
    if ((uint64_t)directory + directory_size > end) return corrupt(e, "The archive's central directory is outside the archive.");
    app_entry *list = calloc(entries ? entries : 1, sizeof *list);
    size_t count = 0;
    uint64_t total = 0;
    size_t p = directory;
    bool ok = true;
    for (uint32_t i = 0; i < entries && ok; i++) {
        if (p + 46 > end || app_get32le(data + p) != 0x02014b50) { ok = corrupt(e, "Central Directory corrupt."); break; }
        uint32_t flags = app_get16le(data + p + 8), method = app_get16le(data + p + 10), crc = app_get32le(data + p + 16);
        uint32_t packed = app_get32le(data + p + 20), size = app_get32le(data + p + 24);
        uint32_t name_length = app_get16le(data + p + 28), extra = app_get16le(data + p + 30), comment = app_get16le(data + p + 32);
        uint32_t entry_disk = app_get16le(data + p + 34), local = app_get32le(data + p + 42);
        if (p + 46 + name_length + extra + comment > end) { ok = corrupt(e, "Central Directory corrupt."); break; }
        const char *name = (const char *)data + p + 46;
        p += 46 + name_length + extra + comment;
        if (packed == 0xFFFFFFFFu || size == 0xFFFFFFFFu || local == 0xFFFFFFFFu) { ok = app_error_set(e, APP_UNSUPPORTED, "ZIP64 archives are not supported."); break; }
        if (entry_disk != 0) { ok = app_error_set(e, APP_UNSUPPORTED, "Multipart archives are not supported."); break; }
        if (flags & 1) { ok = app_error_set(e, APP_UNSUPPORTED, "Encrypted archive entries are not supported."); break; }
        if (method != 0 && method != 8) { ok = app_error_set(e, APP_UNSUPPORTED, "The archive entry was compressed using an unsupported compression method."); break; }
        if (!app_archive_name_valid(name, name_length)) { ok = corrupt(e, "Invalid archive resource name."); break; }
        total += size;
        if (!app_check_bytes(total, e)) { ok = false; break; }
        for (size_t k = 0; k < count; k++)
            if (strlen(list[k].name) == name_length && memcmp(list[k].name, name, name_length) == 0) { ok = corrupt(e, "Duplicate archive resource."); break; }
        if (!ok) break;
        if ((uint64_t)local + 30 > end || app_get32le(data + local) != 0x04034b50) { ok = corrupt(e, "A local file header is corrupt."); break; }
        uint64_t start = (uint64_t)local + 30 + app_get16le(data + local + 26) + app_get16le(data + local + 28);
        if (start + packed > end) { ok = corrupt(e, "A local file header is corrupt."); break; }
        app_entry *entry = &list[count];
        entry->name = app_strndup(name, name_length);
        if (method == 0) {
            if (packed != size) { ok = corrupt(e, "Invalid archive checksum or size."); free(entry->name); break; }
            app_bytes_append(&entry->data, data + start, size);
        } else {
            app_error inner = {0};
            if (!app_decompress(data + start, packed, APP_DEFLATE, size, &entry->data, &inner)) {
                free(entry->name);
                ok = inner.kind == APP_LIMIT_EXCEEDED ? corrupt(e, "Invalid archive checksum or size.") : corrupt(e, inner.message);
                break;
            }
        }
        count++;
        if (entry->data.length != size || app_crc32(0, entry->data.data, entry->data.length) != crc) { ok = corrupt(e, "Invalid archive checksum or size."); break; }
    }
    if (!ok) { app_entries_free(list, count); return false; }
    *entries_out = list;
    *count_out = count;
    return true;
}
