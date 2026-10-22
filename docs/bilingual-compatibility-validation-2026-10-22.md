# Bilingual syntax compatibility — 2026-10-22

The current bilingual compiler was checked against the repository examples and
all locally available Aquarius example applications, libraries and benchmarks.

## Source and project coverage

- **439 `.aqua` source files across 33 current-language repositories compiled**
  through parsing, compiler lowering, Wasm emission and application metadata loading.
  Strings and comments were tokenized as text, including embedded WGSL and Python.
- **42 executable TOML projects built successfully**, including nested library
  examples/tests and all four ONNX showcases.
- The separate historical `AquariusLang` interpreted compiler repository was
  excluded from current-language compatibility totals. Its 14 legacy samples use
  its own `let` / `fn` dialect; their existing incompatibilities predate this change.

## Corrections

`Aqua_PyBridging/main.aqua` used the newly reserved `function` keyword as parameter
and local-variable names. All 23 identifier occurrences were renamed to
`callable`. A token-by-token comparison verified that other tokens, protocol keys,
strings and embedded code stayed identical. The public callable names are unchanged.

The library version is now **1.0.1**. Its integration-test project and the sibling
`Aqua_PyBridging_Showcase` reference that version; the showcase's local dependency
lock was refreshed. Both languages' dependency examples were updated. Existing
1.0.0 cache snapshots were preserved.

Two existing sample dependency pins were also corrected to their current local
library versions: `Aqua_Filament/tests/smoke` uses **1.0.2**, and
`Aqua_WebRTC/examples/basics` uses **1.0.1**. Existing WebRTC source changes were
preserved.

The raw WGPU window demo is interactive and waits for a close request. The example
test runner now closes its disposable test copy after two frames and checks the
reported frame count for both source and source-free Wasm execution.

## Execution results

| Check | Result |
| --- | --- |
| Repository examples: direct source and source-free Wasm | **175 / 175 passed** |
| Nine sibling showcases: rendering and built-in assertions | **17 / 17 passed** |
| Four ONNX showcases: real CPU inference | **4 / 4 passed** |
| OpenCV feature and bilingual checks | **2 / 2 passed**, including all 45 feature checks |
| Java bridge / FastCSV showcase | Passed |
| Python bridge public API integration | Passed, including callbacks, nesting, Unicode, large transfers, pandas and shutdown |
| Dreamer offline Markdown/layout regression | **289 assertions passed** |
| Kanban data and isolated desktop regressions | **70 + 21 assertions passed** |
| Dynamic linking showcase | Passed |

The nine rendered siblings were MarbleRun, Painter, Snake, PBR ModelViewer, Audio,
MediaPipe, PatchCore, Advanced PBR and Sponza. Snake's **236 rule assertions** passed.
Dreamer testing used offline fixtures. Kanban used its dedicated verification
storage namespace. Physical webcam capture remains opt-in and was skipped.

Thirteen initial project builds encountered stale dependency pins or an older
WebSocket package snapshot in the existing cache. After correcting the two pins,
these projects passed using disposable project copies and an isolated cache of
the current local library sources. Existing shared snapshots and project locks
were preserved, except for the intentionally refreshed Python showcase lock.
The results establish current-source compatibility; they do not claim that old
cached libraries or previously published compiler binaries were migrated.

Evidence is retained under `.web-build/bilingual-compatibility/`, including
`source-audit-before.json`, `source-audit-after.json`, `project-builds.json`,
`project-retries.json`, `extra-results.json` and individual logs. Repository and
classic showcase execution reports are `.web-build/desktop-example-results.json`
and `.web-build/projects/desktop-selected-results.json`.
