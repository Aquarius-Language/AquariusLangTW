namespace AquariusLang.Web;
public static class Program {
    public static int Main(string[] args) {
        try {
            if (args.Length < 2 || args.Length > 3) {
                Console.Error.WriteLine("Usage: AquariusWebCompiler <package.bottle|source-directory-or-file> <output-directory> [entry.aqua|entry.rius]");
                return 2;
            }
            WebsiteCompiler.Build(args[0], args[1], args.Length == 3 ? args[2] : null);
            Console.WriteLine($"Website built: {Path.GetFullPath(args[1])}");
            return 0;
        } catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or NotSupportedException or AquariusLang.VM.VmCompilationException) {
            Console.Error.WriteLine(e.Message); return 1;
        }
    }
}
