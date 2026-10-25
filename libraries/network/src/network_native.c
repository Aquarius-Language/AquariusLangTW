/* The Network library for targets with sockets as one translation unit: every function is static, so the entry point
 * (aqua_library_network) is the only symbol the runtime and other libraries can see. Each file stays a unit of its
 * own; this list is the whole library. */
#include "native/native.h"
#include "network.c"
#include "values.c"
#include "url.c"
#include "client.c"
#include "websocket.c"
#include "native/hub.c"
#include "native/socket.c"
#include "native/tls.c"
#include "native/http.c"
#include "native/inflate.c"
#include "native/cookies.c"
#include "native/transfer.c"
#include "native/frames.c"
#include "native/streams.c"
#include "native/server.c"
#if defined(AQUA_OS_WINDOWS)
#include "platform/windows.c"
#elif defined(AQUA_OS_ANDROID)
#include "platform/posix.c"
#include "platform/android.c"
#else
#include "platform/posix.c"
#include "platform/linux.c"
#endif
