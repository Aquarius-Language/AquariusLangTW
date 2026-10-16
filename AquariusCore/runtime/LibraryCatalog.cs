using System;
using System.Collections.Generic;
using System.Linq;

namespace AquariusLang.runtime;

/// <summary>Portable metadata for host libraries; querying this never loads native code.</summary>
public sealed record LibraryFunction(string Library, string EnglishName, string TraditionalChineseName,
    int MinimumArguments, int MaximumArguments, string? ReturnLibrary = null, string? RequiredFeature = null) {
    /// <summary>單一繁體中文名稱不重複列為別名；既有雙語介面仍保留兩個名稱。</summary>
    public IReadOnlyList<string> Names => FunctionRegistration.Names(TraditionalChineseName,
        EnglishName == TraditionalChineseName ? null : EnglishName);
    public string Detail => SignatureDetail + (RequiredFeature == null ? "" : $" Requires / 需要能力：{RequiredFeature}.");
    private string SignatureDetail => EnglishName == TraditionalChineseName
        ? $"{Library}.{TraditionalChineseName} — 參數數量：" + (MinimumArguments == MaximumArguments ? $"{MinimumArguments}。" : $"{MinimumArguments} 至 {MaximumArguments}。")
        : $"{Library}.{TraditionalChineseName} / {EnglishName} — " +
        (MinimumArguments == MaximumArguments ? $"{MinimumArguments} arguments." : $"{MinimumArguments}..{MaximumArguments} arguments.");
    public bool HasName(string name) => name == EnglishName || name == TraditionalChineseName;
}

/// <summary>Read-only fields also describe data-only return types, without inventing callable methods.</summary>
public sealed record LibraryProperty(string Library,string EnglishName,string TraditionalChineseName,string Type,string? ReturnLibrary=null) {
    public IReadOnlyList<string> Names => FunctionRegistration.Names(TraditionalChineseName,EnglishName);
    public bool HasName(string name)=>name==EnglishName||name==TraditionalChineseName;
    public string Detail=>$"{Library}.{TraditionalChineseName} / {EnglishName}: {Type}";
}

public static partial class LibraryCatalog {
    public static IReadOnlyList<LibraryProperty> Properties { get; } = AquariusLang.Network.NetworkCatalog.Properties.Concat(AquariusLang.Terminal.TerminalCatalog.Properties).ToArray();
    public static IEnumerable<LibraryProperty> PropertiesFor(string library)=>Properties.Where(f=>f.Library==CanonicalLibrary(library));
    public static LibraryProperty? FindProperty(string library,string name)=>PropertiesFor(library).FirstOrDefault(f=>f.HasName(name));
    public static bool IsKnownType(string library)=>library=="Thread.Handle"||Members(library).Any()||PropertiesFor(library).Any();
    public static IReadOnlyList<LibraryFunction> Functions { get; }
    private static readonly IReadOnlyDictionary<string, string> chineseNames;
    static LibraryCatalog() {
        Functions = Array.AsReadOnly(definitions.Concat(AquariusLang.Threading.ThreadCatalog.Functions).Concat(meshFunctions).Concat(ArrayLibrary.Functions).Concat(AquariusLang.Network.NetworkCatalog.Functions).Concat(AquariusLang.Application.ApplicationCatalog.Functions).Concat(AquariusLang.Audio.AudioCatalog.Functions).Concat(AquariusLang.Webcam.WebcamCatalog.Functions).Concat(AquariusLang.Camera.CameraCatalog.Functions).Concat(AquariusLang.Input.AndroidInputCatalog.Functions).Concat(AquariusLang.Onnx.OnnxCatalog.Functions).Concat(AquariusLang.Terminal.TerminalCatalog.Functions).Concat(AquariusLang.Linking.DynamicLibraryCatalog.Functions).Concat(AquariusLang.MediaPipe.PoseCatalog.Functions).Concat(AquariusLang.Graphics.PortableGraphicsLibraries.Functions).ToArray());
        chineseNames = Functions.GroupBy(f => f.EnglishName)
            .ToDictionary(g => g.Key, g => g.First().TraditionalChineseName, StringComparer.Ordinal);
    }
    public static IReadOnlyList<LibraryFunction> GlobalFunctions { get; } = Array.AsReadOnly(new[] {
        new LibraryFunction("", "len", "長度", 1, 1), new("", "last", "最後一個", 1, 1),
        new("", "rest", "其餘", 1, 1), new("", "push", "加入", 2, 2),
        new("", "floodFill", "區域填色", 6, 6),
        new("", "print", "印出", 0, int.MaxValue), new("", "import", "匯入", 1, 1),
        new("", "currentTime", "目前時間", 0, 0),
        new("", "currentDate", "目前日期", 0, 0),
        new("", "isOSWindows", "是Windows", 0, 0), new("", "isOSLinux", "是Linux", 0, 0),
        new("", "isOSMacOS", "是MacOS", 0, 0), new("", "execFile", "執行檔案", 2, 2),
        new("", "isOSAndroid", "是Android", 0, 0), new("", "isWeb", "是網頁", 0, 0)
    });
    public static string? TraditionalChinese(string englishName) => chineseNames.TryGetValue(englishName, out var name) ? name : null;
    public static string CanonicalLibrary(string name) => name switch {
        "執行緒" => "Thread", "網路" => "Network", "繪圖" => "Processing", "序列化" => "Serialization", "動態函式庫" => "DynamicLibrary", "GPU數學" => "GPUMath", "陣列" => "Array", "wgpu" => "WGPU", "終端機" => "Terminal", "音訊" => "Audio", "模型" => "Models", "推論引擎" or "ONNXRuntime" => "ONNX", "姿態追蹤" => "MediaPipe", "相機" => "Camera", "Android輸入" => "AndroidInput", "Webcam" or "網路攝影機" => "攝影機", _ => name
    };
    public static IEnumerable<LibraryFunction> Members(string library) => Functions.Where(f => f.Library == CanonicalLibrary(library));
    public static LibraryFunction? Find(string library, string name) => Members(library).FirstOrDefault(f => f.HasName(name));
}
