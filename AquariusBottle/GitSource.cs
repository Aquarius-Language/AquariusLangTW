using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AquariusLang.Bottle;

internal static class GitSource {
    public static void Validate(string url, string? revision) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/?$") || uri.Segments.Any(s => s.Trim('/') is "." or ".."))
            throw new InvalidDataException("Git sources must be HTTPS GitHub repository URLs, e.g. https://github.com/owner/repository.git.");
        if (revision != null && (!Regex.IsMatch(revision, @"^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$") || revision.Contains(".."))) throw new InvalidDataException("Invalid Git revision; use a release tag, branch or commit hash.");
    }
    public static T WithCheckout<T>(string url, string? revision, string? subdirectory, Func<string, string, string, T> action) {
        Validate(url, revision);
        string temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aquarius-bottle-git-" + Guid.NewGuid().ToString("N"));
        try {
            Run("clone", "--depth", "1", "--no-checkout", "--", url, temporary);
            if (revision != null) Run("-C", temporary, "fetch", "--depth", "1", "origin", revision);
            Run("-C", temporary, "checkout", "--detach", revision == null ? "HEAD" : "FETCH_HEAD");
            string commit = Run("-C", temporary, "rev-parse", "HEAD").Trim();
            string root = subdirectory == null ? temporary : SafeFiles.Under(temporary, subdirectory);
            return action(root, "git:" + url.TrimEnd('/') + "#" + commit + (subdirectory == null ? "" : ":" + subdirectory), temporary);
        } finally {
            if (Directory.Exists(temporary)) RemoveCheckout(temporary);
        }
    }
    private static void RemoveCheckout(string directory) {
        // Never recurse through a link supplied by an untrusted repository.
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0) {
                if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry);
                else RemoveCheckout(entry);
            } else {
                if ((attributes & FileAttributes.ReparsePoint) == 0) File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }
        Directory.Delete(directory);
    }
    private static string Run(params string[] args) {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-c", "core.hooksPath=", "-c", "init.templateDir=", "-c", "protocol.file.allow=never", "-c", "protocol.ext.allow=never" }.Concat(args)) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        try {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000)) { process.Kill(true); throw new IOException("GitHub checkout timed out after two minutes."); }
            if (process.ExitCode != 0) throw new IOException("GitHub checkout failed: " + stderr.GetAwaiter().GetResult().Trim());
            return stdout.GetAwaiter().GetResult();
        } catch (System.ComponentModel.Win32Exception e) { throw new IOException("Git is required to install GitHub libraries. Install Git and make it available on PATH.", e); }
    }
}
