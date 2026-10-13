using System;
using System.Collections.Generic;
using AquariusLang.Application;

namespace AquariusLang.Input;

public sealed record AndroidInsets(double Left, double Top, double Right, double Bottom);
public sealed record AndroidTouch(long Id, string Phase, double X, double Y, double Pressure,
    string Tool, double Timestamp, IReadOnlyList<PointerSample> History);
public sealed record AndroidInputSnapshot(IReadOnlyList<AndroidTouch> Touches, AndroidInsets Insets,
    bool KeyboardVisible, bool BackRequested, string Orientation);
/// <summary>Android input in density-independent logical units and monotonic seconds; hosts snapshot once per frame.</summary>
public interface IAndroidInputBackend
{
    bool IsSupported { get; }
    AndroidInputSnapshot Poll();
    void ShowKeyboard(bool visible);
    void Vibrate(int milliseconds);
    void ResolveBack(CloseDecision decision);
}
public static class AndroidInputCatalog
{
    public static IReadOnlyList<runtime.LibraryFunction> Functions { get; } = Array.AsReadOnly(new[] {
        new runtime.LibraryFunction("AndroidInput", "IsSupported", "是否支援", 0, 0),
        new("AndroidInput", "Poll", "讀取輸入", 0, 0),
        new("AndroidInput", "ShowKeyboard", "顯示鍵盤", 1, 1),
        new("AndroidInput", "Vibrate", "震動", 1, 1),
        new("AndroidInput", "ResolveBack", "處理返回", 1, 1)
    });
}
