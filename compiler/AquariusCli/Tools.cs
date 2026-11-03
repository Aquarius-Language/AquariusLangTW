using System.Diagnostics;

namespace AquariusLang.Cli;

/// <summary>Commands that start a tool shipped beside aqua (tools/&lt;name&gt; in the repository): <c>aqua well ...</c> runs
/// <c>aqua_wellplay ...</c> with the same arguments, standard streams and exit code. The tool is found beside this
/// executable (the package), else on PATH.</summary>
public static class Tools
{
    private static readonly Dictionary<string, string> Commands = new() { ["well"] = "aqua_wellplay" };

    public static string Usage => string.Join("\n", Commands.Select(c => $"aqua {c.Key} [argument ...]   (runs {c.Value}; aqua {c.Key} --help)"));

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !Commands.TryGetValue(args[0], out string? tool)) return false;
        string? program = Find(tool) ?? throw new IOException(
            $"{tool} was not found beside aqua or on PATH; it ships in the AquariusLang package (build it with scripts/publish-cli).");
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (string argument in args[1..]) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Cannot start {program}.");
        process.WaitForExit();
        exitCode = process.ExitCode;
        return true;
    }

    private static string? Find(string tool)
    {
        string name = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        string beside = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(beside)) return beside;
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
            string candidate = Path.Combine(folder.Trim('"'), name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
