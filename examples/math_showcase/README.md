# 數學展示 / Math showcase

`main.aqua` shows the number features of the language in one program: number literals (`1.5`, `1e-9`, `0xFF`,
`0b1010`, `1_000_000`, `2d`), the portable `數學` / `Math` module (constants, type-keeping functions, rounding halves
away from zero, IEEE results, musl's transcendental functions), the exact float `%`, integer bitwise operators, a
typed FNV-1a hash and Simpson integration that run in the native tier, seeded PCG32 random numbers, and number text
(`印出` and `字串.轉文字` print the same; `字串.解析數字` reads it back exactly).

```powershell
aqua run examples/math_showcase/main.aqua
```

`expected-output.txt` is its complete output, identical on Windows, Linux and the web (and Android: the program runs
in the same guest runtime there). See [numbers and the Math module](../../docs/math.md).

本範例展示數字字面值、可攜的 `數學` 模組（常數、保持型別的函式、遠離零的四捨五入、IEEE 結果）、精確的浮點餘數、整數位元
運算、在原生層執行的雜湊與積分、有種子的亂數，以及 `印出` 與 `字串.轉文字` 一致的數字文字；每個平台的輸出完全相同。
