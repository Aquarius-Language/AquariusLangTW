using System;
using System.Collections.Generic;
using AquariusLang.runtime;
using AquariusLang.Object;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.VM;

/// <summary>Runs bytecode with an operand stack and explicit (non-recursive) Aquarius call frames.</summary>
public sealed class VirtualMachine {
    private readonly Builtins builtins;
    public VirtualMachine(Builtins? builtins = null) { this.builtins = builtins ?? new Builtins(); }

    // Every execution owns its stacks, allowing native callbacks to reenter the same VM safely.
    public IObject Execute(Bytecode program, AquaEnvironment? environment = null) {
        if (program == null) throw new ArgumentNullException(nameof(program));
        return new Execution(builtins).Run(program, environment ?? AquaEnvironment.NewEnvironment())!;
    }

    public IObject Invoke(IObject function, params IObject[] arguments) {
        if (function is BuiltinObj builtin) return builtin.Fn(arguments);
        if (!(function is FunctionObj closure)) return new ErrorObj($"Not a function: {function?.Type() ?? "NULL"}");
        if (closure.Parameters.Length != arguments.Length)
            return new ErrorObj($"Function expects {closure.Parameters.Length} arguments, got {arguments.Length}.");
        var environment = AquaEnvironment.NewEnclosedEnvironment(closure.Env);
        for (int i = 0; i < arguments.Length; i++) environment.Create(closure.Parameters[i].Value, arguments[i]);
        IObject result = new Execution(VmEvaluator.GetFunctionBuiltins(closure) ?? builtins).Run(VmEvaluator.GetFunctionCode(closure), environment)!;
        return result is ReturnValueObj returned ? returned.Value : result;
    }

    private sealed class Frame {
        internal readonly Bytecode Program;
        internal readonly int Base;
        internal int Position;
        internal AquaEnvironment Environment;
        internal readonly Builtins Builtins;
        internal readonly Stack<LoopScope> Loops = new();
        internal Frame(Bytecode program, AquaEnvironment environment, int stackBase, Builtins builtins) {
            Program = program; Environment = environment; Base = stackBase; Builtins = builtins;
        }
    }
    private sealed class LoopScope {
        internal readonly AquaEnvironment Outer;
        internal readonly int Base;
        internal readonly string? Binding;
        internal LoopScope(AquaEnvironment outer, int stackBase, string? binding) { Outer = outer; Base = stackBase; Binding = binding; }
    }
    private sealed class RuntimeError : Exception {
        internal readonly ErrorObj Error;
        internal RuntimeError(ErrorObj error) { Error = error; }
    }

    private sealed class Execution {
        private readonly Builtins builtins;
        private IObject?[] stack = new IObject?[128];
        private int count;
        private readonly Stack<Frame> frames = new();
        internal Execution(Builtins builtins) { this.builtins = builtins; }
        private void Push(IObject? value) {
            if (value is ErrorObj error) throw new RuntimeError(error);
            if (count == stack.Length) System.Array.Resize(ref stack, count * 2);
            stack[count++] = value;
        }
        private IObject? Pop() { var value = stack[--count]; stack[count] = null; return value; }
        private void Reset(int size) { System.Array.Clear(stack, size, count - size); count = size; }
        private void Fail(string message) => throw new RuntimeError(new ErrorObj(message));
        private IObject? Load(string name, AquaEnvironment environment, Builtins builtins) {
            if (builtins.BuiltinFuncs.TryGetValue(name, out var function)) return function;
            if (builtins._Builtins.TryGetValue(name, out var value)) return value;
            value = environment.Get(name, out bool found);
            if (!found) Fail($"Identifier not found: {name}");
            return value;
        }

