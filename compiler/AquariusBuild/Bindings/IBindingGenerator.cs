using AquariusLang.Build.Libraries;
using AquariusLang.Compiler;

namespace AquariusLang.Build.Bindings;

/// <summary>What a binding is written from: the export table, the library's naming and the platform it is built for
/// (whose [export.&lt;platform&gt;] keys apply, such as java_package).</summary>
public sealed record BindingContext(ExportTable Exports, NativeLibrarySettings Settings, string Platform = "");

/// <summary>A generated file, relative to the output folder (<c>include/geometry.h</c>).</summary>
public sealed record BindingFile(string Path, string Text);

/// <summary>
/// One binding language of native library outputs (docs/native-libraries.md): the files that let programs in that
/// language call the exports. One class per language; every generator has a golden in the tests.
/// </summary>
public interface IBindingGenerator
{
    /// <summary>The name <c>[export.&lt;platform&gt;] bindings</c> uses ("c", "cpp", ...).</summary>
    string Language { get; }
    IReadOnlyList<BindingFile> Generate(BindingContext context);
}

public static class BindingGenerators
{
    public static IReadOnlyList<IBindingGenerator> All { get; } = [new CBinding(), new CppBinding(), new RustBinding(), new SwiftBinding(), new JavaBinding(), new TypeScriptBinding()];
    public static IBindingGenerator? Find(string language) => All.FirstOrDefault(g => g.Language == language);
}
