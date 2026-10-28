# 陣列函式庫 / Array library

`陣列` and `Array` are built-in modules in Aquarius core. No dependency or import is
needed. `匯入("陣列")` / `import("Array")` also work on desktop, web and Android hosts.
All algorithms, allocation, validation and collection execute in the same core
Wasm binary. C# and JavaScript only transport callable tokens and values.
Both global names share one module; both imported names share one module per host.

`陣列` 與 `Array` 是核心內建模組，不需安裝或匯入。桌面、網頁與 Android 亦可透過
`匯入("陣列")` 使用。所有運算都在共用 Wasm 核心執行。

```aquarius
變數 資料 = 陣列.建立(0, 0, 1000);
陣列.追加(資料, 7);
陣列.追加陣列(資料, [8, 9]);
變數 備份 = 陣列.克隆(資料);
陣列.刪除(資料, 1, 3);
印出(資料, 備份); # [7] [7, 8, 9]
```

```aquarius
變數 values = Array.Make(0, 0, 1000);
Array.Append(values, 7);
Array.AppendArray(values, [8, 9]);
變數 backup = Array.Clone(values);
Array.Delete(values, 1, 3);
print(values, backup);
```

| 繁體中文 | English | Parameters / 參數 | Result / 行為 |
| --- | --- | --- | --- |
| 建立 | Make | length, value, optional capacity | New array filled with value; capacity >= length / 建立填好初值的陣列 |
| 長度 | Len | array | Element count / 元素數 |
| 容量 | Cap | array | Allocated element capacity / 已配置容量 |
| 擴充 | Grow | array, additional | Reserve length + additional, keep length / 預留額外空間 |
| 追加 | Append | array, value | Append one value in place, return array / 原地追加一個值 |
| 追加陣列 | AppendArray | array, source | Append all source values, including self / 批次追加，支援自身 |
| 複製 | Copy | destination, source, optional destinationStart, sourceStart, count | Copy the smaller available range, return count / 複製可容納的範圍，回傳數量 |
| 克隆 | Clone | array | Independent shallow copy / 獨立淺拷貝 |
| 切片 | Slice | array, start, end | Independent shallow copy of [start, end) / 半開區間的獨立淺拷貝 |
| 填入 | Fill | array, value | Fill in place, return array / 原地填入 |
| 刪除 | Delete | array, start, end | Remove [start, end), return array / 原地刪除半開區間 |
| 反轉 | Reverse | array | Reverse in place, return array / 原地反轉 |
| 尋找 | Index | array, value | First matching index or -1 / 第一個相符索引，找不到為 -1 |
| 包含 | Contains | array, value | Boolean membership / 是否包含 |
| 排序 | Sort | array, optional compare | Sort in place, return array; numbers ascending (NaN last) or strings by code unit, or by `compare(x, y)` (negative: x first); stable / 原地排序 |
| 總和 | Sum | array | Sum with the rules of `+`, left to right; 0 for `[]` / 依 `+` 規則加總 |
| 最小值 | Min | array | First smallest number or string; null for `[]` / 最小元素 |
| 最大值 | Max | array | First largest number or string; null for `[]` / 最大元素 |
| 映射 | Map | array, function | New array of `function(element)` / 新陣列 |
| 篩選 | Filter | array, function | New array of the elements for which `function` returns a true value / 篩選後的新陣列 |
| 歸約 | Reduce | array, function, initial | `function(accumulator, element)` from left to right / 由左至右累積 |

