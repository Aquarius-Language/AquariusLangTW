using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AquariusTests.LanguageServer;

/// <summary>
/// One language server process (<c>dotnet AquariusLanguageServer.dll</c> from the test assembly folder) driven over its
/// standard streams with Content-Length framed JSON-RPC, the way editors use it. Every message the server writes is
/// kept until a test takes it.
/// </summary>
internal sealed class LanguageServerSession : IDisposable
{
    public const string DefaultUri = "file:///test/example.aqua";
    public static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Process process;
    private readonly Stream input;
    private readonly List<JsonElement> messages = [];
    private readonly StringBuilder errors = new();
    private string? protocolError;
    private bool closed;
    private int nextId;

    public LanguageServerSession(IReadOnlyDictionary<string, string>? environment = null)
    {
        string server = Path.Combine(AppContext.BaseDirectory, "AquariusLanguageServer.dll");
        Assert.True(File.Exists(server), "The language server is not beside the tests: " + server);
        Assert.True(File.Exists(Path.ChangeExtension(server, ".runtimeconfig.json")), "The language server's runtimeconfig is not beside the tests.");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(server);
        if (Environment.GetEnvironmentVariable("AQUARIUS_HOME") is { Length: > 0 } home) start.Environment["AQUARIUS_HOME"] = home;
        // Editor analysis must never touch the per-user cache unless a test chooses one.
        start.Environment["AQUA_BOTTLE_HOME"] = Path.Combine(Path.GetTempPath(), "aquarius-lsp-cache-" + Guid.NewGuid().ToString("N"));
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
        process = Process.Start(start)!;
        input = process.StandardInput.BaseStream;
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
        process.BeginErrorReadLine();
        new Thread(Read) { IsBackground = true, Name = "language server output" }.Start();
    }

    public string Errors { get { lock (errors) return errors.ToString(); } }

    private void Read()
    {
        var stream = new BufferedStream(process.StandardOutput.BaseStream);
        try {
            while (true) {
                var header = new List<byte>();
                int b;
                while ((b = stream.ReadByte()) >= 0) {
                    header.Add((byte)b);
                    if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n') break;
                }
                if (b < 0) break;
                string text = Encoding.ASCII.GetString(header.ToArray());
                var match = Regex.Match(text, @"Content-Length: (\d+)", RegexOptions.IgnoreCase);
                if (!match.Success) { lock (messages) protocolError = "stdout must contain only LSP messages, got: " + text; break; }
                byte[] body = new byte[int.Parse(match.Groups[1].Value)];
                stream.ReadExactly(body);
                var message = JsonDocument.Parse(body).RootElement.Clone();
                lock (messages) { messages.Add(message); Monitor.PulseAll(messages); }
            }
        } catch (Exception e) when (e is IOException or JsonException or ObjectDisposedException) {
            lock (messages) protocolError ??= "Unreadable server output: " + e.Message;
        } finally {
            lock (messages) { closed = true; Monitor.PulseAll(messages); }
        }
    }

    /// <summary>Removes and returns the first message that matches, waiting for it.</summary>
    public JsonElement Take(Func<JsonElement, bool> predicate, int timeoutMs = 60_000)
    {
        var clock = Stopwatch.StartNew();
        lock (messages) {
            while (true) {
                int index = messages.FindIndex(m => predicate(m));
                if (index >= 0) { var found = messages[index]; messages.RemoveAt(index); return found; }
                if (protocolError != null) throw new InvalidDataException(protocolError);
                if (closed) throw new InvalidOperationException($"The server exited ({(process.HasExited ? process.ExitCode : -1)}): {Errors}");
                long remaining = timeoutMs - clock.ElapsedMilliseconds;
                if (remaining <= 0) throw new TimeoutException("Timed out waiting for the server: " + Errors);
                Monitor.Wait(messages, (int)remaining);
            }
        }
    }

