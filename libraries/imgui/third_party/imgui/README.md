# Dear ImGui (pinned)

Dear ImGui compiled into the ImGui library. Only the core sources, `imconfig.h`, the stb headers it bundles and
`LICENSE.txt` of the upstream repository are kept; backends, examples, docs and `misc/` are not.

- Upstream: https://github.com/ocornut/imgui, release `v1.93.0-docking` (the docking branch's tag of 1.93.0).
- Archive: `https://github.com/ocornut/imgui/archive/refs/tags/v1.93.0-docking.tar.gz`,
  SHA-256 `746c265540bd3aa109570f29492ef4d8f5b421dd9fd0049c35ca35f21496f1a5`.
- License: MIT (`LICENSE.txt`); the bundled stb_truetype, stb_rect_pack and stb_textedit are MIT or public domain.
- No local changes: the library's configuration is `src/aqua_imconfig.h`, selected with `IMGUI_USER_CONFIG`.