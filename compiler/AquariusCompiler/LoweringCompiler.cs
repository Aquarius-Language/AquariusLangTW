using System;
using System.Collections.Generic;
using System.Linq;
using AquariusLang.ast;

using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;

namespace AquariusLang.Compiler;

public sealed class CompilationException : Exception {
    public CompilationException(string message) : base(message) { }
}

/// <summary>Translates syntax trees into compiler IR for subsequent WebAssembly emission.</summary>
public sealed class LoweringCompiler {
    private readonly List<Instruction> code = new();
    private readonly List<object> constants = new();
    private readonly Dictionary<string, int> names = new();
    private readonly Stack<List<int>> breaks = new();
    // The 繼續 / continue jumps of each enclosing loop of this body, patched to its next iteration.
    private readonly Stack<List<int>> continues = new();
    private readonly List<(int, int, int, NativeSignature?)> loops = new();
    // The TypeChecker result for the whole program (runtime checks, native candidates), shared by nested function bodies.
    private TypeChecker? types;

    public LoweringCompiler() { }
    internal LoweringCompiler(TypeChecker? types) { this.types = types; }

    public LoweredProgram Compile(string source) {
        var lexer = Lexer.NewInstance(source);
        var parser = Parser.NewInstance(lexer);
        var tree = parser.ParseAST();
        if (lexer.Errors.Count > 0 || parser.Errors.Count > 0)
            throw new CompilationException(string.Join("\n", lexer.Errors.Select(e => e.Message).Concat(parser.Errors)));
        return Compile(tree);
    }

    public LoweredProgram Compile(INode node) {
        if (node == null) throw new ArgumentNullException(nameof(node));
        code.Clear(); constants.Clear(); names.Clear(); breaks.Clear(); continues.Clear(); loops.Clear();
        if (node is AbstractSyntaxTree tree && types == null) {
            var checker = TypeChecker.Check(tree);
            if (checker.Errors.Count > 0) throw new CompilationException(string.Join("\n", checker.Errors.Select(e => e.Message)));
            types = checker;
        }
        Node(node);
        return new LoweredProgram(code.ToArray(), constants.ToArray(), !(node is AbstractSyntaxTree), loops.ToArray());
    }

    /// <summary>A function body: typed parameters are checked on entry and the implicit result like a return.</summary>
    internal LoweredProgram CompileFunction(FunctionLiteral function) {
        code.Clear(); constants.Clear(); names.Clear(); breaks.Clear(); continues.Clear(); loops.Clear();
        foreach (var parameter in function.Parameters ?? Array.Empty<Identifier>()) {
            if (parameter.Type == null) continue;
            // Assign consumes the target's loaded value below the new value, like `p = p`.
            Emit(IrOperation.Load, Name(parameter.Value));
            Emit(IrOperation.Load, Name(parameter.Value));
            EmitCheck(new TypeCheck(parameter.Type.Code, parameter.Value));
            Emit(IrOperation.Assign, Name(parameter.Value));
            Emit(IrOperation.Pop);
        }
        Node(function.Body);
        Check(function);
        return new LoweredProgram(code.ToArray(), constants.ToArray(), true, loops.ToArray());
    }

    // The subject shares the name constant: emitters identify a binding by its first name constant.
    private int TypeOperand(TypeCheck check) => Name(check.Subject) << 5 | check.Code;
    private void Check(INode site) {
        if (types != null && types.Checks.TryGetValue(site, out var check)) EmitCheck(check);
    }
    // A struct annotation names its type: "id\1subject\1type name" (rt_checkStruct).
    private void EmitCheck(TypeCheck check) {
        if (!TypeChecker.IsStruct(check.Code)) { Emit(IrOperation.CheckType, TypeOperand(check)); return; }
        Emit(IrOperation.CheckStruct, Constant($"{check.Code >> 6}\u0001{check.Subject}\u0001{TypeChecker.Name(check.Code)}"));
    }

    private int Emit(IrOperation op, int operand = 0) { code.Add(new Instruction(op, operand)); return code.Count - 1; }
    private void Patch(int instruction) => code[instruction] = new Instruction(code[instruction].Code, code.Count);
    private int Constant(object value) { constants.Add(value); return constants.Count - 1; }
    private int Name(string name) {
        if (!names.TryGetValue(name, out int index)) names[name] = index = Constant(name);
        return index;
    }
    private void Value(IObject value) => Emit(IrOperation.Constant, Constant(value));
    private void Error(string message) => Emit(IrOperation.Error, Constant(message));

    private void Statements(IStatement[] statements, bool block = false) {
        var endings = new List<int>();
        if (statements.Length == 0) Emit(IrOperation.Void);
        for (int i = 0; i < statements.Length; i++) {
            if (i > 0) {
                if (block) endings.Add(Emit(IrOperation.JumpIfBreak));
                Emit(IrOperation.Pop);
            }
            Node(statements[i]);
        }
        foreach (int ending in endings) Patch(ending);
    }

