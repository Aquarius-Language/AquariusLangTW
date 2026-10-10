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
    Duplicate
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
    internal object[] Pool { get; }
    internal bool WrapReturn { get; }
    public IReadOnlyList<Instruction> Instructions { get; }
    public IReadOnlyList<object> Constants { get; }

    internal LoweredProgram(Instruction[] code, object[] constants, bool wrapReturn) {
        Code = code; Pool = constants; WrapReturn = wrapReturn;
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
    internal string? BodyDisplay { get; }
    internal LoweredProgram BodyCode { get; }
    internal FunctionCode(FunctionLiteral function) {
        Parameters = function.Parameters;
        Body = function.Body;
        BodyDisplay = function.Body.String();
        BodyCode = new LoweringCompiler().Compile(function.Body);
    }
    internal FunctionCode(Identifier[] parameters, string bodyDisplay, LoweredProgram bodyCode) {
        Parameters = parameters; BodyDisplay = bodyDisplay; BodyCode = bodyCode;
    }
}

internal sealed class Assignment {
    internal string Name { get; }
    internal IrOperation Operation { get; }
    internal Assignment(string name, IrOperation operation) { Name = name; Operation = operation; }
}
