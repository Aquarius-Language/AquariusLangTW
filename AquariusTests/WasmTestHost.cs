using System.Runtime.CompilerServices;

namespace AquariusTests;

internal static class WasmTestHost {
    [ModuleInitializer]
    internal static void Initialize() => AquariusLang.Desktop.runtime.WasmtimeEngine.Register();
}