        internal IObject? Run(Bytecode program, AquaEnvironment environment) {
            frames.Push(new Frame(program, environment, 0, builtins));
            try {
                while (frames.Count > 0) {
                    Frame frame = frames.Peek();
                    if (frame.Position == frame.Program.Code.Length) {
                        IObject? result = count > frame.Base ? Pop() : null;
                        Reset(frame.Base); frames.Pop();
                        if (frames.Count == 0) return result;
                        Push(result); continue;
                    }
                    var instruction = frame.Program.Code[frame.Position++];
                    int operand = instruction.Operand;
                    var pool = frame.Program.Pool;
                    switch (instruction.Code) {
                        case OpCode.Constant: {
                            // Literal objects are mutable to native hosts; never expose the constant pool's prototypes.
                            var value = (IObject)pool[operand];
                            Push(value switch {
                                IntegerObj integer => new IntegerObj(integer.Value), FloatObj number => new FloatObj(number.Value),
                                DoubleObj number => new DoubleObj(number.Value), StringObj text => new StringObj(text.Value), _ => value
                            }); break;
                        }
                        case OpCode.Void: Push(null); break;
                        case OpCode.Null: Push(RepeatedPrimitives.NULL); break;
                        case OpCode.Pop: Pop(); break;
                        case OpCode.Load: Push(Load((string)pool[operand], frame.Environment, frame.Builtins)); break;
                        case OpCode.Declare: frame.Environment.Create((string)pool[operand], Pop()!); Push(null); break;
                        case OpCode.Assign: {
                            var value = Pop(); Pop();
                            Set(frame.Environment, (string)pool[operand], value); Push(null); break;
                        }
                        case OpCode.CompoundAssign: {
                            var right = Pop(); Pop(); var assignment = (Assignment)pool[operand];
                            var value = VmOperations.Compound(assignment.Operation, Load(assignment.Name, frame.Environment, frame.Builtins), right);
                            if (value is ErrorObj error) throw new RuntimeError(error);
                            Set(frame.Environment, assignment.Name, value); Push(null); break;
                        }
                        case OpCode.Add: case OpCode.Subtract: case OpCode.Multiply: case OpCode.Divide:
                        case OpCode.Less: case OpCode.Greater: case OpCode.LessEqual: case OpCode.GreaterEqual:
                        case OpCode.Equal: case OpCode.NotEqual: case OpCode.And: case OpCode.Or: {
                            var right = Pop(); var left = Pop(); Push(VmOperations.Binary(instruction.Code, left, right)); break;
                        }
                        case OpCode.Not: {
                            var value = Pop(); Push(VmOperations.Bool(value is BooleanObj boolean ? !boolean.Value : value == RepeatedPrimitives.NULL)); break;
                        }
                        case OpCode.Negate: {
                            var value = Pop();
                            Push(value is INumberObj number ? VmOperations.Number(-number.GetNumValue(), value) : new ErrorObj($"Unknown operator: -{value?.Type() ?? "NULL"}")); break;
                        }
                        case OpCode.IncrementPrefix: case OpCode.IncrementPostfix: {
                            string name = (string)pool[operand]; var original = frame.Environment.Get(name, out bool exists);
                            if (!exists) Fail($"Identifier not found: {name}");
                            if (!(original is INumberObj)) Fail($"++ 只適用於數值變數，得到 {original?.Type() ?? "NULL"}。");
                            var incremented = VmOperations.Number(((INumberObj)original!).GetNumValue() + 1, original!);
                            Set(frame.Environment, name, incremented);
                            Push(instruction.Code == OpCode.IncrementPrefix ? incremented : original); break;
                        }
                        case OpCode.Jump: frame.Position = operand; break;
                        case OpCode.JumpIfFalse: if (!VmOperations.Truthy(Pop())) frame.Position = operand; break;
                        case OpCode.JumpIfBreak: if (stack[count - 1] is BreakObj) frame.Position = operand; break;
                        case OpCode.Closure: {
                            var function = (FunctionCode)pool[operand];
                            var closure = new FunctionObj(function.Parameters, function.Body, frame.Environment, function.BodyDisplay);
                            VmEvaluator.Register(closure, function.BodyCode, frame.Builtins);
                            Push(closure); break;
                        }
                        case OpCode.Call: Call(frame, operand); break;
                        case OpCode.Return: {
                            var result = Pop(); Reset(frame.Base); frames.Pop();
                            if (frames.Count == 0) return frame.Program.WrapReturn ? new ReturnValueObj(result!) : result;
                            Push(result); break;
                        }
                        case OpCode.Array: Push(new ArrayObj(Arguments(operand))); break;
                        case OpCode.CheckHashKey:
                            if (!(stack[count - 1] is IHashable)) Fail($"Unusable as hash key: {stack[count - 1]?.Type() ?? "NULL"}");
                            break;
                        case OpCode.Hash: {
                            var pairs = new Dictionary<HashKey, HashPair>(); var values = Arguments(operand * 2);
                            for (int i = 0; i < values.Length; i += 2) {
                                if (!(values[i] is IHashable)) Fail($"Unusable as hash key: {values[i]?.Type() ?? "NULL"}");
                                pairs[((IHashable)values[i]).HashKey()] = new HashPair(values[i], values[i + 1]);
                            }
                            Push(new HashObj(pairs)); break;
                        }
                        case OpCode.Index: { var index = Pop(); Push(VmOperations.Index(Pop(), index)); break; }
                        case OpCode.CheckArrayWrite: {
                            var error = VmOperations.CheckArrayWrite(stack[count - 2], stack[count - 1]);
                            if (error != null) throw new RuntimeError(error); break;
                        }
                        case OpCode.WriteIndex: {
                            var value = Pop(); var index = Pop(); var array = Pop();
                            var error = VmOperations.CheckArrayWrite(array, index);
                            if (error != null) throw new RuntimeError(error);
                            ((ArrayObj)array!).Elements[((IntegerObj)index!).Value] = value!; Push(value); break;
                        }
                        case OpCode.Member: case OpCode.MemberFunction: {
                            var receiver = Pop(); string name = (string)pool[operand];
                            if (!(receiver is ModuleObj)) Fail($"Cannot access member of {receiver?.Type() ?? "NULL"}");
                            var module = (ModuleObj)receiver!;
                            if (instruction.Code == OpCode.MemberFunction) Push(Load(name, module._Environment, frame.Builtins));
                            else {
                                var member = module._Environment.Get(name, out bool found);
                                if (!found) Fail($"Module member not found: {name}");
                                Push(member);
                            }
                            break;
                        }
                        case OpCode.ResolveMemberFunction: {
                            var receiver = Pop();
                            if (!(receiver is ModuleObj)) Fail($"Cannot access member of {receiver?.Type() ?? "NULL"}");
                            frames.Push(new Frame((Bytecode)pool[operand], ((ModuleObj)receiver!)._Environment, count, frame.Builtins)); break;
                        }
                        case OpCode.EnterLoop:
                            frame.Loops.Push(new LoopScope(frame.Environment, count, operand < 0 ? null : (string)pool[operand]));
                            frame.Environment = AquaEnvironment.NewEnclosedEnvironment(frame.Environment); break;
                        case OpCode.LoopCondition: {
                            var value = Pop();
                            if (value == null) Fail("No boolean result for for loop condition evaluation. Got=NULL.");
                            if (!(value is BooleanObj)) Fail($"Expected bool from for loop conditionals. Got={value!.Type()}");
                            if (!((BooleanObj)value!).Value) frame.Position = operand;
                            break;
                        }
                        case OpCode.NextIteration: {
                            var loop = frame.Loops.Peek();
                            var value = loop.Binding == null ? null : frame.Environment.Get(loop.Binding, out _);
                            frame.Environment = AquaEnvironment.NewEnclosedEnvironment(loop.Outer);
                            if (loop.Binding != null) frame.Environment.Create(loop.Binding, value!);
                            break;
                        }
                        case OpCode.Break: Reset(frame.Loops.Peek().Base); frame.Position = operand; break;
                        case OpCode.LeaveLoop: {
                            var loop = frame.Loops.Pop(); Reset(loop.Base); frame.Environment = loop.Outer; Push(null); break;
                        }
                        case OpCode.Error: Fail((string)pool[operand]); break;
                        default: throw new InvalidOperationException($"Invalid opcode: {instruction.Code}");
                    }
                }
            } catch (RuntimeError error) { return error.Error; }
            return null;
        }

