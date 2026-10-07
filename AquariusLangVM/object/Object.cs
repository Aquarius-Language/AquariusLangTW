using System;
using System.Collections.Generic;
using System.Text;
using AquariusLang.ast;

namespace AquariusLang.Object {

    public static class ObjectType {
        public const string NULL_OBJ = "NULL";
        public const string ERROR_OBJ = "ERROR";
        public const string INTEGER_OBJ = "INTEGER";
        public const string FLOAT_OBJ = "FLOAT";
        public const string DOUBLE_OBJ = "DOUBLE";
        public const string BOOLEAN_OBJ = "BOOLEAN";
        public const string RETURN_VALUE_OBJ = "RETURN_VALUE";
        public const string BREAK_OBJ = "BREAK_OBJ";
        public const string FUNCTION_OBJ = "FUNCTION";
        public const string STRING_OBJ = "STRING";
        public const string BUILTIN_OBJ = "BUILTIN";
        public const string ARRAY_OBJ = "ARRAY";
        public const string HASH_OBJ = "HASH";
        public const string MODULE_OBJ = "MODULE";

        private const int is_number = 0;

        /// <summary>
        /// For faster lookup check if object type are certain group types. 
        /// </summary>
        private static Dictionary<string, int> typeGroupsLookup = new Dictionary<string, int>() {
            { INTEGER_OBJ, is_number },
            { FLOAT_OBJ, is_number },
            { DOUBLE_OBJ, is_number },
        };

        public static bool IsNumber(string objectType) {
            if (!typeGroupsLookup.ContainsKey(objectType)) {
                return false;
            }

            return typeGroupsLookup[objectType] == is_number;
        }
    }

    public interface IObject {
        string Type(); // Corresponds to ObjectType members.
        string Inspect();
    }

    /// <summary>
    /// Interface for getting value of number type objects. This can reduce if/else if/else conditions during evaluation.
    /// </summary>
    public interface INumberObj {
        double GetNumValue();
    }

    public interface IHashable {
        HashKey HashKey();
    }

    /// <summary>
    /// Value equality makes equivalent type/value pairs usable as dictionary keys.
    /// A reference-type replacement must implement consistent Equals and GetHashCode methods.
    /// </summary>
    public struct HashKey {
        private string type;
        private int value;

        public HashKey(string type, int value) {
            this.type = type;
            this.value = value;
        }

        public string Type {
            get => type;
            set => type = value;
        }

        public int Value {
            get => value;
            set => this.value = value;
        }
    }

    public class IntegerObj : IObject, INumberObj, IHashable {
        private int value;

        public IntegerObj() {
        }

        public IntegerObj(int value) {
            this.value = value;
        }

        public string Type() {
            return ObjectType.INTEGER_OBJ;
        }

        public string Inspect() {
            return value.ToString();
        }

        public HashKey HashKey() {
            return new HashKey(Type(), value);
        }

        public double GetNumValue() {
            return value;
        }

        public int Value {
            get => value;
            set => this.value = value;
        }
    }

    public class FloatObj : IObject, INumberObj, IHashable {
        private float value;

        public FloatObj() {
        }

        public FloatObj(float value) {
            this.value = value;
        }

        public string Type() {
            return ObjectType.FLOAT_OBJ;
        }

        public string Inspect() {
            return value.ToString();
        }

        public HashKey HashKey() {
            return new HashKey(Type(), value.GetHashCode());
        }

        public double GetNumValue() {
            return value;
        }

        public float Value {
            get => value;
            set => this.value = value;
        }
    }

    public class DoubleObj : IObject, INumberObj, IHashable {
        private double value;

        public DoubleObj() {
        }

        public DoubleObj(double value) {
            this.value = value;
        }

        public string Type() {
            return ObjectType.DOUBLE_OBJ;
        }

        public string Inspect() {
            return value.ToString();
        }

        public HashKey HashKey() {
            return new HashKey(Type(), value.GetHashCode());
        }

        public double GetNumValue() {
            return value;
        }

        public double Value {
            get => value;
            set => this.value = value;
        }
    }

    public class BooleanObj : IObject, IHashable {
        private bool value;

        public BooleanObj(bool value) {
            this.value = value;
        }

        public string Type() {
            return ObjectType.BOOLEAN_OBJ;
        }

        public string Inspect() {
            return value ? "真" : "假";
        }

        public bool Value {
            get => value;
            set => this.value = value;
        }

        public HashKey HashKey() {
            int _value = value ? 1 : 0;
            return new HashKey(Type(), _value);
        }
    }

    public class StringObj : IObject, IHashable {
        private string value;

        public StringObj(string value) {
            this.value = value;
        }

        public string Type() {
            return ObjectType.STRING_OBJ;
        }

        public string Inspect() {
            return value;
        }

        public string Value {
            get => value;
            set => this.value = value;
        }

        public HashKey HashKey() {
            return new HashKey(Type(), value.GetHashCode());
        }
    }