    public void Write(byte[] bytes) { input.Write(bytes); input.Flush(); }
    public void WriteFrame(byte[] body) => Write([.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body]);

    public void Send(string method, object? parameters = null, object? id = null)
    {
        var message = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters != null) message["params"] = parameters;
        if (id != null) message["id"] = id;
        WriteFrame(JsonSerializer.SerializeToUtf8Bytes(message, Json));
    }

    public static bool HasId(JsonElement message, object id) => message.TryGetProperty("id", out var value) && id switch {
        int number => value.ValueKind == JsonValueKind.Number && value.GetInt32() == number,
        string text => value.ValueKind == JsonValueKind.String && value.GetString() == text,
        _ => false,
    };

    public JsonElement Request(string method, object? parameters = null)
    {
        int id = ++nextId;
        Send(method, parameters ?? new { }, id);
        var response = Take(m => HasId(m, id));
        Assert.False(response.TryGetProperty("error", out var error), $"{method} failed: {error}");
        return response.GetProperty("result");
    }

    /// <summary>The error code of the response to a request sent with <see cref="Send"/>.</summary>
    public int ErrorCode(object id) => Take(m => HasId(m, id)).GetProperty("error").GetProperty("code").GetInt32();

    public JsonElement Initialize()
    {
        var result = Request("initialize", new { processId = Environment.ProcessId, rootUri = (string?)null, capabilities = new { } });
        Send("initialized", new { });
        return result;
    }

    public JsonElement Open(string text, string uri = DefaultUri)
    {
        Send("textDocument/didOpen", new { textDocument = new { uri, languageId = "aquarius", version = 1, text } });
        return Diagnostics(1, uri);
    }

    public void Change(string uri, int version, params object[] changes) =>
        Send("textDocument/didChange", new { textDocument = new { uri, version }, contentChanges = changes });

    public JsonElement Diagnostics(int version, string uri = DefaultUri) =>
        Take(m => m.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics" &&
            m.GetProperty("params").GetProperty("uri").GetString() == uri &&
            m.GetProperty("params").TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() == version)
        .GetProperty("params").GetProperty("diagnostics");

    public JsonElement Feature(string method, int line, int character, string uri = DefaultUri) =>
        Request($"textDocument/{method}", new { textDocument = new { uri }, position = new { line, character } });

    public JsonElement Symbols(string uri = DefaultUri) => Request("textDocument/documentSymbol", new { textDocument = new { uri } });

    public int WaitForExit(int timeoutMs = 30_000)
    {
        Assert.True(process.WaitForExit(timeoutMs), "The server did not exit.");
        return process.ExitCode;
    }

    public void Dispose()
    {
        try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(10_000); } } catch (InvalidOperationException) { }
        process.Dispose();
    }

    // Reading results.
    public static string[] Labels(JsonElement items) => items.EnumerateArray().Select(i => i.GetProperty("label").GetString()!).ToArray();
    public static bool HasItem(JsonElement items, string label, int? kind = null) =>
        items.EnumerateArray().Any(i => i.GetProperty("label").GetString() == label && (kind == null || i.GetProperty("kind").GetInt32() == kind));
    public static string HoverText(JsonElement hover) => hover.GetProperty("contents").GetProperty("value").GetString()!;
    public static (int Line, int Character) Start(JsonElement location) => Point(location.GetProperty("range").GetProperty("start"));
    public static (int Line, int Character) End(JsonElement location) => Point(location.GetProperty("range").GetProperty("end"));
    private static (int, int) Point(JsonElement position) => (position.GetProperty("line").GetInt32(), position.GetProperty("character").GetInt32());
    public static string[] Messages(JsonElement diagnostics) => diagnostics.EnumerateArray().Select(d => d.GetProperty("message").GetString()!).ToArray();
    public static void NoDiagnostics(JsonElement diagnostics, string? context = null) =>
        Assert.True(diagnostics.GetArrayLength() == 0, $"{context} unexpected diagnostics: {diagnostics}");
    public static string FileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
}
