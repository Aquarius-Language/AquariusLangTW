# WebFrame / 網頁框架: embedded pages (iframes) in a web application's page. The OS part is src/platform/<os>.c, where
# <os> is the target's platform AQUA_OS: the web places iframes through web-glue/frame, other targets (src/platform/none.c) report
# that they have no page to embed into. Builds inside the runtime (a default library) or on its own as an external one.
cmake_minimum_required(VERSION 3.20)
project(aqua_webframe C)
include(AquariusLibrary)

set(webframe_platform src/platform/none.c)
if(AQUA_OS STREQUAL "web")
    set(webframe_platform src/platform/web.c)
endif()
aqua_add_library(webframe SOURCES src/webframe.c ${webframe_platform})
