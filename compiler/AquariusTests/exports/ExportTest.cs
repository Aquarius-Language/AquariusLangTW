using AquariusLang;
using AquariusLang.ast;
using AquariusLang.Bottle;
using AquariusLang.Build.Bindings;
using AquariusLang.Build.Libraries;
using AquariusLang.Compiler;
using AquariusLang.lexer;
using AquariusLang.Packaging;
using AquariusLang.parser;
using AquariusLang.Wasm;

namespace AquariusTests.Exports;

/// <summary>Native library outputs (docs/native-libraries.md), the compiler's part: the 匯出 / export keyword, the
/// TypeChecker's export rules (one test each), the export table and its custom section, the binding goldens and the
/// [export] manifest table. scripts/test-exports.ps1 builds and runs the libraries themselves.</summary>
public class ExportTest
{
    private static readonly string Repository = AquariusInstallation.Require().Root;
    private static readonly string Geometry = Path.Combine(Repository, "tests", "exports", "geometry", "main.aqua");

    private static AbstractSyntaxTree Parse(string source)
    {
        var parser = Parser.NewInstance(Lexer.NewInstance(source));
        var tree = parser.ParseAST();
        Assert.Empty(parser.Errors);
        return tree;
    }
    private static IReadOnlyList<string> Errors(string source) => TypeChecker.Check(Parse(source)).Errors.Select(e => e.Message).ToArray();

    // ---- the keyword ----

    [Fact]
    public void ExportMarksVariableConstantAndStructDeclarations()
    {
        var tree = Parse("匯出 變數 面積, area = 函式(寬: 雙精度): 雙精度 { 寬; };\nexport(\"dist\") const d = function(): int { 1; };\n匯出 結構 點, Point { x: 雙精度 }");
        var area = Assert.IsType<LetStatement>(tree.Statements[0]);
        Assert.NotNull(area.Export); Assert.Null(area.Export!.Name); Assert.True(area.Constant);
        Assert.Equal("dist", Assert.IsType<LetStatement>(tree.Statements[1]).Export!.Name);
        var point = Assert.IsType<StructStatement>(tree.Statements[2]);
        Assert.Equal("Point", point.Alias!.Value);
        Assert.NotNull(point.Export);
    }

    [Theory]
    [InlineData("var export = 1; export + 1;")]
    [InlineData("var 匯出 = function(x) { x; }; 匯出(\"a\"); 匯出(2);")]
    [InlineData("var export = function(x) { x; }; export(\"a\")\nvar y = 2;")]
    public void ExportIsAKeywordOnlyBeforeADeclaration(string source)
    {
        // The last case reads as export("a") var y: the declaration form, which a program calling a function named
        // export and declaring a variable on the next line without a semicolon now means.
        var tree = Parse(source);
        Assert.NotEmpty(tree.Statements);
    }

    [Fact]
    public void StructsMayHaveASecondName()
    {
        Assert.Empty(Errors("結構 點, Point { x: 雙精度 }\n變數 p: Point = 點(1.0);\n變數 q: 點 = Point(2.0);\np.x + q.x;"));
    }

    // ---- the TypeChecker's rules ----

