# 型別宣告 / Type declarations

Three versions of the same tour of optional `name: type` annotations: [English](english.aqua),
[繁體中文](traditional_chinese.aqua) and [mixed](mixed.aqua) (both syntaxes in one file). Each covers:

1. Scalar types `整數`/`int`, `浮點數`/`float`, `雙精度`/`double`, `布林`/`bool`, `字串`/`string`, widening, explicit
   narrowing (`整數(x)` / `int(x)`) and compound assignments that keep a variable's type.
2. Typed functions: parameter types and a return type after the parameter list (`函式(n: 整數): 整數`).
3. Typed arrays (`整數[]`, `雙精度[]`, `布林[]`, `字串[]`) and `陣列`/`array`, an array of any values; elements take
   compound assignments and `++`.
4. Structs (`結構` / `struct`) with typed fields, named and positional construction, field updates, struct-typed
   parameters, `空值`/`null` and a self-referencing linked list walked until `null`.
5. Checks: a definite mismatch is rejected at compile time; a value from untyped code is checked at run time (the
   failing lines are commented out so the examples finish).

```sh
aqua build english.aqua -o types-en.wasm
aqua run types-en.wasm
aqua build traditional_chinese.aqua -o types-zh.wasm
aqua run types-zh.wasm
aqua build mixed.aqua -o types-mixed.wasm
aqua run types-mixed.wasm
```

所有標註都是選用的，有標註與沒標註的程式碼可以混用。完整的規則見 [語言核心](../../compiler/AquariusCompiler/LANGUAGE.md)
與 [結構](../../docs/structs.md)。
