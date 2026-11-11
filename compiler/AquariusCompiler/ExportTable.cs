using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AquariusLang.ast;
using AquariusLang.token;

namespace AquariusLang.Compiler;

/// <summary>The kind of an exported value: nothing (a function without a result annotation), a scalar, a typed array of
/// numbers or Booleans, or an exported struct.</summary>
public enum ExportShape : byte { Void = 0, Scalar = 1, Array = 2, Struct = 3 }

/// <summary>An exported type. <see cref="Scalar"/> is the element of scalars and arrays (the annotation's
/// <see cref="ScalarType"/>); <see cref="Struct"/> indexes <see cref="ExportTable.Structs"/>.</summary>
public sealed record ExportType(ExportShape Shape, ScalarType Scalar = 0, int Struct = -1) {
    public static ExportType Void { get; } = new(ExportShape.Void);
}
public sealed record ExportParameter(string Name, ExportType Type);
public sealed record ExportField(string Name, ExportType Type);
/// <param name="Name">The public ASCII name (bindings convert it: C <c>geo_area</c>, Java <c>area</c>).</param>
/// <param name="Binding">The Aquarius name of the binding in the entry module's scope.</param>
/// <param name="Documentation">The comment above the declaration, without its <c>#</c> markers.</param>
public sealed record ExportFunction(string Name, string Binding, IReadOnlyList<ExportParameter> Parameters, ExportType Result, string Documentation);
public sealed record ExportStruct(string Name, string Binding, IReadOnlyList<ExportField> Fields, string Documentation);

/// <summary>
/// What a program exposes through native library outputs (docs/native-libraries.md): the functions and structs marked
/// <c>匯出</c> / <c>export</c> in its entry module. It is the single description every consumer reads: the build writes it
/// into the application's <c>aquarius.exports</c> custom section (versioned), the embed runtime's thunks and every
/// binding generator are made from it. Programs without exports have no section and compile exactly as before.
/// </summary>
public sealed class ExportTable {
    public const string SectionName = "aquarius.exports";
    public const int FormatVersion = 1;
    private static readonly byte[] Magic = "AQEX"u8.ToArray();
    /// <summary>The prefix of the hidden function the compiler adds for each exported struct: it constructs a value from
    /// its fields in order, so the embed runtime can build struct arguments (no source can write this name).</summary>
    public const string ConstructorPrefix = "export@";

    /// <summary>The names every native library defines (<c>&lt;prefix&gt;initialize</c>, ...) and the array result types.</summary>
    public static readonly IReadOnlyList<string> ReservedNames =
        ["initialize", "shutdown", "last_error", "free", "abi_version", "int_array", "long_array", "float_array", "double_array", "bool_array"];

    /// <summary>The module the exports are in (the application's entry module).</summary>
    public string Module { get; }
    public IReadOnlyList<ExportStruct> Structs { get; }
    public IReadOnlyList<ExportFunction> Functions { get; }
    public ExportTable(string module, IReadOnlyList<ExportStruct> structs, IReadOnlyList<ExportFunction> functions) {
        Module = module; Structs = structs; Functions = functions;
    }
    public bool IsEmpty => Structs.Count == 0 && Functions.Count == 0;
    internal ExportTable InModule(string module) => new(module, Structs, Functions);

    // ---- names ---------------------------------------------------------------------------------------------------

    public static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>The public name of an export: its <c>("name")</c>, else the first ASCII name it declares; null when it has none.</summary>
    public static string? PublicName(ExportMark mark, Identifier name, Identifier? alias) {
        if (mark.Name != null) return IsIdentifier(mark.Name) ? mark.Name : null;
        if (IsIdentifier(name.Value)) return name.Value;
        return alias != null && IsIdentifier(alias.Value) ? alias.Value : null;
    }

    /// <summary><c>distanceTo</c>, <c>DistanceTo</c> and <c>distance_to</c> all give <c>distance_to</c>; <c>HTTPServer</c>
    /// gives <c>http_server</c>.</summary>
    public static string SnakeCase(string name) {
        var text = new StringBuilder();
        for (int i = 0; i < name.Length; i++) {
            char c = name[i];
            if (char.IsAsciiLetterUpper(c) && i > 0 && name[i - 1] != '_'
                && (!char.IsAsciiLetterUpper(name[i - 1]) || i + 1 < name.Length && char.IsAsciiLetterLower(name[i + 1])))
                text.Append('_');
            text.Append(char.ToLowerInvariant(c));
        }
        return text.ToString();
    }

