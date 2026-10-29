using System.Text.Json;
using static AquariusTests.LanguageServer.LanguageServerSession;

namespace AquariusTests.LanguageServer;

/// <summary>The language server's checks of async misuse (compiler/AquariusLanguageServer/AsyncChecks.cs) next to the
/// compiler's own rule that 等待 / await belongs in async functions and the top level.</summary>
public class AsyncDiagnosticsTest
{
    private static (string Message, int Severity, int Line)[] Findings(JsonElement diagnostics) =>
        diagnostics.EnumerateArray().Select(d => (d.GetProperty("message").GetString()!, d.GetProperty("severity").GetInt32(),
            d.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32())).ToArray();

    private static LanguageServerSession Launch()
    {
        var session = new LanguageServerSession();
        session.Initialize();
        return session;
    }

    [Fact]
    public void CorrectAsyncCodeHasNoDiagnostics()
    {
        using var s = Launch();
        NoDiagnostics(s.Open(string.Join("\n",
            "var load = async function(n) { var v = await Promise.Delay(1, n); return v * 2; };",
            "var both = async function() { return await Promise.All([load(1), load(2)]); };",
            "變數 讀 = 非同步 函式(n) { 回傳 等待 讀(n); };",
            "load(3).Then(function(v) { print(v); });",
            "var p = load(4);",
            "print(await p, p.State);",
            "var relay = function() { load(5); };",
            "var 等待 = function(x) { x; }; 等待(1);",
            "var mapped = await Promise.All(import(\"Array\").Map([1, 2], load));")));
    }

    [Fact]
    public void AwaitInASynchronousFunctionIsAnError()
    {
        using var s = Launch();
        var findings = Findings(s.Open("var f = function() {\n  等待 Promise.Delay(1);\n};"));
        Assert.Contains(findings, f => f.Severity == 1 && f.Line == 1 && f.Message.Contains("await is only valid", StringComparison.Ordinal));
    }

    [Fact]
    public void PromisesNobodyAwaitsOrUsedAsValuesAreWarnings()
    {
        using var s = Launch();
        var findings = Findings(s.Open(string.Join("\n",
            "var load = async function(n) { return n; };",
            "load(1);",
            "var total = load(2) + 1;",
            "if (load(3)) { print(1); }",
            "Promise.Delay(10);",
            "var net = import(\"Network\"); net.GetAsync(\"http://example.com\");",
            "var fine = async function() { load(4); return 1; };",
            "print(load(5).Body);")));
        Assert.Contains(findings, f => f.Line == 1 && f.Severity == 2 && f.Message.Contains("neither awaited nor handled", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Line == 2 && f.Severity == 2 && f.Message.Contains("gives a promise, not its value", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Line == 3 && f.Severity == 2 && f.Message.Contains("gives a promise", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Line == 4 && f.Message.Contains("Promise.Delay", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Line == 5 && f.Message.Contains("net.GetAsync", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Line == 6 && f.Message.Contains("neither awaited", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Line == 7 && f.Message.Contains("gives a promise", StringComparison.Ordinal));
    }

    [Fact]
    public void AwaitingNonPromisesAndAsyncCallbacksOfSynchronousCallersAreReported()
    {
        using var s = Launch();
        var findings = Findings(s.Open(string.Join("\n",
            "var plain = function(n) { return n + 1; };",
            "var relay = function(n) { return Promise.Delay(n); };",
            "print(await plain(1), await 42, await relay(1));",
            "var keep = async function(x) { return x > 1; };",
            "var kept = import(\"Array\").Filter([1, 2], keep);",
            "var Array = import(\"Array\"); Array.Sort([2, 1], async function(a, b) { return a - b; });")));
        Assert.Equal(2, findings.Count(f => f.Line == 2 && f.Severity == 3 && f.Message.Contains("await has no effect", StringComparison.Ordinal)));
        Assert.Contains(findings, f => f.Line == 5 && f.Severity == 2 && f.Message.Contains("Array.Sort calls its function synchronously", StringComparison.Ordinal));
    }

    [Fact]
    public void KeywordsHaveHoverAndAsyncSignatures()
    {
        using var s = Launch();
        NoDiagnostics(s.Open("var load = async function(n) { return await Promise.Delay(1, n); };\nload(1).Then(print);"));
        string hover = s.Feature("hover", 0, 12).ToString();
        Assert.Contains("async function", hover, StringComparison.Ordinal);
        string awaitHover = s.Feature("hover", 0, 40).ToString();
        Assert.Contains("Suspend the current async function", awaitHover, StringComparison.Ordinal);
    }
}
