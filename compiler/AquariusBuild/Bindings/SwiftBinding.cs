using System.Text;
using AquariusLang.ast;
using AquariusLang.Compiler;

namespace AquariusLang.Build.Bindings;

/// <summary>
/// The Swift binding (macOS and iOS): <c>include/module.modulemap</c>, which makes the C header a Clang module Swift
/// imports (and links the library), and <c>swift/&lt;Module&gt;.swift</c>, an enum of static functions over it that take
/// and return <c>String</c>, arrays and structs, free what the library returns and <c>throw</c> the library's error. The
/// module and the enum are <c>[export.&lt;platform&gt;] module</c> (default: the library's name in UpperCamelCase); a
/// framework package carries the same module as a framework module (<see cref="FrameworkModuleMap"/>). Programs add the
/// Swift file to their own target.
/// </summary>
public sealed class SwiftBinding : IBindingGenerator
{
    public string Language => "swift";

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal) {
        "associatedtype", "class", "deinit", "enum", "extension", "fileprivate", "func", "import", "init", "inout", "internal", "let",
        "open", "operator", "private", "precedencegroup", "protocol", "public", "rethrows", "static", "struct", "subscript", "typealias",
        "var", "break", "case", "catch", "continue", "default", "defer", "do", "else", "fallthrough", "for", "guard", "if", "in",
        "repeat", "return", "throw", "switch", "where", "while", "Any", "as", "await", "false", "is", "nil", "self", "Self", "super",
        "throws", "true", "try", "Type", "Error"
    };

    /// <summary>The Swift module (and framework) name: <c>[export.&lt;platform&gt;] module</c>, else the name in UpperCamelCase.</summary>
    public static string Module(BindingContext context) => context.Settings.For(context.Platform).Module ?? ExportTable.UpperCamelCase(context.Settings.Name);

    public IReadOnlyList<BindingFile> Generate(BindingContext context) => [
        new("include/module.modulemap", ModuleMap(context)),
        new($"swift/{Module(context)}.swift", Source(context)),
    ];

    /// <summary>The header as a Clang module, for <c>-I include</c> (the folder package and an xcframework's static slices).</summary>
    public static string ModuleMap(BindingContext context) =>
        $"// The C module of the {context.Settings.Name} native library for Swift, written by aqua {AquariusVersion.Current}. Do not edit.\n" +
        $"module {Module(context)} {{\n    header \"{CBinding.HeaderName(context)}\"\n    link \"{context.Settings.Name}\"\n    export *\n}}\n";

    /// <summary>The same module inside a framework (Modules/module.modulemap): the framework's binary is the library.</summary>
    public static string FrameworkModuleMap(BindingContext context) =>
        $"// The C module of the {context.Settings.Name} native library for Swift, written by aqua {AquariusVersion.Current}. Do not edit.\n" +
        $"framework module {Module(context)} {{\n    umbrella header \"{CBinding.HeaderName(context)}\"\n    export *\n    module * {{ export * }}\n}}\n";

    private static string Name(string name) => Keywords.Contains(name) ? $"`{name}`" : name;
    private static string Scalar(ScalarType scalar) => scalar switch {
        ScalarType.Int => "Int32", ScalarType.Long => "Int64", ScalarType.Float => "Float", ScalarType.Double => "Double",
        ScalarType.Bool => "Bool", ScalarType.String => "String", _ => throw new ArgumentException($"{scalar} has no Swift type.")
    };
    private static string StructName(ExportStruct shape) => ExportTable.UpperCamelCase(shape.Name);
    private static string Type(BindingContext context, ExportType type) => type.Shape switch {
        ExportShape.Void => "Void",
        ExportShape.Scalar => Scalar(type.Scalar),
        ExportShape.Array => $"[{Scalar(type.Scalar)}]",
        _ => StructName(context.Exports.Structs[type.Struct])
    };
    private static string Doc(string documentation, string indent) =>
        documentation.Length == 0 ? "" : string.Concat(documentation.Split('\n').Select(l => $"{indent}/// {l}\n"));

    public static string Source(BindingContext context)
    {
        var exports = context.Exports;
        string module = Module(context), prefix = context.Settings.SymbolPrefix;
        var text = new StringBuilder();
        text.Append($"// {module}.swift: the Swift binding of the {context.Settings.Name} native library, written by aqua {AquariusVersion.Current}\n");
        text.Append($"// from {exports.Module} (docs/native-libraries.md). Do not edit; rebuild the library instead.\n");
        text.Append($"import Foundation\nimport {module}\n\n");
        text.Append($"/// The {context.Settings.Name} library. Calls are serialized by the library; the first one runs its top-level code.\n");
        text.Append($"/// A call that fails throws ``{module}/Error`` with the library's reason.\npublic enum {module} {{\n");
        text.Append("    /// A call the library could not complete; `message` is its reason.\n");
        text.Append("    public struct Error: Swift.Error, Equatable, CustomStringConvertible {\n        public let message: String\n");
        text.Append("        public init(message: String) { self.message = message }\n        public var description: String { message }\n    }\n\n");
        foreach (var shape in exports.Structs) Struct(context, shape, text);
        text.Append($"    /// The C ABI version the library implements.\n    public static var abiVersion: UInt32 {{ {prefix}abi_version() }}\n\n");
        text.Append("    /// Starts the library (runs its top-level code once). Optional: the first call starts it.\n");
        text.Append($"    public static func initialize() throws {{\n        if !{prefix}initialize() {{\n            try check()\n");
        text.Append("            throw Error(message: \"The library did not start.\")\n        }\n    }\n\n");
        text.Append($"    /// Stops the library; a later call starts it again.\n    public static func shutdown() {{ {prefix}shutdown() }}\n\n");
        foreach (var function in exports.Functions) Function(context, function, text);
        Helpers(context, text);
        return text.ToString();
    }

    private static void Struct(BindingContext context, ExportStruct shape, StringBuilder text)
    {
        string name = StructName(shape), c = CNames.Struct(context, shape);
        var fields = shape.Fields.Select((f, i) => (Swift: Name(CNames.Parameter(f.Name, i)), Type: Scalar(f.Type.Scalar),
            String: f.Type.Scalar == ScalarType.String)).ToArray();
        text.Append(Doc(shape.Documentation, "    "));
        text.Append($"    public struct {name}: Equatable {{\n");
        foreach (var field in fields) text.Append($"        public var {field.Swift}: {field.Type}\n");
        text.Append($"\n        public init({string.Join(", ", fields.Select(f => $"{f.Swift}: {f.Type}"))}) {{\n");
        foreach (var field in fields) text.Append($"            self.{field.Swift} = {field.Swift}\n");
        text.Append("        }\n\n");
        // To C: string fields are copies the caller frees once the call is over (CopiedArguments).
        text.Append($"        fileprivate func toC(_ copies: inout CopiedArguments) throws -> {c} {{\n");
        text.Append($"            {c}({string.Join(", ", fields.Select(f => $"{f.Swift}: {(f.String ? $"try copies.string({f.Swift})" : f.Swift)}"))})\n        }}\n\n");
        text.Append($"        fileprivate init(taking value: {c}) {{\n");
        text.Append($"            self.init({string.Join(", ", fields.Select(f => $"{f.Swift}: {(f.String ? $"takeString(value.{f.Swift})" : $"value.{f.Swift}")}"))})\n");
        text.Append("        }\n    }\n\n");
    }

    private static void Function(BindingContext context, ExportFunction function, StringBuilder text)
    {
        text.Append(Doc(function.Documentation, "    "));
        var parameters = function.Parameters.Select((p, i) => {
            string name = Name(CNames.Parameter(p.Name, i));
            return $"{(ExportTable.IsIdentifier(p.Name) ? "" : "_ ")}{name}: {Type(context, p.Type)}";
        });
        string result = function.Result.Shape == ExportShape.Void ? "" : " -> " + Type(context, function.Result);
        text.Append($"    public static func {Name(ExportTable.LowerCamelCase(function.Name))}({string.Join(", ", parameters)}) throws{result} {{\n");
        bool copies = function.Parameters.Any(p => p.Type.Shape == ExportShape.Struct ||
            p.Type.Shape == ExportShape.Scalar && p.Type.Scalar == ScalarType.String);
        if (copies) text.Append("        var copies = CopiedArguments()\n        defer { copies.release() }\n");
        var arguments = new List<string>();
        for (int i = 0; i < function.Parameters.Count; i++) {
            var parameter = function.Parameters[i];
            string name = Name(CNames.Parameter(parameter.Name, i));
            switch (parameter.Type.Shape) {
                case ExportShape.Scalar when parameter.Type.Scalar == ScalarType.String: arguments.Add($"copies.string({name})"); break;
                case ExportShape.Scalar: arguments.Add(name); break;
                case ExportShape.Array: arguments.Add(name); arguments.Add($"{name}.count"); break;
                case ExportShape.Struct: arguments.Add($"{name}.toC(&copies)"); break;
            }
        }
        string call = (copies ? "try " : "") + $"{CNames.Function(context, function.Name)}({string.Join(", ", arguments)})";
        var type = function.Result;
        if (type.Shape == ExportShape.Void) { text.Append($"        {call}\n        try check()\n    }}\n\n"); return; }
        text.Append($"        let result = {call}\n        try check()\n");
        text.Append(type.Shape switch {
            ExportShape.Scalar when type.Scalar == ScalarType.String => "        return takeString(result)\n",
            ExportShape.Array => "        return takeArray(result.data, result.count)\n",
            ExportShape.Struct => $"        return {StructName(context.Exports.Structs[type.Struct])}(taking: result)\n",
            _ => "        return result\n"
        });
        text.Append("    }\n\n");
    }

    /// The enum's private helpers and its closing brace, then the file's: taking ownership of what the library returns.
    private static void Helpers(BindingContext context, StringBuilder text)
    {
        string prefix = context.Settings.SymbolPrefix;
        text.Append($"    private static func check() throws {{\n        if let error = {prefix}last_error() {{ throw Error(message: String(cString: error)) }}\n    }}\n\n");
        text.Append("    /// C copies of string arguments, alive until the call is over (the caller releases them).\n");
        text.Append("    fileprivate struct CopiedArguments {\n        private var memory: [UnsafeMutablePointer<CChar>] = []\n\n");
        text.Append("        mutating func string(_ text: String) throws -> UnsafeMutablePointer<CChar> {\n");
        text.Append("            guard !text.utf8.contains(0) else { throw Error(message: \"A string argument contains a NUL character.\") }\n");
        text.Append("            let copy = strdup(text)!\n            memory.append(copy)\n            return copy\n        }\n\n");
        text.Append("        func release() { memory.forEach { free($0) } }\n    }\n}\n\n");
        text.Append("/// A string the library returned, freed.\nprivate func takeString(_ text: UnsafeMutablePointer<CChar>?) -> String {\n");
        text.Append($"    guard let text else {{ return \"\" }}\n    defer {{ {prefix}free(text) }}\n    return String(cString: text)\n}}\n\n");
        text.Append("/// An array the library returned, freed.\nprivate func takeArray<T>(_ data: UnsafeMutablePointer<T>?, _ count: Int) -> [T] {\n");
        text.Append($"    guard let data else {{ return [] }}\n    defer {{ {prefix}free(data) }}\n    return Array(UnsafeBufferPointer(start: data, count: count))\n}}\n");
    }
}
