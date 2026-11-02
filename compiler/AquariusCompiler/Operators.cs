namespace AquariusLang.Compiler;

/// <summary>Operator spellings the front end shares: assignments (<c>=</c> and the compound forms) and the integer
/// bitwise operators.</summary>
internal static class Operators
{
    internal static bool IsAssignment(string op) =>
        op is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=" or ">>>=";
    /// <summary>The binary operator of a compound assignment: <c>&lt;&lt;=</c> gives <c>&lt;&lt;</c>.</summary>
    internal static string Compound(string op) => op[..^1];
    /// <summary>Operators of integers only: and, or, exclusive or, left shift, arithmetic and logical right shift.</summary>
    internal static bool IsBitwise(string op) => op is "&" or "|" or "^" or "<<" or ">>" or ">>>";
}
