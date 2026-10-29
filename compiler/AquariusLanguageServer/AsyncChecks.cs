using AquariusLang.ast;
using AquariusLang.token;

namespace AquariusLanguageServer;

/// <summary>
/// Warnings about mixing async and synchronous code that the compiler accepts but that rarely mean what they say (the
/// compiler itself rejects 等待 / await outside an async function):
/// a promise nobody awaits or handles, a promise used as a plain value, 等待 / await of a value that is never a promise,
/// and an async function handed to an Array function that calls it synchronously. Promises come from async functions
/// the file declares, the Promise / 承諾 module, Then / Catch / Finally and library functions registered with aqua_define_async or named ...Async / 非同步...
/// </summary>
internal static class AsyncChecks
{
    internal sealed record Finding(Token Token, string Message, int Severity);

    private enum Kind { Other, Async, Sync }
    private enum Use { Discarded, Value, Operand, Condition, Awaited, Final }

    private static readonly HashSet<string> PromiseModules = ["Promise", "承諾"];
    private static readonly HashSet<string> NotPromises = ["IsPromise", "是承諾"];
    private static readonly HashSet<string> PromiseMethods = ["Then", "然後", "Catch", "捕捉", "Finally", "最終"];
    private static readonly HashSet<string> PromiseMembers = ["Then", "然後", "Catch", "捕捉", "Finally", "最終", "State", "狀態", "Value", "值", "Reason", "原因", "IsDone", "是否完成"];
    // Array functions that call their function synchronously and use its result as a value (Map's promises can be awaited with Promise.All).
    private static readonly HashSet<string> SynchronousCallers = ["Filter", "篩選", "Sort", "排序", "Reduce", "歸約"];

    public static List<Finding> Check(AbstractSyntaxTree tree, IReadOnlySet<string>? asyncMembers = null)
    {
        var checker = new Checker(asyncMembers ?? new HashSet<string>());
        checker.Statements(tree.Statements, false);
        return checker.Findings;
    }

    private sealed class Checker(IReadOnlySet<string> asyncMembers)
    {
        public readonly List<Finding> Findings = [];
        private readonly List<Dictionary<string, (Kind Kind, FunctionLiteral? Function)>> scopes = [new()];
        private int depth;

        private void Declare(string name, Kind kind, FunctionLiteral? function) => scopes[^1][name] = (kind, function);
        private (Kind Kind, FunctionLiteral? Function) Resolve(string name)
        {
            for (int i = scopes.Count - 1; i >= 0; i--) if (scopes[i].TryGetValue(name, out var entry)) return entry;
            return (Kind.Other, null);
        }

        private static (Kind, FunctionLiteral?) KindOf(IExpression? value) => value switch {
            AsyncFunctionExpression async => (Kind.Async, async.Literal),
            FunctionLiteral function => (Kind.Sync, function),
            _ => (Kind.Other, null)
        };

        // The text naming where a promise comes from, or null when the expression gives no promise this checker knows of.
        private string? PromiseSource(IExpression? expression)
        {
            switch (expression) {
                case AwaitExpression: return null;
                case AsyncFunctionExpression: return null;
                case CallExpression { Function: Identifier callee } when Resolve(callee.Value).Kind == Kind.Async: return callee.Value;
                case CallExpression { Function: AsyncFunctionExpression }: return "async function";
                case InfixExpression { Operator: ".", Right: CallExpression { Function: Identifier member } } access: {
                    string name = member.Value;
                    if (PromiseMethods.Contains(name)) return name;
                    if (access.Left is Identifier module && PromiseModules.Contains(module.Value) && Resolve(module.Value).Kind == Kind.Other && !NotPromises.Contains(name))
                        return module.Value + "." + name;
                    if (asyncMembers.Contains(name) || (name.Length > 5 && name.EndsWith("Async", StringComparison.Ordinal)) || (name.Length > 3 && name.StartsWith("非同步", StringComparison.Ordinal)))
                        return (access.Left is Identifier owner ? owner.Value + "." : "") + name;
                    return null;
                }
                default: return null;
            }
        }

        private void Report(Token token, string message, int severity) => Findings.Add(new Finding(token, message, severity));

