# Yoga (pinned)

Meta's Yoga layout engine, compiled into the Saturn library. Only the `yoga/` source folder and `LICENSE` of the
upstream repository are kept.

- Upstream: https://github.com/facebook/yoga, commit `0747590714df4544ad3124025190689ea038e398` (main, 2026-11-02),
  chosen over the last release (v3.2.1, December 2024) for CSS Grid, `max-content` / `fit-content` / `stretch` sizes and
  their fixes.
- Archive: `https://github.com/facebook/yoga/archive/0747590714df4544ad3124025190689ea038e398.tar.gz`,
  SHA-256 `1d1a952a97bba4ef1db9d9922b2957c8db307fcf5caf1e9aa0528b98ad2f150c`.
- License: MIT (`LICENSE`).
- Local change: `yoga/YGMacros.h` keeps an embedder's `YG_EXPORT` definition (`#ifdef YG_EXPORT` before the
  platform branches), so `CMakeLists.txt` can define it empty and keep Yoga's symbols private to the library. Upstream's
  `yoga/CMakeLists.txt` and `yoga/module.modulemap` are not copied.