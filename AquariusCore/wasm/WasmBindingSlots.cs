using System;
using System.Collections.Generic;
using System.Linq;
using AquariusLang.Compiler;

namespace AquariusLang.Wasm;

/// <summary>Candidate owned-scope slots. The emitter guards count and name at each
/// access; conditional declarations, loop resets and host edits use normal lookup.</summary>
internal static class WasmBindingSlots
{
    internal static int[] Resolve(LoweredProgram body, string[] parameters)
    {
        var slots = Enumerable.Repeat(-1, body.Code.Length).ToArray();
        var scopes = new Stack<Dictionary<string, int>>();
        scopes.Push(new(StringComparer.Ordinal));
        void Declare(string name) { var scope = scopes.Peek(); if (!scope.ContainsKey(name)) scope.Add(name, scope.Count); }
        foreach (var parameter in parameters) Declare(parameter);
        for (int i = 0; i < body.Code.Length; i++)
        {
            var op = body.Code[i];
            if (op.Code == IrOperation.EnterLoop) { scopes.Push(new(StringComparer.Ordinal)); continue; }
            if (op.Code == IrOperation.LeaveLoop) { scopes.Pop(); continue; }
            string? name = op.Code switch {
                IrOperation.Load or IrOperation.Assign or IrOperation.IncrementPrefix or IrOperation.IncrementPostfix or IrOperation.Declare => (string)body.Pool[op.Operand],
                IrOperation.CompoundAssign => ((Assignment)body.Pool[op.Operand]).Name,
                _ => null
            };
            if (name == null) continue;
            if (op.Code == IrOperation.Declare) Declare(name);
            else if (scopes.Peek().TryGetValue(name, out int slot)) slots[i] = slot;
        }
        return slots;
    }
}
