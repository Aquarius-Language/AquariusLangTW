using System;
using System.Collections.Generic;
using System.Linq;
using AquariusLang.ast;
using AquariusLang.runtime;
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
        code.Clear(); constants.Clear(); names.Clear(); breaks.Clear();
        Node(node);
        return new LoweredProgram(code.ToArray(), constants.ToArray(), !(node is AbstractSyntaxTree));
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
                if (declaration.Alias != null) Emit(IrOperation.Duplicate);
                Emit(IrOperation.Declare, Name(declaration.Name.Value));
                if (declaration.Alias != null) {
                    Emit(IrOperation.Pop);
                    Emit(IrOperation.Declare, Name(declaration.Alias.Value));
                }
                break;
            case ReturnStatement returned: Node(returned.ReturnValue); Emit(IrOperation.Return); break;
            case BreakStatement _:
                if (breaks.Count > 0) breaks.Peek().Add(Emit(IrOperation.Break));
                else Value(RepeatedPrimitives.BREAK);
                break;
            case IntegerLiteral integer: Value(new IntegerObj(integer.Value)); break;
            case FloatLiteral floating: Value(new FloatObj(floating.Value)); break;
            case DoubleLiteral number: Value(new DoubleObj(number.Value)); break;
            case BooleanLiteral boolean: Value(boolean.Value ? RepeatedPrimitives.TRUE : RepeatedPrimitives.FALSE); break;
            case StringLiteral text: Value(new StringObj(text.Value)); break;
            case Identifier identifier: Emit(IrOperation.Load, Name(identifier.Value)); break;
            case IncrementExpression increment:
                if (increment.Operand is Identifier variable)
                    Emit(increment.IsPrefix ? IrOperation.IncrementPrefix : IrOperation.IncrementPostfix, Name(variable.Value));
                else Error("++ 的運算元必須是變數名稱。");
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
            case FunctionLiteral function: Emit(IrOperation.Closure, Constant(new FunctionCode(function))); break;
            case CallExpression call:
                Node(call.Function); Arguments(call.Arguments); Emit(IrOperation.Call, call.Arguments.Length); break;
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
        if (op == "=" && expression.Left is IndexExpression index) {
            Node(index.Left); Node(index.Index); Emit(IrOperation.CheckArrayWrite);
            Node(expression.Right); Emit(IrOperation.WriteIndex); return;
        }
        Node(expression.Left);
        if (op == ".") {
            if (expression.Right is Identifier member) Emit(IrOperation.Member, Name(member.Value));
            else if (expression.Right is CallExpression call) {
                if (call.Function is Identifier function) Emit(IrOperation.MemberFunction, Name(function.Value));
                else Emit(IrOperation.ResolveMemberFunction, Constant(new LoweringCompiler().Compile(call.Function)));
                Arguments(call.Arguments); Emit(IrOperation.Call, call.Arguments.Length);
            } else throw new CompilationException("Unsupported module member expression.");
            return;
        }
        Node(expression.Right);
        if (op == "=" || op == "+=" || op == "-=" || op == "*=" || op == "/=") {
            if (!(expression.Left is Identifier variable)) {
                Error($"Left operand for {op} operator not identifier. Got={expression.Left.GetType()}"); return;
            }
            if (op == "=") Emit(IrOperation.Assign, Name(variable.Value));
            else Emit(IrOperation.CompoundAssign, Constant(new Assignment(variable.Value, BinaryCode(op.Substring(0, 1)))));
            return;
        }
        Emit(BinaryCode(op));
    }

    private static IrOperation BinaryCode(string op) => op switch {
        "+" => IrOperation.Add, "-" => IrOperation.Subtract, "*" => IrOperation.Multiply, "/" => IrOperation.Divide,
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
        Emit(IrOperation.EnterLoop, loop.DeclareStatement == null ? -1 : Name(loop.DeclareStatement.Name.Value));
        if (loop.DeclareStatement != null) { Node(loop.DeclareStatement); Emit(IrOperation.Pop); }
        int condition = code.Count;
        if (loop.ConditionalExpression != null) Node(loop.ConditionalExpression); else Emit(IrOperation.Void);
        int end = Emit(IrOperation.LoopCondition);
        breaks.Push(new List<int>());
        if (loop.Body != null) { Node(loop.Body); breaks.Peek().Add(Emit(IrOperation.JumpIfBreak)); Emit(IrOperation.Pop); }
        if (loop.ValueChangeStatement != null) { Node(loop.ValueChangeStatement); Emit(IrOperation.Pop); }
        Emit(IrOperation.NextIteration); Emit(IrOperation.Jump, condition);
        Patch(end);
        foreach (int jump in breaks.Pop()) Patch(jump);
        Emit(IrOperation.LeaveLoop);
    }
}
