using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AquariusLang.FileSystem;

/// <summary>
/// Windows: an on-access scanner or indexer can hold a just-written file open without delete sharing for a moment, so
/// replacing, moving or renaming it (or a directory containing it) fails although nothing else uses it. These helpers
/// retry such failures for a bounded time (about 2 s) before reporting them.
/// </summary>
public static class TransientFileLocks
{
    private const int Attempts = 12;

    // Sharing violation, lock violation, unable to remove/move/rename the replaced file; access denied for directory moves.
    private static bool Transient(Exception e, bool accessDenied) => OperatingSystem.IsWindows() &&
        (e is IOException && (e.HResult & 0xFFFF) is 32 or 33 or 1175 or 1176 or 1177 || accessDenied && (e is UnauthorizedAccessException || e is IOException && (e.HResult & 0xFFFF) == 5));

    public static async Task RetryAsync(Action operation, CancellationToken cancellation = default, bool accessDenied = false)
    {
        for (int attempt = 1; ; attempt++) {
            try { operation(); return; }
            catch (Exception e) when (attempt < Attempts && Transient(e, accessDenied)) { await Task.Delay(25 * attempt, cancellation).ConfigureAwait(false); }
        }
    }

    public static void Retry(Action operation, bool accessDenied = false)
    {
        for (int attempt = 1; ; attempt++) {
            try { operation(); return; }
            catch (Exception e) when (attempt < Attempts && Transient(e, accessDenied)) { Thread.Sleep(25 * attempt); }
        }
    }
}
