using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Compiler;
using Xunit;

namespace AquariusLang.Desktop.Graphics;

public class BilingualLibraryTest {
    public static IEnumerable<object[]> NativeFunctions => LibraryCatalog.Functions.Where(f => !f.Library.Contains('.'))
        .Select(f => new object[] { f.Library, f.EnglishName, f.TraditionalChineseName, f.MaximumArguments });

    [Theory, MemberData(nameof(NativeFunctions))]
    public void EveryNativeFunctionResolvesBothNamesAndSharesValidation(string library, string english, string chinese, int max) {
        using var runtime = new GraphicsRuntime(); Assert.True(runtime.TryImport(library, out var module));
        var original = Assert.IsType<BuiltinObj>(module._Environment.GetOwned(english));
        Assert.Same(original, module._Environment.GetOwned(chinese)); FunctionRegistration.Names(chinese, english);
        var invalid = Enumerable.Repeat<IObject>(new IntegerObj(0), max + 1).ToArray(); var vm = new CompiledRuntime();
        Assert.IsType<ErrorObj>(vm.Invoke(original, invalid));
        Assert.Equal(vm.Invoke(original, invalid).Inspect(), vm.Invoke(module._Environment.GetOwned(chinese), invalid).Inspect());
    }

    internal static void AssertAliases(ModuleObj module, string library) {
        foreach (var pair in module._Environment.OwnedBindings.Where(p => p.Value is BuiltinObj && p.Key.All(c => c < 128))) {
            var descriptor = LibraryCatalog.Find(library, pair.Key);
            Assert.True(descriptor != null, $"Missing catalog entry: {library}.{pair.Key}");
            Assert.Same(pair.Value, module._Environment.GetOwned(descriptor!.TraditionalChineseName));
        }
        foreach (var descriptor in LibraryCatalog.Members(library)) {
            Assert.IsType<BuiltinObj>(module._Environment.GetOwned(descriptor.EnglishName));
            Assert.Same(module._Environment.GetOwned(descriptor.EnglishName), module._Environment.GetOwned(descriptor.TraditionalChineseName));
        }
    }

    [Fact] public void CatalogCoversAllExportsAndProcessingObjectsWithoutNativeInitialization() {
        using var runtime = new GraphicsRuntime();
        foreach (string name in new[] { "GL", "GLAD", "GLFW", "GLM", "STBImage", "TextInput", "Processing", "WGPU", "Jolt" }) {
            Assert.True(runtime.TryImport(name, out var module)); AssertAliases(module, name);
        }
        using var builtins = new DesktopBuiltins(runtime);
        var objects = Assert.IsType<ArrayObj>(new CompiledRuntime(builtins).Execute(new LoweringCompiler().Compile("""
            變數 p=匯入("Processing");[p.建立向量(3,4),p.建立圖片(2,2),p.建立圖形()];
            """))).Elements;
        AssertAliases(Assert.IsType<ModuleObj>(objects[0]), "Processing.Vector");
        AssertAliases(Assert.IsType<ModuleObj>(objects[1]), "Processing.Image");
        AssertAliases(Assert.IsType<ModuleObj>(objects[2]), "Processing.Shape");
        Assert.Equal(344, LibraryCatalog.Members("GL").Count(f => f.EnglishName.StartsWith("gl", StringComparison.Ordinal)));
    }

    [Fact] public void GlobalFunctionsAndWorkingDirectoryOfferBothNames() {
        using var builtins = new DesktopBuiltins(); builtins.NewDefaultBuiltins("example.aqua");
        foreach (var descriptor in LibraryCatalog.GlobalFunctions)
            Assert.Same(builtins.BuiltinFuncs[descriptor.EnglishName], builtins.BuiltinFuncs[descriptor.TraditionalChineseName]);
        Assert.Same(builtins._Builtins["currWorkingDir"], builtins._Builtins["目前工作目錄"]);
        var result = new CompiledRuntime(builtins).Execute(new LoweringCompiler().Compile("[len(push([1],2)),長度(加入([1],2)),last([3]),最後一個([3]),rest([1,2]),其餘([1,2])];"));
        Assert.Equal("[2, 2, 3, 3, [2], [2]]", result.Inspect());
    }

    [Fact] public void CatalogNamesAreUniqueValidAndReturnTypesExist() {
        foreach (var group in LibraryCatalog.Functions.GroupBy(f => f.Library)) {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var descriptor in group) {
                foreach (string name in FunctionRegistration.Names(descriptor.TraditionalChineseName, descriptor.EnglishName))
                    Assert.True(names.Add(name), $"Duplicate {group.Key}.{name}");
                Assert.True(descriptor.MinimumArguments <= descriptor.MaximumArguments);
                if (descriptor.ReturnLibrary != null) Assert.NotEmpty(LibraryCatalog.Members(descriptor.ReturnLibrary));
            }
        }
    }
}
