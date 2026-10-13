using System;
using System.Collections.Generic;
using AquariusLang.runtime;

namespace AquariusLang.Camera;

public static class CameraCatalog
{
    public static IReadOnlyList<LibraryFunction> Functions { get; } = Array.AsReadOnly(new[] {
        new LibraryFunction("Camera", "IsSupported", "是否支援", 0, 0),
        new("Camera", "Enumerate", "列出裝置", 0, 0),
        new("Camera", "Open", "開啟", 4, 4, "Camera.Session"),
        new("Camera.Session", "IsOpen", "是否開啟", 0, 0),
        new("Camera.Session", "Capture", "擷取影格", 0, 0, "Camera.Frame"),
        new("Camera.Session", "Dispose", "釋放", 0, 0),
        new("Camera.Frame", "GetPixels", "取得像素", 0, 0)
    });
}
