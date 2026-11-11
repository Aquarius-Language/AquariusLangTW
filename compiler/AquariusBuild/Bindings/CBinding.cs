using System.Text;
using AquariusLang.Compiler;

namespace AquariusLang.Build.Bindings;

/// <summary>
/// The C header, <c>include/&lt;name&gt;.h</c>: the library's whole ABI. Every library also defines
/// <c>&lt;prefix&gt;initialize</c>, <c>shutdown</c>, <c>last_error</c>, <c>free</c> and <c>abi_version</c>. Values are copied at
/// the boundary: strings and arrays a function returns belong to the caller, who frees them with <c>&lt;prefix&gt;free</c>.
/// A failed call returns zero and leaves the reason in <c>&lt;prefix&gt;last_error()</c> (NULL after a successful call, per
/// thread).
/// </summary>
public sealed class CBinding : IBindingGenerator
{
    /// <summary>The ABI conventions the header describes (<c>&lt;prefix&gt;abi_version()</c> returns the library's).</summary>
    public const int AbiVersion = 1;
    public string Language => "c";

    public static string HeaderName(BindingContext context) => context.Settings.Name + ".h";
    public static string Macro(BindingContext context) => context.Settings.Name.ToUpperInvariant();

    public IReadOnlyList<BindingFile> Generate(BindingContext context) => [new("include/" + HeaderName(context), Header(context))];

    public static string Header(BindingContext context)
    {
        var exports = context.Exports;
        string macro = Macro(context), api = macro + "_API", prefix = context.Settings.SymbolPrefix;
        var text = new StringBuilder();
        text.Append($"/* {HeaderName(context)}: the C interface of the {context.Settings.Name} native library, written by aqua {AquariusVersion.Current}\n");
        text.Append($"   from {exports.Module} (docs/native-libraries.md). Do not edit; rebuild the library instead.\n\n");
        text.Append($"   Values are copied at the boundary. Strings and arrays a function returns belong to the caller: free them with\n");
        text.Append($"   {prefix}free. A failed call returns zero and {prefix}last_error() gives the reason (NULL after a successful call;\n");
        text.Append($"   kept per thread). Calls from several threads are serialized; the first call runs the library's top-level code. */\n");
        text.Append($"#ifndef {macro}_H\n#define {macro}_H\n\n#include <stdbool.h>\n#include <stddef.h>\n#include <stdint.h>\n\n");
        text.Append($"#if defined(_WIN32) && !defined({macro}_STATIC)\n#define {api} __declspec(dllimport)\n#else\n#define {api}\n#endif\n\n");
        text.Append("#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n");
        text.Append($"#define {macro}_ABI_VERSION {AbiVersion}\n\n");
        foreach (var element in CNames.ArrayElements(exports))
            text.Append($"typedef struct {CNames.ArrayStruct(context, element)} {{ {CNames.Spaced(CNames.Scalar(element, false))}*data; size_t count; }} {CNames.ArrayStruct(context, element)};\n");
        if (CNames.ArrayElements(exports).Any()) text.Append('\n');
        foreach (var shape in exports.Structs) {
            text.Append(CNames.Comment(shape.Documentation));
            string fields = string.Join(" ", shape.Fields.Select((f, i) => $"{CNames.Spaced(CNames.Scalar(f.Type.Scalar, true))}{CNames.Parameter(f.Name, i)};"));
            text.Append($"typedef struct {CNames.Struct(context, shape)} {{ {fields} }} {CNames.Struct(context, shape)};\n\n");
        }
        text.Append("/* Starts the library (runs its top-level code once). Optional: the first call starts it. Returns false when it fails. */\n");
        text.Append($"{api} bool {prefix}initialize(void);\n");
        text.Append("/* Stops the library and releases everything it holds; a later call starts it again. */\n");
        text.Append($"{api} void {prefix}shutdown(void);\n");
        text.Append("/* Why this thread's last call failed, or NULL when it succeeded. Valid until this thread's next call. */\n");
        text.Append($"{api} const char *{prefix}last_error(void);\n");
        text.Append("/* Frees a string or an array's data returned by this library. */\n");
        text.Append($"{api} void {prefix}free(void *memory);\n");
        text.Append($"/* The ABI version of the library ({macro}_ABI_VERSION for this header). */\n");
        text.Append($"{api} uint32_t {prefix}abi_version(void);\n\n");
        foreach (var function in exports.Functions) {
            text.Append(CNames.Comment(function.Documentation));
            string parameters = function.Parameters.Count == 0 ? "void" : string.Join(", ", function.Parameters.Select((p, i) => CNames.Declaration(context, p, i)));
            text.Append($"{api} {CNames.Spaced(CNames.Result(context, function.Result))}{CNames.Function(context, function.Name)}({parameters});\n");
        }
        text.Append("\n#ifdef __cplusplus\n}\n#endif\n\n#endif\n");
        return text.ToString();
    }
}
