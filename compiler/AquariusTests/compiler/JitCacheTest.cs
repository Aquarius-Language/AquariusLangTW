using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AquariusLang.Wasm;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary>The compiled-code cache of the desktop runtime JIT (runtime/host/engine_wasmtime.c): each installed module is
/// kept in its own slot file, wasmtime-jit-SSS.bin with SSS = (digest[0] | digest[1] &lt;&lt; 8) % 1024, separate from the
/// applications' 32 slots (wasmtime-SS.bin). An entry is the module's SHA-256 followed by Wasmtime's serialization: a hit
/// leaves the file alone, while an entry of another module or a corrupt one is recompiled and replaced. The program
/// prints the same in every case.</summary>
public class JitCacheTest
{
    // Untyped values from a function: only the runtime JIT compiles these loops (several modules, one per loop).
    private const string Program = "var make = function(v) { v; }; var n = make(4000); var scale = make(0.5d); " +
        "var a = function() { var t = 0.0d; for (var i = 0; i < n; i++) { t += i * scale; } return t; }; " +
        "var b = function(m) { var c = 0; for (var i = 0; i < n; i++) { c += i % m; } return c; }; " +
        "var c = function() { var s = 0; for (var i = 0; i < 60; i++) { for (var j = 0; j < n / 40; j++) { s += i * j; } } return s; }; " +
        "print(a(), b(make(7)), c());";

    private sealed record CacheRun(RunResult Result, string Directory);

    private static CacheRun Run(WasmProgram program, string cache)
    {
        var environment = new Dictionary<string, string> { ["AQUARIUS_JIT"] = "1", ["AQUARIUS_JIT_DIAGNOSTICS"] = "1" };
        // The per-user cache directory of each platform layer (runtime/platform/<os>/platform.c), moved into the test's own.
        if (OperatingSystem.IsWindows()) environment["LOCALAPPDATA"] = cache; else environment["XDG_CACHE_HOME"] = cache;
        string directory = OperatingSystem.IsWindows() ? Path.Combine(cache, "Aquarius", "cache") : Path.Combine(cache, "aquarius");
        return new CacheRun(WasmTestHost.Run(program, environment: environment).Succeeded(), directory);
    }

    private static string[] JitEntries(string directory) =>
        Directory.EnumerateFiles(directory, "wasmtime-jit-*.bin").Order(StringComparer.Ordinal).ToArray();

    [RuntimeFact]
    public void JitModulesKeepTheirOwnSlotsAndRecompileForeignOrCorruptEntries()
    {
        string cache = Path.Combine(Path.GetTempPath(), "aquarius-jit-cache-" + Guid.NewGuid().ToString("N"));
        try {
            var program = new WasmCompiler().Compile(Program);
            string expected = WasmTestHost.Run(new WasmCompiler(false).Compile(Program)).Succeeded().Output;

            // A cold run compiles and stores one entry per installed module, each in the slot its digest selects.
            var first = Run(program, cache);
            Assert.Equal(expected, first.Result.Output);
            int installed = Regex.Matches(first.Result.Error, @"jit: loop \d+ variant \d+ .*: installed").Count;
            Assert.True(installed >= 3, first.Result.Error);
            var entries = JitEntries(first.Directory);
            Assert.Equal(installed, entries.Length);
            foreach (string entry in entries) {
                byte[] bytes = File.ReadAllBytes(entry);
                Assert.True(bytes.Length > 32, entry);
                int slot = (bytes[0] | bytes[1] << 8) % 1024;
                Assert.Equal($"wasmtime-jit-{slot:x3}.bin", Path.GetFileName(entry));
            }
            // The application itself uses the separate 32 slots of two hex digits.
            Assert.Contains(Directory.EnumerateFiles(first.Directory, "wasmtime-*.bin"), f => Regex.IsMatch(Path.GetFileName(f), "^wasmtime-[0-9a-f]{2}\\.bin$"));

            // A warm run reads every entry and rewrites none.
            var snapshot = entries.ToDictionary(e => e, File.ReadAllBytes);
            var stamps = entries.ToDictionary(e => e, File.GetLastWriteTimeUtc);
            var second = Run(program, cache);
            Assert.Equal(expected, second.Result.Output);
            foreach (string entry in entries) {
                Assert.Equal(snapshot[entry], File.ReadAllBytes(entry));
                Assert.Equal(stamps[entry], File.GetLastWriteTimeUtc(entry));
            }

            // Another module's entry in a slot (as after a collision): the digest differs, so the module compiles again
            // and takes its slot back.
            string target = entries[0], other = entries[1];
            File.WriteAllBytes(target, snapshot[other]);
            var third = Run(program, cache);
            Assert.Equal(expected, third.Result.Output);
            Assert.Equal(snapshot[target], File.ReadAllBytes(target));
            Assert.Equal(snapshot[other], File.ReadAllBytes(other));

            // A corrupt entry with the right digest: Wasmtime rejects the code, which is compiled and stored again.
            byte[] corrupt = snapshot[target].Take(32).Concat(RandomNumberGenerator.GetBytes(256)).ToArray();
            File.WriteAllBytes(target, corrupt);
            var fourth = Run(program, cache);
            Assert.Equal(expected, fourth.Result.Output);
            byte[] repaired = File.ReadAllBytes(target);
            Assert.Equal(snapshot[target].Take(32), repaired.Take(32));
            Assert.NotEqual(corrupt, repaired);
            Assert.True(repaired.Length > corrupt.Length, "The repaired entry holds compiled code again.");

            // A truncated entry (shorter than a digest) is a miss too.
            File.WriteAllBytes(target, new byte[] { 1, 2, 3 });
            Assert.Equal(expected, Run(program, cache).Result.Output);
            Assert.Equal(snapshot[target].Take(32), File.ReadAllBytes(target).Take(32));
            // The JIT off never touches the JIT cache.
            foreach (string entry in JitEntries(first.Directory)) File.Delete(entry);
            var off = WasmTestHost.Run(program, environment: new Dictionary<string, string> {
                ["AQUARIUS_JIT"] = "0", [OperatingSystem.IsWindows() ? "LOCALAPPDATA" : "XDG_CACHE_HOME"] = cache }).Succeeded();
            Assert.Equal(expected, off.Output);
            Assert.Empty(JitEntries(first.Directory));
        } finally {
            try { Directory.Delete(cache, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
