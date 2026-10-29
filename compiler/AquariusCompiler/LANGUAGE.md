# Language core and WebAssembly development

AquariusLang is a bilingual Traditional Chinese / English programming language.
The portable language implementation lives entirely in `AquariusCore`.
The desktop host and language server use the same frontend and object model.
The language server analyzes source without running it; the desktop host compiles
and executes scripts, imported modules and callbacks through WebAssembly.

## Source and execution pipeline

The lexer and parser construct an AST. `LoweringCompiler` produces compiler-only
IR, and `WasmCompiler` emits native Wasm functions, structured control flow,
versioned host imports and application metadata. The deployed artifact is `.wasm`;
Aquarius instructions and AST bodies are not interpreted at runtime.

`WasmRuntime` owns dynamic values, lexical environments and compiled continuations.
An `IWasmEngine` executes generated exports; the desktop adapter uses Wasmtime,
while browsers use native WebAssembly. Calls can suspend for browser host services,
and deep recursion uses explicit continuation frames. Native callbacks reenter with
independent execution state. Function objects retain parameters, captured environments
and inspection text, and reference compiled function indices instead of AST bodies.

`CompiledEvaluator` compiles source or AST input and preserves optional session
environments. Treat a cached AST as immutable. `CompiledRuntime` accepts Wasm or
compiler IR for tool integration; IR always goes through Wasm emission before execution.
See [the architecture](../../ARCHITECTURE.md) and [the application format](README.md#application-metadata-abi-4).

Closed scalar functions can additionally use register values and direct typed Wasm
calls, with guarded fallback for types, effects, fuel and recursion depth. Lexical
ancestor slots avoid name lookup while intervening scopes remain uncaptured. These
optimizations preserve the language behavior below; see
[portable numeric compilation](../../docs/portable-numeric-architecture.md).
Fully typed functions and loops over typed bindings compile to native Wasm (typed locals, direct calls, unboxed
typed array access) with the same results, errors and yield points; see
[execution tiers](../../docs/execution-tiers.md#native-tier-for-fully-typed-functions).

## Bilingual syntax

Traditional Chinese, English and mixed syntax are accepted in every `.aqua`
file, imported module and REPL session, with no language setting. Both spellings
map to the same token types, AST nodes and WebAssembly semantics.

| Traditional Chinese | English | Meaning |
| --- | --- | --- |
| `變數` | `var` | Mutable variable declaration |
| `函式` | `function` | Function expression / closure |
| `回傳` | `return` | Return a function result |
| `如果` | `if` | Conditional expression |
| `否則如果` | `else if` or `elif` | Additional conditional branch |
| `否則` | `else` | Fallback branch |
| `迴圈` | `for` | Loop |
| `中斷` | `break` | Exit the innermost loop |
| `繼續` | `continue` | Start the innermost loop's next iteration |
| `真` / `假` | `true` / `false` | Boolean literals |
| `空值` | `null` | The null literal |
| `非同步` / `非同步函式` | `async` | Async function: `非同步 函式(...) { ... }` returns a promise |
| `等待` | `await` | Wait for a promise inside an async function or at the top level |

```aquarius
var 加法, add = function(甲, b) { 回傳 甲 + b; };
變數 result = add(20, 22);
if (result < 0) { print("negative"); }
else 如果 (result == 42) { 印出("答案", result); }
否則 { print("other"); }
```

`else if` consists of two tokens; whitespace, line breaks and comments may
separate them. Either token can use its Chinese spelling (`否則 if`,
`else 如果` or `否則 如果`). Single-token `elif` and `否則如果` can occur
in the same branch chain. A final `else` / `否則` is optional.

Keywords are case-sensitive reserved words in both languages. They only match
whole identifiers: `returnValue`, `if成立` and `真值` remain valid names.
Code that previously used an English keyword as a variable, parameter or host
function name must rename it. Keywords inside strings and comments remain text.
`let` and `fn` remain ordinary identifiers; use `var` and `function` for English
syntax. Functions are declared as `var name = function(...) { ... };`.
`var` preserves Aquarius lexical scope and mutation rules; changing spelling
does not introduce JavaScript hoisting or change closure capture behavior.
User-defined names, module paths, property names and strings are never translated.
Default value inspection still prints booleans as `真` / `假` and null as `null`.

## Language behavior

- Each keyword accepts either spelling from the bilingual syntax table above.
- Arithmetic promotes mixed numeric operands to double, then float, then integer.
  Compound numeric assignments preserve the left variable's numeric type by
  converting the result back to that type. Prefix/postfix `++` also preserve it.
- `%` and `%=` compute remainders with the precedence of `*` and `/`. Integer
  remainders truncate toward zero like integer division; floating operands use
  floating remainders. Integer remainder by zero is a runtime error.
- Integers are 32-bit and identical on every target: `+`, `-`, `*`, negation and `++` wrap around (two's
  complement); integer `/` truncates toward zero and dividing by zero is a runtime error; the minimum integer
  divided by -1 wraps to itself. Converting a float or double to an integer (`整數(x)` / `int(x)`, or a compound
  assignment into an integer variable) truncates toward zero and saturates at the integer range; NaN gives 0.
  Float results are rounded to 32 bits after each operation.
- Optional annotations (`name: type` on declarations, parameters and after a parameter list) fix a binding's type:
  `整數`/`int`, `浮點數`/`float`, `雙精度`/`double`, `布林`/`bool`, `字串`/`string`, arrays such as `整數[]`, and
  `字串[]`/`string[]`, and `陣列`/`array`, an array whose elements may be any values (arrays of arrays, mixed elements;
  every typed array is one, and an `陣列` value passed to `整數[]` is checked when the program runs). Reading an `陣列`
  outside its range gives `null` like an untyped array; a typed array's out-of-range read is an error. A typed array
  keeps its element type: integers written to a `浮點數[]` or `雙精度[]` widen like a typed variable, any other
  mismatch is an error. `空值` / `null` fits only struct annotations (a definite `null` elsewhere is a compile error).
  Type names are recognized only after `:`. `TypeChecker` proves each write, rejects a definite mismatch at compile
  time, or lowers a `CheckType` (rt_checkType) where the value comes from untyped code; integers widen to float
  and double, floats to double, and narrowing needs `整數(x)`, `浮點數(x)` or `雙精度(x)`. Typed parameters are
  checked on entry, typed results at each return and at the implicit final value.
- `結構 名稱 { 欄位: 型別; 欄位 }` / `struct Name { field: type; field }` declares a struct type (`StructStatement`;
  `結構` and `struct` are keywords only where a declaration starts). Calling the type constructs a value, with named
  (`名稱(欄位: 值)`, a `CallNamed`) or positional arguments; fields left out are `null`, and annotated number, Boolean,
  string and array fields must be given. Struct names are annotations; the TypeChecker checks constructions, field
  reads and writes of annotated values, and lowers `CheckStruct` where values come from untyped code. Field writes
  (`SetMember`, `CompoundMember`) are checked against the field's annotation at run time. Structs are reference
  values compared by identity; `型別()` returns the declared name. See [structs](../../docs/structs.md).
- `++值` returns the updated number; `值++` returns the original number. The operand is a numeric variable, an array
  element or hash entry (`計數[鍵]++`, `IncrementIndex`) or a struct field (`點.x++`, `IncrementMember`); the value keeps
  its numeric type.
- Compound assignments (`+= -= *= /= %=`) work on variables, elements and hash entries (`表[i] += 1`, `CompoundIndex`)
  and fields. The target's receiver and index are evaluated once; an element keeps its numeric type like a variable.
  Indexing and postfix `++` bind tighter than `.` in the parser, which rebuilds `物件.清單[0]` as `(物件.清單)[0]`, so
  reads, writes and compound assignments through members work like on variables. Any other assignment target is a
  compile error.
- Boolean `&&` and `||` short-circuit: the right operand runs only when the left one does not decide the result
  (`f != null && f(x)` never calls a null `f`). An operand that runs must be a Boolean, or the operation is an error.
  Both have the same precedence and group from the left, so `a || b && c` is `(a || b) && c`.
- Functions support explicit `回傳` / `return`, a final expression as an implicit result,
  recursion and lexical closures. Assignments update the scope owning a variable.
- `變數 加法, add = 函式(甲, 乙) { 甲 + 乙; };` gives one function two public
  names, in either order. A single name continues to work. Two-name declarations
  require a function literal and distinct identifiers. Both bindings receive the
  same function object and captured environment; subsequent assignment to one
  binding follows ordinary variable semantics and does not rebind the other.
  Declared names and parameters shadow global builtins. Module member calls look
  up the receiver's exports, so aliases such as `vector.長度()` cannot accidentally
  call an unrelated global builtin.
- A loop's declared control variable stays local to the loop and carries its value
  into the next iteration. Body declarations get a fresh scope each iteration;
  closures can retain the environment from the iteration that created them. The next iteration starts before the
  update part runs, so a closure keeps its own iteration's value of the variable (0, 1, 2, ... for `i++`). Each of the
  three parts may be left out (`迴圈 (; 條件;)`, `迴圈 (;;)`); a loop without a condition runs until `中斷` / `break`.
  `繼續` / `continue` starts the innermost loop's next iteration (its update part runs, then the condition); outside
  a loop of the same function it is a compile error.
- A statement that starts with `如果` / `if` or `迴圈` / `for` ends at its closing brace, so a next statement starting
  with `(`, `[`, `-` or `++` stands alone. Inside an expression (`(如果 (c) { 1; } 否則 { 2; }) + 3`) both stay values.
- String literals escape only `\"` and `\\`; any other backslash stays as written (Windows paths, regular
  expressions). `-2147483648` is the smallest integer literal; other literals outside 32 bits are compile errors.
- Expressions nest at most 256 levels; deeper source gives one parser error.
- Arrays and hashes support indexed writes. Hash assignment replaces an existing
  typed key or adds a new one, preserving aliases and nested object identity.
  Invalid keys are rejected before evaluating the right-hand side.
  Hash keys use `IHashable` and `HashKey` values.
  Out-of-range array reads and missing hash entries return `NullObj`; invalid
  array writes return a language error.
- The built-in `陣列` / `Array` module provides capacity-aware creation and in-place
  array manipulation, and the kernels `排序` / `Sort`, `總和` / `Sum`, `最小值` / `Min`, `最大值` / `Max`,
  `映射` / `Map`, `篩選` / `Filter` and `歸約` / `Reduce`. `加入` / `push` still preserves the input; mutating library
  operations preserve Aquarius reference aliases. Kernels that call functions continue in the guest prelude
  (`runtime/guest/prelude.aqua`), which `WasmCompiler` adds to programs naming them. See [array semantics](../../docs/arrays.md).
- The built-in `雜湊` / `Hash` module enumerates and edits hashes (keys, values,
  entries, has, get with default, delete, clone, clear, merge), and `字串` / `String`
  provides portable text operations (`轉文字` / `From`, slicing, searching, splitting,
  joining, replacing, trimming, case, code points, integer parsing). `長度` / `len`
  also counts hash entries. See [hash and string libraries](../../docs/hash-and-string.md).
- Imported script modules expose their environment through `ModuleObj`.
  Module member calls evaluate arguments in the caller's environment and execute
  function bodies in their captured environment.
- Runtime failures produce `ErrorObj` and stop execution before later side effects.
  Compiler syntax failures raise `CompilationException`; `CompiledEvaluator` converts
  them to `ErrorObj` for hosts using its evaluation APIs.

See the repository [syntax and examples](../../README.md#語法) and the
[Starship example](../../examples/starship_expedition/README.md).

## Async functions

`非同步 函式(...) { ... }` / `async function(...) { ... }` (also `非同步函式(...)`) makes an async function: a call returns
a promise (`承諾` / `Promise`) at once and the body runs as its own task of the runtime's scheduler, starting inside
the call until it first waits. `等待 值` / `await value` suspends that task until a promise settles and gives its value,
or fails with its reason; any other value is the result itself. `等待` / `await` is valid inside async functions and at
the top level of the entry script; inside a synchronous function it is a compile error. Like `結構` / `struct`, the
four words are keywords only where they start an async function or an await, so programs using them as names keep
working (`執行緒.等待(工作)`). The parser represents both as calls (`AsyncFunctionExpression` of the builtin `async@`
with the literal, `AwaitExpression` of `await@`), so every pass over calls handles them; `runtime/host/promise.c`
implements the builtins and the Promise module on every target. See [async functions and promises](../../docs/async.md).

## Measuring elapsed time

`目前時間()` / `currentTime()` is a core global function available without imports
on desktop and browser hosts. It takes no arguments and returns a `double` reading
of a monotonic clock in milliseconds, including fractional milliseconds. Subtract
two readings taken in the same host execution to measure an interval:

```aquarius
變數 開始 = 目前時間();
變數 總和 = 0;
迴圈 (變數 i = 0; i < 10000; i++) { 總和 += i; }
變數 耗時毫秒 = 目前時間() - 開始;
印出("耗時（毫秒）：", 耗時毫秒);
```

Divide the difference by `1000.0d` for seconds. Both names refer to the same
function, and readings do not reset between calls, module imports or REPL entries.
The clock's origin is arbitrary and host-specific: readings are not Unix or
calendar timestamps and must not be compared across separate processes or pages.
The clock is unaffected by wall-clock corrections; precision depends on the host
(browsers may reduce timer precision).

`目前日期()` / `currentDate()` is a zero-argument core global returning the host's
local calendar date as `yyyy-MM-dd`. Use it for due dates and calendar comparisons;
use `目前時間()` for elapsed durations. It is available on desktop and browser hosts.

## Inspecting value types

`型別(值)` / `typeOf(value)` is a core global available on every host. Both names
return the same stable English tag: `integer`, `float`, `double`, `string`, `boolean`,
`null`, `array`, `hash`, `function`, `module` or `error`; a struct value gives its declared name and a struct
type gives `type`. Use it for JSON documents and
other dynamic data whose fields may hold different kinds of values:

```aquarius
變數 錯誤 = 回應.解析JSON()["error"];
如果 (型別(錯誤) == "hash") { 印出(錯誤["message"]); } 否則如果 (型別(錯誤) == "string") { 印出(錯誤); }
```

Builtin and compiled functions both report `function`. Missing hash entries and
out-of-range array reads produce `null`, so `型別(表["鍵"]) == "null"` tests absence
without a separate null literal.

## Extending the language

When introducing syntax, keep Chinese and English keyword spellings equivalent.
Update tokens, lexer rules, parser callbacks and operator
precedences, then the AST and `LoweringCompiler`. Add an opcode or `ValueOperations` behavior
when needed, and update language-server facts and editor highlighting/snippets.

Keep AST nodes as classes to preserve the current mutable node identity and
reference behavior. `HashKey` is a value type with value equality for dictionary
lookup; a replacement reference type must implement consistent equality and
hashing. Avoid relying on reference identity for equivalent hash keys.

Be careful about `NextToken` calls in parser branches, particularly loop parsing.
New prefix/infix operators must be registered at the appropriate precedence.
`List.Append` returns an enumerable; use `Add` to mutate an instruction or node list.

Verify frontend behavior and Wasm execution in `AquariusTests`. Include
compiler and runtime cases for changes affecting stack balance, jump targets,
scopes, errors or calls. Update the comprehensive Starship example when introducing
new language tokens. Run [the test suite](../AquariusTests/README.md).

## Host libraries and future ideas

Host functions use `BuiltinObj` and the `AquariusLang.runtime.Builtins` registry.
Desktop-only facilities, imports and graphics belong in `AquariusDesktop`;
keep the portable library free of native desktop dependencies. Hosts must dispose
`DesktopBuiltins` to release graphics resources. Native callbacks invoke Aquarius
functions through `CompiledEvaluator.Invoke`.

Host libraries can register either or both names with the portable API:

```csharp
var function = new BuiltinObj(args => new IntegerObj(42));
FunctionRegistration.Define(moduleEnvironment, function,
    traditionalChineseName: "答案", englishName: "answer");
FunctionRegistration.Define(moduleEnvironment, anotherFunction, englishName: "englishOnly");
builtins.DefineFunction(function, traditionalChineseName: "答案", englishName: "answer");
```

Omit a name with `null`. Empty names, keywords, duplicate names and collisions
with existing exports fail before any binding is added. The registry accepts
native `BuiltinObj` and compiled `FunctionObj` callables and preserves identity.
`LibraryCatalog` supplies metadata shared by the desktop host and LSP without
loading native libraries. Update `native/generate_library_catalog.py`, then run
it to regenerate the catalog and naming reference when changing library APIs.
OpenGL aliases retain the `gl` prefix and overload/type suffixes; singular and
plural entry points have distinct names. The appended `Duplicate` opcode binds
two names to one closure and is validated during `.aqua` loading. Existing
bilingual names share one compiled closure and remain identical across exported Wasm applications.

`FunctionRegistration.Replace` explicitly rebinds an existing alias group during
library reloads. It checks that all names refer to the same old function before
replacing them; Processing uses this when a closed sketch starts again.

Possible future work includes Git, cross-language interoperability, calculus
operators, desktop GUI libraries, PyTorch bindings and additional graphics/game
libraries such as Wicked Engine, sokol, Raylib, Magnum, SFML/SDL2 or MonoGame.
JIT/AOT, WASI, libgccjit or code generation through other languages remain
exploratory options. The compiler emits standard WebAssembly, with platform engines and versioned capability contracts. Future optimizers can specialize value operations while retaining the host ABI.
