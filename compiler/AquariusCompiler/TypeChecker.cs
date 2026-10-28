using System.Collections.Generic;
using System.Linq;
using AquariusLang.ast;
using AquariusLang.parser;
using AquariusLang.token;

namespace AquariusLang.Compiler;

/// <summary>A runtime annotation check the lowering emits after evaluating a node: the type code
/// (<see cref="TypeAnnotation.Code"/>) and the subject named in the error.</summary>
internal sealed record TypeCheck(int Code, string Subject);

/// <summary>A fully typed function literal (every parameter and the result annotated with a scalar or array type): a
/// candidate for the native tier. <see cref="Outer"/> holds the names its body resolves outside itself, with the
/// declaration they resolve to; names it does not resolve (core globals such as 整數 or 長度) are absent.</summary>
internal sealed class NativeSignature {
    internal sealed record OuterName(int Type, FunctionLiteral? Function, bool Assigned, bool Sibling, StructStatement? Struct = null, int Guess = 0);
    /// <summary>The struct types the program's annotations name, by code (shared by every signature of a program).</summary>
    internal IReadOnlyDictionary<int, StructStatement> Structs = new Dictionary<int, StructStatement>();
    internal readonly int[] Parameters;
    /// <summary>The result type; for a clone, the one the native tier is trying (see <see cref="Clone"/>).</summary>
    internal int Result;
    internal readonly object DefiningScope;
    internal readonly Dictionary<string, OuterName> Outer = new(System.StringComparer.Ordinal);
    /// <summary>A typed clone of an untyped function (no annotations at all): its parameters are their guesses (0 where a
    /// parameter has none), set when the whole program is checked, and the native tier infers its result type. Its entry
    /// takes only arguments of exactly those types; any other call runs the generic body.</summary>
    internal readonly bool Clone;
    internal NativeSignature(int[] parameters, int result, object definingScope, bool clone = false) { Parameters = parameters; Result = result; DefiningScope = definingScope; Clone = clone; }
}

/// <summary>
/// Checks optional type annotations before lowering. Every write to a typed binding is visible in the source (only plain
/// names can be assigned and modules cannot assign each other's bindings), so each one is either proven at compile
/// time, rejected with a diagnostic, or given a runtime check (<see cref="Checks"/>) when its value comes from untyped
/// code. Scopes follow the runtime's: the program, each function and each loop; names resolve to every declaration of
/// their scope regardless of order, because closures resolve names when they run.
/// </summary>
public sealed class TypeChecker {
    /// <summary>Static types: 0 is unknown (untyped), -1 is no value (void), otherwise a type code.</summary>
    internal const int Dynamic = 0, Void = -1, Int = 1, Float = 2, Double = 3, Bool = 4, String = 5, ArrayFlag = 16, StructFlag = 32;
    internal static bool IsStruct(int code) => code > 0 && (code & StructFlag) != 0;
    /// <summary>The names of struct type codes seen by any check (for messages).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> structNames = new();
    /// <summary>The declaration of each struct type code this program uses.</summary>
    internal readonly Dictionary<int, StructStatement> Structs = new();

    private sealed class Declaration {
        internal TypeAnnotation? Type;
        internal FunctionLiteral? Function;
        internal StructStatement? Struct;
        internal int Count;
        internal bool Assigned;
        /// <summary>An untyped binding's likely type, a guess the native tier may speculate on behind a tag check: the
        /// static type of every value its declaration and plain assignments give it when they agree (0 when none is
        /// known, Conflict when they disagree).</summary>
        internal int Guess;
        internal const int Conflict = -2;
        internal void Observe(int value) {
            if (value == Conflict) Guess = Conflict;
            // Arrays of different element types are arrays of any values.
            else if (value > 0 && Guess != Conflict) Guess = Guess == 0 || Guess == value ? value : IsArrayGuess(Guess) && IsArrayGuess(value) ? ArrayFlag : Conflict;
        }
        private static bool IsArrayGuess(int type) => type > 0 && type < StructFlag && (type & ArrayFlag) != 0;
    }
    private sealed class Scope {
        internal readonly Scope? Outer;
        internal readonly Dictionary<string, Declaration> Names = new(System.StringComparer.Ordinal);
        internal Scope(Scope? outer) { Outer = outer; }
    }

