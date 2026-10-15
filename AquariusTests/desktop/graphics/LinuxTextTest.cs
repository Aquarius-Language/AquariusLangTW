using AquariusLang.Desktop.Application;

namespace AquariusLang.Desktop.Graphics;

public class LinuxTextTest {
    [Fact] public void LinuxPickersPreserveUnicodeNamesAndValidateFilters() {
        var options = new AquariusLang.Application.PickerOptions("開啟圖片", "星泉 image.png", true,
            new[] { new AquariusLang.Application.FileFilter("Images", new[] { "png", ".jpg" }) });
        var open = DesktopDialogs.LinuxFileArguments(options, false);
        Assert.Contains("--filename=星泉 image.png", open);
        Assert.Contains("--multiple", open);
        Assert.Contains("--file-filter=Images | *.png *.jpg", open);
        var save = DesktopDialogs.LinuxFileArguments(options, true);
        Assert.Contains("--confirm-overwrite", save); Assert.DoesNotContain("--multiple", save);
        Assert.Throws<ArgumentException>(() => DesktopDialogs.LinuxFileArguments(options with {
            Filters = new[] { new AquariusLang.Application.FileFilter("invalid", new[] { "png;*" }) }
        }, false));
        if (OperatingSystem.IsLinux()) {
            Assert.ThrowsAny<OperationCanceledException>(() => DesktopDialogs.DirectoryDialog(new CancellationToken(true)));
            var tree = new DesktopAccessibility(IntPtr.Zero);
            tree.Update(new[] { new AquariusLang.Application.AccessibilityNode("status", "status", "已儲存") });
            Assert.Equal("已儲存", tree.get_accName(1));
            Assert.Throws<ArgumentException>(() => tree.Update(new[] {
                new AquariusLang.Application.AccessibilityNode("a", "status", "a"),
                new AquariusLang.Application.AccessibilityNode("a", "status", "b")
            }));
        }
    }
    [Fact] public async Task InstalledFontsAndUnicodeTextUseTheLinuxFontSystem() {
        if (!OperatingSystem.IsLinux()) return;
        var fonts = new DesktopFonts();
        var installed = await fonts.Enumerate();
        Assert.NotEmpty(installed);
        Assert.True(fonts.Available(installed[0].Family));
        Assert.False(fonts.Available("aquarius-nonexistent-font-" + Guid.NewGuid()));
        Assert.True(fonts.Measure("WWW", "sans-serif", 24).Width > fonts.Measure("iii", "sans-serif", 24).Width);
        foreach (string text in new[] { "繁體中文", "日本語 한글", "العربية", "e\u0301 😀" }) {
            var measured = TextRaster.Measure(text, "sans-serif", 24);
            var raster = TextRaster.Rasterize(text, "sans-serif", 24);
            Assert.Equal(measured, (raster.w, raster.h));
            Assert.Equal(raster.w * raster.h * 4, raster.bytes.Length);
            Assert.Contains(Enumerable.Range(0, raster.w * raster.h), i => raster.bytes[i * 4 + 3] != 0);
            Assert.Contains(Enumerable.Range(0, raster.w * raster.h), i => raster.bytes[i * 4 + 3] == 0);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fonts.Enumerate(new CancellationToken(true)));
        Assert.Throws<ArgumentException>(() => TextRaster.Rasterize(new string('x', 4097), "sans-serif", 24));
    }
}
