using System.Text;
using AquariusLang.ast;
using AquariusLang.Compiler;

namespace AquariusLang.Build.Bindings;

/// <summary>The C ABI's names and types (shared by the C header, the C++ binding and the embed runtime's export table):
/// functions and structs <c>&lt;prefix&gt;snake_case</c>, arrays <c>&lt;prefix&gt;&lt;element&gt;_array</c>.</summary>
public static class CNames
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal) {
        "auto", "bool", "break", "case", "char", "class", "const", "continue", "default", "delete", "do", "double", "else", "enum",
        "explicit", "extern", "false", "float", "for", "friend", "goto", "if", "inline", "int", "long", "namespace", "new", "operator",
        "private", "protected", "public", "register", "restrict", "return", "short", "signed", "sizeof", "static", "struct", "switch",
        "template", "this", "throw", "true", "try", "typedef", "typename", "union", "unsigned", "using", "virtual", "void", "volatile",
        "while", "and", "or", "not", "xor", "nullptr", "alignas", "alignof", "char8_t", "concept", "requires", "noexcept", "static_assert",
        "thread_local", "mutable", "constexpr", "decltype", "export", "import", "module", "co_await", "co_return", "co_yield"
    };

    public static string Function(BindingContext context, string name) => context.Settings.SymbolPrefix + ExportTable.SnakeCase(name);
    public static string Struct(BindingContext context, ExportStruct shape) => context.Settings.SymbolPrefix + ExportTable.SnakeCase(shape.Name);
    public static string ArrayStruct(BindingContext context, ScalarType element) => context.Settings.SymbolPrefix + Element(element) + "_array";
    /// <summary>The element names of array types (<c>double</c> in <c>geo_double_array</c>).</summary>
    public static string Element(ScalarType element) => element switch {
        ScalarType.Int => "int", ScalarType.Long => "long", ScalarType.Float => "float", ScalarType.Double => "double", ScalarType.Bool => "bool",
        _ => throw new ArgumentException($"No arrays of {element} are exported.")
    };

    /// <summary>The C type of a scalar.</summary>
    public static string Scalar(ScalarType scalar, bool result) => scalar switch {
        ScalarType.Int => "int32_t", ScalarType.Long => "int64_t", ScalarType.Float => "float", ScalarType.Double => "double",
        ScalarType.Bool => "bool", ScalarType.String => result ? "char *" : "const char *",
        _ => throw new ArgumentException($"Unknown scalar {scalar}.")
    };

    /// <summary>A parameter or field name usable in C, C++ and Rust: the Aquarius name when it is an ASCII identifier and
    /// not a keyword, else <c>arg&lt;index&gt;</c> (or the name with <c>_</c> appended).</summary>
    public static string Parameter(string name, int index) =>
        !ExportTable.IsIdentifier(name) ? "arg" + index : Keywords.Contains(name) ? name + "_" : name;

    /// <summary>The result's C type.</summary>
    public static string Result(BindingContext context, ExportType type) => type.Shape switch {
        ExportShape.Void => "void",
        ExportShape.Scalar => Scalar(type.Scalar, true),
        ExportShape.Array => ArrayStruct(context, type.Scalar),
        _ => Struct(context, context.Exports.Structs[type.Struct])
    };

    /// <summary>A parameter's C declaration: arrays are a pointer and a count.</summary>
    public static string Declaration(BindingContext context, ExportParameter parameter, int index)
    {
        string name = Parameter(parameter.Name, index);
        return parameter.Type.Shape switch {
            ExportShape.Scalar => $"{Spaced(Scalar(parameter.Type.Scalar, false))}{name}",
            ExportShape.Array => $"const {Scalar(parameter.Type.Scalar, false)} *{name}, size_t {name}_count",
            ExportShape.Struct => $"{Struct(context, context.Exports.Structs[parameter.Type.Struct])} {name}",
            _ => throw new ArgumentException("Parameters have a type.")
        };
    }

    /// <summary>A C type followed by a name: <c>double x</c>, <c>char *x</c>.</summary>
    public static string Spaced(string type) => type.EndsWith('*') ? type : type + " ";

    /// <summary>The array element types the exports use, in a fixed order.</summary>
    public static IEnumerable<ScalarType> ArrayElements(ExportTable exports) =>
        exports.Functions.SelectMany(f => f.Parameters.Select(p => p.Type).Append(f.Result))
            .Where(t => t.Shape == ExportShape.Array).Select(t => t.Scalar).Distinct().Order();

    /// <summary>A comment block for documentation, each line prefixed (<c>/* ... */</c> in C).</summary>
    public static string Comment(string documentation, string indent = "")
    {
        if (documentation.Length == 0) return "";
        var lines = documentation.Split('\n');
        var text = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
            text.Append(indent).Append(i == 0 ? "/* " : "   ").Append(lines[i].Replace("*/", "* /")).Append(i == lines.Length - 1 ? " */\n" : "\n");
        return text.ToString();
    }
}
