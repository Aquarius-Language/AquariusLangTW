using AquariusLang.Application;

namespace AquariusREPL.Application;

public sealed class DesktopApplicationHost : IApplicationHost, IHostServices {
    public DesktopFiles FileProvider { get; } = new();
    public IFileProvider Files => FileProvider;
    public IImageCodec Images { get; } = new DesktopImages();
    public IClipboard Clipboard { get; }
    public IApplicationStorage Storage { get; }
    public IFontService Fonts { get; } = new DesktopFonts();
    public IHostServices Services => this;
    public IReadOnlyList<FileResource> LaunchFiles { get; }
    public IReadOnlyDictionary<string, Capability> Capabilities { get; }
    internal Action<IReadOnlyList<AccessibilityNode>>? AccessibilityUpdater { get; set; }
    public DesktopApplicationHost(string applicationId = "AquariusLang", IEnumerable<string>? launchFiles = null, string? storageRoot = null) {
        Clipboard = new DesktopClipboard(Images); Storage = new DesktopStorage(FileProvider, applicationId, storageRoot);
        LaunchFiles = (launchFiles ?? Array.Empty<string>()).Select(FileProvider.Resolve).ToArray(); bool windows = OperatingSystem.IsWindows();
        Capabilities = new Dictionary<string, Capability> { ["files"] = new(true), ["codecs"] = new(true), ["persistentStorage"] = new(true),
            ["pickers"] = new(windows, windows ? null : "Native picker adapter currently targets Windows."), ["printing"] = new(windows), ["imageAcquisition"] = new(windows, "Image acquisition selects an existing image; scanner/camera hardware adapter is not installed."),
            ["fontEnumeration"] = new(windows), ["accessibility"] = new(windows, "Read-only MSAA mirror; requires an active graphics window."), ["fileAssociations"] = new(windows, "Registration is explicit and per-user."), ["activation"] = new(true, "Launch resources are supplied by the embedding host.") };
    }
    public ValueTask<IReadOnlyList<FileResource>> OpenFiles(PickerOptions options, CancellationToken cancellation = default) => ValueTask.FromResult<IReadOnlyList<FileResource>>(DesktopDialogs.FileDialog(options, false, cancellation).Select(FileProvider.Resolve).ToArray());
    public ValueTask<FileResource?> SaveFile(PickerOptions options, CancellationToken cancellation = default) { var path = DesktopDialogs.FileDialog(options, true, cancellation).FirstOrDefault(); return ValueTask.FromResult(path == null ? null : FileProvider.Resolve(path)); }
    public ValueTask<FileResource?> PickDirectory(CancellationToken cancellation = default) { var path = DesktopDialogs.DirectoryDialog(cancellation); return ValueTask.FromResult(path == null ? null : FileProvider.Resolve(path)); }
    public ValueTask Print(PixelImage image, CancellationToken cancellation = default) { DesktopPrinting.Print(image, cancellation); return ValueTask.CompletedTask; }
    public async ValueTask<PixelImage?> AcquireImage(CancellationToken cancellation = default) {
        var selection = await OpenFiles(new("Select image", Filters: new[] { new FileFilter("Images", new[] { "png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff" }) }), cancellation);
        return selection.Count == 0 ? null : await Images.Decode(await new FileService(Files).Read(selection[0], cancellation), 0, new(), cancellation);
    }
    public void UpdateAccessibility(IReadOnlyList<AccessibilityNode> nodes) {
        if (AccessibilityUpdater == null) throw new ApplicationFailure(FailureKind.Unavailable, "No active graphics window accessibility adapter."); AccessibilityUpdater(nodes);
    }
    public void RegisterFileAssociation(FileAssociation association) => DesktopActivation.Register(association);
}
