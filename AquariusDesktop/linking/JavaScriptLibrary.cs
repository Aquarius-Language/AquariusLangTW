using System.Collections.Concurrent;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using AquariusLang.Linking;

namespace AquariusLang.Desktop.Linking;

/// <summary>Bundled ES modules use the same byte ABI as browser libraries. No engine-specific library adapters.</summary>
internal sealed class JavaScriptLibrary : IDynamicLibrary {
    private static readonly BlockingCollection<Action> work = new();
    private static readonly Thread worker = Start();
    private static readonly Lazy<V8Runtime> runtime = new(() => new V8Runtime("Aquarius libraries"));
    private readonly V8ScriptEngine engine;
    private bool disposed;
    private static Thread Start() {
        var thread = new Thread(() => { foreach (var action in work.GetConsumingEnumerable()) action(); }) { IsBackground = true, Name = "Aquarius libraries" };
        thread.Start(); return thread;
    }
    private static T OnThread<T>(Func<T> action) {
        if (Thread.CurrentThread == worker) return action();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Add(() => { try { result.SetResult(action()); } catch (Exception e) { result.SetException(e); } });
        return result.Task.GetAwaiter().GetResult();
    }
    private static object Await(object value) => value is Task<object> task ? task.GetAwaiter().GetResult() : value;
    internal JavaScriptLibrary(string path) {
        engine = OnThread(() => {
            var e = runtime.Value.CreateScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
            try {
                e.AddHostObject("decodeBase64", new Func<string, string>(s => System.Text.Encoding.Latin1.GetString(Convert.FromBase64String(s))));
                e.Execute("""
                    globalThis.console={log(){},warn(){},error(){}};
                    globalThis.performance={now:()=>Date.now()};
                    globalThis.atob=s=>decodeBase64(s);
                    globalThis.TextEncoder=class {encode(s){return Uint8Array.from(unescape(encodeURIComponent(s)),c=>c.charCodeAt(0));}};
                    globalThis.TextDecoder=class {decode(b){let s='';for(const c of b)s+=String.fromCharCode(c);return decodeURIComponent(escape(s));}};
                    globalThis.URL=class {constructor(p){this.href=p;}toString(){return this.href;}};
                    """);
                e.DocumentSettings.AddSystemDocument("aquarius-library", ModuleCategory.Standard, File.ReadAllText(path));
                e.Execute(new DocumentInfo("aquarius-library-loader") { Category = ModuleCategory.Standard }, "import * as library from 'aquarius-library';globalThis.aquaLibraryNamespace=library;");
                e.Execute("if(aquaLibraryNamespace.aqua_library_abi()!==1)throw Error('Unsupported library ABI');globalThis.aquaLibrary=aquaLibraryNamespace.aqua_create_library();if(!aquaLibrary||typeof aquaLibrary!=='object'||typeof aquaLibrary.then==='function')throw Error('Invalid library factory');");
                e.Execute("globalThis.aquaInvoke=async(n,b)=>{const r=await aquaLibrary[n](b);if(!(r instanceof Uint8Array))throw Error('Library must return Uint8Array');return r;};");
                return e;
            } catch { e.Dispose(); throw; }
        });
    }
    private void Live() { if (disposed) throw new ObjectDisposedException(nameof(JavaScriptLibrary)); }
    public bool HasSymbol(string name) { DynamicLibraryAbi.Symbol(name); return OnThread(() => { Live(); engine.Script.aquaSymbol = name; return (bool)engine.Evaluate("Object.hasOwn(aquaLibrary,aquaSymbol)&&typeof aquaLibrary[aquaSymbol]==='function'"); }); }
    public byte[] Invoke(string name, byte[] input) {
        DynamicLibraryAbi.Symbol(name); DynamicLibraryAbi.Buffer(input.Length);
        return OnThread(() => {
            Live(); if (!HasSymbol(name)) throw new EntryPointNotFoundException(name);
            using var bytes = (ITypedArray<byte>)engine.Evaluate($"new Uint8Array({input.Length})");
            bytes.Write(input, 0, (ulong)input.Length, 0);
            using var output = (ITypedArray<byte>)Await(engine.Script.aquaInvoke(name, bytes));
            var result = output.ToArray(); DynamicLibraryAbi.Buffer(result.Length); return result;
        });
    }
    public void Dispose() => OnThread(() => { if (!disposed) { disposed = true; try { Await(engine.Evaluate("Promise.resolve(aquaLibrary.aqua_dispose?.())")); } finally { engine.Dispose(); } } return true; });
}