    internal readonly Dictionary<INode, TypeCheck> Checks = new(ReferenceEqualityComparer.Instance);
    internal readonly Dictionary<FunctionLiteral, NativeSignature> Natives = new(ReferenceEqualityComparer.Instance);
    /// <summary>Every loop as a candidate native region of untyped code (no parameters, no result).</summary>
    internal readonly Dictionary<ForLoopLiteral, NativeSignature> Loops = new(ReferenceEqualityComparer.Instance);
    // Each function and loop being checked: its own scope and the outer names its body uses.
    private readonly Stack<(Scope Own, NativeSignature? Native, Dictionary<string, (Declaration, Scope)> Uses)> functions = new();
    private readonly List<(NativeSignature Native, Dictionary<string, (Declaration, Scope)> Uses)> pending = new();
    private readonly List<ParseError> errors = new();
    // Guesses for untyped parameters: the static argument types of every direct call, merged like Declaration.Observe.
    private readonly Dictionary<FunctionLiteral, Declaration[]> parameterDeclarations = new(ReferenceEqualityComparer.Instance);
    // Guesses for the results of untyped functions: the values of their return statements, merged the same way.
    private readonly Dictionary<FunctionLiteral, Declaration> resultDeclarations = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<Declaration?> results = new();
    private readonly List<(FunctionLiteral Function, int Index, int Type)> arguments = new();
    // Guesses that copy another untyped variable's guess (or its array's element type), known only once every
    // parameter's guess is: a target declaration or a parameter (function, index), the source and whether it is an element.
    private readonly List<(Declaration? Target, FunctionLiteral? Function, int Index, Declaration Source, bool Element)> derived = new();
    private readonly Stack<TypeAnnotation?> returns = new();
    private Scope scope = new(null);

    public IReadOnlyList<ParseError> Errors => errors;

    /// <summary>Checks a whole program. Errors are collected in <see cref="Errors"/>.</summary>
    public static TypeChecker Check(AbstractSyntaxTree tree) {
        var checker = new TypeChecker();
        checker.Declare(tree.Statements);
        foreach (var statement in tree.Statements) checker.Statement(statement);
        // Assignments and calls anywhere in the program are known only now.
        foreach (var (function, index, type) in checker.arguments)
            if (checker.parameterDeclarations.TryGetValue(function, out var declared) && index < declared.Length) declared[index].Observe(type);
        // Derived guesses until none changes (a guess only goes from none to a type to a conflict).
        for (bool changed = true; changed;) {
            changed = false;
            foreach (var (target, function, index, source, element) in checker.derived) {
                var into = target ?? (checker.parameterDeclarations.TryGetValue(function!, out var parameters) && index < parameters.Length ? parameters[index] : null);
                if (into == null) continue;
                int guess = element ? ((source.Guess & ArrayFlag) != 0 ? source.Guess & 15 : 0) : source.Guess, before = into.Guess;
                into.Observe(guess); changed |= into.Guess != before;
            }
        }
        foreach (var (native, uses) in checker.pending)
            foreach (var (name, (declaration, owner)) in uses)
                native.Outer[name] = new NativeSignature.OuterName(Code(declaration.Type), declaration.Count == 1 ? declaration.Function : null,
                    declaration.Assigned || declaration.Count != 1, owner == native.DefiningScope, declaration.Count == 1 ? declaration.Struct : null,
                    declaration.Count == 1 && declaration.Guess > 0 ? declaration.Guess : 0);
        foreach (var (native, _) in checker.pending) native.Structs = checker.Structs;
        // A clone's parameters are their guesses: numbers, Booleans and arrays of one of them.
        foreach (var (function, native) in checker.Natives)
            if (native.Clone && checker.parameterDeclarations.TryGetValue(function, out var guessed))
                for (int i = 0; i < guessed.Length; i++)
                    native.Parameters[i] = guessed[i].Guess is > 0 and < StructFlag && ((guessed[i].Guess & ~ArrayFlag) is Int or Float or Double or Bool || guessed[i].Guess is ArrayFlag or String)
                        ? guessed[i].Guess : Dynamic;
        return checker;
    }

