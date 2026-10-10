using AquariusLang.Object;

namespace AquariusLang.runtime;

/// <summary>Shared primitive values used by the runtime and desktop libraries.</summary>
public static class RepeatedPrimitives {
    public static readonly NullObj NULL = new();
    public static readonly BooleanObj TRUE = new(true);
    public static readonly BooleanObj FALSE = new(false);
    public static readonly BreakObj BREAK = new();
}
