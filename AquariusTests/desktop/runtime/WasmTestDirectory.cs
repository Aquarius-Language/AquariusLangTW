namespace AquariusLang.Desktop.runtime;

internal sealed class ApplicationTestDirectory : IDisposable {
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aquarius-wasm-" + Guid.NewGuid().ToString("N"));
    internal ApplicationTestDirectory() { Directory.CreateDirectory(Path); }
    internal string Write(string relativePath, string text) {
        string file = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }
    internal string FilePath(string relativePath) => System.IO.Path.Combine(Path, relativePath);
    public void Dispose() { Directory.Delete(Path, true); }
}