    /// <summary><c>distance_to</c> and <c>DistanceTo</c> give <c>distanceTo</c>.</summary>
    public static string LowerCamelCase(string name) {
        var parts = SnakeCase(name).Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return name;
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    /// <summary><c>Point</c> and <c>point</c> give <c>Point</c> (Swift, Java and JavaScript type names).</summary>
    public static string UpperCamelCase(string name) { string lower = LowerCamelCase(name); return char.ToUpperInvariant(lower[0]) + lower[1..]; }

    // ---- building from a checked program -------------------------------------------------------------------------

    /// <summary>The program with the hidden constructor of each exported struct appended (<see cref="ConstructorPrefix"/>):
    /// <c>函式(f0, f1, ...) { 結構(f0, f1, ...) }</c>. A program without exported structs is returned unchanged.</summary>
    public static AbstractSyntaxTree WithConstructors(AbstractSyntaxTree tree) {
        var shapes = tree.Statements.OfType<StructStatement>().Where(s => s.Export != null).ToList();
        if (shapes.Count == 0) return tree;
        var statements = tree.Statements.ToList();
        foreach (var shape in shapes) {
            Token token = shape.Token;
            var parameters = shape.Fields.Select((_, i) => new Identifier(token, "f" + i)).ToArray();
            var call = new CallExpression(token, new Identifier(token, shape.Name.Value), parameters.Select(p => (IExpression)new Identifier(token, p.Value)).ToArray());
            var function = new FunctionLiteral(token, parameters, new BlockStatement(token, [new ExpressionStatement(token, call)]));
            statements.Add(new LetStatement(token, new Identifier(token, ConstructorPrefix + shape.Name.Value), function) { Constant = true });
        }
        return new AbstractSyntaxTree(statements.ToArray());
    }

    /// <summary>The exports of a program that passed the TypeChecker (its rules guarantee every type below maps).</summary>
    public static ExportTable Build(AbstractSyntaxTree tree, string source) {
        var shapes = tree.Statements.OfType<StructStatement>().Where(s => s.Export != null).ToList();
        var structs = shapes.Select(shape => new ExportStruct(PublicName(shape.Export!, shape.Name, shape.Alias)!, shape.Name.Value,
            shape.Fields.Select(f => new ExportField(f.Value, new ExportType(ExportShape.Scalar, f.Type!.Element))).ToArray(),
            Documentation(source, shape.Export!.Keyword.Start))).ToArray();
        ExportType Type(TypeAnnotation? type) {
            if (type == null) return ExportType.Void;
            if (type.StructName != null)
                return new ExportType(ExportShape.Struct, Struct: shapes.FindIndex(s => (StructFlagCode(s)) == type.StructCode));
            return new ExportType(type.IsArray ? ExportShape.Array : ExportShape.Scalar, type.Element);
        }
        var functions = tree.Statements.OfType<LetStatement>().Where(l => l.Export != null).Select(let => {
            var function = (FunctionLiteral)let.Value;
            return new ExportFunction(PublicName(let.Export!, let.Name, let.Alias)!, let.Name.Value,
                (function.Parameters ?? []).Select(p => new ExportParameter(p.Value, Type(p.Type))).ToArray(), Type(function.ReturnType),
                Documentation(source, let.Export!.Keyword.Start));
        }).ToArray();
        return new ExportTable("", structs, functions);
    }
    private static int StructFlagCode(StructStatement shape) => TypeChecker.StructFlag | shape.Id << 6;

    /// <summary>The comment directly above an offset: consecutive <c>#</c> lines, or a <c>## ... ##</c> block, with no blank
    /// line between it and the declaration.</summary>
    internal static string Documentation(string source, int offset) {
        int lineStart = source.LastIndexOf('\n', Math.Max(0, Math.Min(offset, source.Length) - 1)) + 1;
        var lines = source[..lineStart].Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var documentation = new List<string>();
        if (lines.Count > 0 && lines[^1].Trim().EndsWith("##", StringComparison.Ordinal)) {
            for (int i = lines.Count - 1; i >= 0; i--) {
                documentation.Insert(0, lines[i]);
                if (lines[i].TrimStart().StartsWith("##", StringComparison.Ordinal) && (i < lines.Count - 1 || lines[i].Trim().Length > 2)) break;
            }
            string block = string.Join("\n", documentation).Trim();
            block = block[2..^2];
            return string.Join("\n", block.Split('\n').Select(l => l.Trim())).Trim();
        }
        for (int i = lines.Count - 1; i >= 0 && lines[i].TrimStart().StartsWith('#'); i--) {
            string text = lines[i].TrimStart()[1..];
            documentation.Insert(0, text.StartsWith(' ') ? text[1..] : text);
        }
        return string.Join("\n", documentation).Trim();
    }

    // ---- the custom section --------------------------------------------------------------------------------------

    /// <summary>The <c>aquarius.exports</c> section's payload: "AQEX", the format version, the module, structs and functions.</summary>
    public byte[] Encode() {
        using var stream = new MemoryStream();
        stream.Write(Magic); stream.WriteByte(FormatVersion);
        void Text(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); Wasm.WasmEncoding.WriteUnsigned(stream, (uint)bytes.Length); stream.Write(bytes); }
        void Count(int count) => Wasm.WasmEncoding.WriteUnsigned(stream, (uint)count);
        void Type(ExportType type) {
            stream.WriteByte((byte)type.Shape); stream.WriteByte((byte)type.Scalar);
            if (type.Shape == ExportShape.Struct) Count(type.Struct);
        }
        Text(Module);
        Count(Structs.Count);
        foreach (var shape in Structs) {
            Text(shape.Name); Text(shape.Binding); Text(shape.Documentation); Count(shape.Fields.Count);
            foreach (var field in shape.Fields) { Text(field.Name); Type(field.Type); }
        }
        Count(Functions.Count);
        foreach (var function in Functions) {
            Text(function.Name); Text(function.Binding); Text(function.Documentation); Count(function.Parameters.Count);
            foreach (var parameter in function.Parameters) { Text(parameter.Name); Type(parameter.Type); }
            Type(function.Result);
        }
        return stream.ToArray();
    }

