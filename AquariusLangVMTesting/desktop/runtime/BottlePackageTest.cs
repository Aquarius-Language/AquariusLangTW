using System.IO.Compression;
using System.Text.Json;
using AquariusLang.Object;
using AquariusLang.VM;

namespace AquariusREPL.runtime;

public class BottlePackageTest {
    [Fact]
    public void MultipleInputsPreserveRootAndNestedPathsWithFirstInputAsEntryPoint() {
        using var temp = new BottleTestDirectory();
        var files = new[] { temp.Write("test1.aqua", "42;"), temp.Write("test/test2.aqua", "2;"), temp.Write("test/test3.aqua", "3;") };
        string bottle = temp.FilePath("test1.bottle");
        BottlePackage.Compile(files, bottle, temp.Path);
        using var zip = ZipFile.OpenRead(bottle);
        Assert.Equal(new[] { "bottle.json", "test1.rius", "test/test2.rius", "test/test3.rius" }, zip.Entries.Select(entry => entry.FullName));
        var package = BottlePackage.Load(bottle);
        Assert.Equal("test1.rius", package.EntryPoint);
        Assert.Equal(3, package.Scripts.Count);
        foreach (var script in package.Scripts.Values) Assert.IsType<IntegerObj>(new VirtualMachine().Execute(script));
    }

    [Fact]
    public void SameBasenameInDifferentFoldersAndUnicodePathsRemainDistinct() {
        using var temp = new BottleTestDirectory();
        var files = new[] { temp.Write("main.aqua", "0;"), temp.Write("甲/test.aqua", "1;"), temp.Write("乙/test.aqua", "2;") };
        string bottle = temp.FilePath("星泉.bottle");
        BottlePackage.Compile(files, bottle, temp.Path);
        var package = BottlePackage.Load(bottle);
        Assert.Equal(1, Assert.IsType<IntegerObj>(new VirtualMachine().Execute(package.Scripts["甲/test.rius"])).Value);
        Assert.Equal(2, Assert.IsType<IntegerObj>(new VirtualMachine().Execute(package.Scripts["乙/test.rius"])).Value);
    }

    [Theory]
    [InlineData("變數 = ;")]
    [InlineData("\"unterminated")]
    public void CompilationFailureKeepsExistingOutputAndLeavesNoTemporaryFiles(string invalid) {
        using var temp = new BottleTestDirectory();
        string good = temp.Write("good.aqua", "42;"), bad = temp.Write("bad.aqua", invalid);
        string output = temp.Write("output.bottle", "existing-output");
        var error = Assert.Throws<VmCompilationException>(() => BottlePackage.Compile(new[] { good, bad }, output, temp.Path));
        Assert.Contains("bad.aqua", error.Message);
        Assert.Equal("existing-output", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void MissingSourceAndDuplicateInputsDoNotCreatePackages() {
        using var temp = new BottleTestDirectory();
        string source = temp.Write("main.aqua", "42;"), output = temp.FilePath("main.bottle");
        Assert.Throws<FileNotFoundException>(() => BottlePackage.Compile(new[] { source, temp.FilePath("missing.aqua") }, output, temp.Path));
        Assert.Throws<ArgumentException>(() => BottlePackage.Compile(new[] { source, source }, output, temp.Path));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void EmptyInputWrongExtensionsAndPathsOutsideRootAreRejected() {
        using var temp = new BottleTestDirectory();
        string source = temp.Write("main.aqua", "42;"), output = temp.FilePath("main.bottle");
        Assert.Throws<ArgumentException>(() => BottlePackage.Compile(Array.Empty<string>(), output, temp.Path));
        Assert.Throws<ArgumentException>(() => BottlePackage.Compile(new[] { temp.Write("main.txt", "42;") }, output, temp.Path));
        Assert.Throws<ArgumentException>(() => BottlePackage.Compile(new[] { source }, temp.FilePath("main.zip"), temp.Path));
        Assert.Throws<InvalidDataException>(() => BottlePackage.Compile(new[] { source }, output, temp.FilePath("nested")));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void SuccessfulRecompileAtomicallyReplacesOutput() {
        using var temp = new BottleTestDirectory();
        string source = temp.Write("main.aqua", "1;"), output = temp.FilePath("main.bottle");
        BottlePackage.Compile(new[] { source }, output, temp.Path);
        File.WriteAllText(source, "42;");
        BottlePackage.Compile(new[] { source }, output, temp.Path);
        Assert.Equal(42, Assert.IsType<IntegerObj>(new VirtualMachine().Execute(BottlePackage.Load(output).Scripts["main.rius"])).Value);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("../escape.rius")]
    [InlineData("/absolute.rius")]
    [InlineData("C:/absolute.rius")]
    [InlineData("test\\main.rius")]
    [InlineData("test//main.rius")]
    [InlineData("test/./main.rius")]
    [InlineData("test/../main.rius")]
    [InlineData("main.aqua")]
    public void UnsafeOrNonBytecodeArchiveEntriesAreRejected(string path) {
        using var temp = new BottleTestDirectory();
        string output = temp.FilePath("bad.bottle");
        using (var zip = ZipFile.Open(output, ZipArchiveMode.Create)) zip.CreateEntry(path);
        Assert.Throws<InvalidDataException>(() => BottlePackage.Load(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{invalid json")]
    [InlineData("{\"format\":\"aquarius-bottle\",\"version\":2,\"entryPoint\":\"main.rius\"}")]
    [InlineData("{\"format\":\"aquarius-bottle\",\"version\":1,\"entryPoint\":\"missing.rius\"}")]
    [InlineData("{\"format\":\"aquarius-bottle\",\"version\":1,\"entryPoint\":\"../main.rius\"}")]
    public void InvalidMissingOrUnsupportedManifestIsRejected(string text) {
        using var temp = new BottleTestDirectory();
        string source = temp.Write("main.aqua", "42;"), output = temp.FilePath("bad.bottle");
        BottlePackage.Compile(new[] { source }, output, temp.Path);
        using (var zip = ZipFile.Open(output, ZipArchiveMode.Update)) {
            zip.GetEntry(BottlePackage.ManifestPath)!.Delete();
            if (text.Length > 0) { using var writer = new StreamWriter(zip.CreateEntry(BottlePackage.ManifestPath).Open()); writer.Write(text); }
        }
        Assert.Throws<InvalidDataException>(() => BottlePackage.Load(output));
    }

    [Fact]
    public void DuplicateArchivePathsAndInvalidBytecodeAreRejected() {
        using var temp = new BottleTestDirectory();
        string source = temp.Write("main.aqua", "42;"), output = temp.FilePath("bad.bottle");
        BottlePackage.Compile(new[] { source }, output, temp.Path);
        using (var zip = ZipFile.Open(output, ZipArchiveMode.Update)) {
            using var stream = zip.CreateEntry("MAIN.rius").Open();
            BytecodeSerializer.Write(new VmCompiler().Compile("1;"), stream);
        }
        Assert.Throws<InvalidDataException>(() => BottlePackage.Load(output));
        BottlePackage.Compile(new[] { source }, output, temp.Path);
        using (var zip = ZipFile.Open(output, ZipArchiveMode.Update)) {
            zip.GetEntry("main.rius")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("main.rius").Open()); writer.Write("not bytecode");
        }
        Assert.Throws<InvalidDataException>(() => BottlePackage.Load(output));
    }
}
