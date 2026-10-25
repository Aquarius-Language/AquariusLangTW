/* The five codecs behind Images (images.c dispatches by signature). Each one inspects every frame or page within the
 * limits, decodes one explicitly selected frame to straight top-down RGBA8 with its metadata, and encodes an RGBA8
 * picture whose alpha images.c already prepared for the target format. */
#ifndef APP_CODECS_H
#define APP_CODECS_H

#include "../application.h"

#define APP_CODEC(name) \
    bool app_##name##_inspect(const uint8_t *data, size_t length, app_image_info *info, app_error *e); \
    bool app_##name##_decode(const uint8_t *data, size_t length, int32_t frame, app_picture *out, app_error *e); \
    bool app_##name##_encode(const app_picture *picture, const uint8_t *rgba, const app_encode_options *options, app_bytes *out, app_error *e);
APP_CODEC(png)
APP_CODEC(jpeg)
APP_CODEC(bmp)
APP_CODEC(gif)
APP_CODEC(tiff)
#undef APP_CODEC

/* "{ColorType}, {depth} bits/channel", the source pixel format text of the old metadata. */
void app_source_format(app_metadata *metadata, const char *color_type, int depth);
/* EXIF orientation (tag 274) from a TIFF-structured EXIF block ("II*\0" or "MM\0*"), or 1. */
int32_t app_exif_orientation(const uint8_t *tiff, size_t length);
/* An EXIF block holding only the orientation: "II*\0", one IFD entry (274, SHORT). `header` prefixes "Exif\0\0". */
void app_exif_block(app_bytes *out, int32_t orientation, bool header);
bool app_frame_out_of_range(int32_t frame, int32_t frames, app_error *e);

#endif
