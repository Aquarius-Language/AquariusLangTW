using System.Diagnostics;
using System.Runtime.InteropServices;
using AquariusLang.Audio;
using AquariusLang.Desktop;
using AquariusLang.Desktop.runtime;
using AquariusLang.Object;
using AquariusLang.Compiler;
using AquariusLang.External;

namespace AquariusTests.Audio;

public class AudioTest {
    [Fact]
    public void ImportsAndInvalidCallsNeverCreateDevices() {
        var backend = new Unavailable(); using var runtime = new AudioRuntime(backend, _ => throw new IOException());
        Assert.True(runtime.TryImport("音訊", out var chinese)); Assert.True(runtime.TryImport("Audio", out var english)); Assert.Same(chinese, english);
        Assert.False(runtime.TryImport("Unknown", out _));
        var create = (BuiltinObj)chinese._Environment.Get("建立裝置", out _);
        Assert.IsType<ErrorObj>(create.Fn(new IObject[] { new IntegerObj(1) })); Assert.Equal(0, backend.Calls);
        Assert.DoesNotContain(typeof(AudioRuntime).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Wasmtime") || a.Name.StartsWith("ClearScript"));
        Assert.Equal(ExternalLibraryProfile.CoreWasm, ExternalLibraries.Audio.Profile);
    }
    private sealed class Unavailable : IAudioBackend {
        public int Calls;
        public IAudioDevice CreateDevice(bool offline) { Calls++; throw new InvalidOperationException(); }
    }
    [Fact]
    public void TraditionalChineseLanguageCanRenderWithoutOpeningHardware() {
        using var builtins = new DesktopBuiltins();
        var result = new CompiledRuntime(builtins).Execute(new LoweringCompiler().Compile(@"
變數 音訊 = 匯入(""音訊"");
變數 裝置 = 音訊.建立離線裝置();
變數 聲音 = 裝置.建立音調(440, 0.1d);
聲音.設定音量(0.5d); 聲音.設定聲像(-0.5d); 聲音.設定循環(真); 聲音.播放();
變數 樣本 = 裝置.產生樣本(256);
變數 成功 = 長度(樣本) == 512 && 聲音.是否播放中() && 聲音.取得時長() == 0.1d;
聲音.暫停(); 聲音.停止(); 聲音.跳至(0.01d); 聲音.釋放(); 聲音.釋放(); 裝置.釋放(); 裝置.釋放();
成功;
"));
        Assert.True(Assert.IsType<BooleanObj>(result).Value);
    }
    [Fact]
    public void WasmtimeAndBrowserEngineProduceIdenticalPcmBytes() {
        using var engine = new MiniaudioEngine(new WasmtimeEngine());
        int tone = engine.Tone(440, .1f), buffer = engine.Buffer(Enumerable.Range(0,512).Select(i => i%2==1?.1f:-.1f).ToArray());
        engine.Command(tone,3,AudioFormat.FloatBits(.7f)); engine.Command(tone,4,AudioFormat.FloatBits(.35f));
        engine.Command(tone,5,AudioFormat.FloatBits(1.25f)); engine.Command(tone,6,1); engine.Command(tone,0,0);
        engine.Command(buffer,6,1); engine.Command(buffer,0,0);
        var expected = MemoryMarshal.AsBytes(engine.Render(4096).AsSpan()).ToArray().ToList();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while(directory != null && !File.Exists(Path.Combine(directory.FullName,"AquariusLang.sln"))) directory=directory.Parent;
        engine.Command(tone,2,0);engine.Command(buffer,2,0);
        foreach (var format in new[] { "flac", "mp3" }) {
            int id=engine.Decode(File.ReadAllBytes(Path.Combine(directory!.FullName,$"AquariusWebCompiler/tests/fixtures/audio/tone.{format}")));
            engine.Command(id,0,0);expected.AddRange(MemoryMarshal.AsBytes(engine.Render(4096).AsSpan()).ToArray());engine.Release(id);
        }
        var start = new ProcessStartInfo("node") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        start.ArgumentList.Add(Path.Combine(directory!.FullName,"AquariusWebCompiler/tests/audio-parity.mjs"));
        using var process=Process.Start(start)!; var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(15000));Assert.True(process.ExitCode==0,error.GetAwaiter().GetResult());
        Assert.Equal(expected.ToArray(),Convert.FromBase64String(output.GetAwaiter().GetResult().Trim()));
    }
}
