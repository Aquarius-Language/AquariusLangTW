using System.Text;
using AquariusLang.ast;
using AquariusLang.Compiler;

namespace AquariusLang.Build.Bindings;

/// <summary>
/// The JavaScript binding of the web (the "javascript" binding), <c>&lt;name&gt;.d.ts</c>: the types of the object
/// <c>loadLibrary()</c> of the web glue builds from the library's export table when it loads (no JavaScript is generated;
/// CLAUDE.md rule 5). Functions are lowerCamelCase, numbers <c>number</c>, longs <c>bigint</c>, arrays plain arrays,
/// structs interfaces; a failed call throws an <c>Error</c>.
/// </summary>
public sealed class TypeScriptBinding : IBindingGenerator
{
    public string Language => "javascript";

    public IReadOnlyList<BindingFile> Generate(BindingContext context) => [new(context.Settings.Name + ".d.ts", Declarations(context))];

    private static string Scalar(ScalarType scalar) => scalar switch {
        ScalarType.Long => "bigint", ScalarType.Bool => "boolean", ScalarType.String => "string", _ => "number"
    };
    private static string Type(BindingContext context, ExportType type, bool parameter) => type.Shape switch {
        ExportShape.Void => "void",
        ExportShape.Scalar => Scalar(type.Scalar),
        ExportShape.Array => parameter ? $"ArrayLike<{Scalar(type.Scalar)}>" : $"{Scalar(type.Scalar)}[]",
        _ => ExportTable.UpperCamelCase(context.Exports.Structs[type.Struct].Name)
    };
    private static string Doc(string documentation, string indent) => documentation.Length == 0 ? ""
        : $"{indent}/**\n{string.Concat(documentation.Split('\n').Select(l => $"{indent} * {l.Replace("*/", "* /")}\n"))}{indent} */\n";

    public static string Declarations(BindingContext context)
    {
        var exports = context.Exports;
        string name = ExportTable.UpperCamelCase(context.Settings.Name);
        var text = new StringBuilder();
        text.Append($"// {context.Settings.Name}.d.ts: the types of the {context.Settings.Name} library on the web, written by aqua {AquariusVersion.Current}\n");
        text.Append($"// from {exports.Module} (docs/native-libraries.md). Do not edit; rebuild the library instead. Load it with the glue:\n");
        text.Append($"//   import {{ loadLibrary }} from './glue/index.mjs';\n");
        text.Append($"//   const {context.Settings.Name} = (await loadLibrary({{ library: '{context.Settings.Name}.wasm', base: 'runtime/' }})) as {name};\n\n");
        foreach (var shape in exports.Structs) {
            text.Append(Doc(shape.Documentation, ""));
            text.Append($"export interface {ExportTable.UpperCamelCase(shape.Name)} {{\n");
            for (int i = 0; i < shape.Fields.Count; i++) text.Append($"    {CNames.Parameter(shape.Fields[i].Name, i)}: {Scalar(shape.Fields[i].Type.Scalar)};\n");
            text.Append("}\n\n");
        }
        text.Append($"/** The {context.Settings.Name} library: a failed call throws an Error with the library's reason. */\nexport interface {name} {{\n");
        text.Append("    /** Starts the library (runs its top-level code once). Optional: the first call starts it. */\n    initialize(): void;\n");
        text.Append("    /** Stops the library and resolves once a fresh instance is linked; the next call starts it again. */\n    shutdown(): Promise<void>;\n");
        text.Append("    /** The C ABI version the library implements. */\n    readonly abiVersion: number;\n");
        foreach (var function in exports.Functions) {
            text.Append(Doc(function.Documentation, "    "));
            string parameters = string.Join(", ", function.Parameters.Select((p, i) => $"{CNames.Parameter(p.Name, i)}: {Type(context, p.Type, true)}"));
            text.Append($"    {ExportTable.LowerCamelCase(function.Name)}({parameters}): {Type(context, function.Result, false)};\n");
        }
        text.Append("}\n");
        return text.ToString();
    }
}
