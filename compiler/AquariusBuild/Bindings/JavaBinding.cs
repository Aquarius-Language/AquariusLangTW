using System.Text;
using AquariusLang.ast;
using AquariusLang.Compiler;

namespace AquariusLang.Build.Bindings;

/// <summary>
/// The Java binding (Android, and the desktop JVM on Windows and Linux), <c>java/&lt;package path&gt;/&lt;Class&gt;.java</c>:
/// one final class whose static initializer loads the library and whose static native methods the library registers in
/// JNI_OnLoad (runtime/embed/jni.c), with a nested class per exported struct (public fields), a nested
/// <c>AquariusException</c> thrown when a call fails, and nested <c>NonNull</c>/<c>Nullable</c> annotations on reference
/// types. Kotlin calls it as it is. The class is <c>[export.&lt;platform&gt;] java_package</c> (default: the library's name)
/// and <c>java_class</c> (default: its name in UpperCamelCase).
/// </summary>
public sealed class JavaBinding : IBindingGenerator
{
    public string Language => "java";

    public static string Package(BindingContext context) => context.Settings.For(context.Platform).JavaPackage ?? context.Settings.Name;
    public static string Class(BindingContext context) => context.Settings.For(context.Platform).JavaClass ?? ExportTable.UpperCamelCase(context.Settings.Name);
    /// <summary>The class as JNI names it (<c>org/example/geometry/Geometry</c>).</summary>
    public static string JniClass(BindingContext context) => Package(context).Replace('.', '/') + "/" + Class(context);
    public static string StructName(ExportStruct shape) => ExportTable.UpperCamelCase(shape.Name);
    public static string MethodName(ExportFunction function) => ExportTable.LowerCamelCase(function.Name);

    public IReadOnlyList<BindingFile> Generate(BindingContext context) =>
        [new($"java/{Package(context).Replace('.', '/')}/{Class(context)}.java", Source(context))];

    private static string Scalar(ScalarType scalar) => scalar switch {
        ScalarType.Int => "int", ScalarType.Long => "long", ScalarType.Float => "float", ScalarType.Double => "double",
        ScalarType.Bool => "boolean", ScalarType.String => "String", _ => throw new ArgumentException($"{scalar} has no Java type.")
    };
    private static string Type(BindingContext context, ExportType type) => type.Shape switch {
        ExportShape.Void => "void",
        ExportShape.Scalar => Scalar(type.Scalar),
        ExportShape.Array => Scalar(type.Scalar) + "[]",
        _ => StructName(context.Exports.Structs[type.Struct])
    };
    private static bool Reference(ExportType type) => type.Shape is ExportShape.Array or ExportShape.Struct || type.Shape == ExportShape.Scalar && type.Scalar == ScalarType.String;

    /// <summary>The JNI descriptor of a type (<c>D</c>, <c>[D</c>, <c>Ljava/lang/String;</c>, <c>Lorg/x/Geometry$Point;</c>).</summary>
    public static string Descriptor(BindingContext context, ExportType type) => type.Shape switch {
        ExportShape.Void => "V",
        ExportShape.Array => "[" + ScalarDescriptor(type.Scalar),
        ExportShape.Struct => $"L{JniClass(context)}${StructName(context.Exports.Structs[type.Struct])};",
        _ => ScalarDescriptor(type.Scalar)
    };
    private static string ScalarDescriptor(ScalarType scalar) => scalar switch {
        ScalarType.Int => "I", ScalarType.Long => "J", ScalarType.Float => "F", ScalarType.Double => "D", ScalarType.Bool => "Z",
        _ => "Ljava/lang/String;"
    };
    public static string Signature(BindingContext context, ExportFunction function) =>
        "(" + string.Concat(function.Parameters.Select(p => Descriptor(context, p.Type))) + ")" + Descriptor(context, function.Result);

    private static string Doc(string documentation, string indent) => documentation.Length == 0 ? ""
        : $"{indent}/**\n{string.Concat(documentation.Split('\n').Select(l => $"{indent} * {l.Replace("*/", "* /")}\n"))}{indent} */\n";

    public static string Source(BindingContext context)
    {
        var exports = context.Exports;
        string name = Class(context);
        var text = new StringBuilder();
        text.Append($"// {name}.java: the Java binding of the {context.Settings.Name} native library, written by aqua {AquariusVersion.Current}\n");
        text.Append($"// from {exports.Module} (docs/native-libraries.md). Do not edit; rebuild the library instead.\n");
        text.Append($"package {Package(context)};\n\nimport java.lang.annotation.ElementType;\nimport java.lang.annotation.Retention;\nimport java.lang.annotation.RetentionPolicy;\nimport java.lang.annotation.Target;\n\n");
        text.Append($"/** The {context.Settings.Name} library. Calls are serialized by the library; the first one runs its top-level code. */\n");
        text.Append($"public final class {name} {{\n    static {{\n        System.loadLibrary(\"{context.Settings.Name}\");\n    }}\n\n    private {name}() {{}}\n\n");
        text.Append("    /** A call the library could not complete; the message is its reason. */\n");
        text.Append("    public static final class AquariusException extends RuntimeException {\n        public AquariusException(String message) { super(message); }\n    }\n\n");
        text.Append("    /** A reference that is never null. */\n    @Retention(RetentionPolicy.CLASS) @Target({ElementType.METHOD, ElementType.PARAMETER, ElementType.FIELD})\n    public @interface NonNull {}\n\n");
        text.Append("    /** A reference that may be null. */\n    @Retention(RetentionPolicy.CLASS) @Target({ElementType.METHOD, ElementType.PARAMETER, ElementType.FIELD})\n    public @interface Nullable {}\n\n");
        foreach (var shape in exports.Structs) {
            text.Append(Doc(shape.Documentation, "    "));
            string structName = StructName(shape);
            text.Append($"    public static final class {structName} {{\n");
            var fields = shape.Fields.Select((f, i) => (Name: CNames.Parameter(f.Name, i), Type: Scalar(f.Type.Scalar), String: f.Type.Scalar == ScalarType.String)).ToArray();
            foreach (var field in fields) text.Append($"        public {(field.String ? "@Nullable " : "")}{field.Type} {field.Name}{(field.String ? " = \"\"" : "")};\n");
            text.Append($"\n        public {structName}() {{}}\n\n");
            text.Append($"        public {structName}({string.Join(", ", fields.Select(f => $"{(f.String ? "@NonNull " : "")}{f.Type} {f.Name}"))}) {{\n");
            foreach (var field in fields) text.Append($"            this.{field.Name} = {field.Name};\n");
            text.Append("        }\n    }\n\n");
        }
        text.Append("    /** Starts the library (runs its top-level code once). Optional: the first call starts it. */\n    public static native boolean initialize();\n\n");
        text.Append("    /** Stops the library; a later call starts it again. */\n    public static native void shutdown();\n\n");
        text.Append("    /** The C ABI version the library implements. */\n    public static native int abiVersion();\n\n");
        foreach (var function in exports.Functions) {
            text.Append(Doc(function.Documentation, "    "));
            string parameters = string.Join(", ", function.Parameters.Select((p, i) =>
                $"{(Reference(p.Type) ? "@NonNull " : "")}{Type(context, p.Type)} {CNames.Parameter(p.Name, i)}"));
            string result = (Reference(function.Result) ? "@NonNull " : "") + Type(context, function.Result);
            text.Append($"    public static native {result} {MethodName(function)}({parameters});\n\n");
        }
        text.Length -= 1;
        text.Append("}\n");
        return text.ToString();
    }
}
