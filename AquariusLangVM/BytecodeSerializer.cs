using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AquariusLang.ast;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.token;

namespace AquariusLang.VM;

/// <summary>Portable, versioned .rius instructions and constants. Loading never parses source.</summary>
public static class BytecodeSerializer {
    public const int FormatVersion = 1;
    public const int MaxFileBytes = 64 * 1024 * 1024;
    private const int MaxItems = 1_000_000;
    private const int MaxStringBytes = 4 * 1024 * 1024;
    private const int MaxDepth = 128;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static void Write(Bytecode program, Stream output) {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(output);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Utf8, true)) {
            writer.Write(new byte[] { (byte)'R', (byte)'I', (byte)'U', (byte)'S' });
            writer.Write(FormatVersion);
            int remaining = MaxItems;
            WriteProgram(writer, program, 0, ref remaining);
        }
        if (buffer.Length > MaxFileBytes) throw Invalid("Bytecode exceeds the size limit.");
        buffer.Position = 0;
        buffer.CopyTo(output);
    }

    public static Bytecode Read(Stream input) {
        ArgumentNullException.ThrowIfNull(input);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = input.Read(chunk, 0, chunk.Length)) > 0) {
            if (buffer.Length + count > MaxFileBytes) throw Invalid("Bytecode exceeds the size limit.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        using var reader = new BinaryReader(buffer, Utf8, true);
        try {
            if (!reader.ReadBytes(4).SequenceEqual(new byte[] { (byte)'R', (byte)'I', (byte)'U', (byte)'S' }))
                throw Invalid("Missing RIUS header.");
            if (reader.ReadInt32() != FormatVersion) throw Invalid("Unsupported .rius version.");
            int remaining = MaxItems;
            var program = ReadProgram(reader, 0, ref remaining);
            if (buffer.Position != buffer.Length) throw Invalid("Trailing bytecode data.");
            return program;
        } catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException) {
            throw new InvalidDataException("Invalid .rius: truncated or invalid data.", error);
        }
    }

    private static void Budget(int count, int depth, ref int remaining) {
        if (depth > MaxDepth || count < 0 || count > remaining) throw Invalid("Bytecode structure exceeds the limit.");
        remaining -= count;
    }

    private static void WriteProgram(BinaryWriter writer, Bytecode program, int depth, ref int remaining) {
        Budget(program.Code.Length + program.Pool.Length, depth, ref remaining);
        writer.Write(program.WrapReturn);
        writer.Write(program.Code.Length);
        foreach (var instruction in program.Code) { writer.Write((byte)instruction.Code); writer.Write(instruction.Operand); }
        writer.Write(program.Pool.Length);
        foreach (var value in program.Pool) {
            switch (value) {
                case string name: writer.Write((byte)0); WriteString(writer, name); break;
                case IntegerObj integer: writer.Write((byte)1); writer.Write(integer.Value); break;
                case FloatObj number: writer.Write((byte)2); writer.Write(number.Value); break;
                case DoubleObj number: writer.Write((byte)3); writer.Write(number.Value); break;
                case BooleanObj boolean: writer.Write((byte)4); writer.Write(boolean.Value); break;
                case StringObj text: writer.Write((byte)5); WriteString(writer, text.Value); break;
                case NullObj: writer.Write((byte)6); break;
                case BreakObj: writer.Write((byte)7); break;
                case Assignment assignment:
                    writer.Write((byte)8); WriteString(writer, assignment.Name); writer.Write((byte)assignment.Operation); break;
                case FunctionCode function:
                    writer.Write((byte)9);
                    Budget(function.Parameters.Length, depth, ref remaining);
                    writer.Write(function.Parameters.Length);
                    foreach (var parameter in function.Parameters) WriteString(writer, parameter.Value);
                    WriteString(writer, function.BodyDisplay ?? function.Body!.String());
                    WriteProgram(writer, function.BodyCode, depth + 1, ref remaining); break;
                case Bytecode nested:
                    writer.Write((byte)10); WriteProgram(writer, nested, depth + 1, ref remaining); break;
                default: throw Invalid($"Unsupported constant: {value.GetType().Name}.");
            }
        }
    }

    private static Bytecode ReadProgram(BinaryReader reader, int depth, ref int remaining) {
        bool wrapReturn = ReadBoolean(reader);
        int count = reader.ReadInt32();
        Budget(count, depth, ref remaining);
        if (count > (reader.BaseStream.Length - reader.BaseStream.Position) / 5) throw Invalid("Invalid instruction count.");
        var code = new Instruction[count];
        for (int i = 0; i < count; i++) code[i] = new Instruction((OpCode)reader.ReadByte(), reader.ReadInt32());
        count = reader.ReadInt32();
        Budget(count, depth, ref remaining);
        if (count > reader.BaseStream.Length - reader.BaseStream.Position) throw Invalid("Invalid constant count.");
        var pool = new object[count];
        for (int i = 0; i < count; i++) {
            pool[i] = reader.ReadByte() switch {
                0 => ReadString(reader),
                1 => new IntegerObj(reader.ReadInt32()),
                2 => new FloatObj(reader.ReadSingle()),
                3 => new DoubleObj(reader.ReadDouble()),
                4 => ReadBoolean(reader) ? RepeatedPrimitives.TRUE : RepeatedPrimitives.FALSE,
                5 => new StringObj(ReadString(reader)),
                6 => RepeatedPrimitives.NULL,
                7 => RepeatedPrimitives.BREAK,
                8 => ReadAssignment(reader),
                9 => ReadFunction(reader, depth, ref remaining),
                10 => ReadProgram(reader, depth + 1, ref remaining),
                _ => throw Invalid("Unknown constant type.")
            };
        }
        var program = new Bytecode(code, pool, wrapReturn);
        Validate(program);
        return program;
    }

    private static Assignment ReadAssignment(BinaryReader reader) {
        string name = ReadString(reader);
        var operation = (OpCode)reader.ReadByte();
        if (operation is not (OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide))
            throw Invalid("Invalid assignment operation.");
        return new Assignment(name, operation);
    }

    private static FunctionCode ReadFunction(BinaryReader reader, int depth, ref int remaining) {
        int count = reader.ReadInt32();
        Budget(count, depth, ref remaining);
        var parameters = new Identifier[count];
        for (int i = 0; i < count; i++) {
            string name = ReadString(reader);
            parameters[i] = new Identifier(new Token { Type = TokenType.IDENT, Literal = name }, name);
        }
        string display = ReadString(reader);
        return new FunctionCode(parameters, display, ReadProgram(reader, depth + 1, ref remaining));
    }

    private static bool ReadBoolean(BinaryReader reader) => reader.ReadByte() switch {
        0 => false, 1 => true, _ => throw Invalid("Invalid boolean.")
    };

    private static void WriteString(BinaryWriter writer, string value) {
        var bytes = Utf8.GetBytes(value);
        if (bytes.Length > MaxStringBytes) throw Invalid("String exceeds the size limit.");
        writer.Write(bytes.Length); writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader) {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaxStringBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw Invalid("Invalid string length.");
        return Utf8.GetString(reader.ReadBytes(length));
    }

    private static void Validate(Bytecode program) {
        foreach (var instruction in program.Code) {
            int operand = instruction.Operand;
            if (!Enum.IsDefined(typeof(OpCode), instruction.Code)) throw Invalid("Unknown opcode.");
            object? constant = operand >= 0 && operand < program.Pool.Length ? program.Pool[operand] : null;
            bool valid = instruction.Code switch {
                OpCode.Constant => constant is IObject,
                OpCode.Load or OpCode.Declare or OpCode.Assign or OpCode.Member or OpCode.MemberFunction or
                OpCode.IncrementPrefix or OpCode.IncrementPostfix or OpCode.Error => constant is string,
                OpCode.Closure => constant is FunctionCode,
                OpCode.CompoundAssign => constant is Assignment,
                OpCode.ResolveMemberFunction => constant is Bytecode,
                OpCode.EnterLoop => operand == -1 || constant is string,
                OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfBreak or OpCode.LoopCondition or OpCode.Break => operand >= 0 && operand <= program.Code.Length,
                OpCode.Call or OpCode.Array or OpCode.Hash => operand >= 0 && operand <= MaxItems,
                _ => operand == 0
            };
            if (!valid) throw Invalid($"Invalid operand for {instruction.Code}.");
        }
        ValidateStack(program);
    }

    // Check reachable control flow before instructions can reach the VM's operand stack.
    private static void ValidateStack(Bytecode program) {
        var pending = new Queue<(int Position, int Depth, int[] Loops)>();
        var seen = new Dictionary<int, (int Depth, int[] Loops)>();
        pending.Enqueue((0, 0, System.Array.Empty<int>()));
        while (pending.Count > 0) {
            var state = pending.Dequeue();
            if (seen.TryGetValue(state.Position, out var previous)) {
                bool resetsStack = state.Position < program.Code.Length && program.Code[state.Position].Code == OpCode.LeaveLoop;
                if ((!resetsStack && previous.Depth != state.Depth) || !previous.Loops.SequenceEqual(state.Loops))
                    throw Invalid("Inconsistent stack or loop scope at a branch.");
                continue;
            }
            seen[state.Position] = (state.Depth, state.Loops);
            if (state.Position == program.Code.Length) {
                if (state.Depth > 1 || state.Loops.Length != 0) throw Invalid("Invalid final stack or loop scope.");
                continue;
            }
            var instruction = program.Code[state.Position];
            int operand = instruction.Operand;
            int needed = instruction.Code switch {
                OpCode.Assign or OpCode.CompoundAssign or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or
                OpCode.Less or OpCode.Greater or OpCode.LessEqual or OpCode.GreaterEqual or OpCode.Equal or OpCode.NotEqual or
                OpCode.And or OpCode.Or or OpCode.Index or OpCode.CheckArrayWrite => 2,
                OpCode.WriteIndex => 3,
                OpCode.Call => operand + 1,
                OpCode.Array => operand,
                OpCode.Hash => operand * 2,
                OpCode.Pop or OpCode.Declare or OpCode.Negate or OpCode.Not or OpCode.JumpIfFalse or OpCode.JumpIfBreak or
                OpCode.Return or OpCode.CheckHashKey or OpCode.Member or OpCode.MemberFunction or OpCode.ResolveMemberFunction or OpCode.LoopCondition => 1,
                _ => 0
            };
            int loopBase = state.Loops.Length == 0 ? 0 : state.Loops[^1];
            if (state.Depth - loopBase < needed) throw Invalid("Operand stack underflow.");
            int depth = state.Depth;
            var loops = state.Loops;
            switch (instruction.Code) {
                case OpCode.Constant: case OpCode.Void: case OpCode.Null: case OpCode.Load: case OpCode.Closure:
                case OpCode.IncrementPrefix: case OpCode.IncrementPostfix: depth++; break;
                case OpCode.Pop: case OpCode.JumpIfFalse: case OpCode.LoopCondition: depth--; break;
                case OpCode.Call: case OpCode.Array: case OpCode.Hash: depth += 1 - needed; break;
                case OpCode.WriteIndex: depth -= 2; break;
                case OpCode.Assign: case OpCode.CompoundAssign: case OpCode.Add: case OpCode.Subtract: case OpCode.Multiply:
                case OpCode.Divide: case OpCode.Less: case OpCode.Greater: case OpCode.LessEqual: case OpCode.GreaterEqual:
                case OpCode.Equal: case OpCode.NotEqual: case OpCode.And: case OpCode.Or: case OpCode.Index: depth--; break;
                case OpCode.EnterLoop:
                    if (loops.Length >= MaxDepth) throw Invalid("Loop nesting exceeds the limit.");
                    loops = loops.Append(depth).ToArray(); break;
                case OpCode.NextIteration: case OpCode.Break: case OpCode.LeaveLoop:
                    if (loops.Length == 0) throw Invalid("Missing loop scope.");
                    if (instruction.Code == OpCode.Break) depth = loopBase;
                    if (instruction.Code == OpCode.LeaveLoop) { depth = loopBase + 1; loops = loops.Take(loops.Length - 1).ToArray(); }
                    break;
            }
            if (instruction.Code is OpCode.Return or OpCode.Error) continue;
            if (instruction.Code is OpCode.Jump or OpCode.Break) pending.Enqueue((operand, depth, loops));
            else {
                pending.Enqueue((state.Position + 1, depth, loops));
                if (instruction.Code is OpCode.JumpIfFalse or OpCode.JumpIfBreak or OpCode.LoopCondition)
                    pending.Enqueue((operand, depth, loops));
            }
        }
    }

    private static InvalidDataException Invalid(string message) => new("Invalid .rius: " + message);
}
