using System.Text;
using AquariusLang.ast;
using AquariusLang.Compiler;

namespace AquariusLang.Build.Bindings;

/// <summary>
/// The Rust binding, a crate in <c>rust/&lt;name&gt;/</c>: <c>Cargo.toml</c>, <c>build.rs</c> (links the shared library
/// from the output's lib/ folder, or <c>&lt;NAME&gt;_LIB_DIR</c>) and <c>src/lib.rs</c>: the C ABI as <c>ffi</c> (an
/// <c>extern "C"</c> block and <c>#[repr(C)]</c> types) and safe functions over it that take <c>&amp;str</c> and slices,
/// return <c>String</c>, <c>Vec</c> and structs with <c>String</c> fields, free what the library returns and give
/// <c>Result&lt;T, Error&gt;</c>.
/// </summary>
public sealed class RustBinding : IBindingGenerator
{
    public string Language => "rust";

    public IReadOnlyList<BindingFile> Generate(BindingContext context)
    {
        string name = context.Settings.Name, folder = $"rust/{name}/";
        return [
            new(folder + "Cargo.toml", Manifest(context)),
            new(folder + "build.rs", BuildScript(context)),
            new(folder + "src/lib.rs", Library(context)),
        ];
    }

    private static string Manifest(BindingContext context) => $"""
        # The Rust binding of the {context.Settings.Name} native library, written by aqua {AquariusVersion.Current}. Do not edit.
        [package]
        name = "{context.Settings.Name}"
        version = "{context.Settings.Version ?? "0.0.0"}"
        edition = "2021"
        links = "{context.Settings.Name}"
        build = "build.rs"

        [lib]
        path = "src/lib.rs"

        """;

    private static string BuildScript(BindingContext context)
    {
        string name = context.Settings.Name, variable = CBinding.Macro(context) + "_LIB_DIR";
        return $$"""
            // Links the {{name}} shared library: from {{variable}}, else the lib/ folder of the output this crate came with.
            use std::path::PathBuf;

            fn main() {
                let folder = match std::env::var("{{variable}}") {
                    Ok(folder) => PathBuf::from(folder),
                    Err(_) => PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../lib"),
                };
                println!("cargo:rerun-if-env-changed={{variable}}");
                println!("cargo:rustc-link-search=native={}", folder.display());
                println!("cargo:rustc-link-lib=dylib={{name}}");
            }

            """;
    }

    private static string Scalar(ScalarType scalar) => scalar switch {
        ScalarType.Int => "i32", ScalarType.Long => "i64", ScalarType.Float => "f32", ScalarType.Double => "f64", ScalarType.Bool => "bool",
        _ => throw new ArgumentException($"{scalar} has no Rust scalar.")
    };
    private static string StructName(ExportStruct shape) => ExportTable.UpperCamelCase(shape.Name);
    private static string FfiArray(ScalarType element) => CNames.Element(element) + "_array";

    /// <summary>A safe function's Rust type: what it takes (<paramref name="parameter"/>) or returns.</summary>
    private static string Type(BindingContext context, ExportType type, bool parameter) => type.Shape switch {
        ExportShape.Void => "()",
        ExportShape.Scalar when type.Scalar == ScalarType.String => parameter ? "&str" : "String",
        ExportShape.Scalar => Scalar(type.Scalar),
        ExportShape.Array => parameter ? $"&[{Scalar(type.Scalar)}]" : $"Vec<{Scalar(type.Scalar)}>",
        _ => (parameter ? "&" : "") + StructName(context.Exports.Structs[type.Struct])
    };

    /// <summary>An ffi declaration's type of a C value.</summary>
    private static string FfiType(BindingContext context, ExportType type, bool result) => type.Shape switch {
        ExportShape.Scalar when type.Scalar == ScalarType.String => result ? "*mut c_char" : "*const c_char",
        ExportShape.Scalar => Scalar(type.Scalar),
        ExportShape.Array => FfiArray(type.Scalar),
        _ => ExportTable.SnakeCase(context.Exports.Structs[type.Struct].Name)
    };

    private static string Doc(string documentation, string indent = "") =>
        documentation.Length == 0 ? "" : string.Concat(documentation.Split('\n').Select(l => $"{indent}/// {l}\n"));

