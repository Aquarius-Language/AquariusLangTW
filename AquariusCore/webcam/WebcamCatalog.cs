using System.Collections.Generic;
using AquariusLang.runtime;

namespace AquariusLang.Webcam;

/// <summary>攝影機僅提供繁體中文公開名稱，供編譯器、編輯器及各宿主共用。</summary>
public static class WebcamCatalog {
    public static IReadOnlyList<LibraryFunction> Functions { get; } = System.Array.AsReadOnly(new[] {
        new LibraryFunction("攝影機", "是否支援", "是否支援", 0, 0),
        new("攝影機", "列出裝置", "列出裝置", 0, 0),
        new("攝影機", "開啟", "開啟", 4, 4, "攝影機.工作階段"),
        new("攝影機.工作階段", "是否開啟", "是否開啟", 0, 0),
        new("攝影機.工作階段", "擷取影格", "擷取影格", 0, 0, "攝影機.影格"),
        new("攝影機.工作階段", "釋放", "釋放", 0, 0),
        new("攝影機.影格", "取得像素", "取得像素", 0, 0)
    });
}
