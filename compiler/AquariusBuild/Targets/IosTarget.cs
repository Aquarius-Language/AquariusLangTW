using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AquariusLang.Compiler;

namespace AquariusLang.Build.Targets;

/// <summary>
/// An iOS and iPadOS application (<c>Name.app</c>), built on a Mac. iOS lets no application generate machine code, so
/// the application is compiled ahead of time (CLAUDE.md rule 11): its .wasm is translated by the pinned wasm2c and
/// linked with the runtime pack's libaquarius.a (the host, the UIKit layer and the default libraries) by the pack's
/// app/ project into the bundle's executable. The bundle also holds the overlay (application.aquarius: the same .wasm
/// the host reads metadata from, checked against the compiled code's SHA-256) and an Info.plist, and is code signed:
/// ad hoc for the simulator (<see cref="SimulatorOption"/>, the ios-simulator-arm64 pack), with
/// <see cref="SigningIdentityOption"/> and <see cref="ProvisioningProfileOption"/> for devices (the ios-arm64 pack).
/// </summary>
public sealed class IosTarget : IWasmBuildTarget
{
    public const string SimulatorOption = "--simulator", BundleIdOption = "--bundle-id", NameOption = "--app-name";
    public const string SigningIdentityOption = "--signing-identity", ProvisioningProfileOption = "--provisioning-profile";
    public const string Payload = "application.aquarius";
    /// <summary>Marks a bundle this target wrote, so rebuilding may replace it.</summary>
    public const string Marker = ".aquarius-ios-output";
    public string Name => "ios";
    public string ArtifactDescription => "iOS application";
    public IReadOnlyCollection<string> OptionNames => [SimulatorOption, BundleIdOption, NameOption, SigningIdentityOption, ProvisioningProfileOption];

    public void Build(WasmBuildRequest request)
    {
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Output));
        if (!output.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("iOS builds write a *.app bundle.");
        string name = Path.GetFileNameWithoutExtension(output);
        if (name.Length == 0) throw new ArgumentException("iOS applications need a name before .app.");
        bool simulator = request.Flag(SimulatorOption);
        string runtimeTarget = simulator ? "ios-simulator-arm64" : "ios-arm64";
        string? identity = request.Option(SigningIdentityOption), profile = request.Option(ProvisioningProfileOption);
        if (simulator && profile != null) throw new ArgumentException($"{ProvisioningProfileOption} applies to device builds, not the simulator.");
        if (profile != null && !File.Exists(profile)) throw new FileNotFoundException("The provisioning profile does not exist.", profile);
        var (application, entry) = Output.Read(request, Name);
        string id = request.Option(BundleIdOption) ?? "org.aquariuslang.app.p" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output + "\n" + entry)))[..16].ToLowerInvariant();
        if (!Regex.IsMatch(id, @"\A[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+\z")) throw new ArgumentException("Invalid iOS bundle identifier.");
        string label = request.Option(NameOption) ?? name;
        AquariusInstallation.RequireBuildableHere(runtimeTarget, "iOS applications");
        foreach (var library in Output.NeededLibraries(application, request))
            if (library.Library is { External: true, NativeName: not null })
                throw new ArgumentException($"The library {library.Name} has native code: iOS applications link native libraries into the runtime, which external libraries cannot be yet.");
        var installation = AquariusInstallation.Require();
        string pack = installation.RequireRuntimePack(runtimeTarget, "libaquarius.a", Path.Combine("app", "CMakeLists.txt"));

        Output.ReplaceDirectory(output, Marker, app => {
            AheadOfTime.Compile(request.Input, Path.Combine(app, name), runtimeTarget, Path.Combine(pack, "app"));
            using (var wasm = File.OpenRead(request.Input)) using (var payload = new FileStream(Path.Combine(app, Payload), FileMode.CreateNew))
                ExecutableBundle.WritePayload(wasm, payload, entry);
            File.WriteAllText(Path.Combine(app, "Info.plist"), InfoPlist(name, id, label, simulator));
            if (profile != null) File.Copy(profile, Path.Combine(app, "embedded.mobileprovision"));
            Output.Run("codesign", ["--force", "--sign", identity ?? "-", "--timestamp=none", app]);
        });
    }

    /// <summary>The bundle's Info.plist: its executable and identifier, iPhone and iPad, full screen, every orientation.</summary>
    public static string InfoPlist(string name, string id, string label, bool simulator)
    {
        string Xml(string text) => System.Security.SecurityElement.Escape(text);
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleExecutable</key><string>{Xml(name)}</string>
              <key>CFBundleIdentifier</key><string>{Xml(id)}</string>
              <key>CFBundleName</key><string>{Xml(label)}</string>
              <key>CFBundleDisplayName</key><string>{Xml(label)}</string>
              <key>CFBundlePackageType</key><string>APPL</string>
              <key>CFBundleShortVersionString</key><string>1.0</string>
              <key>CFBundleVersion</key><string>1</string>
              <key>CFBundleSupportedPlatforms</key><array><string>{(simulator ? "iPhoneSimulator" : "iPhoneOS")}</string></array>
              <key>MinimumOSVersion</key><string>16.0</string>
              <key>LSRequiresIPhoneOS</key><true/>
              <key>UIDeviceFamily</key><array><integer>1</integer><integer>2</integer></array>
              <key>UIApplicationSceneManifest</key><dict>
                <key>UIApplicationSupportsMultipleScenes</key><false/>
                <key>UISceneConfigurations</key><dict>
                  <key>UIWindowSceneSessionRoleApplication</key><array><dict>
                    <key>UISceneConfigurationName</key><string>Default</string>
                    <key>UISceneDelegateClassName</key><string>AquaSceneDelegate</string>
                  </dict></array>
                </dict>
              </dict>
              <key>UILaunchScreen</key><dict/>
              <key>UIRequiresFullScreen</key><true/>
              <key>UIStatusBarHidden</key><true/>
              <key>UISupportedInterfaceOrientations</key><array><string>UIInterfaceOrientationPortrait</string><string>UIInterfaceOrientationLandscapeLeft</string><string>UIInterfaceOrientationLandscapeRight</string></array>
              <key>UISupportedInterfaceOrientations~ipad</key><array><string>UIInterfaceOrientationPortrait</string><string>UIInterfaceOrientationPortraitUpsideDown</string><string>UIInterfaceOrientationLandscapeLeft</string><string>UIInterfaceOrientationLandscapeRight</string></array>
            </dict>
            </plist>

            """;
    }
}
