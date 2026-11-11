using System.Text;
using AquariusLang.ast;
using AquariusLang.Compiler;

namespace AquariusLang.Build.Bindings;

/// <summary>
/// The C++20 binding, <c>include/&lt;name&gt;.hpp</c>: inline functions over the C header in namespace <c>&lt;name&gt;</c> that
/// take and return standard types (<c>std::string</c>, <c>std::vector</c>, structs with <c>std::string</c> fields), free
/// what the library returns and throw <c>&lt;name&gt;::error</c> when a call fails. Header only: nothing else to link.
/// </summary>
public sealed class CppBinding : IBindingGenerator
{
    public string Language => "cpp";

    public IReadOnlyList<BindingFile> Generate(BindingContext context) =>
        [new("include/" + context.Settings.Name + ".hpp", Header(context))];

    private static string Type(BindingContext context, ExportType type) => type.Shape switch {
        ExportShape.Void => "void",
        ExportShape.Scalar => Scalar(type.Scalar),
        ExportShape.Array => $"std::vector<{Scalar(type.Scalar)}>",
        _ => ExportTable.SnakeCase(context.Exports.Structs[type.Struct].Name)
    };
    private static string Scalar(ScalarType scalar) => scalar == ScalarType.String ? "std::string" : CNames.Scalar(scalar, false);
    private static string Parameter(BindingContext context, ExportParameter parameter, int index)
    {
        string name = CNames.Parameter(parameter.Name, index);
        return parameter.Type.Shape == ExportShape.Scalar && parameter.Type.Scalar != ScalarType.String
            ? $"{Scalar(parameter.Type.Scalar)} {name}" : $"const {Type(context, parameter.Type)} &{name}";
    }

    public static string Header(BindingContext context)
    {
        var exports = context.Exports;
        string prefix = context.Settings.SymbolPrefix;
        var text = new StringBuilder();
        text.Append($"// {context.Settings.Name}.hpp: the C++ binding of the {context.Settings.Name} native library, written by aqua {AquariusVersion.Current}\n");
        text.Append($"// from {exports.Module} (docs/native-libraries.md). Do not edit; rebuild the library instead.\n");
        text.Append("#pragma once\n\n");
        text.Append($"#include \"{CBinding.HeaderName(context)}\"\n\n#include <cstddef>\n#include <cstdint>\n#include <memory>\n#include <stdexcept>\n#include <string>\n#include <vector>\n\n");
        text.Append($"namespace {context.Settings.Name} {{\n\n");
        text.Append("/// A call the library could not complete; what() is its reason.\n");
        text.Append("class error : public std::runtime_error {\npublic:\n    using std::runtime_error::runtime_error;\n};\n\n");
        text.Append("namespace detail {\n");
        text.Append($"inline void check() {{\n    if (const char *message = {prefix}last_error()) throw error(message);\n}}\n");
        text.Append($"inline std::string take(char *text) {{\n    std::string value = text ? text : \"\";\n    {prefix}free(text);\n    return value;\n}}\n");
        text.Append($"template <class T, class Array> std::vector<T> take_array(Array array) {{\n    std::vector<T> values(array.data, array.data + array.count);\n    {prefix}free(array.data);\n    return values;\n}}\n");
        text.Append("/// std::vector<bool> stores bits; the C ABI takes bool elements.\n");
        text.Append("inline std::unique_ptr<bool[]> bools(const std::vector<bool> &values) {\n    auto copy = std::make_unique<bool[]>(values.size() ? values.size() : 1);\n    for (std::size_t i = 0; i < values.size(); i++) copy[i] = values[i];\n    return copy;\n}\n");
        text.Append("} // namespace detail\n\n");
        foreach (var shape in exports.Structs) {
            string name = ExportTable.SnakeCase(shape.Name), c = CNames.Struct(context, shape);
            if (shape.Documentation.Length > 0) text.Append(string.Concat(shape.Documentation.Split('\n').Select(l => "/// " + l + "\n")));
            text.Append($"struct {name} {{\n");
            for (int i = 0; i < shape.Fields.Count; i++) text.Append($"    {Scalar(shape.Fields[i].Type.Scalar)} {CNames.Parameter(shape.Fields[i].Name, i)}{{}};\n");
            text.Append("};\n\n");
            // To C: string fields point into the C++ value, which outlives the call.
            text.Append($"namespace detail {{\ninline {c} to_c(const {name} &value) {{\n    {c} result;\n");
            for (int i = 0; i < shape.Fields.Count; i++) {
                string field = CNames.Parameter(shape.Fields[i].Name, i);
                text.Append(shape.Fields[i].Type.Scalar == ScalarType.String
                    ? $"    result.{field} = const_cast<char *>(value.{field}.c_str());\n" : $"    result.{field} = value.{field};\n");
            }
            text.Append($"    return result;\n}}\ninline {name} from_c({c} value) {{\n    {name} result;\n");
            for (int i = 0; i < shape.Fields.Count; i++) {
                string field = CNames.Parameter(shape.Fields[i].Name, i);
                text.Append(shape.Fields[i].Type.Scalar == ScalarType.String ? $"    result.{field} = take(value.{field});\n" : $"    result.{field} = value.{field};\n");
            }
            text.Append("    return result;\n}\n} // namespace detail\n\n");
        }
        text.Append("/// Starts the library (runs its top-level code once). Optional: the first call starts it.\n");
        text.Append($"inline void initialize() {{\n    if (!{prefix}initialize()) detail::check();\n}}\n");
        text.Append("/// Stops the library; a later call starts it again.\n");
        text.Append($"inline void shutdown() noexcept {{ {prefix}shutdown(); }}\n\n");
        foreach (var function in exports.Functions) Function(context, function, text);
        text.Append($"}} // namespace {context.Settings.Name}\n");
        return text.ToString();
    }