    public static string Name(int code) {
        if (code == Void) return "無值 / no value";
        if (code == ArrayFlag) return "陣列 array";
        if (IsStruct(code)) return structNames.TryGetValue(code, out var name) ? name : "結構 struct";
        string suffix = (code & ArrayFlag) != 0 ? "[]" : "";
        return (code & 15) switch {
            Int => $"整數{suffix} int{suffix}", Float => $"浮點數{suffix} float{suffix}", Double => $"雙精度{suffix} double{suffix}",
            Bool => $"布林{suffix} bool{suffix}", String => $"字串 string", _ => "?" };
    }

    private void Error(string message, Token token) => errors.Add(new ParseError(message, token));

    // ---- declarations ----------------------------------------------------------------------------

    /// <summary>Records the declarations a scope owns: its statements, through blocks and conditionals but not into
    /// functions or loops, which own their own scopes.</summary>
    private void Declare(IEnumerable<IStatement> statements) {
        // Struct types first, so annotations anywhere in the scope (and struct fields, recursively) can name them.
        var list = statements.ToList();
        foreach (var declaration in list.OfType<StructStatement>()) AddStruct(declaration);
        foreach (var declaration in list.OfType<StructStatement>()) foreach (var field in declaration.Fields) Bind(field.Type);
        foreach (var statement in list) DeclareIn(statement);
    }
    private void AddStruct(StructStatement declaration) {
        if (scope.Names.ContainsKey(declaration.Name.Value)) {
            Error($"{declaration.Name.Value} 在同一範圍重複宣告 / {declaration.Name.Value} is declared twice in the same scope.", declaration.Name.Token);
            return;
        }
        scope.Names[declaration.Name.Value] = new Declaration { Struct = declaration, Count = 1 };
    }
    /// <summary>Resolves a struct type name in an annotation to its code (32 | id &lt;&lt; 6).</summary>
    private void Bind(TypeAnnotation? type) {
        if (type?.StructName == null || type.StructCode != 0) return;
        for (var s = scope; s != null; s = s.Outer)
            if (s.Names.TryGetValue(type.StructName, out var declaration)) {
                if (declaration.Struct is not { } found) break;
                type.StructCode = StructFlag | found.Id << 6;
                Structs[type.StructCode] = found; structNames[type.StructCode] = found.Name.Value;
                return;
            }
        Error($"未知的型別 {type.StructName}（可用：整數、浮點數、雙精度、布林、字串、陣列或結構名稱）/ Unknown type {type.StructName} (use int, float, double, bool, string, array or a struct name).", type.Token);
    }
    private void DeclareIn(INode? node) {
        switch (node) {
            case StructStatement: break;
            case LetStatement let:
                Bind(let.Name.Type);
                Add(let.Name, let.Value as FunctionLiteral);
                if (let.Alias != null) Add(let.Alias, let.Value as FunctionLiteral);
                DeclareIn(let.Value); break;
            case ExpressionStatement statement: DeclareIn(statement.Expression); break;
            case ReturnStatement returned: DeclareIn(returned.ReturnValue); break;
            case BlockStatement block: Declare(block.Statements); break;
            case IfExpression conditional:
                DeclareIn(conditional.Condition); DeclareIn(conditional.Consequence);
                foreach (var c in conditional.AlternativeConditions ?? System.Array.Empty<IExpression>()) DeclareIn(c);
                foreach (var b in conditional.Alternatives ?? System.Array.Empty<BlockStatement>()) DeclareIn(b);
                DeclareIn(conditional.LastResort); break;
            case InfixExpression infix: DeclareIn(infix.Left); DeclareIn(infix.Right); break;
            case PrefixExpression prefix: DeclareIn(prefix.Right); break;
            case CallExpression call: DeclareIn(call.Function); foreach (var a in call.Arguments ?? System.Array.Empty<IExpression>()) DeclareIn(a); break;
            case IndexExpression index: DeclareIn(index.Left); DeclareIn(index.Index); break;
            case ArrayLiteral array: foreach (var e in array.Elements ?? System.Array.Empty<IExpression>()) DeclareIn(e); break;
            case HashLiteral hash: foreach (var pair in hash.Pairs) { DeclareIn(pair.Key); DeclareIn(pair.Value); } break;
        }
    }
    private void Add(Identifier name, FunctionLiteral? function) {
        if (!scope.Names.TryGetValue(name.Value, out var declaration)) {
            scope.Names[name.Value] = new Declaration { Type = name.Type, Function = function, Count = 1 };
            return;
        }
        if (Code(declaration.Type) != Code(name.Type))
            Error($"{name.Value} 在同一範圍以不同型別宣告 / {name.Value} is declared with different types in the same scope.", name.Token);
        declaration.Count++; declaration.Function = null;
    }

