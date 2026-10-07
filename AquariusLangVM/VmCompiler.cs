using System;
using System.Collections.Generic;
using System.Linq;
using AquariusLang.ast;
using AquariusLang.evaluator;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;

namespace AquariusLang.VM;

public sealed class VmCompilationException : Exception {
    public VmCompilationException(string message) : base(message) { }
}

/// <summary>Translates syntax trees to stack instructions. No AST evaluation is used by the VM.</summary>
public sealed class VmCompiler {
    private readonly List<Instruction> code = new();
    private readonly List<object> constants = new();
    private readonly Dictionary<string, int> names = new();
    private readonly Stack<List<int>> breaks = new();

    public Bytecode Compile(string source) {
        var lexer = Lexer.NewInstance(source);
        var parser = Parser.NewInstance(lexer);
        var tree = parser.ParseAST();
        if (lexer.Errors.Count > 0 || parser.Errors.Count > 0)
            throw new VmCompilationException(string.Join("\n", lexer.Errors.Select(e => e.Message).Concat(parser.Errors)));
        return Compile(tree);
    }

    public Bytecode Compile(INode node) {
        if (node == null) throw new ArgumentNullException(nameof(node));
        code.Clear(); constants.Clear(); names.Clear(); breaks.Clear();
        Node(node);
        return new Bytecode(code.ToArray(), constants.ToArray(), !(node is AbstractSyntaxTree));
    }

    private int Emit(OpCode op, int operand = 0) { code.Add(new Instruction(op, operand)); return code.Count - 1; }
    private void Patch(int instruction) => code[instruction] = new Instruction(code[instruction].Code, code.Count);
    private int Constant(object value) { constants.Add(value); return constants.Count - 1; }
    private int Name(string name) {
        if (!names.TryGetValue(name, out int index)) names[name] = index = Constant(name);
        return index;
    }
    private void Value(IObject value) => Emit(OpCode.Constant, Constant(value));
    private void Error(string message) => Emit(OpCode.Error, Constant(message));