    private static void Function(BindingContext context, ExportFunction function, StringBuilder text)
    {
        if (function.Documentation.Length > 0) text.Append(string.Concat(function.Documentation.Split('\n').Select(l => "/// " + l + "\n")));
        string parameters = string.Join(", ", function.Parameters.Select((p, i) => Parameter(context, p, i)));
        text.Append($"inline {Type(context, function.Result)} {ExportTable.SnakeCase(function.Name)}({parameters}) {{\n");
        var arguments = new List<string>();
        for (int i = 0; i < function.Parameters.Count; i++) {
            var parameter = function.Parameters[i];
            string name = CNames.Parameter(parameter.Name, i);
            switch (parameter.Type.Shape) {
                case ExportShape.Scalar: arguments.Add(parameter.Type.Scalar == ScalarType.String ? name + ".c_str()" : name); break;
                case ExportShape.Struct: arguments.Add($"detail::to_c({name})"); break;
                case ExportShape.Array when parameter.Type.Scalar == ScalarType.Bool:
                    text.Append($"    auto {name}_items = detail::bools({name});\n");
                    arguments.Add($"{name}_items.get(), {name}.size()"); break;
                case ExportShape.Array: arguments.Add($"{name}.data(), {name}.size()"); break;
            }
        }
        string call = $"{CNames.Function(context, function.Name)}({string.Join(", ", arguments)})";
        var result = function.Result;
        if (result.Shape == ExportShape.Void) { text.Append($"    {call};\n    detail::check();\n}}\n\n"); return; }
        text.Append($"    auto result = {call};\n    detail::check();\n");
        text.Append(result.Shape switch {
            ExportShape.Scalar when result.Scalar == ScalarType.String => "    return detail::take(result);\n",
            ExportShape.Array => $"    return detail::take_array<{Scalar(result.Scalar)}>(result);\n",
            ExportShape.Struct => "    return detail::from_c(result);\n",
            _ => "    return result;\n"
        });
        text.Append("}\n\n");
    }
}
