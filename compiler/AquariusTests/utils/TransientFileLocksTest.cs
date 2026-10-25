using AquariusLang.FileSystem;

namespace AquariusTests.Utils;

public class TransientFileLocksTest {
    // A scanner holding one copied file must not fail moving its staging directory into place (bottle cache restores).
    [Fact] public void MovingADirectoryWaitsForABriefHoldOnAFileInside() {
        string root = Path.Combine(Path.GetTempPath(), "aquarius-locks-" + Guid.NewGuid().ToString("N")), stage = Path.Combine(root, "stage"), target = Path.Combine(root, "target");
        Directory.CreateDirectory(stage); File.WriteAllText(Path.Combine(stage, "file.txt"), "content");
        try {
            var held = new FileStream(Path.Combine(stage, "file.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var release = Task.Delay(150).ContinueWith(_ => held.Dispose());
            TransientFileLocks.Retry(() => Directory.Move(stage, target), accessDenied: true);
            release.Wait();
            Assert.Equal("content", File.ReadAllText(Path.Combine(target, "file.txt")));
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
