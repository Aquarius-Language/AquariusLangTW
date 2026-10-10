using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.Application;

public static class ApplicationValues {
    public static string Text(IObject value) => value is StringObj text ? text.Value : throw new ArgumentException("Expected a string.");
    public static double Number(IObject value) => value is INumberObj number && double.IsFinite(number.GetNumValue()) ? number.GetNumValue() : throw new ArgumentException("Expected a finite number.");
    public static double WholeNumber(IObject value) { double n = Number(value); if (n != Math.Truncate(n)) throw new ArgumentException("Expected an integer."); return n; }
    public static int Integer(IObject value) => checked((int)WholeNumber(value));
    public static bool Boolean(IObject value) => value is BooleanObj boolean ? boolean.Value : throw new ArgumentException("Expected a Boolean.");
    public static byte[] Bytes(IObject value) {
        if (value is not ArrayObj array) throw new ArgumentException("Expected a byte array."); new ApplicationLimits().Bytes(array.Elements.Length);
        return array.Elements.Select(v => checked((byte)Integer(v))).ToArray();
    }
    public static IObject Binary(byte[] bytes) => new ArrayObj(bytes.Select(b => (IObject)new IntegerObj(b)).ToArray());
    public static object? JsonValue(IObject value, int depth = 0) {
        if (depth > 64) throw new ApplicationFailure(FailureKind.LimitExceeded, "JSON depth exceeds 64 (or contains a cycle).");
        return value switch {
            NullObj => null, StringObj text => text.Value, BooleanObj boolean => boolean.Value, INumberObj => Number(value),
            ArrayObj array => array.Elements.Select(v => JsonValue(v, depth + 1)).ToArray(),
            HashObj hash => hash.Pairs.Values.ToDictionary(p => Text(p.Key), p => JsonValue(p.Value, depth + 1), StringComparer.Ordinal),
            _ => throw new ApplicationFailure(FailureKind.Unsupported, "JSON supports null, Boolean, finite numbers, strings, arrays and string-keyed hashes.")
        };
    }
    public static IObject FromJson(JsonElement value) => value.ValueKind switch {
        JsonValueKind.Null => RepeatedPrimitives.NULL, JsonValueKind.True => new BooleanObj(true), JsonValueKind.False => new BooleanObj(false),
        JsonValueKind.String => new StringObj(value.GetString()!), JsonValueKind.Number => value.TryGetInt32(out int integer) ? new IntegerObj(integer) : new DoubleObj(value.GetDouble()),
        JsonValueKind.Array => new ArrayObj(value.EnumerateArray().Select(FromJson).ToArray()),
        JsonValueKind.Object => new HashObj(value.EnumerateObject().GroupBy(p => p.Name).Select(g => g.Last()).ToDictionary(p => new StringObj(p.Name).HashKey(), p => new HashPair(new StringObj(p.Name), FromJson(p.Value)))),
        _ => throw new ArgumentException("Unsupported JSON value.")
    };
    public static ModuleObj Record(object value) {
        var env = AquaEnvironment.NewEnvironment(); var json = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        foreach (var field in json.EnumerateObject()) env.Create(field.Name, FromJson(field.Value)); return new(env);
    }
}
