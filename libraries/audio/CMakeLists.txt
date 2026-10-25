# The Audio|音訊 library. Builds inside the runtime (a default library) or on its own as an external library:
#   cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=<runtime/sdk/cmake/toolchains/<target>.cmake>
# miniaudio 0.11.25 is vendored in third_party/miniaudio and compiled privately into src/engine.c.
cmake_minimum_required(VERSION 3.20)
project(aqua_audio C)
include(AquariusLibrary)

set(miniaudio_header "${CMAKE_CURRENT_SOURCE_DIR}/third_party/miniaudio/miniaudio.h")
file(SHA256 "${miniaudio_header}" miniaudio_hash)
if(NOT miniaudio_hash STREQUAL "ac7af4de748b7e26b777f37e01cee313a308a7296a3eb080e2906b320cc55c89")
    message(FATAL_ERROR "${miniaudio_header} is not the pinned miniaudio 0.11.25 (upstream bytes, LF line endings).")
endif()

# Native targets play through miniaudio's device API on a pump thread (pthreads; winpthreads on Windows). miniaudio
# loads the platform's audio libraries (WASAPI, PulseAudio/ALSA, AAudio/OpenSL ES) at run time.
set(audio_libraries "")
if(WIN32)
    set(audio_libraries -l:libwinpthread.a) # static, so aqua_audio.dll needs no libwinpthread-1.dll
elseif(NOT AQUA_TARGET STREQUAL "web")
    set(audio_libraries ${CMAKE_DL_LIBS} m)
    if(NOT ANDROID)
        list(APPEND audio_libraries pthread)
    endif()
endif()

aqua_add_library(audio SOURCES src/audio.c src/engine.c WEB_SOURCES src/output_web.c LINK_LIBRARIES ${audio_libraries})
# Identical PCM on every target: no fused multiply-add contraction (miniaudio's SIMD paths are off in engine.c).
set_source_files_properties(src/engine.c PROPERTIES COMPILE_OPTIONS "-ffp-contract=off")
