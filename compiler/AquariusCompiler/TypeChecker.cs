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
    internal sealed record OuterName(int Type, FunctionLiteral? Function, bool Assigned, bool Sibling);
    internal readonly int[] Parameters;
    internal readonly int Result;
    internal readonly object DefiningScope;
    internal readonly Dictionary<string, OuterName> Outer = new(System.StringComparer.Ordinal);
    internal NativeSignature(int[] parameters, int result, object definingScope) { Parameters = parameters; Result = result; DefiningScope = definingScope; }
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
    internal const int Dynamic = 0, Void = -1, Int = 1, Float = 2, Double = 3, Bool = 4, String = 5, ArrayFlag = 16;

    private sealed class Declaration {
        internal TypeAnnotation? Type;
        internal FunctionLiteral? Function;
        internal int Count;
        internal bool Assigned;
    }
    private sealed class Scope {
        internal readonly Scope? Outer;
        internal readonly Dictionary<string, Declaration> Names = new(System.StringComparer.Ordinal);
        internal Scope(Scope? outer) { Outer = outer; }
    }

    internal readonly Dictionary<INode, TypeCheck> Checks = new(ReferenceEqualityComparer.Instance);
    internal readonly Dictionary<FunctionLiteral, NativeSignature> Natives = new(ReferenceEqualityComparer.Instance);
    // Each function being checked: its own scope and, for a fully typed one, the outer names its body uses.
    private readonly Stack<(Scope Own, NativeSignature? Native, Dictionary<string, (Declaration, Scope)> Uses)> functions = new();
    private readonly List<(NativeSignature Native, Dictionary<string, (Declaration, Scope)> Uses)> pending = new();
    private readonly List<ParseError> errors = new();
    private readonly Stack<TypeAnnotation?> returns = new();
    private Scope scope = new(null);

    public IReadOnlyList<ParseError> Errors => errors;

    /// <summary>Checks a whole program. Errors are collected in <see cref="Errors"/>.</summary>
    public static TypeChecker Check(AbstractSyntaxTree tree) {
        var checker = new TypeChecker();
        checker.Declare(tree.Statements);
        foreach (var statement in tree.Statements) checker.Statement(statement);
        // Assignments anywhere in the program are known only now.
        foreach (var (native, uses) in checker.pending)
            foreach (var (name, (declaration, owner)) in uses)
                native.Outer[name] = new NativeSignature.OuterName(Code(declaration.Type), declaration.Count == 1 ? declaration.Function : null,
                    declaration.Assigned || declaration.Count != 1, owner == native.DefiningScope);
        return checker;
    }

    public static string Name(int code) {
        if (code == Void) return "無值 / no value";
        string suffix = (code & ArrayFlag) != 0 ? "[]" : "";
        return (code & 15) switch {
            Int => $"整數{suffix} int{suffix}", Float => $"浮點數{suffix} float{suffix}", Double => $"雙精度{suffix} double{suffix}",
            Bool => $"布林{suffix} bool{suffix}", String => $"字串 string", _ => "?" };
    }

    private void Error(string message, Token token) => errors.Add(new ParseError(message, token));

    // ---- declarations ----------------------------------------------------------------------------

    /// <summary>Records the declarations a scope owns: its statements, through blocks and conditionals but not into
    /// functions or loops, which own their own scopes.</summary>
    private void Declare(IEnumerable<IStatement> statements) { foreach (var statement in statements) DeclareIn(statement); }
    private void DeclareIn(INode? node) {
        switch (node) {
            case LetStatement let:
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
        bool inside = true;
        for (var s = scope; s != null; s = s.Outer) {
            if (s.Names.TryGetValue(name, out var declaration)) {
                if (!inside && functions.Count > 0) functions.Peek().Uses.TryAdd(name, (declaration, s));
                return declaration;
            }
            if (functions.Count > 0 && s == functions.Peek().Own) inside = false;
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
                break;
            }
            case ReturnStatement returned: {
                int value = Expression(returned.ReturnValue);
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
        if (value == Dynamic || Widens(value, target)) { Checks[site] = new TypeCheck(target, subject); return; }
        Error($"型別不符：{subject} 需要 {Name(target)}，卻是 {Name(value)} / Type mismatch: {subject} expects {Name(target)}, got {Name(value)}.", token);
    }
    private static bool Widens(int from, int to) => from is Int or Float && to is Float or Double && from < to;
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
                var parts = new List<IStatement>();
                if (loop.DeclareStatement != null) parts.Add(loop.DeclareStatement);
                if (loop.ValueChangeStatement != null) parts.Add(loop.ValueChangeStatement);
                if (loop.Body != null) parts.AddRange(loop.Body.Statements);
                Declare(parts);
                if (loop.DeclareStatement != null) Statement(loop.DeclareStatement);
                Expression(loop.ConditionalExpression);
                if (loop.Body != null) Statement(loop.Body);
                if (loop.ValueChangeStatement != null) Statement(loop.ValueChangeStatement);
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
            Expression(infix.Left);
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
        if (op is "=" or "+=" or "-=" or "*=" or "/=" or "%=") {
            int value = Expression(infix.Right);
            if (infix.Left is not Identifier variable) return Void;
            var declaration = Resolve(variable.Value);
            if (declaration == null) return Void;
            if (op == "=") declaration.Assigned = true;
            if (declaration.Type is not { } type) return Void;
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

    private int Call(CallExpression call) {
        int[] arguments = (call.Arguments ?? System.Array.Empty<IExpression>()).Select(Expression).ToArray();
        if (call.Function is not Identifier name) { Expression(call.Function); return Dynamic; }
        var declaration = Resolve(name.Value);
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
                if (parameters[i].Type is { } type && arguments[i] != Dynamic && arguments[i] != type.Code && !Widens(arguments[i], type.Code))
                    Error($"型別不符：參數 {parameters[i].Value} 需要 {Name(type.Code)}，卻是 {Name(arguments[i])} / Type mismatch: parameter {parameters[i].Value} expects {Name(type.Code)}, got {Name(arguments[i])}.", name.Token);
        return Code(function.ReturnType);
    }

    private void Function(FunctionLiteral function) {
        var outer = scope; scope = new Scope(outer);
        var parameterTypes = (function.Parameters ?? System.Array.Empty<Identifier>()).Select(p => Code(p.Type)).ToArray();
        NativeSignature? native = function.ReturnType != null && parameterTypes.All(t => t is Int or Float or Double or Bool)
            && Code(function.ReturnType) is Int or Float or Double or Bool ? new NativeSignature(parameterTypes, Code(function.ReturnType), outer) : null;
        var uses = new Dictionary<string, (Declaration, Scope)>(System.StringComparer.Ordinal);
        functions.Push((scope, native, uses));
        if (native != null) { Natives[function] = native; pending.Add((native, uses)); }
        foreach (var parameter in function.Parameters ?? System.Array.Empty<Identifier>()) Add(parameter, null);
        var statements = function.Body?.Statements ?? System.Array.Empty<IStatement>();
        Declare(statements);
        returns.Push(function.ReturnType);
        for (int i = 0; i < statements.Length; i++) {
            if (i == statements.Length - 1 && function.ReturnType is { } type && statements[i] is ExpressionStatement last)
                Require(type.Code, Expression(last.Expression), function, "回傳值 return value", function.ReturnType.Token);
            else Statement(statements[i]);
        }
        if (function.ReturnType is { } result && (statements.Length == 0 || statements[^1] is LetStatement or BreakStatement))
            Error($"函式必須回傳 {Name(result.Code)} / The function must return {Name(result.Code)}.", result.Token);
        returns.Pop();
        functions.Pop();
        scope = outer;
    }
}