    public static string Library(BindingContext context)
    {
        var exports = context.Exports;
        string prefix = context.Settings.SymbolPrefix;
        var text = new StringBuilder();
        text.Append($"//! The Rust binding of the {context.Settings.Name} native library, written by aqua {AquariusVersion.Current} from\n");
        text.Append($"//! {exports.Module} (docs/native-libraries.md). Do not edit; rebuild the library instead.\n");
        text.Append("//! Calls are serialized by the library; the first one runs its top-level code. A failed call gives `Err`.\n");
        text.Append("#![allow(non_snake_case)]\n\nuse std::ffi::{c_char, c_void, CStr, CString};\nuse std::fmt;\n\n");
        Ffi(context, text);
        text.Append("/// A call the library could not complete, with its reason.\n#[derive(Debug, Clone, PartialEq, Eq)]\npub struct Error(pub String);\n\n");
        text.Append("impl fmt::Display for Error {\n    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result { f.write_str(&self.0) }\n}\n\nimpl std::error::Error for Error {}\n\n");
        text.Append($"fn check() -> Result<(), Error> {{\n    let error = unsafe {{ ffi::{prefix}last_error() }};\n");
        text.Append("    if error.is_null() { Ok(()) } else { Err(Error(unsafe { CStr::from_ptr(error) }.to_string_lossy().into_owned())) }\n}\n\n");
        text.Append($"fn take_string(text: *mut c_char) -> String {{\n    if text.is_null() {{ return String::new(); }}\n");
        text.Append($"    let value = unsafe {{ CStr::from_ptr(text) }}.to_string_lossy().into_owned();\n    unsafe {{ ffi::{prefix}free(text as *mut c_void) }};\n    value\n}}\n\n");
        text.Append($"fn take_vec<T: Copy>(data: *mut T, count: usize) -> Vec<T> {{\n    if data.is_null() {{ return Vec::new(); }}\n");
        text.Append($"    let values = unsafe {{ std::slice::from_raw_parts(data, count) }}.to_vec();\n    unsafe {{ ffi::{prefix}free(data as *mut c_void) }};\n    values\n}}\n\n");
        text.Append("fn c_string(text: &str) -> Result<CString, Error> {\n    CString::new(text).map_err(|_| Error(\"A string argument contains a NUL character.\".into()))\n}\n\n");
        foreach (var shape in exports.Structs) Struct(context, shape, text);
        text.Append($"/// Starts the library (runs its top-level code once). Optional: the first call starts it.\npub fn initialize() -> Result<(), Error> {{\n");
        text.Append($"    if unsafe {{ ffi::{prefix}initialize() }} {{ Ok(()) }} else {{ check().and(Err(Error(\"The library did not start.\".into()))) }}\n}}\n\n");
        text.Append($"/// Stops the library; a later call starts it again.\npub fn shutdown() {{\n    unsafe {{ ffi::{prefix}shutdown() }}\n}}\n\n");
        foreach (var function in exports.Functions) Function(context, function, text);
        return text.ToString();
    }

    private static void Ffi(BindingContext context, StringBuilder text)
    {
        var exports = context.Exports;
        string prefix = context.Settings.SymbolPrefix;
        text.Append("/// The C ABI (the library's C header), for what the safe functions do not cover.\npub mod ffi {\n    #![allow(non_camel_case_types)]\n    use std::ffi::{c_char, c_void};\n\n");
        foreach (var element in CNames.ArrayElements(exports))
            text.Append($"    #[repr(C)]\n    #[derive(Debug, Clone, Copy)]\n    pub struct {FfiArray(element)} {{ pub data: *mut {Scalar(element)}, pub count: usize }}\n\n");
        foreach (var shape in exports.Structs) {
            text.Append($"    #[repr(C)]\n    #[derive(Debug, Clone, Copy)]\n    pub struct {ExportTable.SnakeCase(shape.Name)} {{\n");
            for (int i = 0; i < shape.Fields.Count; i++)
                text.Append($"        pub {CNames.Parameter(shape.Fields[i].Name, i)}: {FfiType(context, shape.Fields[i].Type, true)},\n");
            text.Append("    }\n\n");
        }
        text.Append("    extern \"C\" {\n");
        text.Append($"        pub fn {prefix}initialize() -> bool;\n        pub fn {prefix}shutdown();\n        pub fn {prefix}last_error() -> *const c_char;\n");
        text.Append($"        pub fn {prefix}free(memory: *mut c_void);\n        pub fn {prefix}abi_version() -> u32;\n");
        foreach (var function in exports.Functions) {
            var parameters = new List<string>();
            for (int i = 0; i < function.Parameters.Count; i++) {
                var parameter = function.Parameters[i];
                string name = CNames.Parameter(parameter.Name, i);
                if (parameter.Type.Shape == ExportShape.Array) {
                    parameters.Add($"{name}: *const {Scalar(parameter.Type.Scalar)}");
                    parameters.Add($"{name}_count: usize");
                } else parameters.Add($"{name}: {FfiType(context, parameter.Type, false)}");
            }
            string result = function.Result.Shape == ExportShape.Void ? "" : " -> " + FfiType(context, function.Result, true);
            text.Append($"        pub fn {CNames.Function(context, function.Name)}({string.Join(", ", parameters)}){result};\n");
        }
        text.Append("    }\n}\n\n");
    }

