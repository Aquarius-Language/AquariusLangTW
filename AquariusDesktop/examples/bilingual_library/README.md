# Bilingual library functions

Run `dotnet run --project AquariusDesktop -- AquariusDesktop/examples/bilingual_library/main.aqua` from the repository root.

`變數 加法, add = 函式(甲, 乙) { 甲 + 乙; };` defines one function with two public names.
Either order works. Keep a single name for Chinese-only or English-only functions.
Both names share captured state and can be passed as callbacks.

This example mixes script-library aliases with Chinese and English GLM and
Processing calls, and prints `BILINGUAL_LIBRARY_OK` followed by `真`.
See the [complete library naming reference](../../LIBRARY_NAMES.md).