    private void Statements(IStatement[] statements, bool block = false) {
        var endings = new List<int>();
        if (statements.Length == 0) Emit(OpCode.Void);
        for (int i = 0; i < statements.Length; i++) {
            if (i > 0) {
                if (block) endings.Add(Emit(OpCode.JumpIfBreak));
                Emit(OpCode.Pop);
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
                Node(declaration.Value); Emit(OpCode.Declare, Name(declaration.Name.Value)); break;
            case ReturnStatement returned: Node(returned.ReturnValue); Emit(OpCode.Return); break;
            case BreakStatement _:
                if (breaks.Count > 0) breaks.Peek().Add(Emit(OpCode.Break));
                else Value(RepeatedPrimitives.BREAK);
                break;
            case IntegerLiteral integer: Value(new IntegerObj(integer.Value)); break;
            case FloatLiteral floating: Value(new FloatObj(floating.Value)); break;
            case DoubleLiteral number: Value(new DoubleObj(number.Value)); break;
            case BooleanLiteral boolean: Value(boolean.Value ? RepeatedPrimitives.TRUE : RepeatedPrimitives.FALSE); break;
            case StringLiteral text: Value(new StringObj(text.Value)); break;
            case Identifier identifier: Emit(OpCode.Load, Name(identifier.Value)); break;
            case IncrementExpression increment:
                if (increment.Operand is Identifier variable)
                    Emit(increment.IsPrefix ? OpCode.IncrementPrefix : OpCode.IncrementPostfix, Name(variable.Value));
                else Error("++ 的運算元必須是變數名稱。");
                break;
            case PrefixExpression prefix:
                Node(prefix.Right);
                if (prefix.Operator == "!") Emit(OpCode.Not);
                else if (prefix.Operator == "-") Emit(OpCode.Negate);
                else throw new VmCompilationException($"Unsupported prefix operator: {prefix.Operator}");
                break;
            case InfixExpression infix: Infix(infix); break;
            case IfExpression conditional: Conditional(conditional); break;
            case ForLoopLiteral loop: Loop(loop); break;
            case FunctionLiteral function: Emit(OpCode.Closure, Constant(new FunctionCode(function))); break;
            case CallExpression call:
                Node(call.Function); Arguments(call.Arguments); Emit(OpCode.Call, call.Arguments.Length); break;
            case ArrayLiteral array: Arguments(array.Elements); Emit(OpCode.Array, array.Elements.Length); break;
            case HashLiteral hash:
                foreach (var pair in hash.Pairs) { Node(pair.Key); Emit(OpCode.CheckHashKey); Node(pair.Value); }
                Emit(OpCode.Hash, hash.Pairs.Count); break;
            case IndexExpression index: Node(index.Left); Node(index.Index); Emit(OpCode.Index); break;
            default: throw new VmCompilationException($"Unsupported syntax node: {node.GetType().Name}");
        }
    }

    private void Arguments(IExpression[] arguments) { foreach (var argument in arguments) Node(argument); }

    private void Infix(InfixExpression expression) {
        string op = expression.Operator;
        if (op == "=" && expression.Left is IndexExpression index) {
            Node(index.Left); Node(index.Index); Emit(OpCode.CheckArrayWrite);
            Node(expression.Right); Emit(OpCode.WriteIndex); return;
        }
        Node(expression.Left);
        if (op == ".") {
            if (expression.Right is Identifier member) Emit(OpCode.Member, Name(member.Value));
            else if (expression.Right is CallExpression call) {
                if (call.Function is Identifier function) Emit(OpCode.MemberFunction, Name(function.Value));
                else Emit(OpCode.ResolveMemberFunction, Constant(new VmCompiler().Compile(call.Function)));
                Arguments(call.Arguments); Emit(OpCode.Call, call.Arguments.Length);
            } else throw new VmCompilationException("Unsupported module member expression.");
            return;
        }
        Node(expression.Right);
        if (op == "=" || op == "+=" || op == "-=" || op == "*=" || op == "/=") {
            if (!(expression.Left is Identifier variable)) {
                Error($"Left operand for {op} operator not identifier. Got={expression.Left.GetType()}"); return;
            }
            if (op == "=") Emit(OpCode.Assign, Name(variable.Value));
            else Emit(OpCode.CompoundAssign, Constant(new Assignment(variable.Value, BinaryCode(op.Substring(0, 1)))));
            return;
        }
        Emit(BinaryCode(op));
    }

    private static OpCode BinaryCode(string op) => op switch {
        "+" => OpCode.Add, "-" => OpCode.Subtract, "*" => OpCode.Multiply, "/" => OpCode.Divide,
        "<" => OpCode.Less, ">" => OpCode.Greater, "<=" => OpCode.LessEqual, ">=" => OpCode.GreaterEqual,
        "==" => OpCode.Equal, "!=" => OpCode.NotEqual, "&&" => OpCode.And, "||" => OpCode.Or,
        _ => throw new VmCompilationException($"Unsupported operator: {op}")
    };

    private void Conditional(IfExpression expression) {
        var endings = new List<int>();
        Node(expression.Condition);
        int next = Emit(OpCode.JumpIfFalse);
        Node(expression.Consequence); endings.Add(Emit(OpCode.Jump)); Patch(next);
        if (expression.AlternativeConditions != null) {
            for (int i = 0; i < expression.AlternativeConditions.Length; i++) {
                Node(expression.AlternativeConditions[i]); next = Emit(OpCode.JumpIfFalse);
                Node(expression.Alternatives[i]); endings.Add(Emit(OpCode.Jump)); Patch(next);
            }
        }
        if (expression.LastResort != null) Node(expression.LastResort); else Emit(OpCode.Null);
        foreach (int ending in endings) Patch(ending);
    }

    private void Loop(ForLoopLiteral loop) {
        Emit(OpCode.EnterLoop, loop.DeclareStatement == null ? -1 : Name(loop.DeclareStatement.Name.Value));
        if (loop.DeclareStatement != null) { Node(loop.DeclareStatement); Emit(OpCode.Pop); }
        int condition = code.Count;
        if (loop.ConditionalExpression != null) Node(loop.ConditionalExpression); else Emit(OpCode.Void);
        int end = Emit(OpCode.LoopCondition);
        breaks.Push(new List<int>());
        if (loop.Body != null) { Node(loop.Body); breaks.Peek().Add(Emit(OpCode.JumpIfBreak)); Emit(OpCode.Pop); }
        if (loop.ValueChangeStatement != null) { Node(loop.ValueChangeStatement); Emit(OpCode.Pop); }
        Emit(OpCode.NextIteration); Emit(OpCode.Jump, condition);
        Patch(end);
        foreach (int jump in breaks.Pop()) Patch(jump);
        Emit(OpCode.LeaveLoop);
    }
}