    private void Node(INode node) {
        switch (node) {
            case AbstractSyntaxTree tree: Statements(tree.Statements); break;
            case BlockStatement block: Statements(block.Statements, true); break;
            case ExpressionStatement statement: Node(statement.Expression); break;
            case LetStatement declaration:
                if (declaration.Alias != null && (!(declaration.Value is FunctionLiteral) || declaration.Alias.Value == declaration.Name.Value))
                    throw new CompilationException("Two distinct names require a function literal.");
                Node(declaration.Value);
                Check(declaration);
                if (declaration.Alias != null) Emit(IrOperation.Duplicate);
                Emit(IrOperation.Declare, Name(declaration.Name.Value));
                if (declaration.Alias != null) {
                    Emit(IrOperation.Pop);
                    Emit(IrOperation.Declare, Name(declaration.Alias.Value));
                }
                break;
            case ReturnStatement returned: Node(returned.ReturnValue); Check(returned); Emit(IrOperation.Return); break;
            case StructStatement shape:
                // "id\1name\1field\2code\2type\1...": the runtime's struct type descriptor (rt_structType).
                Emit(IrOperation.StructType, Constant($"{shape.Id}\u0001{shape.Name.Value}" + string.Concat(shape.Fields.Select(f =>
                    $"\u0001{f.Value}\u0002{f.Type?.Code ?? 0}\u0002{f.Type?.String() ?? ""}"))));
                Emit(IrOperation.Declare, Name(shape.Name.Value));
                break;
            case ContinueStatement:
                if (continues.Count == 0) throw new CompilationException("繼續 只能用在迴圈中 / continue can only be used inside a loop.");
                continues.Peek().Add(Emit(IrOperation.Break));
                break;
            case BreakStatement _:
                if (breaks.Count > 0) breaks.Peek().Add(Emit(IrOperation.Break));
                else Value(RepeatedPrimitives.BREAK);
                break;
            case IntegerLiteral integer: Value(new IntegerObj(integer.Value)); break;
            case FloatLiteral floating: Value(new FloatObj(floating.Value)); break;
            case DoubleLiteral number: Value(new DoubleObj(number.Value)); break;
            case BooleanLiteral boolean: Value(boolean.Value ? RepeatedPrimitives.TRUE : RepeatedPrimitives.FALSE); break;
            case NullLiteral: Emit(IrOperation.Null); break;
            case StringLiteral text: Value(new StringObj(text.Value)); break;
            case Identifier identifier: Emit(IrOperation.Load, Name(identifier.Value)); break;
            case IncrementExpression increment:
                if (increment.Operand is Identifier variable)
                    Emit(increment.IsPrefix ? IrOperation.IncrementPrefix : IrOperation.IncrementPostfix, Name(variable.Value));
                else if (increment.Operand is IndexExpression element) {
                    Node(element.Left); Node(element.Index); Emit(IrOperation.CheckArrayWrite);
                    Emit(IrOperation.IncrementIndex, increment.IsPrefix ? 1 : 0);
                } else if (increment.Operand is InfixExpression { Operator: ".", Right: Identifier field } member) {
                    Node(member.Left); Emit(IrOperation.IncrementMember, Name(field.Value) << 1 | (increment.IsPrefix ? 1 : 0));
                } else throw new CompilationException("++ 的運算元必須是變數、欄位或索引 / The operand of ++ must be a variable, a field or an index.");
                break;
            case PrefixExpression prefix:
                Node(prefix.Right);
                if (prefix.Operator == "!") Emit(IrOperation.Not);
                else if (prefix.Operator == "-") Emit(IrOperation.Negate);
                else throw new CompilationException($"Unsupported prefix operator: {prefix.Operator}");
                break;
            case InfixExpression infix: Infix(infix); break;
            case IfExpression conditional: Conditional(conditional); break;
            case ForLoopLiteral loop: Loop(loop); break;
            case FunctionLiteral function: Emit(IrOperation.Closure, Constant(new FunctionCode(function, types))); break;
            case CallExpression call:
                Node(call.Function); Arguments(call.Arguments);
                if (call.ArgumentNames != null) Emit(IrOperation.CallNamed, Constant(string.Join("\u0001", call.ArgumentNames)));
                else Emit(IrOperation.Call, call.Arguments.Length);
                break;
            case ArrayLiteral array: Arguments(array.Elements); Emit(IrOperation.Array, array.Elements.Length); break;
            case HashLiteral hash:
                foreach (var pair in hash.Pairs) { Node(pair.Key); Emit(IrOperation.CheckHashKey); Node(pair.Value); }
                Emit(IrOperation.Hash, hash.Pairs.Count); break;
            case IndexExpression index: Node(index.Left); Node(index.Index); Emit(IrOperation.Index); break;
            default: throw new CompilationException($"Unsupported syntax node: {node.GetType().Name}");
        }
    }

    private void Arguments(IExpression[] arguments) { foreach (var argument in arguments) Node(argument); }

