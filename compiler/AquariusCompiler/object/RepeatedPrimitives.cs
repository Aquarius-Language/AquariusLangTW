namespace AquariusLang.Object;

/// <summary>Shared constant values of the compiler's IR.</summary>
public static class RepeatedPrimitives {
    public static readonly NullObj NULL = new();
    public static readonly BooleanObj TRUE = new(true);
    public static readonly BooleanObj FALSE = new(false);
    public static readonly BreakObj BREAK = new();
}
