# miniaudio 0.11.25

Unmodified upstream single-header library (with its built-in WAV, FLAC and MP3 decoders) and its license: a choice of
Public Domain (Unlicense) or MIT No Attribution, see `LICENSE`.

- Source: https://github.com/mackron/miniaudio/tree/0.11.25 (`miniaudio.h` and `LICENSE` at that tag)
- `miniaudio.h` SHA-256 (upstream LF bytes): `ac7af4de748b7e26b777f37e01cee313a308a7296a3eb080e2906b320cc55c89`,
  checked by `../../CMakeLists.txt` at configure time; `../../.gitattributes` keeps its LF line endings.

`src/engine.c` is the only file that compiles it, with `MA_API` defined as `static` (and its two other global
definitions renamed with an `audio_` prefix), so no miniaudio symbol leaves the library.
