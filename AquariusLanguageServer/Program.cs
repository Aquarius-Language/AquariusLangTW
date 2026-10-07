using System.Text.Json;
using AquariusLanguageServer;

return await RunAsync();

static async Task<int> RunAsync()
{
    var protocol = new Protocol(Console.OpenStandardInput(), Console.OpenStandardOutput());
    var documents = new Dictionary<string, Document>(StringComparer.Ordinal);
    bool initialized = false, shutdown = false;
    try
    {
        while (await protocol.ReadAsync() is byte[] body)
        {
            JsonDocument json;
            try { json = JsonDocument.Parse(body); }
            catch (JsonException) { await protocol.ErrorAsync(null, -32700, "Parse error."); continue; }
            using (json)
            {
                var message = json.RootElement;
                if (message.ValueKind != JsonValueKind.Object)
                { await protocol.ErrorAsync(null, -32600, "Invalid JSON-RPC request."); continue; }
                bool request = message.TryGetProperty("id", out var id);
                if (!message.TryGetProperty("jsonrpc", out var rpc) || rpc.ValueKind != JsonValueKind.String || rpc.GetString() != "2.0"
                    || !message.TryGetProperty("method", out var methodValue) || methodValue.ValueKind != JsonValueKind.String
                    || (request && id.ValueKind != JsonValueKind.String && id.ValueKind != JsonValueKind.Number))
                {
                    await protocol.ErrorAsync(request ? id : null, -32600, "Invalid JSON-RPC request.");
                    continue;
                }
                string method = methodValue.GetString()!;
                if (method == "exit") return shutdown ? 0 : 1;
                if (!initialized && method != "initialize")
                { if (request) await protocol.ErrorAsync(id, -32002, "Server not initialized."); continue; }
                if (shutdown)
                { if (request) await protocol.ErrorAsync(id, -32600, "Server has shut down."); continue; }
                try
                {
                    var parameters = message.TryGetProperty("params", out var p) ? p : default;
                    object? result = null;
                    switch (method)
                    {
                        case "initialize":
                            if (initialized || !request) throw new ArgumentException("initialize must be called once as a request.");
                            initialized = true;
                            result = new
                            {
                                capabilities = new
                                {
                                    positionEncoding = "utf-16",
                                    textDocumentSync = new { openClose = true, change = 2 },
                                    completionProvider = new { resolveProvider = false },
                                    hoverProvider = true, definitionProvider = true, documentSymbolProvider = true
                                },
                                serverInfo = new { name = "Aquarius Language Server", version = "0.1.0" }
                            };
                            break;
                        case "initialized": case "$/cancelRequest": case "$/setTrace": case "textDocument/didSave":
                            break;
                        case "shutdown": shutdown = true; break;
                        case "textDocument/didOpen":
                        {
                            var doc = parameters.GetProperty("textDocument");
                            var document = new Document(doc.GetProperty("uri").GetString()!, doc.GetProperty("version").GetInt32(), doc.GetProperty("text").GetString()!);
                            documents[document.Uri] = document;
                            await PublishAsync(document);
                            break;
                        }
                        case "textDocument/didChange":
                        {
                            var document = GetDocument(parameters);
                            int version = parameters.GetProperty("textDocument").GetProperty("version").GetInt32();
                            if (version <= document.Version) break;
                            string text = document.Source.Text;
                            foreach (var change in parameters.GetProperty("contentChanges").EnumerateArray())
                            {
                                string replacement = change.GetProperty("text").GetString()!;
                                if (change.TryGetProperty("range", out var range))
                                {
                                    var source = new SourceText(text);
                                    int start = source.Offset(ReadPosition(range.GetProperty("start")));
                                    int end = source.Offset(ReadPosition(range.GetProperty("end")));
                                    if (end < start) throw new ArgumentException("Reversed change range.");
                                    text = text[..start] + replacement + text[end..];
                                }
                                else text = replacement;
                            }
                            var updated = new Document(document.Uri, version, text);
                            documents[document.Uri] = updated;
                            await PublishAsync(updated);
                            break;
                        }
                        case "textDocument/didClose":
                        {
                            string uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString()!;
                            documents.Remove(uri);
                            await protocol.NotifyAsync("textDocument/publishDiagnostics", new { uri, diagnostics = Array.Empty<object>() });
                            break;
                        }
                        case "textDocument/completion": result = GetDocument(parameters).Completion(ReadPosition(parameters.GetProperty("position"))); break;
                        case "textDocument/hover": result = GetDocument(parameters).Hover(ReadPosition(parameters.GetProperty("position"))); break;
                        case "textDocument/definition": result = GetDocument(parameters).Definition(ReadPosition(parameters.GetProperty("position"))); break;
                        case "textDocument/documentSymbol": result = GetDocument(parameters).Symbols(); break;
                        default:
                            if (request) await protocol.ErrorAsync(id, -32601, $"Unknown method: {method}");
                            continue;
                    }
                    if (request) await protocol.ResultAsync(id, result);
                }
                catch (Exception error)
                {
                    await Console.Error.WriteLineAsync($"{method}: {error.Message}");
                    if (request) await protocol.ErrorAsync(id,
                        error is ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException ? -32602 : -32603,
                        error.Message);
                }
            }
        }
        return shutdown ? 0 : 1;
    }
    catch (Exception error)
    {
        await Console.Error.WriteLineAsync(error.Message);
        return 1;
    }

    Document GetDocument(JsonElement parameters)
    {
        string uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString()!;
        if (!documents.TryGetValue(uri, out var document)) throw new ArgumentException("Document is not open.");
        return document;
    }

    Task PublishAsync(Document document) => protocol.NotifyAsync("textDocument/publishDiagnostics",
        new { uri = document.Uri, version = document.Version, diagnostics = document.Diagnostics });
}

static Position ReadPosition(JsonElement value) => new(value.GetProperty("line").GetInt32(), value.GetProperty("character").GetInt32());