        private void Expect(IExpression? expression, Use use)
        {
            string? source = use is Use.Discarded or Use.Operand or Use.Condition ? PromiseSource(expression) : null;
            // A statement attaching Then / Catch / Finally handles the promise it starts from.
            if (use == Use.Discarded && expression is InfixExpression { Operator: ".", Right: CallExpression { Function: Identifier attached } }
                && PromiseMethods.Contains(attached.Value)) source = null;
            if (source != null) {
                Token token = expression is InfixExpression member ? member.Right is CallExpression call ? call.Token : member.Token
                    : ((CallExpression)expression!).Token;
                if (expression is InfixExpression { Right: CallExpression { Function: Identifier name } }) token = name.Token;
                else if (expression is CallExpression { Function: Identifier callee }) token = callee.Token;
                if (use == Use.Discarded)
                    Report(token, $"「{source}」的承諾沒有被等待或處理 / The promise of {source} is neither awaited nor handled: use 等待 / await, or Then / Catch.", 2);
                else
                    Report(token, $"「{source}」回傳承諾而不是值；請先等待 / {source} gives a promise, not its value: await it (等待) first.", 2);
            }
            Walk(expression, use);
        }

        public void Statements(IStatement[]? statements, bool function)
        {
            if (statements == null) return;
            for (int i = 0; i < statements.Length; i++) {
                bool final = function && i == statements.Length - 1;
                Statement(statements[i], final);
            }
        }

        private void Statement(IStatement? statement, bool final)
        {
            switch (statement) {
                case ExpressionStatement expression:
                    // A function's final expression is its result: returning a promise is fine.
                    Expect(expression.Expression, final ? Use.Final : Use.Discarded);
                    break;
                case LetStatement let:
                    var (kind, function) = KindOf(let.Value);
                    if (let.Name != null) Declare(let.Name.Value, kind, function);
                    if (let.Alias != null) Declare(let.Alias.Value, kind, function);
                    Expect(let.Value, Use.Value);
                    break;
                case ReturnStatement returned: Expect(returned.ReturnValue, Use.Value); break;
                case BlockStatement block: Statements(block.Statements, final); break;
            }
        }

        private void Function(FunctionLiteral function)
        {
            scopes.Add(new());
            foreach (var parameter in function.Parameters ?? []) Declare(parameter.Value, Kind.Other, null);
            Statements(function.Body?.Statements, true);
            scopes.RemoveAt(scopes.Count - 1);
        }

        private void Walk(INode? node, Use use)
        {
            if (node == null || depth >= 256) return;
            depth++;
            try { WalkCore(node, use); }
            finally { depth--; }
        }

        private void WalkCore(INode node, Use use)
        {
            switch (node) {
                case AwaitExpression awaited:
                    CheckAwaited(awaited);
                    Expect(awaited.Operand, Use.Awaited);
                    break;
                case AsyncFunctionExpression async: Function(async.Literal); break;
                case FunctionLiteral function: Function(function); break;
                case CallExpression call:
                    Walk(call.Function, Use.Operand);
                    foreach (var argument in call.Arguments ?? []) Expect(argument, Use.Value);
                    break;
                case InfixExpression { Operator: "." } member:
                    // Reading a promise's own members is fine; anything else on a promise means its value was meant.
                    bool own = member.Right is Identifier field && PromiseMembers.Contains(field.Value)
                        || member.Right is CallExpression { Function: Identifier method } && PromiseMembers.Contains(method.Value);
                    Expect(member.Left, own ? Use.Value : Use.Operand);
                    if (member.Right is CallExpression right) {
                        CheckSynchronousCaller(member, right);
                        foreach (var argument in right.Arguments ?? []) Expect(argument, Use.Value);
                    }
                    break;
                case InfixExpression assignment when assignment.Operator == "=":
                    if (assignment.Left is Identifier target) { var (kind, function) = KindOf(assignment.Right); Declare(target.Value, kind, function); }
                    Walk(assignment.Left, Use.Value);
                    Expect(assignment.Right, Use.Value);
                    break;
                case InfixExpression infix:
                    Use operand = infix.Operator is "&&" or "||" ? Use.Condition : Use.Operand;
                    Expect(infix.Left, operand);
                    Expect(infix.Right, operand);
                    break;
                case PrefixExpression prefix: Expect(prefix.Right, Use.Operand); break;
                case IncrementExpression increment: Walk(increment.Operand, Use.Value); break;
                case IfExpression conditional:
                    Expect(conditional.Condition, Use.Condition);
                    Statements(conditional.Consequence?.Statements, use == Use.Final);
                    foreach (var condition in conditional.AlternativeConditions ?? []) Expect(condition, Use.Condition);
                    foreach (var alternative in conditional.Alternatives ?? []) Statements(alternative?.Statements, use == Use.Final);
                    Statements(conditional.LastResort?.Statements, use == Use.Final);
                    break;
                case ForLoopLiteral loop:
                    scopes.Add(new());
                    Statement(loop.DeclareStatement, false);
                    Expect(loop.ConditionalExpression, Use.Condition);
                    Statement(loop.ValueChangeStatement as IStatement, false);
                    Statements(loop.Body?.Statements, false);
                    scopes.RemoveAt(scopes.Count - 1);
                    break;
                case ArrayLiteral array: foreach (var element in array.Elements ?? []) Expect(element, Use.Value); break;
                case HashLiteral hash: foreach (var pair in hash.Pairs ?? []) { Expect(pair.Key, Use.Operand); Expect(pair.Value, Use.Value); } break;
                case IndexExpression index: Expect(index.Left, Use.Operand); Expect(index.Index, Use.Operand); break;
            }
        }

