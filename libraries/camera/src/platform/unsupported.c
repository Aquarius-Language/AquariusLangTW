/* Targets without a camera layer yet (new operating systems start here): both modules report themselves unsupported. */
#include "../camera.h"
#if !defined(CAMERA_WEBCAM) && !defined(CAMERA_MOBILE)
bool camera_platform_supported(void) { return false; }
void camera_platform_list(camera_listing *l) { camera_listed(l); }
void camera_platform_open(camera_session *s) { camera_fail(s, CAMERA_UNSUPPORTED, "此環境未提供攝影機介面。"); }
void camera_platform_request(camera_session *s) { (void)s; }
bool camera_platform_live(camera_session *s) { (void)s; return true; }
void camera_platform_close(camera_session *s) { (void)s; }
#endif
