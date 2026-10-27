# 結構 / Structs

A struct type is a named record with a fixed set of fields. Declare it with `結構` / `struct`, construct values by
calling the type with named or positional arguments, and read or write fields with `.`:

```aquarius
結構 節點 { 左: 節點; 右: 節點; 值: 整數 }
變數 樹 = 節點(左: 節點(值: 1), 值: 2);
印出(樹.左.值, 樹);                   # 1 節點{左: 節點{左: null, 右: null, 值: 1}, 右: null, 值: 2}

struct Point { x: double, y: double, label }
var p = Point(3, 4);                  # positional: the fields in declaration order
p.label = "corner";
p.x += 0.5d;
var length2 = function(q: Point): double { return q.x * q.x + q.y * q.y; };
```

結構型別是欄位固定的具名紀錄。以 `結構` / `struct` 宣告，以具名或依序的引數呼叫型別來建立值，以 `.` 讀寫欄位。

## Fields / 欄位

- Fields are separated by `;` or `,`. A field may be annotated with any type: `整數`, `字串[]`, `陣列`, or another (or
  the same) struct type; unannotated fields hold any value.
- A field left out of a construction is `null`. Fields annotated with a number, Boolean, string or array type cannot
  be `null`, so they must be given; struct-typed and unannotated fields may stay `null`.
- Every write is checked against the field's annotation (integers widen to float and double, like variables); a
  compound assignment (`+=`, `*=`, ...) keeps the field's numeric type.
- Reading an unknown field, or any field of `null`, is an error (`Cannot access member of NULL`).

欄位以 `;` 或 `,` 分隔，可標註任何型別（含結構本身），未標註的欄位可存任何值。建立時省略的欄位為 `null`；標註為數字、
布林、字串或陣列的欄位不能是 `null`，所以必須給值。每次寫入都會依標註檢查；讀取不存在的欄位或 `null` 的欄位是錯誤。

## Values / 值

- Structs are reference values like arrays and hashes: assignment and calls share the same struct, and `==` compares
  identity. They cannot be hash keys.
- `型別(值)` / `typeOf(value)` returns the struct's declared name (`節點`); a struct type itself is a value of type `type`
  that can be stored, passed and called (`var make = Point; make(1, 2)`).
- `印出` / `print` shows `名稱{欄位: 值, ...}`; a struct type prints as `結構 名稱`.
- Struct types declared in a module are its exports: `var shapes = import("shapes.aqua"); shapes.Point(x: 1, y: 2)`.
- Structs cross threads by copy (their type is kept), become JSON objects in `Serialization`, and read like
  string-keyed hashes in native libraries (`aqua_hash_count` / `aqua_hash_entry`, value type `AQUA_STRUCT`).
- `結構` and `struct` are keywords only where a declaration starts, so existing programs using them as names still work.

結構和陣列、雜湊表一樣是參考值：指派與呼叫共用同一個結構，`==` 比較身分，不能作為雜湊表的鍵。`型別(值)` 回傳結構名稱；
結構型別本身也是值（型別為 `type`），可儲存、傳遞與呼叫。模組中宣告的結構型別會隨模組匯出；結構在執行緒間以複本傳遞並保留型別，
在 `Serialization` 中成為 JSON 物件，原生函式庫則以字串鍵雜湊表的方式讀取。

## Annotations and performance / 標註與效能

A struct name is a type annotation like `整數`: `變數 根: 節點 = ...`, `函式(t: 節點): 整數 { ... }`. The compiler rejects
definite mistakes in annotated code (an unknown field, a mistyped or missing field, the wrong struct type) and checks
values that come from untyped code when the program runs. Fully typed functions that use structs run in the native
tier: fields are read and written in place at fixed offsets, constructions allocate directly, and struct-typed
parameters are passed as references after one type check. A typed binary tree built from structs runs about 35%
faster than the same tree built from two-element `陣列` arrays.

結構名稱可作為型別標註。編譯器會拒絕標註程式碼中可確定的錯誤（不存在的欄位、型別錯誤或缺少的欄位、錯誤的結構型別），
來自未標註程式碼的值則在執行時檢查。使用結構的完整型別函式會以原生層執行：欄位以固定位移就地讀寫，建立時直接配置，
因此以結構建立的型別二元樹比以兩元素 `陣列` 建立的快約 35%。