    private void Infix(InfixExpression expression) {
        string op = expression.Operator;
        if (expression.Left is InfixExpression { Operator: "." } target && target.Right is Identifier field
            && op is "=" or "+=" or "-=" or "*=" or "/=" or "%=") {
            Node(target.Left); Node(expression.Right);
            if (op == "=") Emit(IrOperation.SetMember, Name(field.Value));
            else Emit(IrOperation.CompoundMember, Constant(new Assignment(field.Value, BinaryCode(op.Substring(0, 1)))));
            return;
        }
        if (op is "=" or "+=" or "-=" or "*=" or "/=" or "%=" && expression.Left is IndexExpression index) {
            Node(index.Left); Node(index.Index); Emit(IrOperation.CheckArrayWrite);
            Node(expression.Right);
            if (op == "=") Emit(IrOperation.WriteIndex);
            else Emit(IrOperation.CompoundIndex, Constant(new Assignment("", BinaryCode(op.Substring(0, 1)))));
            return;
        }
        Node(expression.Left);
        if (op == ".") {
            // The parser rebuilds index chains on a member (`模組.表["鍵"]`) as indexing the member.
            if (expression.Right is Identifier member) Emit(IrOperation.Member, Name(member.Value));
            else if (expression.Right is CallExpression call) {
                if (call.Function is Identifier function) Emit(IrOperation.MemberFunction, Name(function.Value));
                else Emit(IrOperation.ResolveMemberFunction, Constant(new LoweringCompiler(types).Compile(call.Function)));
                Arguments(call.Arguments); Emit(IrOperation.Call, call.Arguments.Length);
            } else throw new CompilationException("成員存取的右側必須是名稱或呼叫 / The right side of a member access must be a name or a call.");
            return;
        }
        Node(expression.Right);
        if (op == "=" || op == "+=" || op == "-=" || op == "*=" || op == "/=" || op == "%=") {
            if (!(expression.Left is Identifier variable)) {
                throw new CompilationException($"{op} 的左側必須是變數、欄位或索引 / The left side of {op} must be a variable, a field or an index.");
            }
            if (op == "=") { Check(expression); Emit(IrOperation.Assign, Name(variable.Value)); }
            else Emit(IrOperation.CompoundAssign, Constant(new Assignment(variable.Value, BinaryCode(op.Substring(0, 1)))));
            return;
        }
        Emit(BinaryCode(op));
    }

    private static IrOperation BinaryCode(string op) => op switch {
        "+" => IrOperation.Add, "-" => IrOperation.Subtract, "*" => IrOperation.Multiply, "/" => IrOperation.Divide, "%" => IrOperation.Modulo,
        "<" => IrOperation.Less, ">" => IrOperation.Greater, "<=" => IrOperation.LessEqual, ">=" => IrOperation.GreaterEqual,
        "==" => IrOperation.Equal, "!=" => IrOperation.NotEqual, "&&" => IrOperation.And, "||" => IrOperation.Or,
        _ => throw new CompilationException($"Unsupported operator: {op}")
    };

    private void Conditional(IfExpression expression) {
        var endings = new List<int>();
        Node(expression.Condition);
        int next = Emit(IrOperation.JumpIfFalse);
        Node(expression.Consequence); endings.Add(Emit(IrOperation.Jump)); Patch(next);
        if (expression.AlternativeConditions != null) {
            for (int i = 0; i < expression.AlternativeConditions.Length; i++) {
                Node(expression.AlternativeConditions[i]); next = Emit(IrOperation.JumpIfFalse);
                Node(expression.Alternatives[i]); endings.Add(Emit(IrOperation.Jump)); Patch(next);
            }
        }
        if (expression.LastResort != null) Node(expression.LastResort); else Emit(IrOperation.Null);
        foreach (int ending in endings) Patch(ending);
    }

    private void Loop(ForLoopLiteral loop) {
        int enter = Emit(IrOperation.EnterLoop, loop.DeclareStatement == null ? -1 : Name(loop.DeclareStatement.Name.Value));
        if (loop.DeclareStatement != null) { Node(loop.DeclareStatement); Emit(IrOperation.Pop); }
        int condition = code.Count;
        // A loop without a condition runs until 中斷 / break.
        if (loop.ConditionalExpression != null) Node(loop.ConditionalExpression); else Value(RepeatedPrimitives.TRUE);
        int end = Emit(IrOperation.LoopCondition);
        breaks.Push(new List<int>()); continues.Push(new List<int>());
        if (loop.Body != null) { Node(loop.Body); breaks.Peek().Add(Emit(IrOperation.JumpIfBreak)); Emit(IrOperation.Pop); }
        // The next iteration starts before the update, so closures made in this iteration keep its value of the loop
        // variable (迴圈 (變數 i = 0; ...; i++) makes closures that see 0, 1, 2, ...).
        foreach (int jump in continues.Pop()) Patch(jump);
        Emit(IrOperation.NextIteration);
        if (loop.ValueChangeStatement != null) { Node(loop.ValueChangeStatement); Emit(IrOperation.Pop); }
        Emit(IrOperation.Jump, condition);
        Patch(end);
        foreach (int jump in breaks.Pop()) Patch(jump);
        int leave = Emit(IrOperation.LeaveLoop);
        loops.Add((enter, condition, leave, types != null && types.Loops.TryGetValue(loop, out var region) ? region : null));
    }
}
