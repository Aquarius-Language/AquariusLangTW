using System;
using System.Collections.Generic;
using System.Linq;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.Terminal;

public sealed record TerminalRequest(string Executable, string[] Arguments, string? WorkingDirectory = null, int TimeoutMs = 60000);
public sealed record TerminalResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);
/// <summary>Host capability for explicitly requested child processes. Arguments are never shell-expanded.</summary>
public interface ITerminalBackend {
    string Platform { get; }
    bool IsSupported { get; }
    TerminalResult Run(TerminalRequest request);
}
public static class TerminalCatalog {
    public static IReadOnlyList<LibraryFunction> Functions { get; } = Array.AsReadOnly(new[] {
        new LibraryFunction("Terminal", "IsSupported", "是否支援", 0, 0),
        new("Terminal", "Run", "執行", 2, 3, "Terminal.Result"),
        new("Terminal", "RunScript", "執行腳本", 1, 2, "Terminal.Result")
    });
    public static IReadOnlyList<LibraryProperty> Properties { get; } = Array.AsReadOnly(new[] {
        new LibraryProperty("Terminal", "Platform", "平台", "string"),
        new("Terminal.Result", "ExitCode", "結束代碼", "int"), new("Terminal.Result", "StandardOutput", "標準輸出", "string"),
        new("Terminal.Result", "StandardError", "標準錯誤", "string"), new("Terminal.Result", "TimedOut", "已逾時", "bool")
    });
}
public sealed class TerminalRuntime {
    public const int MaxOutputCharacters = 1024 * 1024;
    private readonly ModuleObj module = new(AquaEnvironment.NewEnvironment());
    public TerminalRuntime(ITerminalBackend backend, Func<string, string>? resolvePath = null) {
        Field(module, "Platform", "平台", new StringObj(backend.Platform));
        foreach (var spec in TerminalCatalog.Functions) {
            var fn = new BuiltinObj(args => {
                try {
                    if (args.Length < spec.MinimumArguments || args.Length > spec.MaximumArguments) throw new ArgumentException("Wrong argument count.");
                    if (spec.EnglishName == "IsSupported") return new BooleanObj(backend.IsSupported);
                    if (!backend.IsSupported) throw new NotSupportedException("Terminal processes are unavailable on this platform.");
                    string executable; string[] arguments; int optionIndex;
                    if (spec.EnglishName == "RunScript") {
                        string script = Text(args[0]); optionIndex = 1;
                        executable = backend.Platform == "windows" ? "powershell.exe" : "/bin/sh";
                        arguments = backend.Platform == "windows" ? ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);$OutputEncoding=[Console]::OutputEncoding;" + script] : ["-c", script];
                        if (backend.Platform == "android") executable = "/system/bin/sh";
                    } else {
                        executable = Text(args[0]); optionIndex = 2;
                        arguments = args[1] is ArrayObj array ? array.Elements.Select(Text).ToArray() : throw new ArgumentException("Arguments must be a string array.");
                        if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("Executable is required.");
                        if (executable.Contains('/') || executable.Contains('\\')) executable = (resolvePath ?? (p => p))(executable);
                    }
                    string? cwd = null; int timeout = 60000;
                    if (args.Length > optionIndex) {
                        if (args[optionIndex] is not HashObj options) throw new ArgumentException("Options must be a map.");
                        foreach (var pair in options.Pairs.Values) {
                            switch (Text(pair.Key)) {
                                case "WorkingDirectory": cwd = (resolvePath ?? (p => p))(Text(pair.Value)); break;
                                case "TimeoutMs":
                                    if (pair.Value is not INumberObj n || n.GetNumValue() < 1 || n.GetNumValue() > 3600000 || n.GetNumValue() != Math.Truncate(n.GetNumValue())) throw new ArgumentException("TimeoutMs must be an integer in 1..3600000.");
                                    timeout = (int)n.GetNumValue(); break;
                                default: throw new ArgumentException("Unknown terminal option.");
                            }
                        }
                    }
                    var result = backend.Run(new(executable, arguments, cwd, timeout));
                    var record = new ModuleObj(AquaEnvironment.NewEnvironment());
                    Field(record, "ExitCode", "結束代碼", new IntegerObj(result.ExitCode));
                    Field(record, "StandardOutput", "標準輸出", new StringObj(result.StandardOutput));
                    Field(record, "StandardError", "標準錯誤", new StringObj(result.StandardError));
                    Field(record, "TimedOut", "已逾時", new BooleanObj(result.TimedOut));
                    return record;
                } catch (Exception e) { return new ErrorObj($"Terminal / 終端機.{spec.EnglishName}: {e.Message}"); }
            }) { RetainedEnvironment = module._Environment };
            FunctionRegistration.Define(module._Environment, fn, spec.TraditionalChineseName, spec.EnglishName);
        }
    }
    private static string Text(IObject value) => value is StringObj s && !s.Value.Contains('\0') ? s.Value : throw new ArgumentException("Expected a string without NUL characters.");
    private static void Field(ModuleObj m, string english, string chinese, IObject value) { m._Environment.Create(english, value); m._Environment.Create(chinese, value); }
    public bool TryImport(string name, out ModuleObj result) { result = module; return name is "Terminal" or "終端機"; }
}
