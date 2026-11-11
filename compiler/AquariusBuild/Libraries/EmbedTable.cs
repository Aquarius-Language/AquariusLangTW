using System.Text;
using AquariusLang.Build.Bindings;
using AquariusLang.Compiler;

namespace AquariusLang.Build.Libraries;

/// <summary>
/// What the embed runtime's per-library half (runtime/embed/exports.c) is compiled with: the X-macro table exports.def
/// (its rows are documented in exports.c) and the list of symbols the library exports.
/// </summary>
public static class EmbedTable
{
    /// <summary>The functions every native library defines, before its own.</summary>
    public static readonly IReadOnlyList<string> Lifecycle = ["initialize", "shutdown", "last_error", "free", "abi_version"];

    /// <summary>The symbols the library exports; with the Java binding, JNI_OnLoad too.</summary>
    public static IReadOnlyList<string> Symbols(BindingContext context, bool java = false) =>
        Lifecycle.Select(name => context.Settings.SymbolPrefix + name)
            .Concat(context.Exports.Functions.Select(f => CNames.Function(context, f.Name))).Concat(java ? ["JNI_OnLoad"] : []).ToArray();

    /// <summary>The table; with <paramref name="java"/>, the Java binding's rows too (jni.c).</summary>
    public static string Definitions(BindingContext context, bool java = false)
    {
        var exports = context.Exports;
        var text = new StringBuilder();
        text.Append($"/* exports.def: the export table of {context.Settings.Name} for runtime/embed/exports.c, written by aqua {AquariusVersion.Current}. */\n");
        for (int s = 0; s < exports.Structs.Count; s++) {
            var shape = exports.Structs[s];
            string name = ExportTable.SnakeCase(shape.Name);
            text.Append($"AQUA_STRUCT({s}, {name}, {Literal(shape.Binding)}, {shape.Fields.Count})\n");
            for (int f = 0; f < shape.Fields.Count; f++)
                text.Append($"AQUA_FIELD({name}, {f}, {Type(context, shape.Fields[f].Type)}, {CNames.Parameter(shape.Fields[f].Name, f)})\n");
            text.Append($"AQUA_STRUCT_END({name}, {s})\n");
        }
        for (int i = 0; i < exports.Functions.Count; i++) {
            var function = exports.Functions[i];
            string result = Type(context, function.Result);
            text.Append($"AQUA_FUNCTION({i}, {ExportTable.SnakeCase(function.Name)}, {Literal(function.Binding)}, {result})\n");
            if (function.Parameters.Count == 0) text.Append("AQUA_NO_PARAMETERS\n");
            for (int p = 0; p < function.Parameters.Count; p++)
                text.Append($"AQUA_PARAMETER({(p == 0 ? "FIRST" : "NEXT")}, {Type(context, function.Parameters[p].Type)}, {CNames.Parameter(function.Parameters[p].Name, p)})\n");
            text.Append($"AQUA_CALL({i}, {result})\n");
            for (int p = 0; p < function.Parameters.Count; p++)
                text.Append($"AQUA_ARGUMENT({Type(context, function.Parameters[p].Type)}, {CNames.Parameter(function.Parameters[p].Name, p)})\n");
            text.Append($"AQUA_RETURN({result})\n");
        }
        if (java) {
            for (int s = 0; s < exports.Structs.Count; s++) {
                string javaClass = JavaBinding.JniClass(context) + "$" + JavaBinding.StructName(exports.Structs[s]);
                text.Append($"AQUA_JAVA_STRUCT({s}, {Literal(javaClass)})\n");
            }
            foreach (var function in exports.Functions) {
                string name = ExportTable.SnakeCase(function.Name), method = Literal(JavaBinding.MethodName(function));
                text.Append($"AQUA_JAVA_FUNCTION({name}, {method}, {Literal(JavaBinding.Signature(context, function))})\n");
            }
        }
        return text.ToString();
    }

    private static string Type(BindingContext context, ExportType type) => type.Shape switch {
        ExportShape.Void => "VOID",
        ExportShape.Scalar => type.Scalar.ToString().ToUpperInvariant(),
        ExportShape.Array => type.Scalar.ToString().ToUpperInvariant() + "_ARRAY",
        _ => $"STRUCT({ExportTable.SnakeCase(context.Exports.Structs[type.Struct].Name)})"
    };

    /// <summary>A C string literal of an Aquarius name (UTF-8 source).</summary>
    private static string Literal(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
