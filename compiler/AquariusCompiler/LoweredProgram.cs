using System;
using System.Collections.Generic;
using System.Text;
using AquariusLang.ast;

namespace AquariusLang.Compiler;

/// <summary>Compiler-only operations used to construct the control-flow graph. Never serialized or interpreted.</summary>
public enum IrOperation : byte {
    Constant, Void, Null, Pop, Load, Declare, Assign,
    Add, Subtract, Multiply, Divide, Less, Greater, LessEqual, GreaterEqual,
    Equal, NotEqual, And, Or, Negate, Not, IncrementPrefix, IncrementPostfix,
    CompoundAssign, Jump, JumpIfFalse, JumpIfBreak, Closure, Call, Return,
    Array, Hash, CheckHashKey, Index, CheckArrayWrite, WriteIndex, Member, MemberFunction, ResolveMemberFunction,
    EnterLoop, LoopCondition, NextIteration, LeaveLoop, Break, Error,
    Duplicate, Modulo, CheckType,
    // Structs: a declaration's type, a construction with named arguments, field assignment, compound field assignment
    // and a struct annotation check.
    StructType, CallNamed, SetMember, CompoundMember, CheckStruct
}

public readonly struct Instruction {
    public IrOperation Code { get; }
    public int Operand { get; }
    public Instruction(IrOperation code, int operand = 0) { Code = code; Operand = operand; }
    public override string ToString() => $"{Code} {Operand}";
}

/// <summary>Immutable lowered intermediate representation consumed by the WebAssembly emitter.</summary>
public sealed class LoweredProgram {
    internal Instruction[] Code { get; }
    /// <summary>The number of named arguments of a CallNamed instruction (its constant lists the names).</summary>
    internal int NamedArguments(int operand) => ((string)Pool[operand]).Split('').Length;
    internal object[] Pool { get; }
    internal bool WrapReturn { get; }
    public IReadOnlyList<Instruction> Instructions { get; }
    public IReadOnlyList<object> Constants { get; }

    /// <summary>Each loop's EnterLoop, condition (loop head) and LeaveLoop positions, with its native region
    /// candidate from the TypeChecker (outer names it resolves outside itself).</summary>
    internal (int Enter, int Head, int Leave, NativeSignature? Region)[] Loops { get; }

    internal LoweredProgram(Instruction[] code, object[] constants, bool wrapReturn, (int, int, int, NativeSignature?)[]? loops = null) {
        Code = code; Pool = constants; WrapReturn = wrapReturn; Loops = loops ?? System.Array.Empty<(int, int, int, NativeSignature?)>();
        Instructions = System.Array.AsReadOnly(code);
        Constants = System.Array.AsReadOnly(constants);
    }

    /// <summary>Compiler diagnostics, including nested function bodies and branch destinations.</summary>
    public string Disassemble() {
        var text = new StringBuilder();
        for (int i = 0; i < Code.Length; i++) {
            var instruction = Code[i];
            text.Append($"{i:D4} {instruction}");
            if (instruction.Code is IrOperation.Constant or IrOperation.Load or IrOperation.Declare or IrOperation.Assign or IrOperation.Member or IrOperation.MemberFunction or IrOperation.Error) {
                var value = Pool[instruction.Operand];
                text.Append(" ; ").Append(value is AquariusLang.Object.IObject obj ? obj.Type() + " " + obj.Inspect() : value.ToString());
            }
            text.AppendLine();
        }
        for (int i = 0; i < Pool.Length; i++) {
            if (Pool[i] is FunctionCode function) {
                text.AppendLine($"function {i}:");
                text.Append(function.BodyCode.Disassemble());
            }
        }
        return text.ToString();
    }
}

internal sealed class FunctionCode {
    internal Identifier[] Parameters { get; }
    internal BlockStatement? Body { get; }
    internal LoweredProgram BodyCode { get; }
    /// <summary>The literal and, when it is fully typed, its native-tier signature.</summary>
    internal FunctionLiteral Literal { get; }
    internal NativeSignature? Native { get; }
    internal FunctionCode(FunctionLiteral function, TypeChecker? types = null) {
        Literal = function;
        Native = types != null && types.Natives.TryGetValue(function, out var native) ? native : null;
        Parameters = function.Parameters;
        Body = function.Body;
        BodyCode = new LoweringCompiler(types).CompileFunction(function);
    }
}

internal sealed class Assignment {
    internal string Name { get; }
    internal IrOperation Operation { get; }
    internal Assignment(string name, IrOperation operation) { Name = name; Operation = operation; }
}
