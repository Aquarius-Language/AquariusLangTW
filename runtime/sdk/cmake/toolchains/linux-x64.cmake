# Linux x64: the pinned Clang with the system's glibc headers and libraries, LLVM's libc++ and lld. The C++ runtime
# links statically so the runtime needs only glibc, libm, libdl, libpthread and the window system's libraries.
set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR x86_64)
include("${CMAKE_CURRENT_LIST_DIR}/AquariusToolchain.cmake")
set(AQUA_TARGET linux-x64)
set(CMAKE_C_COMPILER "${AQUA_LLVM}/bin/clang")
set(CMAKE_CXX_COMPILER "${AQUA_LLVM}/bin/clang++")
set(CMAKE_AR "${AQUA_LLVM}/bin/llvm-ar" CACHE FILEPATH "")
set(CMAKE_RANLIB "${AQUA_LLVM}/bin/llvm-ranlib" CACHE FILEPATH "")
set(CMAKE_C_COMPILER_TARGET x86_64-unknown-linux-gnu)
set(CMAKE_CXX_COMPILER_TARGET x86_64-unknown-linux-gnu)
set(CMAKE_CXX_FLAGS_INIT "-stdlib=libc++")
# libc++.a leaves exception/RTTI support in libc++abi.a; external C++ libraries must carry both.
set(CMAKE_CXX_STANDARD_LIBRARIES_INIT "-Wl,-Bstatic -Wl,--start-group -lc++ -lc++abi -Wl,--end-group -Wl,-Bdynamic")
set(CMAKE_EXE_LINKER_FLAGS_INIT "-fuse-ld=lld -static-libstdc++ -Wl,--export-dynamic")
set(CMAKE_SHARED_LINKER_FLAGS_INIT "-fuse-ld=lld -static-libstdc++ -Wl,--exclude-libs,ALL")
