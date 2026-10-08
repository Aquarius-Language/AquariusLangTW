using System.Diagnostics;
using System.Numerics;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusREPL.Graphics;

internal sealed partial class GraphicsRuntime {
    internal Func<IObject,IObject[],IObject>? InvokeAqua;
    private ProcessingCanvas screen=null!;
    private AquaEnvironment processing=null!;
    private WindowObject? sketchWindow;
    private WgpuDevice? processingDevice;
    private readonly List<ProcessingCanvas> canvases=new();
    private readonly Dictionary<ModuleObj,ProcessingImage> images=new();
    private readonly Dictionary<string,IObject> events=new();
    private bool looping=true, redraw, running, exiting;
    private double targetFps=60;
    private int frameCount;
    private readonly Stopwatch sketchClock=new();
    private readonly bool[] keys=new bool[349];
    private bool previousMouse;
    private double previousX,previousY;
    private bool rebindingCanvas;

    private void PBind(AquaEnvironment env,string name,int min,int max,Func<IObject[],IObject> fn) {
        var function = new BuiltinObj(a=> {
            try {
                if(disposed) throw new InvalidOperationException("Graphics runtime has been disposed.");
                if(a.Length<min||a.Length>max) throw new ArgumentException($"Expected {min}..{max} arguments, got {a.Length}.");
                return fn(a);
            } catch(Exception e) when(e is ArgumentException or InvalidOperationException or IOException or OverflowException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) {
                return new ErrorObj($"Processing.{name}: {e.Message}");
            }
        });
        if (rebindingCanvas && env == processing)
            FunctionRegistration.Replace(env, function, LibraryCatalog.TraditionalChinese(name), name);
        else FunctionRegistration.Define(env, function, LibraryCatalog.TraditionalChinese(name), name);
    }
    private void PAction(AquaEnvironment env,string name,int min,int max,Action<IObject[]> fn) => PBind(env,name,min,max,a=>{fn(a);return Null();});
    private static float F(IObject v) { float f=(float)Number(v); if(!float.IsFinite(f))throw new ArgumentException("Number exceeds float range.");return f; }
    private static uint Packed(IObject v) { double n=Integral(v); if(n<0||n>uint.MaxValue)throw new ArgumentException("Expected an ARGB color in 0..4294967295.");return (uint)n; }
    private static int Choice(IObject v,params int[] options) { int n=Int(v); if(!options.Contains(n))throw new ArgumentException("Invalid mode constant.");return n; }
    private Vector4 Color(ProcessingStyle style,IObject[] a) {
        if(a.Length<1||a.Length>4)throw new ArgumentException("Expected grayscale[,alpha], RGB/HSB[,alpha], or packed color.");
        float alpha=a.Length is 2 or 4?F(a[^1])/style.ColorMax.W:1;
        if(a.Length<=2) {
            double n=Number(a[0]);
            if(n>style.ColorMax.Z) { var c=ProcessingGeometry.Unpack(Packed(a[0])); if(a.Length==2)c.W=alpha;return c; }
            float gray=(float)n/style.ColorMax.Z; return Vector4.Clamp(new(gray,gray,gray,alpha),Vector4.Zero,Vector4.One);
        }
        var values=new Vector4(F(a[0])/style.ColorMax.X,F(a[1])/style.ColorMax.Y,F(a[2])/style.ColorMax.Z,alpha);
        return Vector4.Clamp(style.ColorMode==1?ProcessingGeometry.Hsb(values.X,values.Y,values.Z,values.W):values,Vector4.Zero,Vector4.One);
    }
    private void RegisterProcessing() {
        processing=Module("Processing");
        screen=new ProcessingCanvas(this,640,480,false); screen.Module=modules["Processing"];
        processing.Create("backend",new StringObj("wgpu"));
        RegisterCanvas(processing,screen); RegisterProcessingMath(processing);
        RegisterProcessingTextInput();
        foreach(var pair in new Dictionary<string,int>{["CORNER"]=0,["CORNERS"]=1,["CENTER"]=2,["RADIUS"]=3,
            ["RGB"]=0,["HSB"]=1,["ARGB"]=2,["OPEN"]=0,["CHORD"]=1,["PIE"]=2,["CLOSE"]=1,
            ["POINTS"]=0,["LINES"]=1,["TRIANGLES"]=4,["TRIANGLE_STRIP"]=5,["TRIANGLE_FAN"]=6,["QUADS"]=7,["QUAD_STRIP"]=8,["POLYGON"]=9,
            ["LEFT"]=0,["RIGHT"]=1,["TOP"]=101,["BOTTOM"]=102,["BASELINE"]=103,["ROUND"]=1,["SQUARE"]=0,["PROJECT"]=2,["MITER"]=0,["BEVEL"]=2,
            ["BLEND"]=0,["ADD"]=1,["MULTIPLY"]=2,["SCREEN"]=3,["REPLACE"]=4,
            ["GRAY"]=0,["INVERT"]=1,["THRESHOLD"]=2,["POSTERIZE"]=3,["OPAQUE"]=4,["BLUR"]=5,
            ["NORMAL"]=0,["IMAGE"]=1,["ESC"]=256,["SPACE"]=32}) processing.Create(pair.Key,N(pair.Value));
        processing.Create("P2D",new StringObj("P2D")); processing.Create("P3D",new StringObj("P3D"));
        PAction(processing,"size",2,4,a=>Size(Int(a[0]),Int(a[1]),a.Length>2?Text(a[2]):"P2D",a.Length>3?Text(a[3]):"Aquarius Processing"));
        PAction(processing,"resize",2,2,a=> {
            RequireSketch();int w=Dimension(a[0]),h=Dimension(a[1]);
            Native.aqua_set_window_size(sketchWindow!.Handle,w,h);
        });
        PAction(processing,"fullScreen",0,2,a=> {if(sketchWindow!=null)throw new InvalidOperationException("fullScreen() must be called before size().");CallModule("GLFW","Init");
            try{if(Native.aqua_monitor_size(out int w,out int h)==0)throw new InvalidOperationException("Primary monitor is unavailable.");Size(w,h,a.Length>0?Text(a[0]):"P2D",a.Length>1?Text(a[1]):"Aquarius Processing");if(Native.aqua_fullscreen(sketchWindow!.Handle)==0)throw new InvalidOperationException("Fullscreen monitor is unavailable.");}
            catch{CloseSketch();Terminate();throw;}});
        PAction(processing,"noCursor",0,0,_=>{RequireSketch();Native.aqua_input(sketchWindow!.Handle,0x33001,0x34002);});
        PAction(processing,"cursor",0,0,_=>{RequireSketch();Native.aqua_input(sketchWindow!.Handle,0x33001,0x34001);});
        PAction(processing,"run",2,2,a=>Run(a[0],a[1]));
        PAction(processing,"on",2,2,a=> {
            string name=Text(a[0]); if(!new[]{"mousePressed","mouseReleased","mouseClicked","mouseMoved","mouseDragged","keyPressed","keyReleased","keyTyped","mouseWheel","windowResized","textInput","compositionStarted","compositionUpdated","compositionEnded"}.Contains(name))throw new ArgumentException("Unknown event name.");
            ValidateCallback(a[1]); events[name]=a[1];
        });
        PAction(processing,"beginFrame",0,0,_=>BeginFrame());
        PAction(processing,"endFrame",0,0,_=>EndFrame());
        PAction(processing,"noLoop",0,0,_=>looping=false); PAction(processing,"loop",0,0,_=>looping=true); PAction(processing,"redraw",0,0,_=>redraw=true);
        PAction(processing,"exit",0,0,_=>exiting=true); PAction(processing,"close",0,0,_=>CloseSketch());
        PAction(processing,"frameRate",1,1,a=> { double fps=Number(a[0]); if(fps<=0||fps>1000)throw new ArgumentException("Frame rate must be 0..1000 (exclusive zero).");targetFps=fps; });
        PBind(processing,"millis",0,0,_=>new DoubleObj(sketchClock.Elapsed.TotalMilliseconds));
        PBind(processing,"keyDown",1,1,a=>{RequireSketch();int code=Int(a[0]);if(!ValidKey(code))throw new ArgumentException("Invalid GLFW key code.");return new BooleanObj(Native.aqua_key(sketchWindow!.Handle,code)==1);});
        PBind(processing,"createGraphics",2,3,a=> {
            RequireSketch(); int w=Dimension(a[0]),h=Dimension(a[1]); bool threeD=a.Length==3&&Renderer(a[2]);
            var canvas=new ProcessingCanvas(this,w,h,threeD); canvas.Module=new ModuleObj(AquaEnvironment.NewEnvironment());
            canvas.Initialize(processingDevice!,true); canvases.Add(canvas); RegisterCanvas(canvas.Module._Environment,canvas);
            images[canvas.Module]=canvas.Surface!;
            UpdateCanvasResolution(canvas);
            PAction(canvas.Module._Environment,"resize",2,4,a=> {
                RequireSketch();
                if(canvas.Disposed)throw new InvalidOperationException("Canvas is disposed.");
                if(canvas.Drawing)throw new InvalidOperationException("Call resize() outside beginDraw()/endDraw().");
                if(a.Length==3)throw new ArgumentException("resize() takes two or four dimensions.");
                int width=Dimension(a[0]),height=Dimension(a[1]);
                int pw=a.Length==4?Dimension(a[2]):width,ph=a.Length==4?Dimension(a[3]):height;
                if(checked((long)pw*ph)>16*1024*1024)throw new ArgumentException("Canvas supports at most 16 million pixels.");
                if(canvas.Resize(width,height,pw,ph)) {
                    canvas.Bind();var surface=canvas.Surface!;
                    surface.Width=pw;surface.Height=ph;surface.Bytes=new byte[pw*ph*4];
                    canvas.Module._Environment.Create("pixels",Null());
                    UpdateCanvasResolution(canvas);
                }
            });
            PAction(canvas.Module._Environment,"beginDraw",0,0,_=> { if(canvas.Drawing)throw new InvalidOperationException("beginDraw() is already active.");canvas.Drawing=true;canvas.Model=Matrix4x4.Identity;canvas.Bind(); });
            PAction(canvas.Module._Environment,"endDraw",0,0,_=> { canvas.RequireDrawing();canvas.Target.Flush();canvas.Drawing=false;screen.Bind(); });
            screen.Bind();return canvas.Module;
        });
        PBind(processing,"createImage",2,3,a=> {int format=a.Length==3?Choice(a[2],0,2):2;var image=ProcessingImage.Create(this,Dimension(a[0]),Dimension(a[1]));if(format==0)for(int i=3;i<image.Bytes.Length;i+=4)image.Bytes[i]=255;return RegisterImage(image).Module; });
        PBind(processing,"loadImage",1,1,a=>LoadProcessingImage(Text(a[0])).Module);
        PBind(processing,"createFont",2,2,a=> { var env=AquaEnvironment.NewEnvironment();env.Create("name",new StringObj(Text(a[0])));env.Create("size",new FloatObj(Positive(a[1])));return new ModuleObj(env); });
        UpdateState();
    }
    private static int Dimension(IObject o) { int d=Int(o);if(d<=0||d>8192)throw new ArgumentException("Dimension must be 1..8192.");return d; }
    private static float Positive(IObject o) { float n=F(o);if(n<=0)throw new ArgumentException("Expected a positive number.");return n; }
    private static float Nonnegative(IObject o) {float n=F(o);if(n<0)throw new ArgumentException("Expected a nonnegative number.");return n;}
    private static bool Renderer(IObject o) { string s=Text(o);if(s!="P2D"&&s!="P3D")throw new ArgumentException("Renderer must be P2D or P3D.");return s=="P3D"; }
    private IObject CallModule(string module,string name,params IObject[] a) {
        var result=((BuiltinObj)modules[module]._Environment.Get(name,out _)).Fn(a);
        if(result is ErrorObj e)throw new InvalidOperationException(e.Message); return result;
    }
    private void Size(int w,int h,string renderer,string title) {
        if(sketchWindow!=null)throw new InvalidOperationException("size() can only be called once per sketch.");
        if(screen.Disposed) {
            screen=new ProcessingCanvas(this,640,480,false);screen.Module=modules["Processing"];
            rebindingCanvas=true;
            try { RegisterCanvas(processing,screen); }
            finally { rebindingCanvas=false; }
        }
        w=Dimension(N(w));h=Dimension(N(h));bool threeD=Renderer(new StringObj(renderer));
        if(windows.Count!=0)throw new InvalidOperationException("Close raw GLFW windows before starting a Processing sketch.");
        try {
            CallModule("GLFW","Init"); CallModule("GLFW","WindowHint",N(0x22001),N(0));
            sketchWindow=(WindowObject)CallModule("GLFW","CreateWindow",N(w),N(h),new StringObj(title));
            processingDevice=new WgpuDevice(sketchWindow.Handle);
            screen.Is3D=threeD;screen.Drawing=true;
            Native.aqua_framebuffer(sketchWindow.Handle,out int pw,out int ph);
            screen.Resize(w,h,pw,ph);screen.DefaultCamera();
            screen.Initialize(processingDevice); sketchClock.Restart();System.Array.Clear(keys,0,keys.Length);previousMouse=false;previousX=previousY=0;UpdateState();
        } catch { CloseSketch();Terminate();throw; }
    }
    private void RequireSketch() { if(sketchWindow==null||screen.Disposed||processingDevice==null)throw new InvalidOperationException("Call Processing.size() first."); RequireInit();processingDevice.RequireLive(); }
    private static void UpdateCanvasResolution(ProcessingCanvas canvas) {
        var env=canvas.Module!._Environment;
        env.Create("width",N(canvas.Width));env.Create("height",N(canvas.Height));
        env.Create("pixelWidth",N(canvas.PixelWidth));env.Create("pixelHeight",N(canvas.PixelHeight));
    }
    private static void ValidateCallback(IObject o) { if(o is not FunctionObj && o is not BuiltinObj)throw new ArgumentException("Expected a function callback."); if(o is FunctionObj f&&f.Parameters.Length!=0)throw new ArgumentException("Sketch callbacks must have zero parameters; read event fields from Processing."); }
    private void Callback(IObject callback) {
        if(InvokeAqua==null)throw new InvalidOperationException("This host does not support sketch callbacks.");
        IObject result=InvokeAqua(callback,System.Array.Empty<IObject>());
        if(result is ErrorObj e)throw new InvalidOperationException(e.Message);
    }
    private void Event(string name) { if(events.TryGetValue(name,out var callback))Callback(callback); }
    private void Run(IObject setup,IObject draw) {
        ValidateCallback(setup); ValidateCallback(draw); if(running)throw new InvalidOperationException("run() cannot be nested.");
        int limit=0; string? frames=System.Environment.GetEnvironmentVariable("AQUARIUS_GRAPHICS_FRAMES");
        if(!string.IsNullOrEmpty(frames)&&(!int.TryParse(frames,out limit)||limit<0))throw new ArgumentException("AQUARIUS_GRAPHICS_FRAMES must be a nonnegative integer.");
        running=true;exiting=false;frameCount=0;
        try {
            Callback(setup);RequireSketch();
            while(!exiting&&Native.aqua_should_close(sketchWindow!.Handle)==0) {
                var start=Stopwatch.GetTimestamp(); BeginFrame();
                if(exiting)break;
                if(screen.PixelWidth==0||screen.PixelHeight==0) { Thread.Sleep(10);continue; }
                if(looping||redraw||frameCount==0) {
                    redraw=false;frameCount++;UpdateState();Callback(draw);
                    if(limit>0&&frameCount>=limit) {
                        string? capture=System.Environment.GetEnvironmentVariable("AQUARIUS_GRAPHICS_CAPTURE");
                        if(!string.IsNullOrEmpty(capture))SaveCanvas(screen,capture);
                    }
                    EndFrame(); if(limit>0&&frameCount>=limit)break;
                }
                double elapsed=(Stopwatch.GetTimestamp()-start)/(double)Stopwatch.Frequency;
                double sleep=1000/targetFps-elapsed*1000;
                while(sleep>=1&&!exiting&&Native.aqua_should_close(sketchWindow.Handle)==0) {Thread.Sleep((int)Math.Min(sleep,10));Native.aqua_poll();sleep=1000/targetFps-(Stopwatch.GetTimestamp()-start)*1000/(double)Stopwatch.Frequency;}
                elapsed=(Stopwatch.GetTimestamp()-start)/(double)Stopwatch.Frequency;processing.Create("actualFrameRate",new DoubleObj(elapsed>0?1/elapsed:targetFps));
            }
        } finally { running=false;CloseSketch(); }
    }
    private void BeginFrame() {
        RequireSketch(); Native.aqua_poll();
        Native.aqua_framebuffer(sketchWindow!.Handle,out int w,out int h);
        Native.aqua_window_size(sketchWindow.Handle,out int logicalW,out int logicalH);
        bool resized=screen.Resize(logicalW,logicalH,w,h);
        screen.Model=Matrix4x4.Identity; screen.Matrices.Clear();screen.Style.Lit=false;screen.Style.Lights.Clear();screen.Style.Ambient=Vector3.Zero;
        // Resize attachments before callbacks can draw/read the new resolution.
        screen.Bind();UpdateState();
        if(resized) {redraw=true;if(w>0&&h>0)Event("windowResized");}
        PollInput();
    }
    private void EndFrame() { RequireSketch();if(screen.ShapeMode!=-1)throw new InvalidOperationException("Unfinished beginShape() at end of frame.");if(screen.PixelWidth<=0||screen.PixelHeight<=0)return;screen.Bind();processingDevice!.Present(screen.Target); }
    private void UpdateState() {
        processing.Create("width",N(screen.Width));processing.Create("height",N(screen.Height));
        processing.Create("pixelWidth",N(screen.PixelWidth));processing.Create("pixelHeight",N(screen.PixelHeight)); processing.Create("frameCount",N(frameCount));
        foreach(string s in new[]{"mouseX","mouseY","pmouseX","pmouseY","keyCode","mouseButton","wheelCount"}) if(processing.Get(s,out _)==null)processing.Create(s,N(0));
        foreach(string s in new[]{"isMousePressed","isKeyPressed"})if(processing.Get(s,out _)==null)processing.Create(s,new BooleanObj(false));
        if(processing.Get("key",out _)==null)processing.Create("key",new StringObj(""));
    }
    private void PollInput() {
        Native.aqua_cursor(sketchWindow!.Handle,out double x,out double y);
        processing.Create("pmouseX",new DoubleObj(previousX));processing.Create("pmouseY",new DoubleObj(previousY));
        processing.Create("mouseX",new DoubleObj(x));processing.Create("mouseY",new DoubleObj(y));
        int button=Native.aqua_mouse(sketchWindow.Handle,0)==1?0:Native.aqua_mouse(sketchWindow.Handle,1)==1?1:Native.aqua_mouse(sketchWindow.Handle,2)==1?2:-1; bool pressed=button>=0;
        processing.Create("isMousePressed",new BooleanObj(pressed)); if(pressed)processing.Create("mouseButton",N(button));
        if(pressed&&!previousMouse)Event("mousePressed");
        if(!pressed&&previousMouse){Event("mouseReleased");Event("mouseClicked");}
        if(x!=previousX||y!=previousY)Event(pressed?"mouseDragged":"mouseMoved");
        previousMouse=pressed;previousX=x;previousY=y;
        bool imeHandled=false;
        if(sketchWindow.TextInputEnabled) {
            imeHandled=((BooleanObj)processing.Get("isComposing",out _)).Value;
            // Drain the legacy queue; keyTyped is delivered from the complete text queue.
            while(Native.aqua_character()!=0) { }
            foreach(var input in ReadTextInput(sketchWindow.Handle)) {
                if(sketchWindow==null||!sketchWindow.TextInputEnabled)break;
                imeHandled|=input.Type!="textInput";
                DispatchTextInput(input);
            }
            if(sketchWindow==null)return;
        }
        for(int code=32;code<349;code++) {
            // GLFW reserves gaps in its key enumeration; skip those to avoid GLFW errors.
            if(!ValidKey(code))continue;
            bool down=Native.aqua_key(sketchWindow.Handle,code)==1;
            if(down!=keys[code]) { keys[code]=down;processing.Create("keyCode",N(code));processing.Create("key",new StringObj(code<127?((char)code).ToString():""));
                processing.Create("isKeyPressed",new BooleanObj(keys.Any(k=>k)));if(!imeHandled)Event(down?"keyPressed":"keyReleased"); }
        }
        if(!sketchWindow.TextInputEnabled) {
            int character;while((character=Native.aqua_character())!=0) {processing.Create("key",new StringObj(char.ConvertFromUtf32(character)));Event("keyTyped");}
        }
        double wheel=Native.aqua_scroll();processing.Create("wheelCount",new DoubleObj(wheel));if(wheel!=0)Event("mouseWheel");
        if(keys[256]&&!sketchWindow.TextInputEnabled)exiting=true;
    }
    private static bool ValidKey(int c)=>c is 32 or 39 or 44 or 45 or 46 or 47 or 59 or 61 or 91 or 92 or 93 or 96 or 161 or 162 || c is >=48 and <=57 or >=65 and <=90 or >=256 and <=269 or >=280 and <=284 or >=290 and <=314 or >=320 and <=336 or >=340 and <=348;
    private void CloseSketch() {
        if(sketchWindow==null)return;
        try { DisposeProcessing(); } finally { Terminate();sketchWindow=null;sketchClock.Stop();ResetProcessingTextInput(); }
    }
    private void DisposeProcessing() {
        if(sketchWindow==null)return;
        foreach(var canvas in canvases)canvas.Dispose();canvases.Clear();
        foreach(var program in processingShaders.Values)program.Dispose();processingShaders.Clear();
        foreach(var image in images.Values.Distinct())image.Dispose(screen);images.Clear();screen.Dispose();
        processingDevice?.Dispose();processingDevice=null;
    }
}
