# The web's native library outputs (docs/native-libraries.md): no per-library native code is built. The library is the
# application's .wasm itself; the host side module carries the embed runtime (embed.c, the table read from the module's
# aquarius.exports section and its description for the glue), and web-glue/core/library.mjs loads it with
# loadLibrary(). platform.cmake includes this file for these sources.
set(embed_web_sources embed/embed.c embed/table.c embed/web.c)
