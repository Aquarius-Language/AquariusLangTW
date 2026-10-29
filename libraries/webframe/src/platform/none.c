/* Targets without a web page (desktop and mobile windows): nothing to embed frames into. */
#include "../webframe.h"

bool webframe_supported(void) { return false; }
int32_t webframe_create(const char *url) { (void)url; return 0; }
void webframe_place(int32_t frame, double x, double y, double width, double height) { (void)frame; (void)x; (void)y; (void)width; (void)height; }
void webframe_navigate(int32_t frame, const char *url) { (void)frame; (void)url; }
void webframe_reload(int32_t frame) { (void)frame; }
void webframe_show(int32_t frame, bool visible) { (void)frame; (void)visible; }
void webframe_remove(int32_t frame) { (void)frame; }
