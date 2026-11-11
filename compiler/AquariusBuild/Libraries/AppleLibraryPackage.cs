using System.Security;
using System.Text;
using AquariusLang.Build.Bindings;
using AquariusLang.Compiler;

namespace AquariusLang.Build.Libraries;

/// <summary>
/// The Apple packages of native library outputs (docs/native-libraries.md): frameworks (a macOS framework is versioned,
/// <c>Versions/A</c> with links to it; an iOS framework is flat) and the iOS xcframework that holds the device and
/// simulator slices. Every Info.plist is written here, directly, so the tests compare them with goldens on any host;
/// only signing (<c>codesign</c>) needs a Mac.
/// </summary>
public static class AppleLibraryPackage
{
    /// <summary>The lowest system versions the runtime packs are built for (the deployment targets of
    /// runtime/sdk/cmake/toolchains/macos-arm64.cmake and ios.cmake).</summary>
    public const string MinimumMacOS = "13.0", MinimumIos = "16.0";

    /// <summary>One slice of an Apple library: its LibraryIdentifier (ios-arm64, ios-arm64-simulator), platform,
    /// architecture and variant, from the runtime target (ios-simulator-arm64 is the simulator variant).</summary>
    public sealed record Slice(string Identifier, string Platform, string Architecture, string? Variant)
    {
        public static Slice Of(RuntimeTarget target)
        {
            string architecture = target.Name[(target.Name.LastIndexOf('-') + 1)..];
            string? variant = target.Name.Contains("-simulator-", StringComparison.Ordinal) ? "simulator" : null;
            return new(target.Platform + "-" + architecture + (variant != null ? "-" + variant : ""), target.Platform, architecture, variant);
        }
        /// <summary>CFBundleSupportedPlatforms of a framework built for the slice.</summary>
        public string BundlePlatform => (Platform, Variant) switch {
            ("macos", _) => "MacOSX", ("ios", "simulator") => "iPhoneSimulator", ("ios", _) => "iPhoneOS",
            _ => throw new ArgumentException($"{Platform} has no Apple bundle platform.")
        };
    }

    /// <summary>The framework of the library: its module name (the framework's and its binary's name).</summary>
    public static string FrameworkName(BindingContext context) => SwiftBinding.Module(context);
    public static string BundleId(BindingContext context) =>
        context.Settings.For(context.Platform).BundleId ?? "org.aquariuslang.library." + context.Settings.Name.Replace('_', '-');

    /// <summary>The install name of the shared library: <c>@rpath/lib&lt;name&gt;.dylib</c> in a folder package, the binary
    /// inside the framework otherwise (versioned on macOS, flat on iOS).</summary>
    public static string InstallName(BindingContext context, RuntimeTarget target, bool framework)
    {
        string module = FrameworkName(context);
        if (!framework) return "@rpath/" + target.SharedLibraryName(context.Settings.Name);
        return target.Platform == "macos" ? $"@rpath/{module}.framework/Versions/A/{module}" : $"@rpath/{module}.framework/{module}";
    }

    /// <summary>The current and compatibility versions of a dylib (<c>major.minor.patch</c>, <c>major.0.0</c>).</summary>
    public static (string Current, string Compatibility) Versions(NativeLibrarySettings settings)
    {
        string version = settings.Version is { } v && v.Split('-', '+')[0] is var core && core.Split('.').All(p => int.TryParse(p, out _)) ? core : "0.0.0";
        return (version, $"{settings.Major}.0.0");
    }

    private static string Xml(string text) => SecurityElement.Escape(text);
    private const string PlistHead = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n<dict>\n";

    /// <summary>A framework's Info.plist (Resources/Info.plist on macOS, at the top of an iOS framework).</summary>
    public static string FrameworkInfoPlist(BindingContext context, Slice slice)
    {
        string module = FrameworkName(context), version = Versions(context.Settings).Current;
        var text = new StringBuilder(PlistHead);
        text.Append($"  <key>CFBundleDevelopmentRegion</key><string>en</string>\n");
        text.Append($"  <key>CFBundleExecutable</key><string>{Xml(module)}</string>\n");
        text.Append($"  <key>CFBundleIdentifier</key><string>{Xml(BundleId(context))}</string>\n");
        text.Append($"  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>\n");
        text.Append($"  <key>CFBundleName</key><string>{Xml(module)}</string>\n");
        text.Append($"  <key>CFBundlePackageType</key><string>FMWK</string>\n");
        text.Append($"  <key>CFBundleShortVersionString</key><string>{version}</string>\n");
        text.Append($"  <key>CFBundleVersion</key><string>{version}</string>\n");
        text.Append($"  <key>CFBundleSupportedPlatforms</key><array><string>{slice.BundlePlatform}</string></array>\n");
        text.Append(slice.Platform == "macos" ? $"  <key>LSMinimumSystemVersion</key><string>{MinimumMacOS}</string>\n"
            : $"  <key>MinimumOSVersion</key><string>{MinimumIos}</string>\n");
        text.Append("</dict>\n</plist>\n");
        return text.ToString();
    }

