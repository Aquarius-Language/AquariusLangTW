using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AquariusLang.Build;

namespace AquariusTests.Packaging;

/// <summary>The overlay the desktop runtime reads from a standalone executable (runtime/platform/desktop/main.c):
/// [runtime executable][wasm][JSON {"EntryPoint":...}][64-byte footer: i32 version 2, i32 metadata length, i64 wasm
/// length, SHA-256 of wasm and metadata, "AQUARIUS-APP-V2!"].</summary>
public class ExecutableBundleTest
{
    private static readonly byte[] Template = "synthetic runtime template"u8.ToArray();

    internal static string Wasm(TestFolder t)
    {
        var files = new[] { t.Write("src/main.aqua", "匯入(\"lib/工具.aqua\").值;"), t.Write("src/lib/工具.aqua", "變數 值=42;") };
        string asset = t.Write("src/assets/文字.txt", "封裝成功"), wasm = t.FilePath("app.wasm");
        WasmApplication.Compile(files, wasm, t.FilePath("src"), [asset]);
        return wasm;
    }
    private static MemoryStream Bundle(string wasm, string? entry = null)
    {
        using var host = new MemoryStream(Template);
        using var input = File.OpenRead(wasm);
        var result = new MemoryStream(); ExecutableBundle.Write(host, input, result, entry); return result;
    }

    [Fact]
    public void OverlayPreservesTemplateAndWasmAndCanonicalEntryAfterSourceRemoval()
    {
        using var t = new TestFolder(); string wasm = Wasm(t); byte[] application = File.ReadAllBytes(wasm);
        using var executable = Bundle(wasm, "LIB/工具.AQUA");
        Directory.Delete(t.FilePath("src"), true); File.Delete(wasm);
        Assert.True(ExecutableBundle.HasProgram(executable));
        var program = ExecutableBundle.Load(executable);
        Assert.Equal("lib/工具.aqua", program.EntryPoint);
        Assert.Equal("main.aqua", program.Package.EntryPoint);
        Assert.Equal(2, program.Package.Scripts.Count);
        Assert.Equal("封裝成功", Encoding.UTF8.GetString(program.Package.Assets["assets/文字.txt"]));
        Assert.True(executable.CanRead); // The caller keeps the stream.
        byte[] bytes = executable.ToArray();
        Assert.True(bytes.AsSpan().StartsWith(Template));
        Assert.True(bytes.AsSpan(Template.Length).StartsWith(application)); // The wasm is appended unchanged.
    }

    [Fact]
    public void FooterHasTheLayoutTheRuntimeReads()
    {
        using var t = new TestFolder(); string wasm = Wasm(t); byte[] application = File.ReadAllBytes(wasm);
        byte[] bytes = Bundle(wasm).ToArray();
        var footer = bytes.AsSpan(bytes.Length - ExecutableBundle.FooterSize);
        Assert.Equal(64, ExecutableBundle.FooterSize);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(footer));
        int metadataLength = BinaryPrimitives.ReadInt32LittleEndian(footer[4..]);
        Assert.Equal(application.Length, BinaryPrimitives.ReadInt64LittleEndian(footer[8..]));
        Assert.Equal("AQUARIUS-APP-V2!", Encoding.ASCII.GetString(footer[48..]));
        Assert.Equal(Template.Length + application.Length + metadataLength + 64, bytes.Length);
        byte[] metadata = bytes.AsSpan(Template.Length + application.Length, metadataLength).ToArray();
        using (var json = JsonDocument.Parse(metadata)) Assert.Equal("main.aqua", json.RootElement.GetProperty("EntryPoint").GetString());
        Assert.Equal(SHA256.HashData(bytes.AsSpan(Template.Length, application.Length + metadataLength)), footer[16..48].ToArray());
    }

    [Theory]
    [InlineData("version")][InlineData("length")][InlineData("metadataLength")][InlineData("checksum")][InlineData("payload")]
    [InlineData("magic")][InlineData("truncated")][InlineData("entry")]
    public void InvalidOverlaysAreRejectedBeforeExecuting(string damage)
    {
        using var t = new TestFolder(); using var executable = Bundle(Wasm(t));
        byte[] bytes = executable.ToArray(); int footer = bytes.Length - ExecutableBundle.FooterSize;
        switch (damage) {
            case "version": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(footer), 999); break;
            case "length": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(footer + 8), long.MaxValue); break;
            case "metadataLength": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(footer + 4), 0); break;
            case "checksum": bytes[footer + 20] ^= 1; break;
            case "payload": bytes[Template.Length + 30] ^= 1; break;
            case "magic": bytes[^1] ^= 1; break;
            case "truncated": bytes = bytes[..^1]; break;
            case "entry": bytes = Rebundle(bytes, JsonSerializer.SerializeToUtf8Bytes(new { EntryPoint = "missing.aqua" })); break;
        }
        using var broken = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => ExecutableBundle.Load(broken));
    }

    // A consistent overlay (lengths and checksum match) with other entry metadata.
    private static byte[] Rebundle(byte[] bytes, byte[] metadata)
    {
        var tail = bytes.AsSpan(bytes.Length - ExecutableBundle.FooterSize).ToArray();
        long wasmLength = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(8));
        byte[] wasm = bytes.AsSpan(Template.Length, (int)wasmLength).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(4), metadata.Length);
        SHA256.HashData([.. wasm, .. metadata]).CopyTo(tail.AsSpan(16));
        return [.. Template, .. wasm, .. metadata, .. tail];
    }

    [Fact]
    public void RebundledOverlaysWithAValidEntryLoad()
    {
        using var t = new TestFolder(); using var executable = Bundle(Wasm(t));
        byte[] bytes = Rebundle(executable.ToArray(), JsonSerializer.SerializeToUtf8Bytes(new { EntryPoint = "lib/工具.aqua" }));
        Assert.Equal("lib/工具.aqua", ExecutableBundle.Load(new MemoryStream(bytes)).EntryPoint);
    }

    [Fact]
    public void OnlyValidApplicationsAreBundled()
    {
        using var t = new TestFolder();
        string wasm = Wasm(t);
        using (var invalid = new MemoryStream("invalid archive"u8.ToArray())) using (var host = new MemoryStream(Template))
            Assert.Throws<InvalidDataException>(() => ExecutableBundle.Write(host, invalid, new MemoryStream()));
        using (var input = File.OpenRead(wasm)) using (var host = new MemoryStream(Template))
            Assert.Throws<InvalidDataException>(() => ExecutableBundle.Write(host, input, new MemoryStream(), "missing.aqua"));
        using (var input = File.OpenRead(wasm)) using (var empty = new MemoryStream())
            Assert.Throws<InvalidDataException>(() => ExecutableBundle.Write(empty, input, new MemoryStream()));
        Assert.False(ExecutableBundle.HasProgram(new MemoryStream(Template)));
    }

    [Fact]
    public void Abi3ApplicationsCanStillBeBundled()
    {
        using var t = new TestFolder();
        string wasm = t.Write("old.wasm", WasmArtifactTest.Abi3Artifact());
        using var executable = Bundle(wasm);
        var program = ExecutableBundle.Load(executable);
        Assert.Equal(3, program.Package.Program.Metadata.AbiVersion);
        Assert.Contains(".aquarius/Stars.aqua", program.Package.Scripts.Keys);
    }
}