    private Declaration? Resolve(string name) {
        int passed = 0; // enclosing functions and loops (innermost first) whose own scope the walk has left
        for (var s = scope; s != null; s = s.Outer) {
            if (s.Names.TryGetValue(name, out var declaration)) {
                foreach (var region in functions.Take(passed)) region.Uses.TryAdd(name, (declaration, s));
                return declaration;
            }
            while (passed < functions.Count && functions.ElementAt(passed).Own == s) passed++;
        }
        return null;
    }
    private static int Code(TypeAnnotation? type) => type?.Code ?? Dynamic;

    // ---- statements --------------------------------------------------------------------------------

    private void Statement(IStatement statement) {
        switch (statement) {
            case LetStatement let: {
                int value = Expression(let.Value);
                if (let.Name.Type != null) Require(let.Name.Type.Code, value, let, let.Name.Value, let.Name.Token);
                else if (scope.Names.TryGetValue(let.Name.Value, out var declared)) Guess(declared, null, 0, value, let.Value);
                break;
            }
            case ReturnStatement returned: {
                int value = Expression(returned.ReturnValue);
                if (results.Count > 0 && results.Peek() is { } result) Guess(result, null, 0, value, returned.ReturnValue);
                if (returns.Count > 0 && returns.Peek() is { } type) Require(type.Code, value, returned, "回傳值 return value", returned.Token);
                break;
            }
            case ExpressionStatement expression: Expression(expression.Expression); break;
            case BlockStatement block: foreach (var s in block.Statements) Statement(s); break;
        }
    }

    /// <summary>Records a check or reports a mismatch for a value of static type <paramref name="value"/> stored where
    /// <paramref name="target"/> is required. Integers widen to float and double, floats to double.</summary>
    private void Require(int target, int value, INode site, string subject, Token token) {
        if (value == target) return;
        if (IsStruct(target) && value != Dynamic) {
            Error($"型別不符：{subject} 需要 {Name(target)}，卻是 {Name(value)} / Type mismatch: {subject} expects {Name(target)}, got {Name(value)}.", token);
            return;
        }
        // An array of any values may hold a typed array: that is checked when the program runs.
        if (value == Dynamic || Widens(value, target) || value == ArrayFlag && (target & ArrayFlag) != 0) { Checks[site] = new TypeCheck(target, subject); return; }
        Error($"型別不符：{subject} 需要 {Name(target)}，卻是 {Name(value)} / Type mismatch: {subject} expects {Name(target)}, got {Name(value)}.", token);
    }
    // Integers widen to float and double, floats to double, and every typed array is an array (陣列 / array).
    private static bool Widens(int from, int to) => from is Int or Float && to is Float or Double && from < to || to == ArrayFlag && (from & ArrayFlag) != 0;
    private static bool Numeric(int type) => type is Int or Float or Double;

    // ---- expressions -------------------------------------------------------------------------------

