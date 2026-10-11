using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AquariusLang.Webcam;

namespace AquariusLang.Desktop.Webcam;

/// <summary>以 FFmpeg 的 DirectShow、Video4Linux2 或 AVFoundation 裝置介面擷取；不經過命令殼層。</summary>
public sealed class DesktopWebcamBackend : IWebcamBackend {
    private readonly string? executable;
    public DesktopWebcamBackend(string? ffmpegExecutable = null) {
        try { executable = FindExecutable(ffmpegExecutable ?? System.Environment.GetEnvironmentVariable("AQUARIUS_FFMPEG") ?? "ffmpeg"); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { executable = null; }
    }
    public bool IsSupported => executable != null && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
    private void RequireSupported() {
        if (!IsSupported) throw new WebcamFailure(WebcamFailureKind.Unsupported, "此桌面環境未提供攝影機介面。請安裝具備裝置擷取支援的 FFmpeg，或設定 AQUARIUS_FFMPEG 執行檔路徑。");
    }
    public async ValueTask<IReadOnlyList<WebcamDevice>> Enumerate(CancellationToken cancellation = default) {
        RequireSupported(); cancellation.ThrowIfCancellationRequested();
        if (OperatingSystem.IsLinux()) {
            return Directory.EnumerateFiles("/dev", "video*").Where(p => Regex.IsMatch(p, @"^/dev/video\d+$"))
                .OrderBy(p => p, StringComparer.Ordinal).Select(p => {
                    string name = Path.Combine("/sys/class/video4linux", Path.GetFileName(p), "name");
                    return new WebcamDevice(p, File.Exists(name) ? File.ReadAllText(name).Trim() : Path.GetFileName(p));
                }).ToArray();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(10000);
        using var process = Start(CreateStartInfo(executable!, OperatingSystem.IsWindows()
            ? new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" }
            : new[] { "-hide_banner", "-list_devices", "true", "-f", "avfoundation", "-i", "" }));
        try {
            var output = ReadDiagnostic(process.StandardError, timeout.Token);
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await process.WaitForExitAsync(timeout.Token); await stdout;
            // 列舉裝置的命令會以非零狀態結束；裝置清單仍由標準錯誤輸出傳回。
            return ParseDevices(await output, OperatingSystem.IsWindows());
        } catch (OperationCanceledException) {
            throw new WebcamFailure(cancellation.IsCancellationRequested ? WebcamFailureKind.Cancelled : WebcamFailureKind.Timeout, "攝影機裝置列舉已取消或逾時。");
        } finally { Stop(process); }
    }
    public async ValueTask<IWebcamSession> Open(string deviceId, WebcamOptions options, CancellationToken cancellation = default) {
        options.Validate(); RequireSupported(); cancellation.ThrowIfCancellationRequested();
        if (deviceId == null) throw new WebcamFailure(WebcamFailureKind.InvalidArgument, "裝置識別碼必須是字串。");
        var devices = await Enumerate(cancellation);
        var device = deviceId.Length == 0 ? devices.FirstOrDefault() : devices.FirstOrDefault(d => d.Id == deviceId);
        if (device == null) throw new WebcamFailure(WebcamFailureKind.NotFound, "找不到指定的攝影機；請重新列出裝置。");
        return await OpenInput(OperatingSystem.IsWindows() ? "dshow" : OperatingSystem.IsLinux() ? "v4l2" : "avfoundation", device.Id, options, cancellation);
    }
    /// <summary>測試可注入 FFmpeg 的虛擬裝置；語言介面不提供任意輸入或檔案。</summary>
    internal async ValueTask<IWebcamSession> OpenInput(string format, string input, WebcamOptions options, CancellationToken cancellation = default) {
        options.Validate(); RequireSupported(); cancellation.ThrowIfCancellationRequested();
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", format };
        if (format == "lavfi") args.Add("-re");
        if (format != "lavfi") args.AddRange(new[] { "-framerate", options.FramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-i", input, "-an", "-sn", "-dn", "-threads", "1", "-s", $"{options.Width}x{options.Height}", "-pix_fmt", "rgba", "-fps_mode", "passthrough", "-flush_packets", "1", "-f", "rawvideo", "pipe:1" });
        var session = new DesktopWebcamSession(Start(CreateStartInfo(executable!, args)), options);
        try { await session.Prime(cancellation); return session; }
        catch { session.Dispose(); throw; }
    }
    internal static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments) {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string argument in arguments) info.ArgumentList.Add(argument); return info;
    }
    private static Process Start(ProcessStartInfo info) {
        try { return Process.Start(info) ?? throw new WebcamFailure(WebcamFailureKind.Unavailable, "無法啟動攝影機擷取程序。"); }
        catch (Win32Exception) { throw new WebcamFailure(WebcamFailureKind.Unsupported, "無法啟動 FFmpeg；請確認安裝與執行權限。"); }
    }
    internal static void Stop(Process process) {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(2000); } }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
    internal static async Task<string> ReadDiagnostic(StreamReader reader, CancellationToken cancellation = default) {
        var result = new StringBuilder(); var buffer = new char[1024]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) != 0)
            if (result.Length < 16384) result.Append(buffer, 0, Math.Min(count, 16384 - result.Length));
        return result.ToString();
    }
    internal static IReadOnlyList<WebcamDevice> ParseDevices(string output, bool windows) {
        var devices = new List<WebcamDevice>(); bool video = windows;
        foreach (string line in output.Split('\n')) {
            if (!windows) {
                if (line.Contains("AVFoundation video devices:")) { video = true; continue; }
                if (line.Contains("AVFoundation audio devices:")) { video = false; continue; }
                var device = Regex.Match(line, @"\]\s+\[(\d+)\]\s+(.+)$");
                if (video && device.Success) devices.Add(new(device.Groups[1].Value + ":none", device.Groups[2].Value.Trim()));
            } else {
                var device = Regex.Match(line, "\"(.*)\" \\(video\\)");
                if (device.Success) { devices.Add(new("video=" + device.Groups[1].Value, device.Groups[1].Value)); video = true; }
                else if (line.Contains("(audio)")) video = false;
                else {
                    var alternative = Regex.Match(line, "Alternative name \"(.*)\"");
                    if (video && alternative.Success && devices.Count > 0) devices[^1] = devices[^1] with { Id = "video=" + alternative.Groups[1].Value };
                }
            }
        }
        return devices.DistinctBy(d => d.Id).ToArray();
    }
    private static string? FindExecutable(string name) {
        if (Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(name) ? Path.GetFullPath(name) : null;
        string filename = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name;
        foreach (string directory in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
            string candidate = Path.Combine(directory.Trim('"'), filename); if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
}