        // 等待 / await of something that is never a promise: a literal, or a call of a synchronous function of this file
        // that makes no promise anywhere in its body.
        private void CheckAwaited(AwaitExpression awaited)
        {
            string? what = awaited.Operand switch {
                IntegerLiteral or FloatLiteral or DoubleLiteral or StringLiteral or BooleanLiteral or NullLiteral or ArrayLiteral or HashLiteral => "this value",
                CallExpression { Function: Identifier callee } when Resolve(callee.Value) is { Kind: Kind.Sync, Function: { } function } && !MakesPromises(function.Body)
                    => callee.Value,
                _ => null
            };
            if (what != null)
                Report(awaited.Token, $"等待沒有作用：{what} 不是承諾 / await has no effect: {what} is not a promise.", 3);
        }

        private void CheckSynchronousCaller(InfixExpression member, CallExpression call)
        {
            if (call.Function is not Identifier name || !SynchronousCallers.Contains(name.Value)) return;
            if (member.Left is not Identifier module || module.Value is not ("Array" or "陣列")) return;
            foreach (var argument in call.Arguments ?? [])
                if (argument is AsyncFunctionExpression || argument is Identifier f && Resolve(f.Value).Kind == Kind.Async)
                    Report(name.Token, $"{module.Value}.{name.Value} 同步呼叫函式，非同步函式在此只會得到承諾 / {module.Value}.{name.Value} calls its function synchronously: an async function gives it a promise, not a result.", 2);
        }

        private bool MakesPromises(INode? node)
        {
            switch (node) {
                case null: return false;
                case AsyncFunctionExpression or AwaitExpression: return true;
                case Identifier identifier: return PromiseModules.Contains(identifier.Value) || Resolve(identifier.Value).Kind == Kind.Async;
                case BlockStatement block: return (block.Statements ?? []).Any(MakesPromises);
                case ExpressionStatement statement: return MakesPromises(statement.Expression);
                case LetStatement let: return MakesPromises(let.Value);
                case ReturnStatement returned: return MakesPromises(returned.ReturnValue);
                case CallExpression call: return MakesPromises(call.Function) || (call.Arguments ?? []).Any(MakesPromises);
                case InfixExpression infix: return MakesPromises(infix.Left) || MakesPromises(infix.Right)
                    || infix.Right is CallExpression { Function: Identifier m } && (PromiseMethods.Contains(m.Value) || m.Value.EndsWith("Async", StringComparison.Ordinal) || m.Value.StartsWith("非同步", StringComparison.Ordinal));
                case PrefixExpression prefix: return MakesPromises(prefix.Right);
                case IfExpression conditional: return MakesPromises(conditional.Condition) || MakesPromises(conditional.Consequence)
                    || (conditional.AlternativeConditions ?? []).Any(MakesPromises) || (conditional.Alternatives ?? []).Any(MakesPromises) || MakesPromises(conditional.LastResort);
                case ForLoopLiteral loop: return MakesPromises(loop.DeclareStatement) || MakesPromises(loop.ConditionalExpression) || MakesPromises(loop.ValueChangeStatement) || MakesPromises(loop.Body);
                case FunctionLiteral function: return MakesPromises(function.Body);
                case ArrayLiteral array: return (array.Elements ?? []).Any(MakesPromises);
                case HashLiteral hash: return (hash.Pairs ?? []).Any(p => MakesPromises(p.Key) || MakesPromises(p.Value));
                case IndexExpression index: return MakesPromises(index.Left) || MakesPromises(index.Index);
                default: return false;
            }
        }
    }
}
