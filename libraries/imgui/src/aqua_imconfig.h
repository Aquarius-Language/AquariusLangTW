/* Dear ImGui build configuration for AquariusLang (IMGUI_USER_CONFIG, see CMakeLists.txt). Assertions become errors of
 * the call that triggered them (context.cpp) instead of aborting the runtime, and ImGui never reaches the OS itself:
 * files, clipboard, IME and shell access go through the binding. */
#pragma once

void aqua_imgui_assert(const char *expression, const char *file, int line);
#define IM_ASSERT(_EXPR) do { if (!(_EXPR)) aqua_imgui_assert(#_EXPR, __FILE__, __LINE__); } while (0)

#define IMGUI_DISABLE_OBSOLETE_FUNCTIONS
#define IMGUI_DISABLE_FILE_FUNCTIONS
#define IMGUI_DISABLE_DEFAULT_SHELL_FUNCTIONS
#define IMGUI_DISABLE_WIN32_DEFAULT_CLIPBOARD_FUNCTIONS
#define IMGUI_DISABLE_WIN32_DEFAULT_IME_FUNCTIONS
#define IMGUI_DISABLE_OSX_FUNCTIONS