    /// <summary>
    /// Null doesn’t wrap any value. It represents the absence of any value.
    /// </summary>
    public class NullObj : IObject {
        public string Type() {
            return ObjectType.NULL_OBJ;
        }

        public string Inspect() {
            return "null";
        }
    }

    public class ReturnValueObj : IObject {
        private IObject value;

        public ReturnValueObj(IObject value) {
            this.value = value;
        }

        public string Type() {
            return ObjectType.RETURN_VALUE_OBJ;
        }

        public string Inspect() {
            return value.Inspect();
        }

        public IObject Value {
            get => value;
            set => this.value = value;
        }
    }

    public class BreakObj : IObject {
        public string Type() {
            return ObjectType.BREAK_OBJ;
        }

        public string Inspect() {
            return "中斷";
        }
    }

    /// <summary>
    /// This is used for internal error handling.
    /// </summary>
    public class ErrorObj : IObject {
        private string message;

        public ErrorObj(string message) {
            this.message = message;
        }

        public string Type() {
            return ObjectType.ERROR_OBJ;
        }

        public string Inspect() {
            return "ERROR: " + message;
        }

        public string Message {
            get => message;
            set => message = value;
        }
    }

    /// <summary>
    /// A function retains the environment where it was defined, allowing lexical closures.
    /// Parameters and the AST body remain available for inspection; VmEvaluator associates
    /// each VM-created closure with its compiled body instructions.
    /// </summary>
    public class FunctionObj : IObject {
        private Identifier[] parameters;
        private BlockStatement body;
        private Environment env;

        public FunctionObj(Identifier[] parameters, BlockStatement body, Environment env) {
            this.parameters = parameters;
            this.body = body;
            this.env = env;
        }

        public string Type() {
            return ObjectType.FUNCTION_OBJ;
        }

        public string Inspect() {
            StringBuilder builder = new StringBuilder();

            List<string> parameters = new List<string>();
            foreach (var parameter in this.parameters) {
                parameters.Add(parameter.String());
            }

            builder.Append("函式")
                .Append('(')
                .Append(string.Join(", ", parameters))
                .Append(") {\n")
                .Append(body.String())
                .Append("\n}");

            return builder.ToString();
        }

        public Identifier[] Parameters {
            get => parameters;
            set => parameters = value;
        }

        public BlockStatement Body {
            get => body;
            set => body = value;
        }

        public Environment Env {
            get => env;
            set => env = value;
        }
    }

    public delegate IObject BuiltinFunction(IObject[] args);

    public class BuiltinObj : IObject {
        private BuiltinFunction fn;
        // private Environment environment;

        public BuiltinObj(BuiltinFunction fn) {
            this.fn = fn;
        }

        public string Type() {
            return ObjectType.BUILTIN_OBJ;
        }

        public string Inspect() {
            return "builtin function";
        }

        public BuiltinFunction Fn {
            get => fn;
            set => fn = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    /// <summary>
    /// Module object that gets created when importing new scripts from another file. (let module = import('./anotherfile.aqua')) 
    /// </summary>
    public class ModuleObj : IObject {
        private Environment environment;

        public ModuleObj(Environment environment) {
            this.environment = environment;
        }

        public string Type() {
            return ObjectType.MODULE_OBJ;
        }

        public string Inspect() {
            return "module";
        }

        public Environment _Environment => environment;
    }

    public class ArrayObj : IObject {
        private IObject[] elements;

        public ArrayObj(IObject[] elements) {
            this.elements = elements;
        }

        public string Type() {
            return ObjectType.ARRAY_OBJ;
        }

        public string Inspect() {
            StringBuilder builder = new StringBuilder();

            List<string> _elements = new List<string>();
            foreach (var element in elements) {
                _elements.Add(element.Inspect());
            }

            builder.Append('[')
                .Append(string.Join(", ", _elements))
                .Append(']');

            return builder.ToString();
        }

        public IObject[] Elements {
            get => elements;
            set => elements = value;
        }
    }

    public class HashPair {
        private IObject key;
        private IObject value;

        public HashPair(IObject key, IObject value) {
            this.key = key;
            this.value = value;
        }

        public IObject Key {
            get => key;
            set => key = value;
        }

        public IObject Value {
            get => value;
            set => this.value = value;
        }
    }

    public class HashObj : IObject {
        private Dictionary<HashKey, HashPair> pairs;

        public HashObj(Dictionary<HashKey, HashPair> pairs) {
            this.pairs = pairs;
        }

        public string Type() {
            return ObjectType.HASH_OBJ;
        }

        public string Inspect() {
            StringBuilder builder = new StringBuilder();
            List<string> _pairs = new List<string>();
            foreach (var pair in pairs) {
                _pairs.Add($"{pair.Value.Key.Inspect()}: {pair.Value.Value.Inspect()}");
            }

            builder.Append('{')
                .Append(string.Join(", ", _pairs))
                .Append('}');

            return builder.ToString();
        }

        public Dictionary<HashKey, HashPair> Pairs {
            get => pairs;
            set => pairs = value;
        }
    }
}
