using Xunit;

namespace AquariusLang.utils; 

public class UtilsTest {
    struct StringToFloatTest {
        public string input;
        public object expected;
    }
    [Fact]
    public void TestStringToDecimal() {
        StringToFloatTest[] tests = {
            new () {input = "12.74f", expected = 12.74F},
            new () {input = "12.f", expected = 12F},
            
            new () {input = "234.8980d", expected = 234.898},
            new () {input = "0.d", expected = 0.0},
        };
        foreach (StringToFloatTest test in tests) {
            if (test.expected is float _test) {
                float? val = Utils.StringToFloat(test.input);
                Assert.Equal(test.expected, val);
            } else if (test.expected is double __test) {
                double? val = Utils.StringToDouble(test.input);
                Assert.Equal(test.expected, val);
            }
        }
    } 
    
    [Fact]
    public void IsFullPath() {
        bool isWindows = OperatingSystem.IsWindows();
        // bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows); // .NET Core

        // These are full paths on Windows, but not on Linux
        tryIsFullPath(@"C:\dir\file.ext", isWindows);
        tryIsFullPath(@"C:\dir\", isWindows);
        tryIsFullPath(@"C:\dir", isWindows);
        tryIsFullPath(@"C:\", isWindows);
        tryIsFullPath(@"\\unc\share\dir\file.ext", isWindows);
        tryIsFullPath(@"\\unc\share", isWindows);

        // These are full paths on Linux, but not on Windows
        tryIsFullPath(@"/some/file", !isWindows);
        tryIsFullPath(@"/dir", !isWindows);
        tryIsFullPath(@"/", !isWindows);

        // Not full paths on either Windows or Linux
        tryIsFullPath(@"file.ext", false);
        tryIsFullPath(@"dir\file.ext", false);
        tryIsFullPath(@"\dir\file.ext", false);
        tryIsFullPath(@"C:", false);
        tryIsFullPath(@"C:dir\file.ext", false);
        tryIsFullPath(@"\dir", false); // An "absolute", but not "full" path

        // Invalid on both Windows and Linux
        tryIsFullPath(null, false, false);
        tryIsFullPath("", false, false);
        tryIsFullPath("   ", false, false); // technically, a valid filename on Linux

        // Invalid on Windows, valid (but not full paths) on Linux
        tryIsFullPath(@"C:\inval|d", false, !isWindows);
        tryIsFullPath(@"\\is_this_a_dir_or_a_hostname", false, !isWindows);
        tryIsFullPath(@"\\is_this_a_dir_or_a_hostname\", false, !isWindows);
        tryIsFullPath(@"\\is_this_a_dir_or_a_hostname\\", false, !isWindows);
        
        // Relative paths.
        tryIsFullPath("./test/testfile.txt", false, isWindows);
        tryIsFullPath("./testfile.txt", false, !isWindows);
    }
    
    private void tryIsFullPath(string path, bool expectedIsFull, bool expectedIsValid = true)
    {
        Assert.Equal(expectedIsFull, Utils.IsFullPath(path));

        if (expectedIsFull) {
            Assert.Equal(path, Path.GetFullPath(path));
        } else if (expectedIsValid) {
            Assert.NotEqual(path, Path.GetFullPath(path));
        }
        else {
            // Assert.That(() => Path.GetFullPath(path), Throws.Exception);
            
        }
    }
}