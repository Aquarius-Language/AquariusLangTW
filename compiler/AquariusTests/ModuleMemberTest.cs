namespace AquariusTests;

/// <summary>Members of imported modules: argument and body scopes, constants, missing members, function values and
/// re-reading members a module changes. Each test is an application of several .aqua modules.</summary>
public class ModuleMemberTest {
    private static RunResult Run(string module, string main) =>
        WasmTestHost.RunModules(new Dictionary<string, string> { ["模組.aqua"] = module, ["main.aqua"] = main }, "main.aqua");

    [RuntimeFact]
    public void ModuleCallsEvaluateArgumentsInCallerScopeAndFunctionsInModuleScope() {
        var run = Run("變數 偏移 = 10; 變數 加法 = 函式(值) { 回傳 值 + 偏移; };",
            "變數 模組 = 匯入(\"模組.aqua\"); 變數 偏移 = 100; 變數 值 = 7; 變數 結果 = 模組.加法(值 + 偏移); 印出(型別(結果), 結果);");
        Assert.Equal("integer 117\n", run.Succeeded().Output);
    }

    [RuntimeFact]
    public void MembersExposeConstantsAndMissingMembersReturnLanguageErrors() {
        Assert.Equal("integer 42\n", Run("變數 常數 = 42;", "變數 模組 = 匯入(\"模組.aqua\"); 印出(型別(模組.常數), 模組.常數);").Succeeded().Output);
        var missing = Run("變數 常數 = 42;", "變數 模組 = 匯入(\"模組.aqua\"); 模組.不存在; 印出(\"after\");");
        Assert.Equal(1, missing.ExitCode);
        Assert.StartsWith("ERROR: ", missing.Output);
        Assert.Contains("member not found", missing.Output);
        Assert.DoesNotContain("after", missing.Output);
        Assert.True(WasmTestHost.Eval("變數 數值 = 3; 數值.常數;").IsError);
    }

    [RuntimeFact]
    public void ArgumentErrorsPropagateAndModuleFunctionsCanBeAssigned() {
        const string module = "變數 原值 = 函式(值) { 印出(\"called\"); 值; };";
        Assert.Equal("called\ninteger 8\n", Run(module, "變數 模組 = 匯入(\"模組.aqua\"); 變數 呼叫 = 模組.原值; 變數 結果 = 呼叫(8); 印出(型別(結果), 結果);").Succeeded().Output);
        // The argument fails in the caller before the module function runs.
        var failed = Run(module, "變數 模組 = 匯入(\"模組.aqua\"); 模組.原值(不存在);");
        Assert.Equal(1, failed.ExitCode);
        Assert.Equal("ERROR: Identifier not found: 不存在\n", failed.Output);
    }

    [RuntimeFact]
    public void NativeBooleanResultsAndStringValuesDriveConditionsCorrectly() {
        // 關閉 is the runtime's own (native) 是網頁, false on desktop; 空字串 returns a native empty string.
        var run = Run("變數 關閉 = 是網頁; 變數 空字串 = 函式() { 字串.切片(\"abc\", 0, 0); };", """
            變數 模組 = 匯入("模組.aqua");
            印出([!模組.關閉(), 如果 (模組.關閉()) { 1; } 否則 { 2; }, 模組.關閉() == 假, 模組.空字串() == "", 型別(模組.關閉()), 型別(模組.空字串())]);
            """);
        Assert.Equal("[真, 2, 真, 真, boolean, string]\n", run.Succeeded().Output);
    }

    [RuntimeFact]
    public void ModuleMembersAreReadAgainAfterTheModuleChangesThem() {
        // Member reads must see every change the module's own functions make to its bindings.
        var run = Run("""
            變數 value = 0;
            變數 bump = 函式() { value++; };
            變數 f = 函式() { 1; };
            變數 swap = 函式() { f = 函式() { 2; }; };
            """, """
            變數 m = 匯入("模組.aqua");
            變數 total = 0;
            迴圈 (變數 i = 0; i < 5; i++) { total += m.value; m.bump(); }
            變數 a = m.f(); m.swap();
            變數 r = [total, a, m.f(), m.value];
            印出(r, [型別(r[0]), 型別(r[1]), 型別(r[2]), 型別(r[3])]);
            """);
        Assert.Equal("[10, 1, 2, 5] [integer, integer, integer, integer]\n", run.Succeeded().Output);
    }
}
