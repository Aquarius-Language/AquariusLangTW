using System.Diagnostics;
using System.Text;
using AquariusLang.Build;
using AquariusLang.Compiler;
using AquariusLang.lexer;
using AquariusLang.Packaging;
using AquariusLang.parser;

namespace AquariusLang.Cli;

/// <summary>
/// <c>aqua repl</c>. The runtime runs whole applications and has no incremental mode, so the session is replayed: each
/// input is compiled after every accepted earlier input and the program runs again; only what the new input prints
/// (and its value) is shown. Earlier inputs therefore run again each time, including their side effects (files they
/// write, output they print before the new input, time they take). An input that fails to compile or stops with an
/// error is shown and left out of the session.
/// </summary>
public static class Repl
{
    public const string Prompt = ">> ", Continuation = ".. ";

    public static int Run(TextReader input, TextWriter output)
    {
        output.WriteLine("AquariusLang REPL. Each input reruns the session's earlier inputs (their side effects repeat); end with Ctrl+Z (Windows) or Ctrl+D.");
        var session = new List<string>();
        string directory = Path.Combine(Path.GetTempPath(), "aquarius-repl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            while (true) {
                output.Write(Prompt);
                string? line = input.ReadLine();
                if (line == null) { output.WriteLine(); return 0; }
                var text = new StringBuilder(line);
                // Unbalanced braces continue the input on the next line.
                while (Depth(text.ToString()) > 0) {
                    output.Write(Continuation);
                    string? more = input.ReadLine();
                    if (more == null) break;
                    text.Append('\n').Append(more);
                }
                string entry = text.ToString();
                if (string.IsNullOrWhiteSpace(entry)) continue;
                var lexer = Lexer.NewInstance(entry);
                var parser = Parser.NewInstance(lexer);
                parser.ParseAST();
                if (lexer.Errors.Count > 0 || parser.Errors.Count > 0) {
                    foreach (string error in lexer.Errors.Select(e => e.Message).Concat(parser.Errors)) output.WriteLine(error);
                    continue;
                }
                if (Evaluate(session, entry, directory, output)) session.Add(entry);
            }
        } finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    /// <summary>Runs the session plus one input; true when the input completed without an error.</summary>
    public static bool Evaluate(IReadOnlyList<string> session, string entry, string directory, TextWriter output)
    {
        string marker = "@@aquarius-repl-" + Guid.NewGuid().ToString("N") + "@@";
        string source = string.Concat(session.Select(s => s + "\n")) + $"印出(\"{marker}\");\n" + entry + "\n";
        string script = Path.Combine(directory, "repl.aqua"), wasm = Path.Combine(directory, "repl.wasm");
        File.WriteAllText(script, source, new UTF8Encoding(false));
        try { WasmApplication.Compile([script], wasm, directory); }
        catch (CompilationException e) { output.WriteLine(e.Message.Replace(script + ": ", "")); return false; }
        var start = NativeRuntime.StartInfo(wasm);
        start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8; start.StandardErrorEncoding = Encoding.UTF8;
        using var process = Process.Start(start) ?? throw new IOException("Cannot start the AquariusLang runtime.");
        var errors = process.StandardError.ReadToEndAsync();
        bool shown = false;
        for (string? line; (line = process.StandardOutput.ReadLine()) != null;) {
            if (shown) output.WriteLine(line);
            else if (line == marker) shown = true;
        }
        process.WaitForExit();
        string error = errors.GetAwaiter().GetResult();
        if (error.Length > 0) output.Write(error);
        return process.ExitCode == 0;
    }

    private static int Depth(string text)
    {
        int depth = 0; bool quoted = false;
        for (int i = 0; i < text.Length; i++) {
            char c = text[i];
            if (quoted) { if (c == '\\') i++; else if (c == '"') quoted = false; }
            else if (c == '"') quoted = true;
            else if (c == '#') { while (i < text.Length && text[i] != '\n') i++; }
            else if (c == '{') depth++;
            else if (c == '}') depth--;
        }
        return depth;
    }
}
