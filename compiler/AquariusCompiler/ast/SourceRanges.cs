using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AquariusLang.token;

namespace AquariusLang.ast {

/// <summary>Where a syntax node is in its source: UTF-16 offset of its first character and its length, as the lexer counts
/// them (<see cref="Token.Start"/>). The parser records the whole extent of every expression and statement it builds
/// (<c>a.b(c)</c> from <c>a</c> to the closing parenthesis); runtime errors underline these ranges.</summary>
public readonly record struct SourceRange(int Start, int Length) {
    public int End => Start + Length;
    public static readonly SourceRange None = new(-1, 0);
    public bool IsKnown => Start >= 0;
    public static SourceRange Of(Token token) => new(token.Start, token.Length);
    public static SourceRange Between(int start, int end) => new(start, Math.Max(0, end - start));
}

/// <summary>The recorded ranges of syntax nodes, kept beside the tree so the node classes stay as they are.</summary>
public static class SourceRanges {
    private sealed class Box { public SourceRange Range; }
    private static readonly ConditionalWeakTable<INode, Box> ranges = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> tokens = new();

    internal static void Set(INode? node, int start, int end) {
        if (node == null || start < 0 || end < start) return;
        var box = ranges.GetValue(node, _ => new Box { Range = SourceRange.None });
        // A node seen again by an enclosing rule keeps the widest extent.
        if (box.Range.IsKnown && box.Range.Start <= start && box.Range.End >= end) return;
        box.Range = SourceRange.Between(start, end);
    }

    /// <summary>The node's recorded range, else its own token's (an operator, a keyword), else <see cref="SourceRange.None"/>.</summary>
    public static SourceRange Of(INode? node) {
        if (node == null) return SourceRange.None;
        if (ranges.TryGetValue(node, out var box) && box.Range.IsKnown) return box.Range;
        return TokenOf(node);
    }

    /// <summary>The range of the node's own token only.</summary>
    public static SourceRange TokenOf(INode? node) {
        if (node == null) return SourceRange.None;
        var property = tokens.GetOrAdd(node.GetType(), t => t.GetProperty("Token", BindingFlags.Public | BindingFlags.Instance) is { PropertyType: var p } info && p == typeof(Token) ? info : null);
        if (property?.GetValue(node) is Token token && token.Length > 0) return SourceRange.Of(token);
        return SourceRange.None;
    }
}
}
