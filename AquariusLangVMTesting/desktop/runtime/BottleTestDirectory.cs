namespace AquariusREPL.runtime;

internal sealed class BottleTestDirectory : IDisposable {
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aquarius-bottle-" + Guid.NewGuid().ToString("N"));
    internal BottleTestDirectory() { Directory.CreateDirectory(Path); }
    internal string Write(string relativePath, string text) {
        string file = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }
    internal string FilePath(string relativePath) => System.IO.Path.Combine(Path, relativePath);
    public void Dispose() { Directory.Delete(Path, true); }
}
