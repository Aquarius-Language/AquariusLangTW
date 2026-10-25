namespace AquariusLang.Object {

    /// <summary>Type tags of the compiler's constant values.</summary>
    public static class ObjectType {
        public const string NULL_OBJ = "NULL";
        public const string INTEGER_OBJ = "INTEGER";
        public const string FLOAT_OBJ = "FLOAT";
        public const string DOUBLE_OBJ = "DOUBLE";
        public const string BOOLEAN_OBJ = "BOOLEAN";
        public const string BREAK_OBJ = "BREAK_OBJ";
        public const string STRING_OBJ = "STRING";
    }

    /// <summary>A literal value in the compiler's IR constant pools. The emitter writes it into the program's data
    /// segment; values at run time live in the guest runtime (runtime/guest), never in C#.</summary>
    public interface IObject {
        string Type();
        string Inspect();
    }

    public sealed class IntegerObj(int value) : IObject {
        public int Value => value;
        public string Type() => ObjectType.INTEGER_OBJ;
        public string Inspect() => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public sealed class FloatObj(float value) : IObject {
        public float Value => value;
        public string Type() => ObjectType.FLOAT_OBJ;
        public string Inspect() => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public sealed class DoubleObj(double value) : IObject {
        public double Value => value;
        public string Type() => ObjectType.DOUBLE_OBJ;
        public string Inspect() => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public sealed class BooleanObj(bool value) : IObject {
        public bool Value => value;
        public string Type() => ObjectType.BOOLEAN_OBJ;
        public string Inspect() => value ? "真" : "假";
    }

    public sealed class StringObj(string value) : IObject {
        public string Value => value;
        public string Type() => ObjectType.STRING_OBJ;
        public string Inspect() => value;
    }

    /// <summary>The absence of a value.</summary>
    public sealed class NullObj : IObject {
        public string Type() => ObjectType.NULL_OBJ;
        public string Inspect() => "null";
    }

    /// <summary>The value a <c>中斷</c> / <c>break</c> statement produces.</summary>
    public sealed class BreakObj : IObject {
        public string Type() => ObjectType.BREAK_OBJ;
        public string Inspect() => "中斷";
    }
}
