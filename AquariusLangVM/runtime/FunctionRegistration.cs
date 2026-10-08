using System;
using System.Collections.Generic;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.token;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.runtime;

/// <summary>Registers one callable under either or both public names, without copying it.</summary>
public static class FunctionRegistration {
    public static string[] Names(string? traditionalChineseName = null, string? englishName = null) {
        var names = new List<string>();
        foreach (string? name in new[] { traditionalChineseName, englishName }) {
            if (name == null) continue;
            var lexer = Lexer.NewInstance(name);
            var token = lexer.NextToken();
            if (token.Type != TokenType.IDENT || token.Literal != name || lexer.NextToken().Type != TokenType.EOF || lexer.Errors.Count != 0)
                throw new ArgumentException("A function name must be a valid non-keyword identifier.");
            if (names.Contains(name)) throw new ArgumentException("Function names must be distinct.");
            names.Add(name);
        }
        if (names.Count == 0) throw new ArgumentException("At least one function name is required.");
        return names.ToArray();
    }

    public static void Define(AquaEnvironment environment, IObject function, string? traditionalChineseName = null, string? englishName = null) {
        if (environment == null) throw new ArgumentNullException(nameof(environment));
        if (!(function is BuiltinObj) && !(function is FunctionObj)) throw new ArgumentException("Expected a function.", nameof(function));
        var names = Names(traditionalChineseName, englishName);
        foreach (string name in names)
            if (environment.Owns(name)) throw new ArgumentException($"Function name already registered: {name}");
        foreach (string name in names) environment.Create(name, function);
    }

    public static void Define(IDictionary<string, BuiltinObj> functions, BuiltinObj function, string? traditionalChineseName = null, string? englishName = null) {
        if (functions == null) throw new ArgumentNullException(nameof(functions));
        if (function == null) throw new ArgumentNullException(nameof(function));
        var names = Names(traditionalChineseName, englishName);
        foreach (string name in names)
            if (functions.ContainsKey(name)) throw new ArgumentException($"Function name already registered: {name}");
        foreach (string name in names) functions.Add(name, function);
    }

    /// <summary>Rebind an existing alias group during a library reload, after checking it is one function.</summary>
    public static void Replace(AquaEnvironment environment, IObject function, string? traditionalChineseName = null, string? englishName = null) {
        if (environment == null) throw new ArgumentNullException(nameof(environment));
        if (!(function is BuiltinObj) && !(function is FunctionObj)) throw new ArgumentException("Expected a function.", nameof(function));
        var names = Names(traditionalChineseName, englishName);
        var previous = environment.GetOwned(names[0]);
        if (!(previous is BuiltinObj) && !(previous is FunctionObj)) throw new ArgumentException("Expected an existing function alias group.");
        foreach (string name in names)
            if (!ReferenceEquals(previous, environment.GetOwned(name))) throw new ArgumentException("Existing aliases must refer to the same function.");
        foreach (string name in names) environment.Create(name, function);
    }
}