    [Theory]
    [InlineData("var f = function() { export var g = function() {}; };", "top level")]
    [InlineData("if (true) { export var g = function() {}; }", "top level")]
    [InlineData("export var n = 5;", "Only functions")]
    [InlineData("export var f = async function() {};", "Async functions")]
    [InlineData("export var f = function(x) {};", "needs a type annotation")]
    [InlineData("export var f = function(x: string[]) {};", "cannot be exported")]
    [InlineData("export var f = function(x: array) {};", "cannot be exported")]
    [InlineData("export var f = function(): string[] { []; };", "cannot be exported")]
    [InlineData("struct P { x: int }\nexport var f = function(p: P) {};", "must be exported too")]
    [InlineData("export struct P { 甲: int }", "ASCII names")]
    [InlineData("export struct P { x }", "needs an int, long")]
    [InlineData("export struct P { x: int[] }", "needs an int, long")]
    [InlineData("匯出 變數 面積 = 函式() {};", "ASCII public name")]
    [InlineData("export(\"not valid\") var f = function() {};", "must be an ASCII identifier")]
    [InlineData("export var fooBar = function() {};\nexport var foo_bar = function() {};", "becomes the same")]
    [InlineData("export struct Point { x: int }\nexport var point = function() {};", "becomes the same")]
    [InlineData("export(\"f\") var a = function() {};\nexport(\"f\") var b = function() {};", "used twice")]
    [InlineData("export var free = function() {};", "reserved")]
    [InlineData("export var f = function() {};\nf = 1;", "constant")]
    public void EachExportRuleReportsItsError(string source, string message)
    {
        Assert.Contains(Errors(source), e => e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void TheGeometryTestLibraryPassesEveryRule()
    {
        Assert.Empty(Errors(File.ReadAllText(Geometry)));
    }

    // ---- the export table ----

    private static (WasmProgram Program, ExportTable Table) CompileGeometry()
    {
        string output = Path.Combine(Path.GetTempPath(), "aquarius-export-test-" + Guid.NewGuid().ToString("N") + ".wasm");
        try {
            WasmApplication.Compile([Geometry], output, Path.GetDirectoryName(Geometry));
            var program = WasmApplication.Load(output).Program;
            return (program, ExportTable.Read(program)!);
        } finally { File.Delete(output); }
    }

    [Fact]
    public void TheEntryModulesExportsAreWrittenAndReadBack()
    {
        var (_, table) = CompileGeometry();
        Assert.Equal("main.aqua", table.Module);
        Assert.Equal(["Point", "Person"], table.Structs.Select(s => s.Name));
        Assert.Equal("點", table.Structs[0].Binding);
        var area = table.Functions.Single(f => f.Name == "area");
        Assert.Equal("面積", area.Binding);
        Assert.Equal("Area of a rectangle.", area.Documentation);
        Assert.Equal(new ExportType(ExportShape.Scalar, ScalarType.Double), area.Result);
        var distance = table.Functions.Single(f => f.Name == "distance");
        Assert.Equal("距離", distance.Binding);
        Assert.Equal(new ExportType(ExportShape.Struct, Struct: 0), distance.Parameters[0].Type);
        Assert.Equal("The point halfway between two points.\nBoth are copied in and the result is copied out.", table.Functions.Single(f => f.Name == "midpoint").Documentation);
        Assert.Equal(ExportType.Void, table.Functions.Single(f => f.Name == "log").Result);
        Assert.Equal(new ExportType(ExportShape.Array, ScalarType.Long), table.Functions.Single(f => f.Name == "squares").Result);
        // The encoding round-trips exactly.
        Assert.Equal(table.Encode(), ExportTable.Decode(table.Encode()).Encode());
    }

    [Fact]
    public void ProgramsWithoutExportsHaveNoSectionAndBadSectionsAreRejected()
    {
        var program = new WasmCompiler().Compile("var f = function(x: int): int { x + 1; }; f(1);");
        Assert.Null(program.CustomSection(ExportTable.SectionName));
        var (_, table) = CompileGeometry();
        byte[] payload = table.Encode();
        payload[4] = 99;
        Assert.Contains("version 99", Assert.Throws<InvalidDataException>(() => ExportTable.Decode(payload)).Message);
        Assert.Throws<InvalidDataException>(() => ExportTable.Decode(table.Encode()[..^1]));
    }

    [Fact]
    public void ExportsOfAnImportedModuleAreOrdinaryDeclarations()
    {
        // parity.aqua imports the exporting module; only an entry module's exports form the table.
        string output = Path.Combine(Path.GetTempPath(), "aquarius-export-test-" + Guid.NewGuid().ToString("N") + ".wasm");
        try {
            string folder = Path.GetDirectoryName(Geometry)!;
            WasmApplication.Compile([Path.Combine(folder, "parity.aqua"), Geometry], output, folder, entryPoint: "parity.aqua");
            Assert.Null(ExportTable.Read(WasmApplication.Load(output).Program));
        } finally { File.Delete(output); }
    }

    [Theory]
    [InlineData("distanceTo", "distance_to", "distanceTo")]
    [InlineData("HTTPServer", "http_server", "httpServer")]
    [InlineData("area", "area", "area")]
    [InlineData("Point", "point", "point")]
    public void NamesConvertForEachBindingLanguage(string name, string snake, string camel)
    {
        Assert.Equal(snake, ExportTable.SnakeCase(name));
        Assert.Equal(camel, ExportTable.LowerCamelCase(name));
    }

    // ---- binding goldens ----

    /// <summary>Every generator's files for the geometry library, plus the embed runtime's table, compared with
    /// exports/goldens/ (the aqua version is written as &lt;version&gt;). AQUARIUS_UPDATE_BINDING_GOLDENS=1 rewrites them.</summary>
    public static IEnumerable<(string Path, string Text)> GeneratedFiles()
    {
        var (_, table) = CompileGeometry();
        var context = new BindingContext(table, new NativeLibrarySettings("geometry", "geo_"));
        foreach (var generator in BindingGenerators.All)
            foreach (var file in generator.Generate(context)) yield return ($"{generator.Language}/{file.Path}", file.Text);
        yield return ("embed/exports.def", EmbedTable.Definitions(context));
        yield return ("embed/symbols.txt", string.Join("\n", EmbedTable.Symbols(context)) + "\n");
    }
    public static string GoldenFolder => Path.Combine(Repository, "compiler", "AquariusTests", "exports", "goldens");

    [Fact]
    public void BindingsMatchTheirGoldens()
    {
        bool update = Environment.GetEnvironmentVariable("AQUARIUS_UPDATE_BINDING_GOLDENS") == "1";
        foreach (var (path, text) in GeneratedFiles()) {
            string normalized = text.Replace(AquariusVersion.Current, "<version>").ReplaceLineEndings("\n");
            string golden = Path.Combine(GoldenFolder, path);
            if (update) { Directory.CreateDirectory(Path.GetDirectoryName(golden)!); File.WriteAllText(golden, normalized); continue; }
            Assert.True(File.Exists(golden), $"No golden for {path}: run with AQUARIUS_UPDATE_BINDING_GOLDENS=1 and review it.");
            Assert.Equal(File.ReadAllText(golden).ReplaceLineEndings("\n"), normalized);
        }
    }

    [Fact]
    public void TheHeaderAbiVersionIsTheEmbedRuntimes()
    {
        string embed = File.ReadAllText(Path.Combine(Repository, "runtime", "embed", "aqua", "embed.h"));
        Assert.Contains($"#define AQUA_EMBED_ABI_VERSION {CBinding.AbiVersion}", embed);
    }

    // ---- [export] in aqua.toml ----

    private const string Project = "manifest_version = 2\n[project]\nname = \"Geo-Lib\"\nversion = \"3.1.0\"\n";
    private static ProjectManifest Manifest(string text) => ProjectManifest.Parse(Path.GetFullPath("aqua.toml"), text);
    private static ManifestException Invalid(string text) => Assert.Throws<ManifestException>(() => Manifest(text));

    [Fact]
    public void ExportTablesParseWithDefaultsAndPlatformOverrides()
    {
        var plain = Manifest(Project + "[export]\n").Export!;
        Assert.Equal("geo_lib", plain.Name);
        Assert.Equal("geo_lib_", plain.SymbolPrefix);
        Assert.Equal(["shared"], plain.KindsFor("windows"));
        Assert.Equal(3, plain.Major);
        var full = Manifest(Project + "[export]\nname = \"geometry\"\nprefix = \"geo_\"\nkinds = [\"shared\", \"static\"]\n" +
            "[export.windows]\nimport_libraries = [\"mingw\"]\nbindings = [\"c\"]\n[export.linux]\nkinds = [\"shared\"]\n").Export!;
        Assert.Equal("geo_", full.SymbolPrefix);
        Assert.Equal(["shared", "static"], full.KindsFor("windows"));
        Assert.Equal(["shared"], full.KindsFor("linux"));
        Assert.Equal(["mingw"], full.For("windows").ImportLibraries);
        Assert.Null(Manifest(Project).Export);
    }

    [Theory]
    [InlineData("[export]\nprefixx = \"a_\"\n", "Unknown [export] key 'prefixx'", "prefixx")]
    [InlineData("[export]\nname = \"9lives\"\n", "'name' must start with a letter", "name")]
    [InlineData("[export]\nprefix = \"geo-\"\n", "C identifier prefix", "prefix")]
    [InlineData("[export]\nkinds = [\"dynamic\"]\n", "'dynamic' is not one of them", "kinds")]
    [InlineData("[export.web]\nkinds = [\"static\"]\n", "'static' is not one of them", "kinds")]
    [InlineData("[export.freebsd]\nkinds = [\"shared\"]\n", "Unknown [export] platform 'freebsd'", "export.freebsd")]
    [InlineData("[export.linux]\nimport_libraries = [\"mingw\"]\n", "Unknown [export.linux] key 'import_libraries'", "import_libraries")]
    [InlineData("[export.android]\nbindings = [\"swift\"]\n", "'swift' is not one of them", "bindings")]
    [InlineData("[export.windows]\ntargets = [\"linux-x64\"]\n", "'linux-x64' is not one of them", "targets")]
    [InlineData("[export.android]\njava_package = \"org.example.\"\n", "Java package", "java_package")]
    [InlineData("[export]\nmodule = \"Geometry\"\n", "applies to a [library]", "module")]
    public void ExportTableErrorsNameTheirKey(string table, string message, string key)
    {
        string text = Project + table;
        var error = Invalid(text);
        Assert.Contains(message, error.Message);
        var issue = Assert.Single(error.Issues);
        Assert.True(text.IndexOf(key, issue.Offset, StringComparison.Ordinal) == issue.Offset, $"The error points at {issue.Offset}, not at {key}.");
    }

    [Fact]
    public void EveryPlatformDescriptorAllowsOnlyKnownValues()
    {
        foreach (var platform in NativeLibraryPlatforms.All) {
            Assert.All(platform.Kinds, k => Assert.Contains(k, NativeLibraryPlatforms.KindNames));
            Assert.All(platform.Bindings, b => Assert.Contains(b, NativeLibraryPlatforms.BindingNames));
            Assert.All(platform.DefaultBindings, b => Assert.Contains(b, platform.Bindings));
            Assert.NotEmpty(platform.Packages);
        }
    }
}