    private int Expression(IExpression? expression) {
        switch (expression) {
            case null: return Dynamic;
            case IntegerLiteral: return Int;
            case FloatLiteral: return Float;
            case DoubleLiteral: return Double;
            case BooleanLiteral: return Bool;
            case StringLiteral: return String;
            case Identifier identifier: return Code(Resolve(identifier.Value)?.Type);
            case IncrementExpression increment: {
                // Only annotations are checked at compile time; untyped mistakes stay runtime errors as before.
                int type = Expression(increment.Operand);
                if (type != Dynamic && !Numeric(type)) Error($"++ 需要數字 / ++ requires a number, got {Name(type)}.", increment.Token);
                return Numeric(type) ? type : Dynamic;
            }
            case PrefixExpression prefix: {
                int type = Expression(prefix.Right);
                if (prefix.Operator == "!") return Bool;
                return Numeric(type) ? type : Dynamic;
            }
            case InfixExpression infix: return Infix(infix);
            case IfExpression conditional: {
                Expression(conditional.Condition); Statement(conditional.Consequence);
                foreach (var c in conditional.AlternativeConditions ?? System.Array.Empty<IExpression>()) Expression(c);
                foreach (var b in conditional.Alternatives ?? System.Array.Empty<BlockStatement>()) Statement(b);
                if (conditional.LastResort != null) Statement(conditional.LastResort);
                return Dynamic;
            }
            case ForLoopLiteral loop: {
                var outer = scope; scope = new Scope(outer);
                var region = new NativeSignature(System.Array.Empty<int>(), 0, outer);
                var uses = new Dictionary<string, (Declaration, Scope)>(System.StringComparer.Ordinal);
                Loops[loop] = region; pending.Add((region, uses)); functions.Push((scope, region, uses));
                var parts = new List<IStatement>();
                if (loop.DeclareStatement != null) parts.Add(loop.DeclareStatement);
                if (loop.ValueChangeStatement != null) parts.Add(loop.ValueChangeStatement);
                if (loop.Body != null) parts.AddRange(loop.Body.Statements);
                Declare(parts);
                if (loop.DeclareStatement != null) Statement(loop.DeclareStatement);
                Expression(loop.ConditionalExpression);
                if (loop.Body != null) Statement(loop.Body);
                if (loop.ValueChangeStatement != null) Statement(loop.ValueChangeStatement);
                functions.Pop();
                scope = outer;
                return Dynamic;
            }
            case FunctionLiteral function: Function(function); return Dynamic;
            case CallExpression call: return Call(call);
            case ArrayLiteral array: foreach (var e in array.Elements ?? System.Array.Empty<IExpression>()) Expression(e); return Dynamic;
            case HashLiteral hash: foreach (var pair in hash.Pairs) { Expression(pair.Key); Expression(pair.Value); } return Dynamic;
            case IndexExpression index: {
                int left = Expression(index.Left), position = Expression(index.Index);
                return (left & ArrayFlag) != 0 ? left & 15 : Dynamic;
            }
            default: return Dynamic;
        }
    }

