# miniaudio 0.11.25

Unmodified upstream single-header library and its Public Domain / MIT license.
Source: https://github.com/mackron/miniaudio/tree/0.11.25

`miniaudio.h` SHA-256 (upstream LF bytes):
`ac7af4de748b7e26b777f37e01cee313a308a7296a3eb080e2906b320cc55c89`

The header uses LF checkout rules so the pinned build fingerprint is stable on
Windows. `scripts/build-audio-runtime.ps1` verifies it before compilation.
Decoder, mixer and sound processing are built into the memory-only core Wasm.
`native/audio_output.c` builds device I/O and the PCM ring only, with no decoder,
engine or generated-signal implementation. Neither build adds package dependencies.
