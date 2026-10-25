using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AquariusLanguageServer;

// Keep stdout exclusively for Content-Length framed JSON-RPC. Logs go to stderr.
internal sealed class Protocol(Stream input, Stream output)
{
    private const int MaxBodyLength = 16 * 1024 * 1024;

    public async Task<byte[]?> ReadAsync()
    {
        var header = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await input.ReadAsync(one) == 0)
            {
                if (header.Count == 0) return null;
                throw new EndOfStreamException("Incomplete LSP header.");
            }
            header.Add(one[0]);
            if (header.Count > 8192) throw new InvalidDataException("LSP header is too large.");
            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
        }

        int? length = null;
        foreach (var line in Encoding.ASCII.GetString(header.ToArray()).Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            if (!line[..colon].Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (length != null || !int.TryParse(line[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                || value <= 0 || value > MaxBodyLength)
                throw new InvalidDataException("Invalid Content-Length.");
            length = value;
        }
        if (length == null) throw new InvalidDataException("Missing Content-Length.");
        var body = new byte[length.Value];
        await input.ReadExactlyAsync(body);
        return body;
    }

    public async Task SendAsync(object message)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await output.WriteAsync(header);
        await output.WriteAsync(body);
        await output.FlushAsync();
    }

    public Task ResultAsync(JsonElement id, object? result) => SendAsync(new { jsonrpc = "2.0", id, result });
    public Task ErrorAsync(object? id, int code, string message) => SendAsync(new { jsonrpc = "2.0", id, error = new { code, message } });
    public Task NotifyAsync(string method, object @params) => SendAsync(new { jsonrpc = "2.0", method, @params });
}