    private static void Struct(BindingContext context, ExportStruct shape, StringBuilder text)
    {
        string name = StructName(shape), ffi = "ffi::" + ExportTable.SnakeCase(shape.Name);
        text.Append(Doc(shape.Documentation));
        text.Append($"#[derive(Debug, Clone, PartialEq)]\npub struct {name} {{\n");
        for (int i = 0; i < shape.Fields.Count; i++) {
            var type = shape.Fields[i].Type;
            text.Append($"    pub {CNames.Parameter(shape.Fields[i].Name, i)}: {(type.Scalar == ScalarType.String ? "String" : Scalar(type.Scalar))},\n");
        }
        text.Append("}\n\n");
        // To C: the C strings live in the returned vector until the call is over.
        string mutable = shape.Fields.Any(f => f.Type.Scalar == ScalarType.String) ? "mut " : "";
        text.Append($"impl {name} {{\n    fn to_c(&self) -> Result<({ffi}, Vec<CString>), Error> {{\n        let {mutable}strings = Vec::new();\n");
        var fields = new List<string>();
        for (int i = 0; i < shape.Fields.Count; i++) {
            string field = CNames.Parameter(shape.Fields[i].Name, i);
            if (shape.Fields[i].Type.Scalar == ScalarType.String) {
                text.Append($"        strings.push(c_string(&self.{field})?);\n        let {field} = strings.last().unwrap().as_ptr() as *mut c_char;\n");
                fields.Add(field);
            } else fields.Add($"{field}: self.{field}");
        }
        text.Append($"        Ok(({ffi} {{ {string.Join(", ", fields)} }}, strings))\n    }}\n\n");
        text.Append($"    fn from_c(value: {ffi}) -> Self {{\n        {name} {{ ");
        text.Append(string.Join(", ", shape.Fields.Select((f, i) => {
            string field = CNames.Parameter(f.Name, i);
            return f.Type.Scalar == ScalarType.String ? $"{field}: take_string(value.{field})" : $"{field}: value.{field}";
        })));
        text.Append(" }\n    }\n}\n\n");
    }

    private static void Function(BindingContext context, ExportFunction function, StringBuilder text)
    {
        text.Append(Doc(function.Documentation));
        var parameters = function.Parameters.Select((p, i) => $"{CNames.Parameter(p.Name, i)}: {Type(context, p.Type, true)}");
        string result = Type(context, function.Result, false);
        text.Append($"pub fn {ExportTable.SnakeCase(function.Name)}({string.Join(", ", parameters)}) -> Result<{result}, Error> {{\n");
        var arguments = new List<string>();
        for (int i = 0; i < function.Parameters.Count; i++) {
            var parameter = function.Parameters[i];
            string name = CNames.Parameter(parameter.Name, i);
            switch (parameter.Type.Shape) {
                case ExportShape.Scalar when parameter.Type.Scalar == ScalarType.String:
                    text.Append($"    let {name} = c_string({name})?;\n");
                    arguments.Add($"{name}.as_ptr()"); break;
                case ExportShape.Scalar: arguments.Add(name); break;
                case ExportShape.Array: arguments.Add($"{name}.as_ptr()"); arguments.Add($"{name}.len()"); break;
                case ExportShape.Struct:
                    text.Append($"    let ({name}, _{name}_strings) = {name}.to_c()?;\n");
                    arguments.Add(name); break;
            }
        }
        string call = $"unsafe {{ ffi::{CNames.Function(context, function.Name)}({string.Join(", ", arguments)}) }}";
        var type = function.Result;
        if (type.Shape == ExportShape.Void) { text.Append($"    {call};\n    check()\n}}\n\n"); return; }
        text.Append($"    let result = {call};\n    check()?;\n");
        text.Append(type.Shape switch {
            ExportShape.Scalar when type.Scalar == ScalarType.String => "    Ok(take_string(result))\n",
            ExportShape.Array => "    Ok(take_vec(result.data, result.count))\n",
            ExportShape.Struct => $"    Ok({StructName(context.Exports.Structs[type.Struct])}::from_c(result))\n",
            _ => "    Ok(result)\n"
        });
        text.Append("}\n\n");
    }
}
