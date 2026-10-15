using AquariusLang.Desktop.Terminal;
using AquariusLang.Terminal;
using AquariusLang.Object;

namespace AquariusTests.Terminal;
public class TerminalTest {
    private sealed class Backend : ITerminalBackend {
        public string Platform => "linux"; public bool IsSupported => true;
        public TerminalRequest? Last;
        public TerminalResult Run(TerminalRequest request) { Last=request;return new(7,"你好","diagnostic",false); }
    }
    private static IObject Call(ModuleObj m,string name,params IObject[] args)=>((BuiltinObj)m._Environment.Get(name,out _)).Fn(args);
    [Fact] public void CoreValidatesBeforeCrossingHostBoundaryAndPreservesAliases() {
        var backend=new Backend();var runtime=new TerminalRuntime(backend);runtime.TryImport("Terminal",out var module);
        Assert.True(runtime.TryImport("終端機",out var alias));Assert.Same(module,alias);
        Assert.IsType<ErrorObj>(Call(module,"Run",new StringObj("tool"),new IntegerObj(1)));Assert.Null(backend.Last);
        var result=Assert.IsType<ModuleObj>(Call(module,"執行",new StringObj("tool"),new ArrayObj(new IObject[]{new StringObj("a b"),new StringObj(";literal")})));
        Assert.Equal(new[]{"a b",";literal"},backend.Last!.Arguments);
        Assert.Equal(7,Assert.IsType<IntegerObj>(result._Environment.Get("ExitCode",out _)).Value);
        Assert.Same(result._Environment.Get("StandardOutput",out _),result._Environment.Get("標準輸出",out _));
    }
    [Fact] public void DesktopCapturesBothStreamsAndEnforcesTimeout() {
        var backend=new DesktopTerminalBackend();
        var request=OperatingSystem.IsWindows()?new TerminalRequest("powershell.exe",["-NoProfile","-NonInteractive","-Command","[Console]::OutputEncoding=[Text.Encoding]::UTF8;[Console]::Write('hello');[Console]::Error.Write('error');exit 7"]):new TerminalRequest("/bin/sh",["-c","printf hello; printf error >&2; exit 7"]);
        var result=backend.Run(request);Assert.Equal(7,result.ExitCode);Assert.Equal("hello",result.StandardOutput);Assert.Equal("error",result.StandardError);Assert.False(result.TimedOut);
        request=OperatingSystem.IsWindows()?new("powershell.exe",["-NoProfile","-Command","Start-Sleep 10"],TimeoutMs:200):new("/bin/sh",["-c","sleep 10"],TimeoutMs:200);
        result=backend.Run(request);Assert.True(result.TimedOut);Assert.Equal(-1,result.ExitCode);
    }
    [Fact] public void DesktopArgumentBoundariesAreNotShellExpanded() {
        var backend=new DesktopTerminalBackend();
        using var t=new AquariusLang.Desktop.runtime.ApplicationTestDirectory();
        string script=t.Write("arguments.ps1","param([string]$Value)\n[Console]::Write($Value)");
        var request=OperatingSystem.IsWindows()?new TerminalRequest("powershell.exe",["-NoProfile","-NonInteractive","-File",script,"a b;literal"]):new TerminalRequest("/bin/sh",["-c","printf '%s' \"$1\"","test","a b;literal"]);
        Assert.Equal("a b;literal",backend.Run(request).StandardOutput);
    }
}