The design takes inspiration from Go's [slices package](https://pkg.go.dev/slices)
and [length/capacity model](https://go.dev/blog/slices-intro). Length and capacity
are separate; capacity grows geometrically and may exceed the requested amount.
`Grow(a,n)` guarantees capacity >= `Len(a)+n`. `Copy` handles overlapping ranges
like `memmove`, never changes length, and defaults both offsets to zero. Omitted
count means all available elements; an explicit count is an upper bound.

設計參考 Go 的長度、容量與半開區間概念。容量可能大於要求值；`擴充(a,n)` 保證
容量至少為 `長度(a)+n`。`複製` 支援重疊範圍，兩個起點預設為零，不改變長度；
省略數量時複製所有可容納元素，指定數量則作為上限。

Aquarius arrays have reference identity: mutating operations also update aliases,
including host-retained arrays. This differs from Go slice headers: `Append` and
`Delete` update the same Aquarius array object, so assigning the returned array is
optional. `Slice` copies instead of returning a shared Go-style view. Clone, Slice
and Make's repeated fill value are shallow: nested arrays, hashes and functions
remain shared. Search uses Aquarius `==` (mixed numeric types compare by value,
NaN does not equal itself, objects compare by identity).

Aquarius 陣列保有物件身分；原地操作也會更新所有別名及主機保留的陣列。
`追加` 與 `刪除` 不需重新指派回傳值。`切片` 會複製，不會產生 Go 的共享視圖。
淺拷貝仍共享巢狀物件；`建立` 使用物件初值時，每個元素都參照同一物件。
搜尋使用語言的 `==` 規則。

Existing `加入` / `push` still returns a new array and keeps the input unchanged.
The runtime detaches shared storage before mutation and preserves reserved capacity.
Collection now recomputes storage owners and the append frontier from live arrays,
allowing a surviving prefix to reuse space after longer snapshots die.

既有 `加入` / `push` 仍產生新陣列，不改變來源。核心在寫入前分離共享儲存，保留
預配置容量。垃圾回收會重算存活擁有者與追加位置，移除已失效快照造成的額外複製。

Sizes, capacity, offsets and range endpoints require nonnegative Aquarius integers.
Reversed or out-of-bounds ranges, invalid types/arity and arithmetic overflow return
language errors before mutation. The Wasm storage limit is 33,554,431 elements per
array; available memory can impose a smaller allocation limit. No platform APIs
or additional native dependencies are used.

尺寸、容量與索引必須為非負整數。範圍、型別、參數數量與溢位錯誤會在修改前回報。
Wasm 單一陣列上限為 33,554,431 個元素，實際可配置大小亦受可用記憶體限制。

Make/Fill/Clone/Slice/Copy/AppendArray are O(n), Reverse/Delete/Index/Contains are
O(n), Len/Cap are O(1), and Append is amortized O(1) when storage is unshared.
Detaching a live push snapshot costs O(length). Grow copies only when needed.
For fixed sizes, prefer Make plus indexed writes. For unknown sizes, Make with a
capacity or Grow plus Append avoids a new array header for every element.

固定長度資料優先使用 `建立` 後直接寫入索引；長度未知時使用預留容量加上 `追加`。
`追加` 在無共享儲存時具有均攤 O(1) 成本，可避免逐次建立新的陣列物件。

Examples: [繁體中文](../examples/arrays/traditional_chinese.aqua),
[English](../examples/arrays/english.aqua). Shared correctness corpus:
`tests/array-library.json`, executed by both Wasmtime and JavaScript WebAssembly tests.

## 型別陣列 / Typed arrays

An annotation such as `變數 旗標: 布林[] = 陣列.建立(n, 真);` or `var a: int[] = Array.Make(n, 0);` checks that every
element has that exact type (`整數[]`/`int[]`, `浮點數[]`/`float[]`, `雙精度[]`/`double[]`, `布林[]`/`bool[]`,
`字串[]`/`string[]`) and then fixes the array's element type: the same array object, so every alias sees the same
contents, but writing another type through any alias (`a[i] = 2.5d`, `Array.Append`, `Array.Fill`, `Array.Copy`,
`floodFill`) is an error. Integers written into a `float[]` or `double[]` (and floats into a `double[]`) widen, like a
typed variable. Reading or writing a typed array outside its bounds is an error instead of `null`. Use `0.0d` (not `0`)
to make a `double[]`. Elements take compound assignments and `++` (`a[i] += 2`, `計數[k]++`): the element keeps its type,
and fully typed functions run them natively for number elements. Fully typed
functions read and write typed arrays natively (see [execution tiers](execution-tiers.md)); arrays copied by `push`,
`Array.Clone` or `Array.Slice` start untyped. Native libraries that write an array's values directly are not checked.

標註 `整數[]`、`浮點數[]`、`雙精度[]`、`布林[]`、`字串[]` 會檢查每個元素的型別，之後該陣列（含所有別名）只能寫入相同型別
（整數寫入浮點數或雙精度陣列時自動轉型）；型別陣列越界讀寫會回報錯誤，不再回傳 `null`。元素也能使用複合指派與 `++`。

`陣列` / `array` (no brackets) annotates an array whose elements may be any values, such as a tree of nested arrays:
`變數 樹: 陣列 = [[0, 0], [0, 0]];`. It checks only that the value is an array and fixes nothing; every typed array is
one, and an `陣列` value given to a typed-array annotation is checked when the program runs. It reads like an untyped
array (`null` outside its range). Fully typed functions with `陣列` parameters or results run natively: they build array
literals natively and pass elements on to `陣列` parameters after checking that each is an array.

`陣列`（不加中括號）標註元素可為任何值的陣列（例如巢狀陣列組成的樹），只檢查值是陣列；越界讀取回傳 `null`。
參數與回傳值都有標註的函式即使使用 `陣列` 也能以原生層執行。

## 核心演算法 / Kernels

`排序`, `總和`, `最小值` and `最大值` run natively in the guest runtime (`runtime/guest/arrays.h`): integer arrays sort with an
introsort, other numbers and strings with a stable merge sort, so sorting 1M integers takes about 30 ms instead of about
280 ms for a quicksort written in Aquarius. `映射`, `篩選`, `歸約` and `排序` with a comparison call Aquarius functions, so
after checking their arguments they continue in the guest prelude (`runtime/guest/prelude.aqua`), Aquarius functions the
compiler adds to programs that name them: they yield, resume and report errors exactly like a loop in the program.
Mixing numbers and strings, or other values, needs a comparison function.

`排序`、`總和`、`最小值`、`最大值` 在客體執行環境中以原生程式執行（整數用 introsort，其他數字與字串用穩定的合併排序）；
`映射`、`篩選`、`歸約` 與帶比較函式的 `排序` 會呼叫星泉函式，因此在檢查引數後於客體前導程式（`runtime/guest/prelude.aqua`）中執行。