        private void Set(AquaEnvironment environment, string name, IObject? value) {
            environment.Get(name, out bool found);
            if (!found) Fail($"Identifier not found: {name}");
            environment.Set(name, value!);
        }
        private IObject[] Arguments(int length) {
            var arguments = new IObject[length];
            for (int i = length - 1; i >= 0; i--) arguments[i] = Pop()!;
            return arguments;
        }
        private void Call(Frame frame, int argumentCount) {
            var arguments = Arguments(argumentCount); var callee = Pop();
            if (callee is BuiltinObj builtin) { Push(builtin.Fn(arguments)); return; }
            if (!(callee is FunctionObj)) Fail($"Not a function: {callee?.Type() ?? "NULL"}");
            var function = (FunctionObj)callee!;
            if (function.Parameters.Length != arguments.Length) Fail($"Function expects {function.Parameters.Length} arguments, got {arguments.Length}.");
            var scope = AquaEnvironment.NewEnclosedEnvironment(function.Env);
            for (int i = 0; i < arguments.Length; i++) scope.Create(function.Parameters[i].Value, arguments[i]);
            frames.Push(new Frame(VmEvaluator.GetFunctionCode(function), scope, count, VmEvaluator.GetFunctionBuiltins(function) ?? frame.Builtins));
        }
    }
}
