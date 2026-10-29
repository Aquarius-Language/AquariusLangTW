using AquariusLang.ast;
using AquariusLang.lexer;
using AquariusLang.parser;
using AquariusTests.Wasm;

namespace AquariusTests.Compiler;

/// <summary><c>非同步</c> / <c>async</c> functions, <c>等待</c> / <c>await</c> and the Promise / 承諾 module
/// (runtime/host/promise.c): the contextual keywords, the compile-time rule for await, results, ordering, errors and
/// unhandled rejections on the native runtime.</summary>
public class AsyncTest
{
    private static (AbstractSyntaxTree Tree, IReadOnlyList<ParseError> Errors) Parse(string source)
    {
        var parser = Parser.NewInstance(Lexer.NewInstance(source));
        var tree = parser.ParseAST();
        return (tree, parser.Diagnostics);
    }

    [Theory]
    [InlineData("var f = async function(x) { return await x; };", "(var f = async function(x) (return (await x)))")]
    [InlineData("變數 f = 非同步 函式(x) { 回傳 等待 x; };", "(變數 f = async 函式(x) (回傳 (await x)))")]
    [InlineData("變數 f = 非同步函式(x) { 回傳 等待 x; };", "(變數 f = async 非同步函式(x) (回傳 (await x)))")]
    [InlineData("await p;", "(await p)")]
    [InlineData("等待 取得(1).值 + 1;", "((await (取得(1) . 值)) + 1)")]
    [InlineData("var g = async function() { return await (a + b); };", "(var g = async function() (return (await (a + b))))")]
    public void KeywordsStartAsyncFunctionsAndAwaits(string source, string expected)
    {
        var (tree, errors) = Parse(source);
        Assert.Empty(errors);
        Assert.Equal(expected, tree.String().Trim());
    }