    private int Infix(InfixExpression infix) {
        string op = infix.Operator;
        if (op == ".") {
            int receiver = Expression(infix.Left);
            // A field of a value of a known struct type has the field's type.
            if (IsStruct(receiver) && infix.Right is Identifier member && Structs.TryGetValue(receiver, out var shape))
                return FieldType(shape, member);
            // The right side is a member name, a member call or an index chain on one; only its arguments are code here.
            var right = infix.Right;
            while (right is IndexExpression indexed) { Expression(indexed.Index); right = indexed.Left; }
            if (right is CallExpression call) foreach (var a in call.Arguments ?? System.Array.Empty<IExpression>()) Expression(a);
            return Dynamic;
        }
        if (op == "=" && infix.Left is IndexExpression) {
            Expression(infix.Left); Expression(infix.Right);
            return Void;
        }
        if (op is "=" or "+=" or "-=" or "*=" or "/=" or "%=" && infix.Left is InfixExpression { Operator: "." } target && target.Right is Identifier field) {
            // A field assignment: definite mismatches are compile errors; the runtime checks every write anyway.
            int receiver = Expression(target.Left), value = Expression(infix.Right);
            if (!IsStruct(receiver) || !Structs.TryGetValue(receiver, out var shape)) return Void;
            int type = FieldType(shape, field);
            string subject = $"{shape.Name.Value}.{field.Value}";
            if (type == Dynamic || value == Dynamic) return Void;
            if (op == "=" && value != type && !Widens(value, type) && !(value == ArrayFlag && (type & ArrayFlag) != 0))
                Error($"型別不符：{subject} 需要 {Name(type)}，卻是 {Name(value)} / Type mismatch: {subject} expects {Name(type)}, got {Name(value)}.", field.Token);
            else if (op != "=" && !(Numeric(type) && Numeric(value) || op == "+=" && type == String && value == String))
                Error($"型別不符：{subject} ({Name(type)}) 不能使用 {op} {Name(value)} / Type mismatch: {subject} ({Name(type)}) cannot use {op} with {Name(value)}.", infix.Token);
            return Void;
        }
        if (op is "=" or "+=" or "-=" or "*=" or "/=" or "%=") {
            int value = Expression(infix.Right);
            if (infix.Left is not Identifier variable) return Void;
            var declaration = Resolve(variable.Value);
            if (declaration == null) return Void;
            if (op == "=") declaration.Assigned = true;
            if (declaration.Type is not { } type) { if (op == "=") Guess(declaration, null, 0, value, infix.Right); return Void; }
            if (op == "=") Require(type.Code, value, infix, variable.Value, variable.Token);
            else if (value != Dynamic && !(Numeric(type.Code) && Numeric(value) || op == "+=" && type.Code == String && value == String))
                Error($"型別不符：{variable.Value} ({Name(type.Code)}) 不能使用 {op} {Name(value)} / Type mismatch: {variable.Value} ({Name(type.Code)}) cannot use {op} with {Name(value)}.", infix.Token);
            else if ((op != "+=" || type.Code != String) && !Numeric(type.Code))
                Error($"{variable.Value} ({Name(type.Code)}) 不能使用 {op} / {variable.Value} ({Name(type.Code)}) cannot use {op}.", infix.Token);
            return Void;
        }
        int left = Expression(infix.Left), rightType = Expression(infix.Right);
        switch (op) {
            case "<": case ">": case "<=": case ">=": case "==": case "!=": case "&&": case "||": return Bool;
            case "+" when left == String && rightType == String: return String;
            case "+": case "-": case "*": case "/": case "%":
                return Numeric(left) && Numeric(rightType) ? System.Math.Max(left, rightType) : Dynamic;
            default: return Dynamic;
        }
    }

    private int FieldType(StructStatement shape, Identifier field) {
        var declared = shape.Fields.FirstOrDefault(f => f.Value == field.Value);
        if (declared == null) {
            Error($"{shape.Name.Value} 沒有欄位 {field.Value} / {shape.Name.Value} has no field {field.Value}.", field.Token);
            return Dynamic;
        }
        return Code(declared.Type);
    }

    /// <summary>A construction of a statically known struct type: every argument names (or, positionally, is) a field
    /// of compatible type, and every field that cannot hold null (a typed number, Boolean, string or array) is given.</summary>
    private int Construct(CallExpression call, StructStatement shape, int[] arguments, Token token) {
        var given = new HashSet<string>(System.StringComparer.Ordinal);
        if (call.ArgumentNames == null && arguments.Length > shape.Fields.Length)
            Error($"{shape.Name.Value} 只有 {shape.Fields.Length} 個欄位 / {shape.Name.Value} has only {shape.Fields.Length} fields.", token);
        for (int i = 0; i < arguments.Length; i++) {
            var field = call.ArgumentNames == null ? (i < shape.Fields.Length ? shape.Fields[i] : null)
                : shape.Fields.FirstOrDefault(f => f.Value == call.ArgumentNames[i]);
            if (field == null) {
                if (call.ArgumentNames != null) Error($"{shape.Name.Value} 沒有欄位 {call.ArgumentNames[i]} / {shape.Name.Value} has no field {call.ArgumentNames[i]}.", token);
                continue;
            }
            given.Add(field.Value);
            int type = Code(field.Type), value = arguments[i];
            if (type != Dynamic && value != Dynamic && value != type && !Widens(value, type) && !(value == ArrayFlag && (type & ArrayFlag) != 0))
                Error($"型別不符：{shape.Name.Value}.{field.Value} 需要 {Name(type)}，卻是 {Name(value)} / Type mismatch: {shape.Name.Value}.{field.Value} expects {Name(type)}, got {Name(value)}.", token);
        }
        foreach (var field in shape.Fields)
            if (!given.Contains(field.Value) && field.Type != null && !IsStruct(field.Type.Code))
                Error($"{shape.Name.Value}.{field.Value}（{Name(field.Type.Code)}）必須給值 / {shape.Name.Value}.{field.Value} ({Name(field.Type.Code)}) needs a value.", token);
        return StructFlag | shape.Id << 6;
    }

