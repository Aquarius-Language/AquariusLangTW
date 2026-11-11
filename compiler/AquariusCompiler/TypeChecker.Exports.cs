using System.Collections.Generic;
using System.Linq;
using AquariusLang.ast;

namespace AquariusLang.Compiler;

/// <summary>The rules of <c>匯出</c> / <c>export</c> (docs/native-libraries.md): what a module may expose through a native
/// library's C ABI. The export table (<see cref="ExportTable"/>) is built only from programs that pass them.</summary>
public sealed partial class TypeChecker {
    // Nesting of blocks (conditionals, function bodies) around the statement being checked; exports need none.
    private int blocks;

    private void MisplacedExport(ExportMark mark) =>
        Error("匯出 只能用在模組的最上層 / export is allowed only at the top level of a module.", mark.Keyword);

    private void CheckExports(IReadOnlyList<IStatement> statements) {
        var structs = statements.OfType<StructStatement>().Where(s => s.Export != null).ToList();
        var functions = statements.OfType<LetStatement>().Where(l => l.Export != null).ToList();
        if (structs.Count == 0 && functions.Count == 0) return;
        var names = new List<(string Name, ExportMark Mark)>();
        foreach (var shape in structs) {
            if (ExportTable.PublicName(shape.Export!, shape.Name, shape.Alias) is { } name) names.Add((name, shape.Export!));
            else MissingPublicName(shape.Export!);
            foreach (var field in shape.Fields) CheckExportedField(shape, field);
        }
        foreach (var let in functions) {
            if (ExportTable.PublicName(let.Export!, let.Name, let.Alias) is { } name) names.Add((name, let.Export!));
            else MissingPublicName(let.Export!);
            CheckExportedFunction(let);
        }
        CheckExportCollisions(names);
    }

    private void MissingPublicName(ExportMark mark) {
        if (mark.Name != null)
            Error($"匯出名稱 \"{mark.Name}\" 必須是 ASCII 識別字 / The export name \"{mark.Name}\" must be an ASCII identifier (letters, digits, _).", mark.Keyword);
        else
            Error("匯出需要 ASCII 公開名稱：加上第二個名稱（變數 面積, area）或 匯出(\"area\") / An export needs an ASCII public name: add one (var 面積, area) or write export(\"area\").", mark.Keyword);
    }

    private void CheckExportedField(StructStatement shape, Identifier field) {
        if (!ExportTable.IsIdentifier(field.Value))
            Error($"匯出結構的欄位 {field.Value} 必須以 ASCII 命名 / Fields of an exported struct need ASCII names: {shape.Name.Value}.{field.Value}.", field.Token);
        else if (field.Type is not { StructName: null, IsArray: false })
            Error($"匯出結構的欄位 {shape.Name.Value}.{field.Value} 需要 整數、長整數、浮點數、雙精度、布林 或 字串 標註 / The field {shape.Name.Value}.{field.Value} of an exported struct needs an int, long, float, double, bool or string annotation.", field.Token);
    }

    private void CheckExportedFunction(LetStatement let) {
        var mark = let.Export!;
        if (let.Value is AsyncFunctionExpression) {
            Error("非同步函式還不能匯出 / Async functions cannot be exported yet.", mark.Keyword);
            return;
        }
        if (let.Value is not FunctionLiteral function) {
            Error($"只能匯出函式：{let.Name.Value} 的初始值必須是函式字面值 / Only functions can be exported: {let.Name.Value} must be initialized with a function literal.", mark.Keyword);
            return;
        }
        foreach (var parameter in function.Parameters ?? System.Array.Empty<Identifier>()) {
            if (parameter.Type == null)
                Error($"匯出函式的參數 {parameter.Value} 需要型別標註 / The parameter {parameter.Value} of an exported function needs a type annotation.", parameter.Token);
            else CheckExportableType(parameter.Type, $"參數 {parameter.Value} / parameter {parameter.Value}");
        }
        if (function.ReturnType != null) CheckExportableType(function.ReturnType, "回傳值 / result");
    }

    /// <summary>Numbers, Booleans and strings; typed arrays of numbers or Booleans; exported structs.</summary>
    private void CheckExportableType(TypeAnnotation type, string subject) {
        if (type.StructName != null) {
            // An unknown struct name was already reported.
            if (type.StructCode != 0 && Structs.TryGetValue(type.StructCode, out var shape) && shape.Export == null)
                Error($"{subject} 的結構 {shape.Name.Value} 也必須匯出 / The struct {shape.Name.Value} of the {subject.Split(" / ")[1]} must be exported too.", type.Token);
            return;
        }
        bool exportable = type.IsArray ? type.Element is ScalarType.Int or ScalarType.Long or ScalarType.Float or ScalarType.Double or ScalarType.Bool
            : type.Element != 0;
        if (!exportable)
            Error($"{subject} 的型別 {type.String()} 不能匯出（可用：數字、布林、字串、數字或布林陣列、匯出的結構）/ The type {type.String()} of the {subject.Split(" / ")[1]} cannot be exported (use numbers, bool, string, arrays of numbers or bool, or exported structs).", type.Token);
    }

    /// <summary>Public names must stay distinct in every binding language's naming (C snake_case, lowerCamelCase) and
    /// apart from the names every library defines.</summary>
    private void CheckExportCollisions(List<(string Name, ExportMark Mark)> names) {
        var seen = new Dictionary<string, string>(System.StringComparer.Ordinal);
        static string[] Forms(string name) => [ExportTable.SnakeCase(name), "camel:" + ExportTable.LowerCamelCase(name)];
        foreach (string reserved in ExportTable.ReservedNames) foreach (string form in Forms(reserved)) seen[form] = "";
        foreach (var (name, mark) in names) {
            foreach (string form in Forms(name)) {
                if (seen.TryGetValue(form, out var other)) {
                    Error(other == "" ? $"匯出名稱 {name} 是保留名稱 / The export name {name} is reserved (every library defines it)."
                        : other == name ? $"匯出名稱 {name} 重複 / The export name {name} is used twice."
                        : $"匯出名稱 {name} 與 {other} 在繫結中相同 / The export name {name} becomes the same as {other} in the bindings.", mark.Keyword);
                    break;
                }
                seen[form] = name;
            }
        }
    }
}
