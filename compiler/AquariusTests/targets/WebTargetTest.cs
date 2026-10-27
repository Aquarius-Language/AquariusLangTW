using System.Text;
using System.Text.Json;
using AquariusLang.Build;
using AquariusLang.Build.Targets;
using AquariusLang.Compiler;
using AquariusLang.Libraries;
using AquariusLang.Wasm;
using AquariusTests.Packaging;

namespace AquariusTests.Targets;

/// <summary>The web target (<see cref="WebTarget"/>): a static site with the page, the web glue, the web runtime pack,
/// only the side modules of the libraries the application needs, the program with its assets beside it and
/// aquarius.json.</summary>
public class WebTargetTest
{
    private static AquariusInstallation Installation => AquariusInstallation.Require();
    private static string Pack => Installation.RuntimePack("web");

    private static string Compile(TestFolder t, IReadOnlyDictionary<string, string> sources, LibrarySet? libraries = null, string name = "app.wasm")
    {
        string wasm = t.FilePath(name);
        var files = sources.Select(s => t.Write("src/" + s.Key, s.Value)).ToArray();
        if (Directory.Exists(t.FilePath("src/assets"))) WasmApplication.Compile(files, wasm, t.FilePath("src"), [t.FilePath("src/assets")], libraries: libraries);
        else WasmApplication.Compile(files, wasm, t.FilePath("src"), libraries: libraries);
        return wasm;
    }
    private static JsonElement Manifest(string site) => JsonDocument.Parse(File.ReadAllText(Path.Combine(site, "aquarius.json"))).RootElement;
    private static string[] Files(string directory) => Directory.Exists(directory)
        ? Directory.GetFiles(directory).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray() : [];

