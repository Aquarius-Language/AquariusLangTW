using System;
using System.Collections.Generic;
using System.Text;
using AquariusLang.ast;

namespace AquariusLang.VM;

/// <summary>The instruction set executed by the Aquarius stack machine.</summary>
// Numeric opcode values are persisted in .rius files; do not reorder them without a format-version change.
public enum OpCode : byte {
    Constant, Void, Null, Pop, Load, Declare, Assign,
    Add, Subtract, Multiply, Divide, Less, Greater, LessEqual, GreaterEqual,
    Equal, NotEqual, And, Or, Negate, Not, IncrementPrefix, IncrementPostfix,
    CompoundAssign, Jump, JumpIfFalse, JumpIfBreak, Closure, Call, Return,
    Array, Hash, CheckHashKey, Index, CheckArrayWrite, WriteIndex, Member, MemberFunction, ResolveMemberFunction,
    EnterLoop, LoopCondition, NextIteration, LeaveLoop, Break, Error,
    Duplicate
}

public readonly struct Instruction {
    public OpCode Code { get; }
    public int Operand { get; }
    public Instruction(OpCode code, int operand = 0) { Code = code; Operand = operand; }
    public override string ToString() => $"{Code} {Operand}";
}

/// <summary>Immutable compiled program; reusable with independent environments and VM instances.</summary>
public sealed class Bytecode {
    internal Instruction[] Code { get; }
    internal object[] Pool { get; }
    internal bool WrapReturn { get; }
    public IReadOnlyList<Instruction> Instructions { get; }
    public IReadOnlyList<object> Constants { get; }

    internal Bytecode(Instruction[] code, object[] constants, bool wrapReturn) {
        Code = code; Pool = constants; WrapReturn = wrapReturn;
        Instructions = System.Array.AsReadOnly(code);
        Constants = System.Array.AsReadOnly(constants);
    }

    /// <summary>Readable VM language, including nested function bodies and jump addresses.</summary>
    public string Disassemble() {
        var text = new StringBuilder();
        for (int i = 0; i < Code.Length; i++) {
            var instruction = Code[i];
            text.Append($"{i:D4} {instruction}");
            if (instruction.Code is OpCode.Constant or OpCode.Load or OpCode.Declare or OpCode.Assign or OpCode.Member or OpCode.MemberFunction or OpCode.Error) {
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
    internal string? BodyDisplay { get; }
    internal Bytecode BodyCode { get; }
    internal FunctionCode(FunctionLiteral function) {
        Parameters = function.Parameters;
        Body = function.Body;
        BodyCode = new VmCompiler().Compile(function.Body);
    }
    internal FunctionCode(Identifier[] parameters, string bodyDisplay, Bytecode bodyCode) {
        Parameters = parameters; BodyDisplay = bodyDisplay; BodyCode = bodyCode;
    }
}

internal sealed class Assignment {
    internal string Name { get; }
    internal OpCode Operation { get; }
    internal Assignment(string name, OpCode operation) { Name = name; Operation = operation; }
}