    private int Call(CallExpression call) {
        int[] arguments = (call.Arguments ?? System.Array.Empty<IExpression>()).Select(Expression).ToArray();
        if (call.Function is not Identifier name) { Expression(call.Function); return Dynamic; }
        var declaration = Resolve(name.Value);
        if (declaration?.Struct is { } shape && declaration.Count == 1 && !declaration.Assigned) {
            int code = Construct(call, shape, arguments, name.Token);
            Structs[code] = shape; structNames[code] = shape.Name.Value;
            return code;
        }
        if (call.ArgumentNames != null && declaration?.Function != null)
            Error($"具名引數只能用於建立結構 / Named arguments construct structs; {name.Value} is a function.", name.Token);
        if (declaration == null) {
            // Unshadowed core globals with a known result type.
            return name.Value switch {
                "int" or "整數" => Int, "float" or "浮點數" => Float, "double" or "雙精度" => Double,
                "len" or "長度" => Int, _ => Dynamic };
        }
        if (declaration.Function is not { } function || declaration.Assigned || declaration.Count != 1) return Dynamic;
        var parameters = function.Parameters ?? System.Array.Empty<Identifier>();
        if (parameters.Length == arguments.Length)
            for (int i = 0; i < arguments.Length; i++)
                if (arguments[i] != Dynamic || Source(call.Arguments![i]) is not { } source) this.arguments.Add((function, i, arguments[i] == Dynamic ? GuessOf(call.Arguments![i]) : arguments[i]));
                else derived.Add((null, function, i, source.Declaration, source.Element));
        if (parameters.Length == arguments.Length)
            for (int i = 0; i < arguments.Length; i++)
                if (parameters[i].Type is { } type && arguments[i] != Dynamic && arguments[i] != type.Code && !Widens(arguments[i], type.Code)
                    && !(arguments[i] == ArrayFlag && (type.Code & ArrayFlag) != 0))
                    Error($"型別不符：參數 {parameters[i].Value} 需要 {Name(type.Code)}，卻是 {Name(arguments[i])} / Type mismatch: parameter {parameters[i].Value} expects {Name(type.Code)}, got {Name(arguments[i])}.", name.Token);
        return Code(function.ReturnType);
    }

    /// <summary>A guess (never proven) for an untyped value: a variable's guess so far, an element of a guessed array, an
    /// array literal of scalars of one type, or an array made by 陣列.建立 / Array.Make with a scalar fill value (an
    /// untyped array a native region may speculate holds only that type; its entry checks the elements).</summary>
    /// <summary>Observes the value a declaration (or, with no target, parameter index of function) receives: its static
    /// type, a guess of its own, or the guess of the variable (or array element) it copies, resolved at the end.</summary>
    private void Guess(Declaration target, FunctionLiteral? function, int index, int value, IExpression? expression) {
        if (value == Dynamic && Source(expression) is { } source) derived.Add((target, function, index, source.Declaration, source.Element));
        else target.Observe(value != Dynamic ? value : GuessOf(expression));
    }
    /// <summary>The untyped variable whose guess a value copies: the variable itself, or the array of an element read.</summary>
    private (Declaration Declaration, bool Element)? Source(IExpression? expression) => expression switch {
        Identifier name when Resolve(name.Value) is { Type: null, Count: 1 } d => (d, false),
        IndexExpression { Left: Identifier array } when Resolve(array.Value) is { Type: null, Count: 1 } d => (d, true),
        CallExpression { Function: Identifier callee } when Resolve(callee.Value) is { Function: { ReturnType: null } function, Assigned: false, Count: 1 }
            => (ResultGuess(function), false),
        _ => null };
    private Declaration ResultGuess(FunctionLiteral function) {
        if (!resultDeclarations.TryGetValue(function, out var result)) resultDeclarations[function] = result = new Declaration();
        return result;
    }
    private int GuessOf(IExpression? expression) {
        switch (expression) {
            case Identifier name: return Resolve(name.Value) is { Type: null, Count: 1 } d && d.Guess > 0 ? d.Guess : 0;
            case IndexExpression index: { int array = GuessOf(index.Left); return (array & ArrayFlag) != 0 ? array & 15 : 0; }
            case ArrayLiteral { Elements: { Length: > 0 } elements }: {
                int first = ScalarOf(elements[0]);
                return first != 0 && elements.All(e => ScalarOf(e) == first) ? first | ArrayFlag : ArrayFlag;
            }
            case InfixExpression { Operator: ".", Left: Identifier module, Right: CallExpression { Function: Identifier make, Arguments: { Length: >= 2 } arguments } }
                when module.Value is "陣列" or "Array" && make.Value is "建立" or "Make" && Resolve(module.Value) == null: {
                int fill = ScalarOf(arguments[1]);
                return fill != 0 ? fill | ArrayFlag : 0;
            }
            case InfixExpression { Operator: "+" or "-" or "*" or "/" or "%" } arithmetic: {
                int left = Known(arithmetic.Left), right = Known(arithmetic.Right);
                return Numeric(left) && Numeric(right) ? System.Math.Max(left, right) : 0;
            }
            default: return 0;
        }
        int Known(IExpression operand) => ScalarOf(operand) is int t and not 0 ? t : GuessOf(operand);
    }
    /// <summary>The scalar type of a literal, a negated number literal or a typed scalar variable, else 0.</summary>
    private int ScalarOf(IExpression expression) => expression switch {
        IntegerLiteral => Int, FloatLiteral => Float, DoubleLiteral => Double, BooleanLiteral => Bool,
        PrefixExpression { Operator: "-", Right: var right } when ScalarOf(right) is Int or Float or Double => ScalarOf(right),
        Identifier name when Code(Resolve(name.Value)?.Type) is Int or Float or Double or Bool => Code(Resolve(name.Value)?.Type),
        _ => 0 };