    [Theory]
    // The words stay names where no async function or await starts: declarations, calls, members, assignments.
    [InlineData("var 等待 = function(x) { x; }; 等待(1);")]
    [InlineData("var async = 1; async + 1;")]
    [InlineData("變數 非同步 = 2; 非同步 * 3;")]
    [InlineData("執行緒.等待(工作); 用戶端.等待(50);")]
    [InlineData("var f = async function() { 執行緒.等待(工作, 0); };")]
    [InlineData("await = 3; [await];")]
    public void TheWordsRemainNamesElsewhere(string source)
    {
        var (tree, errors) = Parse(source);
        Assert.Empty(errors);
        Assert.DoesNotContain(Calls(tree), call => call is AwaitExpression || (call is AsyncFunctionExpression && !source.Contains("async function", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("var f = function() { await Promise.Delay(1); };")]
    [InlineData("var f = function() { 等待 承諾.延遲(1); };")]
    [InlineData("var f = async function() { var g = function() { await x; }; };")]
    public void AwaitOutsideAnAsyncFunctionIsAnError(string source)
    {
        var (_, errors) = Parse(source);
        Assert.Contains(errors, e => e.Message.Contains("await is only valid", StringComparison.Ordinal));
    }

    [Fact]
    public void AsyncFunctionsAreTheirLiteralsWrapped()
    {
        var (tree, _) = Parse("var a, b = async function(n: int): int { return n; };");
        var let = Assert.IsType<LetStatement>(tree.Statements[0]);
        var async = Assert.IsType<AsyncFunctionExpression>(let.Value);
        Assert.True(async.Literal.IsAsync);
        Assert.Equal("b", let.Alias!.Value);
    }

    private static IEnumerable<CallExpression> Calls(INode? node)
    {
        switch (node) {
            case AbstractSyntaxTree t: foreach (var s in t.Statements) foreach (var c in Calls(s)) yield return c; break;
            case BlockStatement b: foreach (var s in b.Statements ?? []) foreach (var c in Calls(s)) yield return c; break;
            case ExpressionStatement e: foreach (var c in Calls(e.Expression)) yield return c; break;
            case LetStatement l: foreach (var c in Calls(l.Value)) yield return c; break;
            case FunctionLiteral f: foreach (var c in Calls(f.Body)) yield return c; break;
            case InfixExpression i: foreach (var c in Calls(i.Left).Concat(Calls(i.Right))) yield return c; break;
            case CallExpression call:
                yield return call;
                foreach (var c in Calls(call.Function)) yield return c;
                foreach (var a in call.Arguments ?? []) foreach (var c in Calls(a)) yield return c;
                break;
        }
    }

    [RuntimeFact]
    public void AwaitGivesResultsInBothLanguages()
    {
        Assert.Equal("promise pending\nfast\nslow\n42\n42\n真 假\n", WasmTestHost.Output("""
            var delayed = async function(ms, value) { await Promise.Delay(ms); return value; };
            var slow = delayed(40, "slow");
            var fast = delayed(5, "fast");
            print(typeOf(slow), slow.State);
            print(await fast);
            print(await slow);
            變數 加倍 = 非同步 函式(n) { 回傳 (等待 承諾.延遲(1, n)) * 2; };
            印出(等待 加倍(21));
            print(await 42);
            print(Promise.IsPromise(加倍(1)), 承諾.是承諾(3));
            """));
    }

    [RuntimeFact]
    public void TheBodyRunsUntilItFirstWaits()
    {
        Assert.Equal("[in, after call, resumed, awaited]\n", WasmTestHost.Output("""
            var Array = import("Array");
            var log = [];
            var f = async function() { Array.Append(log, "in"); await Promise.Delay(1); Array.Append(log, "resumed"); };
            var p = f();
            Array.Append(log, "after call");
            await p;
            Array.Append(log, "awaited");
            print(log);
            """));
    }

    [RuntimeFact]
    public void TasksInterleaveWhileTheyWait()
    {
        // Distinct delays make the order independent of timer precision.
        Assert.Equal("[a0, b0, a1, b1, a2, b2]\n[a, b]\n", WasmTestHost.Output("""
            var Array = import("Array"); var String = import("String");
            var log = [];
            var worker = async function(name, offset) {
                await Promise.Delay(offset);
                for (var i = 0; i < 3; i++) { Array.Append(log, name + String.From(i)); await Promise.Delay(20); }
                return name;
            };
            var names = await Promise.All([worker("a", 0), worker("b", 10)]);
            print(log);
            print(names);
            """));
    }

    [RuntimeFact]
    public void CombinatorsFollowJavaScript()
    {
        Assert.Equal(string.Join("\n",
            "[1, 2, 3]",
            "[{status: rejected, reason: boom}, {status: fulfilled, value: ok}]",
            "b",
            "y",
            "所有承諾都被拒絕 / All promises were rejected.",
            "[]",
            "[{status: rejected, reason: 逾時 / Timeout}]",
            "fast enough",
            "9", ""), WasmTestHost.Output("""
            var delayed = async function(ms, value) { await Promise.Delay(ms); return value; };
            var fail = async function() { await Promise.Delay(1); return Promise.Reject("boom"); };
            print(await Promise.All([delayed(5, 1), delayed(1, 2), 3]));
            print(await Promise.AllSettled([fail(), delayed(1, "ok")]));
            print(await Promise.Race([delayed(40, "a"), delayed(2, "b")]));
            print(await Promise.Any([Promise.Reject("x"), delayed(3, "y")]));
            print(await Promise.Any([]).Catch(function(m) { return m; }));
            print(await Promise.All([]));
            print(await Promise.AllSettled([Promise.Timeout(Promise.Delay(500), 10)]));
            print(await Promise.Timeout(delayed(1, "fast enough"), 500));
            print(await Promise.Resolve(Promise.Resolve(9)));
            """));
    }

    [RuntimeFact]
    public void ThenCatchFinallyAndNew()
    {
        Assert.Equal("60\ncaught: boom\n[finally, 7]\nfrom executor\nnope\nthrown: Type mismatch: NULL + INTEGER\nrejected boom\n", WasmTestHost.Output("""
            var Array = import("Array");
            var delayed = async function(ms, value) { await Promise.Delay(ms); return value; };
            print(await delayed(1, 5).Then(function(v) { return v + 1; }).Then(function(v) { return delayed(1, v * 10); }));
            print(await Promise.Reject("boom").Catch(function(m) { return "caught: " + m; }));
            var log = [];
            var v = await Promise.Delay(1, 7).Finally(function() { Array.Append(log, "finally"); });
            Array.Append(log, v);
            print(log);
            print(await 承諾.建立(函式(解決, 拒絕) { 解決("from executor"); }));
            print(await Promise.New(function(resolve, reject) { reject("nope"); }).Catch(function(m) { m; }));
            print(await Promise.New(function(resolve, reject) { [1][3] + 1; }).Then(null, function(m) { "thrown: " + m; }));
            var p = Promise.Reject("boom");
            p.Catch(function() { 0; });
            print(p.State, p.Reason);
            """));
    }

    [RuntimeFact]
    public void ClosuresRecursionStructsAndTypes()
    {
        Assert.Equal("144\n[0, 1, 2]\nstruct method\n55\n[aa, bb]\n", WasmTestHost.Output("""
            var fib = async function(n) { if (n < 2) { return n; } return (await fib(n - 1)) + (await fib(n - 2)); };
            print(await fib(12));
            var makers = [];
            for (var i = 0; i < 3; i++) { import("Array").Append(makers, async function() { await Promise.Delay(1); return i; }); }
            print(await Promise.All([makers[0](), makers[1](), makers[2]()]));
            struct Box { get }
            var box = Box(async function() { return "struct method"; });
            print(await box.get());
            var sum = async function(n: int): int { var total: int = 0; for (var k: int = 1; k <= n; k++) { total += k; } return total; };
            var total: int = await sum(10);
            print(total);
            var twice = async function(s: string) { return s + s; };
            print(await Promise.All(import("Array").Map(["a", "b"], twice)));
            """));
    }

    [RuntimeFact]
    public void ImmediatelyCalledAsyncFunctions()
    {
        Assert.Equal("inner 3\n", WasmTestHost.Output("""
            var r = await (async function(x) { await Promise.Delay(1); return "inner " + import("String").From(x); })(3);
            print(r);
            """));
    }

    [RuntimeFact]
    public void ARejectionStopsTheAwaitingScript()
    {
        var result = WasmTestHost.Run("""
            var bad = async function() { await Promise.Delay(1); return [1][9] + 1; };
            print("before");
            await bad();
            print("never");
            """);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("before\nERROR: Type mismatch: NULL + INTEGER\n", result.Output);
    }

    [RuntimeFact]
    public void UnhandledRejectionsAreReportedWhenTheProgramEnds()
    {
        var result = WasmTestHost.Run("""
            var bad = async function() { await Promise.Delay(1); return Promise.Reject("lost"); };
            bad();
            var handled = Promise.Reject("seen");
            await handled.Catch(function() { 0; });
            print("main done");
            """);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("main done\n未處理的承諾拒絕 / Unhandled promise rejection: lost\n", result.Output);
    }

    [RuntimeFact]
    public void AwaitCannotWaitInsideASynchronousCall()
    {
        // An imported module's top level runs to completion inside import(), so it cannot wait for a pending promise.
        var result = WasmTestHost.RunModules(new Dictionary<string, string> {
            ["main.aqua"] = "var m = import(\"slow.aqua\"); print(\"unreached\");",
            ["slow.aqua"] = "var x = await Promise.Delay(5, 1);",
        }, "main.aqua");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cannot wait for a pending promise", result.Output, StringComparison.Ordinal);
    }

    [RuntimeFact]
    public void TheProgramEndsWhenOnlyAwaitsOfUnsettledPromisesRemain()
    {
        var result = WasmTestHost.Run("""
            var never = Promise.New(function(resolve, reject) { 0; });
            var waiter = async function() { await never; print("unreachable"); };
            waiter();
            print("main done");
            """);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("main done\n", result.Output);
    }

    [RuntimeFact]
    public void ArityOfCountsDeclaredParameters()
    {
        Assert.Equal("[0, 2, 1, -1, 3]\n", WasmTestHost.Output("""
            print([arityOf(function() { 0; }), 參數個數(function(a, b) { a; }), arityOf(async function(x) { x; }),
                arityOf(print), arityOf(非同步 函式(a, b, c) { 0; })]);
            """));
    }

    [RuntimeFact]
    public void ExistingNamesAndThreadsKeepWorking()
    {
        Assert.Equal("8\n3\n6\n2\n", WasmTestHost.Output("""
            var 等待 = function(x) { return x * 2; };
            print(等待(4));
            var t = Thread.Start(function(n) { return n; }, [3]);
            Thread.Wait(t); 執行緒.等待(t);
            print(Thread.Result(t));
            Thread.Close(t);
            var async = 5; print(async + 1);
            var g = async function() { var wait = 1; return wait + 1; };
            print(await g());
            """));
    }
}
