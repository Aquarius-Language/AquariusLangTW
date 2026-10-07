using System.Runtime.InteropServices;
using AquariusLang.Object;
using AquariusLang.runtime;
using Xunit;

namespace AquariusREPL.Graphics;

public class TextInputTest {
    [Theory]
    [InlineData("", "")]
    [InlineData("中文", "中")]
    [InlineData("日本語", "日本")]
    [InlineData("한글", "한")]
    [InlineData("العربية", "العربي")]
    [InlineData("a😀", "a")]
    [InlineData("ae\u0301", "a")]
    [InlineData("a👨‍👩‍👧‍👦", "a")]
    [InlineData("a🇹🇼", "a")]
    public void BackspaceRemovesOneGrapheme(string source, string expected) {
        using var runtime = new GraphicsRuntime();
        Assert.True(runtime.TryImport("TextInput", out var input));
        Assert.Equal(expected, Assert.IsType<StringObj>(Call(input, "Backspace", new StringObj(source))).Value);
    }
    [Fact] public void InputImportsWithoutNativeInitializationAndValidatesWindowOwnership() {
        using var runtime = new GraphicsRuntime();
        Assert.True(runtime.TryImport("TextInput", out var input));
        foreach(string name in new[]{"Start", "Stop", "Poll", "SetCaretRect", "SupportsComposition", "Backspace"})
            Assert.IsType<BuiltinObj>(input._Environment.Get(name, out _));
        Assert.Contains("GLFW.Init", Assert.IsType<ErrorObj>(Call(input, "Start", new IntegerObj(0))).Message);
        Assert.Contains("string", Assert.IsType<ErrorObj>(Call(input, "Backspace", new IntegerObj(0))).Message);
        Assert.Contains("size()", Assert.IsType<ErrorObj>(ProcessingTest.Evaluate("變數 p=匯入(\"Processing\");p.startTextInput();")).Message);
    }
    [Fact] public void ProcessingSeparatesPreeditFromCommitsAndPreservesUnicodeKeyTyped() {
        using var runtime = new GraphicsRuntime();
        Assert.True(runtime.TryImport("Processing", out var p));
        runtime.InvokeAqua = (callback, args) => ((BuiltinObj)callback).Fn(args);
        var observed = new List<string>();
        foreach(string name in new[]{"textInput", "compositionStarted", "compositionUpdated", "compositionEnded", "keyTyped"}) {
            Call(p, "on", new StringObj(name), new BuiltinObj(args => {
                string field = name == "textInput" ? "inputText" : name == "keyTyped" ? "key" : "compositionText";
                observed.Add(name + ":" + Assert.IsType<StringObj>(p._Environment.Get(field, out _)).Value);
                return RepeatedPrimitives.NULL;
            }));
        }
        runtime.DispatchTextInput(new("compositionStarted", "", 0));
        runtime.DispatchTextInput(new("compositionUpdated", "注音😀", 4));
        Assert.True(Assert.IsType<BooleanObj>(p._Environment.Get("isComposing", out _)).Value);
        Assert.Equal(4, GraphicsRuntime.Number(p._Environment.Get("compositionCursor", out _)));
        Assert.Equal("", Assert.IsType<StringObj>(p._Environment.Get("inputText", out _)).Value);
        runtime.DispatchTextInput(new("textInput", "繁體😀é", 0));
        runtime.DispatchTextInput(new("compositionEnded", "", 0));
        Assert.False(Assert.IsType<BooleanObj>(p._Environment.Get("isComposing", out _)).Value);
        Assert.Equal("", Assert.IsType<StringObj>(p._Environment.Get("compositionText", out _)).Value);
        Assert.Equal(new[]{"compositionStarted:", "compositionUpdated:注音😀", "textInput:繁體😀é", "keyTyped:繁", "keyTyped:體", "keyTyped:😀", "keyTyped:é", "compositionEnded:"}, observed);
        // Cancellation also clears the preedit without producing a commit.
        runtime.DispatchTextInput(new("compositionStarted", "", 0));
        runtime.DispatchTextInput(new("compositionUpdated", "未提交", 2));
        runtime.DispatchTextInput(new("compositionEnded", "", 0));
        Assert.Equal("繁體😀é", Assert.IsType<StringObj>(p._Environment.Get("inputText", out _)).Value);
    }
    [Fact] public void MultilingualExampleParses() {
        var lexer = AquariusLang.lexer.Lexer.NewInstance(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples/multilingual_input/main.aqua")));
        var parser = AquariusLang.parser.Parser.NewInstance(lexer); parser.ParseAST();
        Assert.Empty(lexer.Errors); Assert.Empty(parser.Errors);
    }
    internal static IObject Call(ModuleObj module, string name, params IObject[] args) =>
        ((BuiltinObj)module._Environment.Get(name, out _)).Fn(args);
}

[Collection("Starship console output")]
public class TextInputIntegrationTest {
    [WgpuFact] public void ProcessingConsumesNativeEventsAndRendersTextThroughWgpu() {
        if (!OperatingSystem.IsWindows()) return;
        using var runtime = new GraphicsRuntime();
        runtime.TryImport("Processing", out var p);
        runtime.InvokeAqua = (callback, args) => ((BuiltinObj)callback).Fn(args);
        string title = "TextInput-test-" + Guid.NewGuid();
        Assert.IsNotType<ErrorObj>(TextInputTest.Call(p, "size", new IntegerObj(320), new IntegerObj(100), new StringObj("P2D"), new StringObj(title)));
        var hwnd = FindWindowW(null, title); Assert.NotEqual(IntPtr.Zero, hwnd);
        var commits = new List<string>(); var typed = new List<string>();
        TextInputTest.Call(p, "on", new StringObj("textInput"), new BuiltinObj(args => {
            commits.Add(Assert.IsType<StringObj>(p._Environment.Get("inputText", out _)).Value);
            return RepeatedPrimitives.NULL;
        }));
        TextInputTest.Call(p, "on", new StringObj("keyTyped"), new BuiltinObj(args => {
            typed.Add(Assert.IsType<StringObj>(p._Environment.Get("key", out _)).Value);
            return RepeatedPrimitives.NULL;
        }));
        Assert.IsNotType<ErrorObj>(TextInputTest.Call(p, "startTextInput"));
        TextInputTest.Call(p, "textInputRect", new IntegerObj(20), new IntegerObj(20), new IntegerObj(1), new IntegerObj(28));
        string text = "繁體日本語한글é😀";
        foreach(char codeUnit in text) SendMessageW(hwnd, 0x102, (UIntPtr)codeUnit, IntPtr.Zero);
        Assert.IsNotType<ErrorObj>(TextInputTest.Call(p, "beginFrame"));
        Assert.Equal(text, string.Concat(commits)); Assert.Equal(text, string.Concat(typed));
        TextInputTest.Call(p, "background", new IntegerObj(0));
        TextInputTest.Call(p, "fill", new IntegerObj(255));
        TextInputTest.Call(p, "textSize", new IntegerObj(24));
        Assert.IsNotType<ErrorObj>(TextInputTest.Call(p, "text", new StringObj(text), new IntegerObj(20), new IntegerObj(50)));
        Assert.IsNotType<ErrorObj>(TextInputTest.Call(p, "endFrame"));
        TextInputTest.Call(p, "loadPixels");
        Assert.Contains(Assert.IsType<ArrayObj>(p._Environment.Get("pixels", out _)).Elements, pixel => GraphicsRuntime.Number(pixel) != 0xff000000);
        SendMessageW(hwnd, 0x10d, UIntPtr.Zero, IntPtr.Zero);
        TextInputTest.Call(p, "beginFrame");
        Assert.True(Assert.IsType<BooleanObj>(p._Environment.Get("isComposing", out _)).Value);
        TextInputTest.Call(p, "stopTextInput");
        Assert.False(Assert.IsType<BooleanObj>(p._Environment.Get("isComposing", out _)).Value);
        Assert.False(Assert.IsType<BooleanObj>(p._Environment.Get("textInputEnabled", out _)).Value);
        TextInputTest.Call(p, "close");
    }
    [WgpuFact] public void NoApiWindowDeliversUnicodeBurstsAndResetsAcrossSessions() {
        if (!OperatingSystem.IsWindows()) return; // Native message injection is Windows specific.
        using var runtime = new GraphicsRuntime();
        runtime.TryImport("GLFW", out var glfw); runtime.TryImport("TextInput", out var input);
        TextInputTest.Call(glfw, "Init");
        TextInputTest.Call(glfw, "WindowHint", new IntegerObj(0x22001), new IntegerObj(0));
        TextInputTest.Call(glfw, "WindowHint", new IntegerObj(0x20004), new IntegerObj(0));
        var window = Assert.IsType<WindowObject>(TextInputTest.Call(glfw, "CreateWindow", new IntegerObj(64), new IntegerObj(48), new StringObj("文字 / wgpu")));
        Native.aqua_wgpu_handles(window.Handle, out _, out var hwnd);
        Assert.True(Assert.IsType<BooleanObj>(TextInputTest.Call(input, "SupportsComposition")).Value);
        TextInputTest.Call(input, "Start", window);
        TextInputTest.Call(input, "SetCaretRect", window, new IntegerObj(10), new IntegerObj(20), new IntegerObj(1), new IntegerObj(24));
        Assert.IsType<ErrorObj>(TextInputTest.Call(input, "SetCaretRect", window, new IntegerObj(0), new IntegerObj(0), new IntegerObj(-1), new IntegerObj(1)));
        string text = string.Concat(Enumerable.Repeat("繁體中文日本語한글العربيةé😀", 40));
        foreach(char codeUnit in text) SendMessageW(hwnd, 0x102, (UIntPtr)codeUnit, IntPtr.Zero); // WM_CHAR, including surrogate pairs.
        var events = Assert.IsType<ArrayObj>(TextInputTest.Call(input, "Poll", window));
        Assert.True(events.Elements.Length > 256);
        Assert.All(events.Elements, e => Assert.Equal("textInput", Field(e, "type")));
        Assert.Equal(text, string.Concat(events.Elements.Select(e => Field(e, "text"))));
        Assert.Empty(Assert.IsType<ArrayObj>(TextInputTest.Call(input, "Poll", window)).Elements);
        // Observe composition start/end without requiring a particular installed IME.
        SendMessageW(hwnd, 0x10d, UIntPtr.Zero, IntPtr.Zero);
        SendMessageW(hwnd, 0x10e, UIntPtr.Zero, IntPtr.Zero);
        events = Assert.IsType<ArrayObj>(TextInputTest.Call(input, "Poll", window));
        Assert.Equal(new[]{"compositionStarted", "compositionEnded"}, events.Elements.Select(e => Field(e, "type")));
        SendMessageW(hwnd, 0x10d, UIntPtr.Zero, IntPtr.Zero);
        SendMessageW(hwnd, 0x8, UIntPtr.Zero, IntPtr.Zero); // Focus loss ends the preview.
        events = Assert.IsType<ArrayObj>(TextInputTest.Call(input, "Poll", window));
        Assert.Equal(new[]{"compositionStarted", "compositionEnded"}, events.Elements.Select(e => Field(e, "type")));
        for(int i=0;i<40000;i++) SendMessageW(hwnd, 0x102, (UIntPtr)'a', IntPtr.Zero);
        Assert.Contains("exceeded", Assert.IsType<ErrorObj>(TextInputTest.Call(input, "Poll", window)).Message);
        Assert.Empty(Assert.IsType<ArrayObj>(TextInputTest.Call(input, "Poll", window)).Elements);
        SendMessageW(hwnd, 0x102, (UIntPtr)'x', IntPtr.Zero);
        TextInputTest.Call(input, "Stop", window);
        TextInputTest.Call(input, "Start", window);
        Assert.Empty(Assert.IsType<ArrayObj>(TextInputTest.Call(input, "Poll", window)).Elements);
        using(var other = new GraphicsRuntime()) {
            other.TryImport("TextInput", out var foreign);
            Assert.IsType<ErrorObj>(TextInputTest.Call(foreign, "Poll", window));
        }
        TextInputTest.Call(glfw, "DestroyWindow", window);
        Assert.IsType<ErrorObj>(TextInputTest.Call(input, "Poll", window));
        var next = Assert.IsType<WindowObject>(TextInputTest.Call(glfw, "CreateWindow", new IntegerObj(32), new IntegerObj(32), new StringObj("new session")));
        TextInputTest.Call(input, "Start", next);
        Assert.Empty(Assert.IsType<ArrayObj>(TextInputTest.Call(input, "Poll", next)).Elements);
    }
    private static string Field(IObject value, string field) => Assert.IsType<StringObj>(Assert.IsType<ModuleObj>(value)._Environment.Get(field, out _)).Value;
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, UIntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr FindWindowW(string? className, string windowName);
}