    private void Function(FunctionLiteral function) {
        var outer = scope; scope = new Scope(outer);
        foreach (var parameter in function.Parameters ?? System.Array.Empty<Identifier>()) Bind(parameter.Type);
        Bind(function.ReturnType);
        var parameterTypes = (function.Parameters ?? System.Array.Empty<Identifier>()).Select(p => Code(p.Type)).ToArray();
        // Numbers, Booleans, typed arrays and arrays of any values; string parameters or results keep the function generic.
        static bool NativeType(int t) => (t & 15) is Int or Float or Double or Bool || t == String || t == ArrayFlag || IsStruct(t);
        NativeSignature? native = function.ReturnType != null && parameterTypes.All(NativeType) && NativeType(Code(function.ReturnType))
            ? new NativeSignature(parameterTypes, Code(function.ReturnType), outer)
            : function.ReturnType == null && parameterTypes.All(t => t == Dynamic) ? new NativeSignature(new int[parameterTypes.Length], 0, outer, clone: true) : null;
        var uses = new Dictionary<string, (Declaration, Scope)>(System.StringComparer.Ordinal);
        functions.Push((scope, native, uses));
        if (native != null) { Natives[function] = native; pending.Add((native, uses)); }
        foreach (var parameter in function.Parameters ?? System.Array.Empty<Identifier>()) Add(parameter, null);
        parameterDeclarations[function] = (function.Parameters ?? System.Array.Empty<Identifier>()).Select(p => scope.Names[p.Value]).ToArray();
        var statements = function.Body?.Statements ?? System.Array.Empty<IStatement>();
        Declare(statements);
        returns.Push(function.ReturnType);
        results.Push(function.ReturnType == null ? ResultGuess(function) : null);
        for (int i = 0; i < statements.Length; i++) {
            if (i == statements.Length - 1 && function.ReturnType is { } type && statements[i] is ExpressionStatement last)
                Require(type.Code, Expression(last.Expression), function, "回傳值 return value", function.ReturnType.Token);
            else Statement(statements[i]);
        }
        if (function.ReturnType is { } result && (statements.Length == 0 || statements[^1] is LetStatement or BreakStatement))
            Error($"函式必須回傳 {Name(result.Code)} / The function must return {Name(result.Code)}.", result.Token);
        returns.Pop(); results.Pop();
        functions.Pop();
        scope = outer;
    }
}
