using System;
using System.Linq;
using System.Text.Json;
using AquariusLang.Object;

namespace AquariusLang.VM;

/// <summary>JSON transport of the same instructions executed by the core stack VM.</summary>
public static class WebBytecodeSerializer {
    public const int FormatVersion = 1;
    public static object ToDocument(Bytecode program) => new {
        version = FormatVersion,
        wrapReturn = program.WrapReturn,
        code = program.Instructions.Select(i => new object[] { i.Code.ToString(), i.Operand }).ToArray(),
        pool = program.Constants.Select(Constant).ToArray()
    };
    public static string Serialize(Bytecode program) => JsonSerializer.Serialize(ToDocument(program));
    private static object Constant(object value) => value switch {
        string name => new { type = "name", value = name },
        IntegerObj n => new { type = "int", value = (double)n.Value },
        FloatObj n => new { type = "float", value = (double)n.Value },
        DoubleObj n => new { type = "double", value = n.Value },
        BooleanObj b => new { type = "bool", value = b.Value },
        StringObj s => new { type = "string", value = s.Value },
        NullObj => new { type = "null" },
        BreakObj => new { type = "break" },
        Assignment a => new { type = "assignment", name = a.Name, operation = a.Operation.ToString() },
        FunctionCode f => new { type = "function", parameters = f.Parameters.Select(p => p.Value).ToArray(), program = ToDocument(f.BodyCode) },
        Bytecode b => new { type = "program", program = ToDocument(b) },
        _ => throw new ArgumentException($"Unsupported web constant: {value.GetType().Name}")
    };
}