    public static ExportTable Decode(ReadOnlySpan<byte> payload) {
        var bytes = payload.ToArray();
        int position = 0;
        if (bytes.Length < 5 || !bytes.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidDataException("Invalid aquarius.exports section.");
        if (bytes[4] != FormatVersion) throw new InvalidDataException($"Unsupported aquarius.exports version {bytes[4]} (this compiler reads {FormatVersion}).");
        position = 5;
        byte Byte() => position < bytes.Length ? bytes[position++] : throw new InvalidDataException("Truncated aquarius.exports section.");
        int Count() {
            uint value = 0; int shift = 0; byte b;
            do { b = Byte(); value |= (uint)(b & 0x7f) << shift; shift += 7; } while ((b & 0x80) != 0 && shift < 35);
            return value <= 1_000_000 ? (int)value : throw new InvalidDataException("Invalid count in aquarius.exports.");
        }
        string Text() {
            int length = Count();
            if (length > bytes.Length - position) throw new InvalidDataException("Truncated aquarius.exports section.");
            string value = Encoding.UTF8.GetString(bytes, position, length); position += length; return value;
        }
        ExportType Type() {
            var shape = (ExportShape)Byte(); var scalar = (ScalarType)Byte();
            return new ExportType(shape, scalar, shape == ExportShape.Struct ? Count() : -1);
        }
        string module = Text();
        var structs = new ExportStruct[Count()];
        for (int i = 0; i < structs.Length; i++) {
            string name = Text(), binding = Text(), documentation = Text();
            var fields = new ExportField[Count()];
            for (int f = 0; f < fields.Length; f++) fields[f] = new ExportField(Text(), Type());
            structs[i] = new ExportStruct(name, binding, fields, documentation);
        }
        var functions = new ExportFunction[Count()];
        for (int i = 0; i < functions.Length; i++) {
            string name = Text(), binding = Text(), documentation = Text();
            var parameters = new ExportParameter[Count()];
            for (int p = 0; p < parameters.Length; p++) parameters[p] = new ExportParameter(Text(), Type());
            functions[i] = new ExportFunction(name, binding, parameters, Type(), documentation);
        }
        if (position != bytes.Length) throw new InvalidDataException("Trailing bytes in aquarius.exports.");
        return new ExportTable(module, structs, functions);
    }

    /// <summary>The export table of a compiled application; null when it exports nothing.</summary>
    public static ExportTable? Read(Wasm.WasmProgram program) =>
        program.CustomSection(SectionName) is { } payload ? Decode(payload.Span) : null;
}