    [WebFact]
    public void SiteHasThePageGlueRuntimeProgramAssetsAndOnlyNeededSideModules()
    {
        using var t = new TestFolder();
        t.Write("src/assets/字.txt", "中文");
        string wasm = Compile(t, new Dictionary<string, string> {
            ["main.aqua"] = "印出(\"DO-NOT-RUN\"); 變數 a = 匯入(\"Audio\"); 變數 m = 匯入(\"lib/工具.aqua\"); m.值;",
            ["lib/工具.aqua"] = "變數 值 = 42;",
        });
        string site = t.FilePath("site");
        new WebTarget().Build(new WasmBuildRequest(wasm, site));

        // The page and the glue.
        Assert.Equal(File.ReadAllBytes(Path.Combine(Installation.WebGlue, "page", "index.html")), File.ReadAllBytes(Path.Combine(site, "index.html")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Installation.WebGlue, "page", "app.mjs")), File.ReadAllBytes(Path.Combine(site, "app.mjs")));
        Assert.True(File.Exists(Path.Combine(site, "glue", "index.mjs")));
        foreach (string file in Directory.GetFiles(Path.Combine(Installation.WebGlue, "core")))
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(site, "glue", "core", Path.GetFileName(file))));
        Assert.Empty(Directory.GetFiles(Path.Combine(site, "glue"), "*.md", SearchOption.AllDirectories));
        // The runtime pack, and only the side modules of needed libraries.
        foreach (string file in new[] { "aquarius.wasm", "libc.so", "libc++.so", "libc++abi.so" })
            Assert.Equal(File.ReadAllBytes(Path.Combine(Pack, file)), File.ReadAllBytes(Path.Combine(site, "runtime", file)));
        Assert.Equal(["audio.wasm"], Files(Path.Combine(site, "runtime", "libraries")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Pack, "libraries", "audio.wasm")), File.ReadAllBytes(Path.Combine(site, "runtime", "libraries", "audio.wasm")));
        // The program, with its assets stored beside it.
        var original = WasmProgram.Load(wasm);
        var program = WasmProgram.Load(Path.Combine(site, "program.wasm"));
        Assert.Equal(original.WithExternalAssets().Bytes.ToArray(), program.Bytes.ToArray());
        Assert.True(program.Metadata.ExternalAssets); Assert.Equal(0, program.Assets["assets/字.txt"].Length);
        Assert.Equal(["audio"], program.Metadata.NativeLibraries);
        Assert.Equal("中文", File.ReadAllText(Path.Combine(site, "assets", "assets", "字.txt")));
        var manifest = Manifest(site);
        Assert.Equal("program.wasm", manifest.GetProperty("program").GetString());
        Assert.Equal("main.aqua", manifest.GetProperty("entry").GetString());
        Assert.Equal("assets/assets/%E5%AD%97.txt", manifest.GetProperty("assets").GetProperty("assets/字.txt").GetString());
        Assert.True(File.Exists(Path.Combine(site, WebTarget.Marker)));
        // Nothing else: no sources, no intermediate files.
        Assert.Empty(Directory.GetFiles(site, "*.aqua", SearchOption.AllDirectories));
        Assert.Equal([".aquarius-web-output", "app.mjs", "aquarius.json", "index.html", "program.wasm"], Files(site));
        Assert.Equal(["assets", "glue", "runtime"], Directory.GetDirectories(site).Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal));
    }

    [WebFact]
    public void ProgramsWithoutNativeImportsShipNoSideModulesAndTheEntryCanBeSelected()
    {
        using var t = new TestFolder();
        string wasm = Compile(t, new Dictionary<string, string> { ["main.aqua"] = "1;", ["other.aqua"] = "42;" });
        Directory.Delete(t.FilePath("src"), true);
        string site = t.FilePath("網站 folder");
        new WebTarget().Build(new WasmBuildRequest(wasm, site, "OTHER.aqua"));
        Assert.Equal("other.aqua", Manifest(site).GetProperty("entry").GetString());
        Assert.Empty(Manifest(site).GetProperty("assets").EnumerateObject());
        Assert.Empty(Files(Path.Combine(site, "runtime", "libraries")));
        Assert.Equal(2, WasmProgram.Load(Path.Combine(site, "program.wasm")).Metadata.Modules.Count);
        Assert.False(Directory.Exists(Path.Combine(site, "assets")));
    }

    [WebFact]
    public void RebuildingReplacesThePreviousSite()
    {
        using var t = new TestFolder();
        t.Write("src/assets/old.txt", "old");
        string site = t.FilePath("site");
        new WebTarget().Build(new WasmBuildRequest(Compile(t, new Dictionary<string, string> { ["main.aqua"] = "匯入(\"Terminal\");" }), site));
        Assert.Equal(["terminal.wasm"], Files(Path.Combine(site, "runtime", "libraries")));
        Directory.Delete(t.FilePath("src"), true);
        t.Write("src/assets/new.txt", "new");
        new WebTarget().Build(new WasmBuildRequest(Compile(t, new Dictionary<string, string> { ["main.aqua"] = "匯入(\"音訊\"); 7;" }), site));
        Assert.Equal(["audio.wasm"], Files(Path.Combine(site, "runtime", "libraries")));
        Assert.Equal(["new.txt"], Files(Path.Combine(site, "assets", "assets")));
        Assert.Equal(["assets/new.txt"], Manifest(site).GetProperty("assets").EnumerateObject().Select(p => p.Name));
        // Staging and backup folders are gone.
        Assert.Equal(new[] { "app.wasm", "site", "src" },
            Directory.GetFileSystemEntries(t.Path).Select(e => Path.GetFileName(e)!).Order(StringComparer.Ordinal));
    }

    [WebFact]
    public void FailedBuildsKeepThePreviousSite()
    {
        using var t = new TestFolder();
        string wasm = Compile(t, new Dictionary<string, string> { ["main.aqua"] = "42;" }), site = t.FilePath("site");
        new WebTarget().Build(new WasmBuildRequest(wasm, site));
        byte[] before = File.ReadAllBytes(Path.Combine(site, "aquarius.json"));
        Assert.Throws<InvalidDataException>(() => new WebTarget().Build(new WasmBuildRequest(wasm, site, "missing.aqua")));
        File.WriteAllText(t.FilePath("bad.wasm"), "invalid archive");
        Assert.Throws<InvalidDataException>(() => new WebTarget().Build(new WasmBuildRequest(t.FilePath("bad.wasm"), site)));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(site, "aquarius.json")));
        Assert.Empty(Directory.GetDirectories(t.Path, ".aquarius-*"));
    }

    [WebFact]
    public void FoldersWithOtherFilesAreRefusedAndEmptyFoldersAreUsed()
    {
        using var t = new TestFolder();
        string wasm = Compile(t, new Dictionary<string, string> { ["main.aqua"] = "42;" });
        t.Write("mine/user.txt", "keep");
        Assert.Contains("other files", Assert.Throws<ArgumentException>(() => new WebTarget().Build(new WasmBuildRequest(wasm, t.FilePath("mine")))).Message);
        Assert.Equal(["user.txt"], Files(t.FilePath("mine")));
        // The folder holding the input, and a file, cannot be the output.
        Assert.Throws<ArgumentException>(() => new WebTarget().Build(new WasmBuildRequest(wasm, t.Path)));
        Assert.Throws<ArgumentException>(() => new WebTarget().Build(new WasmBuildRequest(wasm, t.Write("file", "x"))));
        Assert.Throws<ArgumentException>(() => new WebTarget().Build(new WasmBuildRequest(t.FilePath("src/main.aqua"), t.FilePath("site"))));
        Directory.CreateDirectory(t.FilePath("empty"));
        new WebTarget().Build(new WasmBuildRequest(wasm, t.FilePath("empty")));
        Assert.True(File.Exists(t.FilePath("empty/program.wasm")));
        Assert.Empty(Directory.GetDirectories(t.Path, ".aquarius-*"));
    }

    [WebFact]
    public void ExternalLibrariesShipTheirWebSideModule()
    {
        using var t = new TestFolder();
        var set = LibrarySet.Default.With([Fixtures.ExternalLibrary(t, "web")]);
        string wasm = Compile(t, new Dictionary<string, string> { ["main.aqua"] = "匯入(\"星雲\");" }, set), site = t.FilePath("site");
        new WebTarget().Build(new WasmBuildRequest(wasm, site, Libraries: set));
        Assert.Equal(["nebula.wasm"], Files(Path.Combine(site, "runtime", "libraries")));
        Assert.Equal("dummy web binary of nebula", File.ReadAllText(Path.Combine(site, "runtime", "libraries", "nebula.wasm")));
        // Without the library's web binary nothing is written.
        File.Delete(t.FilePath("nebula/native/web/nebula.wasm"));
        var error = Assert.Throws<IOException>(() => new WebTarget().Build(new WasmBuildRequest(wasm, t.FilePath("other"), Libraries: set)));
        Assert.Contains("aqua library build", error.Message);
        Assert.False(Directory.Exists(t.FilePath("other")));
    }

    // ---- Running the site in Chromium (optional: needs node and Playwright) ---------------------------------------

    /// <summary>Serves a site on localhost, opens it in headless Chromium and prints the page's output lines; exits
    /// with the application's exit code (3 when the page fails).</summary>
    private const string RunSite = """
        import { createServer } from 'node:http';
        import { readFile } from 'node:fs/promises';
        import { join, extname } from 'node:path';
        import { createRequire } from 'node:module';
        const [site, playwright] = process.argv.slice(2);
        const types = { '.html': 'text/html', '.mjs': 'text/javascript', '.js': 'text/javascript', '.wasm': 'application/wasm', '.so': 'application/wasm', '.json': 'application/json' };
        const server = createServer(async (request, response) => {
            const pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
            const path = join(site, pathname.endsWith('/') ? pathname + 'index.html' : pathname);
            try { const body = await readFile(path); response.writeHead(200, { 'content-type': types[extname(path)] ?? 'application/octet-stream' }); response.end(body); }
            catch { response.writeHead(404); response.end(); }
        });
        await new Promise((ok) => server.listen(0, '127.0.0.1', ok));
        const { chromium } = createRequire(join(playwright, 'package.json'))('playwright');
        let browser;
        try { browser = await chromium.launch({ headless: true, channel: 'chrome' }); }
        catch { try { browser = await chromium.launch({ headless: true }); } catch (error) { console.error(error.message); server.close(); process.exit(4); } }
        let code = 3;
        try {
            const page = await browser.newPage();
            page.on('pageerror', (error) => console.error('[page]', error.message));
            await page.goto(`http://127.0.0.1:${server.address().port}/`);
            await page.waitForFunction(() => ['finished', 'failed'].includes(window.aquarius?.state?.status), null, { timeout: 60000 });
            const state = await page.evaluate(() => window.aquarius.state);
            for (const line of state.lines) console.log(line);
            code = state.status === 'finished' ? state.exitCode : 3;
        } catch (error) { console.error(error.message); }
        finally { await browser.close(); server.close(); }
        process.exit(code);
        """;

    private static string? Playwright()
    {
        if (AquariusInstallation.Current is not { } installation) return null;
        string folder = Path.Combine(installation.Root, "scripts", "node_modules", "playwright");
        return File.Exists(Path.Combine(folder, "package.json")) ? folder : null;
    }
    private static string? Node() => Processes.OnPath(OperatingSystem.IsWindows() ? "node.exe" : "node");

    [WebFact]
    public void WebGLGlueTranslatesPixelsBuffersAndObjectLifetimes()
    {
        if (Node() is not { } node || Playwright() is not { } playwright) return;
        using var t = new TestFolder();
        string site = t.FilePath("site");
        new WebTarget().Build(new WasmBuildRequest(Compile(t, new Dictionary<string, string> { ["main.aqua"] = "42;" }), site));
        t.Write("site/index.html", """
            <script type="module">
            import {GlueContext,glueImports} from './glue/core/glue.mjs';
            import './glue/webgl/webgl.mjs';
            window.aquarius={state:{status:'running',lines:[]}};
            try {
              const context=new GlueContext();context.memory=new WebAssembly.Memory({initial:8});
              let heap=1024,freed=0;
              context.linker={symbol:name=>name==='malloc'?size=>{const p=heap;heap+=(size+7)&~7;return p;}:()=>{freed++;}};
              const api=glueImports(context),canvas=document.createElement('canvas');canvas.width=2;canvas.height=2;
              document.body.append(canvas);
              const handle=api.webgl_context_create(context.retain({canvas}));
              if(!handle)throw Error('WebGL 2 unavailable');
              api.webgl_context_current(handle);
              api.glClearColor(.25,.5,.75,1);api.glClear(0x4000);
              const buffer=api.webgl_create_buffer();api.glBindBuffer(0x88EB,buffer);
              api.glBufferData(0x88EB,4,0,0x88E1);api.glReadPixels(0,0,1,1,0x1908,0x1401,0);
              api.glGetBufferSubData(0x88EB,0,4,64);
              if(String([...context.bytes.subarray(64,68)])!=='64,128,191,255')throw Error('Pixel pack transfer failed');
              api.glBindBuffer(0x88EB,0);api.webgl_delete_buffer(buffer);
              if(context.object(buffer)!==undefined)throw Error('Deleted buffer retained');
              const texture=api.webgl_create_texture();api.glBindTexture(0x8C1A,texture);
              api.glTexStorage3D(0x8C1A,1,0x8233,2,1,1);
              new Int16Array(context.memory.buffer,80,2).set([-123,456]);
              api.glTexSubImage3D(0x8C1A,0,0,0,0,2,1,1,0x8D94,0x1402,80);
              if(api.glGetError()!==0)throw Error('Signed texture upload failed');
              const supported=canvas.getContext('webgl2').getSupportedExtensions();
              for(const name of supported)api.webgl_extension(context.allocate(name)[0]);
              const extensions=context.text(api.glGetString(0x1F03)).split(' ');
              for(const name of supported) {
                if(!extensions.includes(name.startsWith('WEBGL_')?name:'GL_'+name))throw Error('GLES extension name conversion failed');
              }
              api.glGetString(0x1F02);api.webgl_context_release(handle);
              if(context.object(texture)!==undefined || freed!==2)throw Error('Context resources retained');
              window.aquarius.state={status:'finished',exitCode:0,lines:['WEBGL_GLUE_OK']};
            }catch(error){window.aquarius.state={status:'finished',exitCode:1,lines:[String(error)]};}
            </script>
            """);
        var result = Processes.Run(node, t.Path, [t.Write("run-site.mjs", RunSite), site, playwright], timeoutMs: 120_000);
        if (result.ExitCode == 4) return;
        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        Assert.Equal("WEBGL_GLUE_OK\n", result.Output);
    }

    [WebFact]
    public void TheSiteRunsInChromium()
    {
        if (Node() is not { } node || Playwright() is not { } playwright) return; // Optional: needs node and Playwright.
        using var t = new TestFolder();
        t.Write("src/assets/字.txt", "網頁資產");
        // Assets are read through the application library (Files) when the web pack has its side module.
        bool files = WebFactAttribute.HasSideModule("application");
        string wasm = Compile(t, new Dictionary<string, string> {
            ["main.aqua"] = """
                變數 s = 匯入("星星"); 變數 c = import("StarChart");
                印出("網頁", 40 + 2, s.亮度(3), c.繪製(), 型別(匯入("Audio")), 是網頁());
                印出(匯入(目前工作目錄 + "/lib/value.aqua").answer);

                """ + (files ? "印出(匯入(\"Files\").讀取資產文字(\"assets/字.txt\", \"utf-8\"));\n" +
                    "印出(匯入(\"Files\").讀取資產文字(目前工作目錄 + \"/assets/字.txt\", \"utf-8\"));\n" : ""),
            ["lib/value.aqua"] = "var answer = import(currWorkingDir + \"/nested/answer.aqua\").answer;",
            ["lib/nested/answer.aqua"] = "var answer = 42;",
        }, Fixtures.WithDefaults(Fixtures.Library("stars")));
        string site = t.FilePath("site");
        new WebTarget().Build(new WasmBuildRequest(wasm, site));
        Assert.Equal(files ? ["application.wasm", "audio.wasm"] : ["audio.wasm"], Files(Path.Combine(site, "runtime", "libraries")));
        string script = t.Write("run-site.mjs", RunSite);
        var result = Processes.Run(node, t.Path, [script, site, playwright], timeoutMs: 120_000);
        if (result.ExitCode == 4) return; // No Chromium for Playwright on this machine.
        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        Assert.Equal("網頁 42 30 星星圖 module 真\n42\n" + (files ? "網頁資產\n網頁資產\n" : ""), result.Output);
    }

    [WebFact]
    public void LargeHeapsRunInChromiumAsOnTheDesktop()
    {
        // The web host's stack and libc arena live in the guest's heap: once the heap grew over them (it started anew after
        // they were reserved), programs with a live heap past about 16 MB crashed. A deep chain, over 100 MB of arrays and
        // binary trees of depth 16 must print what they print on the desktop.
        if (Node() is not { } node || Playwright() is not { } playwright) return;
        using var t = new TestFolder();
        const string source = """
            var last = [0, 0]; for (var i = 0; i < 500000; i++) { last = [i, last]; }
            var keep = []; for (var i = 0; i < 500000; i++) { Array.Append(keep, Array.Make(24, i)); }
            var sum = 0; for (var i = 0; i < 500000; i += 1000) { sum += keep[i][5]; }
            var tree = function(d) { if (d == 0) { return [0, 0]; } return [tree(d - 1), tree(d - 1)]; };
            var count = function(t, d) { if (d == 0) { return 1; } return 1 + count(t[0], d - 1) + count(t[1], d - 1); };
            var nodes = 0; for (var r = 0; r < 4; r++) { nodes += count(tree(16), 16); }
            print(len(last), len(keep), sum, nodes);
            """;
        string wasm = Compile(t, new Dictionary<string, string> { ["main.aqua"] = source });
        string site = t.FilePath("site");
        new WebTarget().Build(new WasmBuildRequest(wasm, site));
        var result = Processes.Run(node, t.Path, [t.Write("run-site.mjs", RunSite), site, playwright], timeoutMs: 300_000);
        if (result.ExitCode == 4) return; // No Chromium for Playwright on this machine.
        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        Assert.Equal("2 500000 124750000 524284\n", result.Output);
        Assert.Equal(result.Output, WasmTestHost.Output(source));
    }
}
