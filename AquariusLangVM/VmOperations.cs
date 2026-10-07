using AquariusLang.evaluator;
using AquariusLang.Object;

namespace AquariusLang.VM;

internal static class VmOperations {
    internal static BooleanObj Bool(bool value) => value ? RepeatedPrimitives.TRUE : RepeatedPrimitives.FALSE;
    internal static bool Truthy(IObject? value) => value is BooleanObj boolean ? boolean.Value : value != RepeatedPrimitives.NULL;
    internal static IObject Number(double value, IObject type) => type switch {
        IntegerObj _ => new IntegerObj((int)value), FloatObj _ => new FloatObj((float)value), _ => new DoubleObj(value)
    };
    internal static string Symbol(OpCode op) => op switch {
        OpCode.Add => "+", OpCode.Subtract => "-", OpCode.Multiply => "*", OpCode.Divide => "/",
        OpCode.Less => "<", OpCode.Greater => ">", OpCode.LessEqual => "<=", OpCode.GreaterEqual => ">=",
        OpCode.Equal => "==", OpCode.NotEqual => "!=", OpCode.And => "&&", OpCode.Or => "||", _ => "?"
    };

    internal static IObject Binary(OpCode op, IObject? left, IObject? right) {
        if (left is INumberObj a && right is INumberObj b) {
            double x = a.GetNumValue(), y = b.GetNumValue();
            IObject type = left is DoubleObj ? left : right is DoubleObj ? right :
                left is FloatObj ? left : right is FloatObj ? right : left;
            switch (op) {
                case OpCode.Add: return Number(x + y, type);
                case OpCode.Subtract: return Number(x - y, type);
                case OpCode.Multiply: return Number(x * y, type);
                case OpCode.Divide: return Number(x / y, type);
                case OpCode.Less: return Bool(x < y);
                case OpCode.Greater: return Bool(x > y);
                case OpCode.LessEqual: return Bool(x <= y);
                case OpCode.GreaterEqual: return Bool(x >= y);
                case OpCode.Equal: return Bool(x == y);
                case OpCode.NotEqual: return Bool(x != y);
                default: return new ErrorObj($"Unknown operator: {left.Type()} {Symbol(op)} {right.Type()}");
            }
        }
        if (op == OpCode.Equal || op == OpCode.NotEqual) {
            bool equal = left is StringObj ls && right is StringObj rs ? ls.Value == rs.Value :
                left is BooleanObj lb && right is BooleanObj rb ? lb.Value == rb.Value : left == right;
            return Bool(op == OpCode.Equal ? equal : !equal);
        }
        if (op == OpCode.And || op == OpCode.Or) {
            if (left is BooleanObj lb && right is BooleanObj rb)
                return Bool(op == OpCode.And ? lb.Value && rb.Value : lb.Value || rb.Value);
            return new ErrorObj($"Two operands are not both boolean for {Symbol(op)} operator: {left?.Inspect()}{Symbol(op)}{right?.Inspect()}");
        }
        if (op == OpCode.Add && left is StringObj text && right is StringObj suffix)
            return new StringObj(text.Value + suffix.Value);
        string leftType = left?.Type() ?? "NULL", rightType = right?.Type() ?? "NULL";
        return new ErrorObj($"{(leftType == rightType ? "Unknown operator" : "Type mismatch")}: {leftType} {Symbol(op)} {rightType}");
    }

    internal static IObject Compound(OpCode op, IObject? left, IObject? right) {
        string symbol = Symbol(op) + "=";
        if (right is INumberObj number) {
            if (!(left is INumberObj original)) return new ErrorObj($"Incorrect left operand type for {left?.Type() ?? "NULL"} {symbol} NUMBER");
            double a = original.GetNumValue(), b = number.GetNumValue();
            double result = op switch { OpCode.Add => a + b, OpCode.Subtract => a - b, OpCode.Multiply => a * b, _ => a / b };
            return Number(result, left);
        }
        if (op == OpCode.Add && right is StringObj suffix) {
            if (left is StringObj text) return new StringObj(text.Value + suffix.Value);
            return new ErrorObj($"Incorrect left operand type for STRING +=: {left?.Type() ?? "NULL"}");
        }
        return new ErrorObj($"{right?.Type() ?? "NULL"} as right operand type for {symbol} doesn't exist.");
    }

    internal static IObject Index(IObject? container, IObject? index) {
        if (container is ArrayObj array && index is IntegerObj integer)
            return integer.Value < 0 || integer.Value >= array.Elements.Length ? RepeatedPrimitives.NULL : array.Elements[integer.Value];
        if (container is HashObj hash) {
            if (!(index is IHashable key)) return new ErrorObj($"Unusable as hash key: {index?.Type() ?? "NULL"}");
            return hash.Pairs.TryGetValue(key.HashKey(), out var pair) ? pair.Value : RepeatedPrimitives.NULL;
        }
        return new ErrorObj($"Index operator not supported: {container?.Type() ?? "NULL"}");
    }

    internal static ErrorObj? CheckArrayWrite(IObject? container, IObject? index) {
        if (!(container is ArrayObj array)) return new ErrorObj("Indexed assignment requires an array.");
        if (!(index is IntegerObj integer)) return new ErrorObj("Array assignment index must be an integer.");
        if (integer.Value < 0 || integer.Value >= array.Elements.Length) return new ErrorObj("Array assignment index is out of bounds.");
        return null;
    }
}