    /// <summary>An xcframework's Info.plist: one entry per slice, each a framework or a static library with its headers.</summary>
    public static string XcframeworkInfoPlist(IReadOnlyList<(Slice Slice, string LibraryPath, string BinaryPath, string? HeadersPath)> libraries)
    {
        var text = new StringBuilder(PlistHead);
        text.Append("  <key>AvailableLibraries</key>\n  <array>\n");
        foreach (var (slice, library, binary, headers) in libraries) {
            text.Append("    <dict>\n");
            text.Append($"      <key>BinaryPath</key><string>{Xml(binary)}</string>\n");
            if (headers != null) text.Append($"      <key>HeadersPath</key><string>{Xml(headers)}</string>\n");
            text.Append($"      <key>LibraryIdentifier</key><string>{slice.Identifier}</string>\n");
            text.Append($"      <key>LibraryPath</key><string>{Xml(library)}</string>\n");
            text.Append($"      <key>SupportedArchitectures</key><array><string>{slice.Architecture}</string></array>\n");
            text.Append($"      <key>SupportedPlatform</key><string>{slice.Platform}</string>\n");
            if (slice.Variant != null) text.Append($"      <key>SupportedPlatformVariant</key><string>{slice.Variant}</string>\n");
            text.Append("    </dict>\n");
        }
        text.Append("  </array>\n  <key>CFBundlePackageType</key><string>XFWK</string>\n  <key>XCFrameworkFormatVersion</key><string>1.0</string>\n");
        text.Append("</dict>\n</plist>\n");
        return text.ToString();
    }

    /// <summary>Writes <c>&lt;Module&gt;.framework</c> into <paramref name="folder"/> from a built shared library (whose
    /// install name is the framework's, <see cref="InstallName"/>) and the library's C header. macOS frameworks are
    /// versioned (Versions/A, with Versions/Current and the top-level entries as symbolic links); iOS ones are flat.</summary>
    public static string WriteFramework(BindingContext context, Slice slice, string folder, string library, string includeFolder)
    {
        string module = FrameworkName(context);
        string framework = Path.Combine(folder, module + ".framework");
        bool versioned = slice.Platform == "macos";
        string contents = versioned ? Path.Combine(framework, "Versions", "A") : framework;
        Directory.CreateDirectory(Path.Combine(contents, "Headers"));
        Directory.CreateDirectory(Path.Combine(contents, "Modules"));
        File.Copy(library, Path.Combine(contents, module), true);
        string header = CBinding.HeaderName(context);
        File.Copy(Path.Combine(includeFolder, header), Path.Combine(contents, "Headers", header), true);
        NativeLibraryOutput.Write(Path.Combine(contents, "Modules", "module.modulemap"), SwiftBinding.FrameworkModuleMap(context));
        string plist = versioned ? Path.Combine(contents, "Resources", "Info.plist") : Path.Combine(contents, "Info.plist");
        NativeLibraryOutput.Write(plist, FrameworkInfoPlist(context, slice));
        if (versioned) {
            File.CreateSymbolicLink(Path.Combine(framework, "Versions", "Current"), "A");
            foreach (string entry in new[] { module, "Headers", "Modules", "Resources" })
                File.CreateSymbolicLink(Path.Combine(framework, entry), Path.Combine("Versions", "Current", entry));
        }
        return framework;
    }

    /// <summary>Writes a static slice's folder: the library and Headers/ (the C header and the module map, so Swift imports
    /// it from the xcframework).</summary>
    public static void WriteStaticSlice(BindingContext context, string folder, string library, string includeFolder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "Headers"));
        File.Copy(library, Path.Combine(folder, Path.GetFileName(library)), true);
        string header = CBinding.HeaderName(context);
        File.Copy(Path.Combine(includeFolder, header), Path.Combine(folder, "Headers", header), true);
        NativeLibraryOutput.Write(Path.Combine(folder, "Headers", "module.modulemap"), SwiftBinding.ModuleMap(context));
    }

    /// <summary>Writes an xcframework from slice folders already laid out as <c>&lt;xcframework&gt;/&lt;identifier&gt;/</c>.</summary>
    public static void WriteXcframework(string xcframework, IReadOnlyList<(Slice Slice, string LibraryPath, string BinaryPath, string? HeadersPath)> libraries) =>
        NativeLibraryOutput.Write(Path.Combine(xcframework, "Info.plist"), XcframeworkInfoPlist(libraries));

    /// <summary>Signs a library, framework or xcframework ad hoc (a Mac's codesign); apps re-sign what they embed.</summary>
    public static void SignAdHoc(string path) => Output.Run("codesign", ["--force", "--sign", "-", "--timestamp=none", path]);
}
