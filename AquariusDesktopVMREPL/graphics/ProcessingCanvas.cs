using System.Numerics;
using AquariusLang.Graphics;
using AquariusLang.Object;

namespace AquariusREPL.Graphics;

internal sealed partial class ProcessingCanvas : IDisposable {
    internal WgpuDevice Device=null!;
    internal WgpuTarget Target=null!;
    private WgpuShader? program;
    internal readonly GraphicsRuntime Owner;
    internal GraphicsSurfaceSize Resolution;
    internal int Width => Resolution.Width;
    internal int Height => Resolution.Height;
    internal int PixelWidth => Resolution.PixelWidth;
    internal int PixelHeight => Resolution.PixelHeight;
    internal ProcessingImage? Surface;
    internal ProcessingStyle Style=new();
    internal Matrix4x4 Model=Matrix4x4.Identity, View=Matrix4x4.Identity, Projection;
    internal readonly Stack<Matrix4x4> Matrices=new();
    internal readonly Stack<ProcessingStyle> Styles=new();

    internal WgpuShader? CustomProgram;
    internal bool Is3D, Drawing, Disposed;
    internal int Detail=32, SphereU=24, SphereV=16, ShapeMode=-1, TextureMode;
    internal float Tightness;
    internal readonly List<ProcessingVertex> Vertices=new();
    internal readonly List<List<ProcessingVertex>> Contours=new();
    internal bool InContour;
    internal List<ProcessingVertex> ActiveVertices=>InContour?Contours[^1]:Vertices;
    internal readonly List<Vector3> CurveVertices=new();
    internal ProcessingImage? Texture;
    internal Vector3 Normal=Vector3.UnitZ;
    internal bool Smooth=true;
    internal Vector4? Clip;
    internal ModuleObj? Module;

    internal ProcessingCanvas(GraphicsRuntime owner,int w,int h,bool threeD) {
        Owner=owner; Resolution=new(w,h,w,h); Is3D=threeD; DefaultCamera();
    }
    internal void Initialize(WgpuDevice device,bool offscreen=false) {
        Device=device;
        try {
            Target=new(device,PixelWidth,PixelHeight);program=new(device,WgpuShaders.Vertex,WgpuShaders.Fragment);
            if(offscreen) {
                Surface=ProcessingImage.Create(Owner,PixelWidth,PixelHeight);Surface.Canvas=this;Surface.GpuTexture=Target.Color;
                Surface.Dirty=false;Surface.Premultiplied=true;
            }
            Bind();Background(new Vector4(.8f,.8f,.8f,1));
        } catch {Dispose();throw;}
    }
    internal void RequireReadable() {
        if(Disposed) throw new InvalidOperationException("Canvas has been disposed.");
        if(ShapeMode!=-1) throw new InvalidOperationException("Finish beginShape() with endShape() before drawing another primitive.");
    }
    internal void RequireDrawing() {
        RequireReadable();
        if(!Drawing) throw new InvalidOperationException("Call size() for the main canvas, or beginDraw() for PGraphics first.");
    }
    internal void Bind() {
        if(PixelWidth<=0||PixelHeight<=0)return;
        Target.Configure(PixelWidth,PixelHeight,Smooth);
        if(Surface!=null)Surface.GpuTexture=Target.Color;
    }
    internal bool Resize(int width,int height,int pixelWidth,int pixelHeight) {
        var next=Resolution.Resize(width,height,pixelWidth,pixelHeight);
        bool logical=Width!=next.Width||Height!=next.Height;
        bool changed=Resolution!=next;
        Resolution=next;
        if(logical)DefaultCamera();
        return changed;
    }
    internal void DefaultCamera() {
        Model=Matrix4x4.Identity;
        if(Is3D) {
            float z=Height/2f/MathF.Tan(MathF.PI/6);
            View=Matrix4x4.CreateLookAt(new(Width/2f,Height/2f,z),new(Width/2f,Height/2f,0),Vector3.UnitY);
            // Processing uses downwards screen Y, including its default P3D camera.
            Projection=Perspective(MathF.PI/3,(float)Width/Height,z/10,z*10);
            Projection.M22=-Projection.M22;
        } else { View=Matrix4x4.Identity; Projection=Ortho(0,Width,Height,0,-10000,10000); }
    }
    internal static Matrix4x4 Ortho(float l,float r,float b,float t,float n,float f) {
        if(l==r||b==t||n==f) throw new ArgumentException("Orthographic bounds must have nonzero ranges.");
        return new(2/(r-l),0,0,0,0,2/(t-b),0,0,0,0,-2/(f-n),0,-(r+l)/(r-l),-(t+b)/(t-b),-(f+n)/(f-n),1);
    }
    internal static Matrix4x4 Perspective(float fov,float aspect,float near,float far) {
        if(fov<=0||fov>=MathF.PI||aspect<=0||near<=0||far<=near) throw new ArgumentException("Invalid perspective parameters.");
        float f=1/MathF.Tan(fov/2); return new(f/aspect,0,0,0,0,f,0,0,0,0,(far+near)/(near-far),-1,0,0,2*far*near/(near-far),0);
    }
    internal void Background(Vector4 c) {Bind();Target.Clear(c);}
    internal void Draw(IReadOnlyList<ProcessingVertex> vertices,int mode=4,ProcessingImage? image=null,bool lit=true,int stencilMode=0) {
        if(vertices.Count==0)return;
        Bind();image?.Upload(this);
        var floats=new float[vertices.Count*12];int i=0;
        foreach(var v in vertices)foreach(float f in new[]{v.Position.X,v.Position.Y,v.Position.Z,v.Normal.X,v.Normal.Y,v.Normal.Z,v.UV.X,v.UV.Y,v.Color.X,v.Color.Y,v.Color.Z,v.Color.W})floats[i++]=f;
        Vector4? clip=Clip is Vector4 c?new Vector4(c.X*PixelWidth/Width,c.Y*PixelHeight/Height,c.Z*PixelWidth/Width,c.W*PixelHeight/Height):null;
        Target.Draw(CustomProgram??program!,floats,WgpuShaders.Uniforms(this,image!=null,image?.Premultiplied==true,lit),mode,BlendMode,Is3D,clip,image?.GpuTexture,stencilMode);
    }
    internal ProcessingVertex V(Vector3 p,Vector4 color,Vector2 uv=default,Vector3? normal=null) => new(p,normal??Normal,uv,color);
    internal void Polygon(IReadOnlyList<Vector3> points,bool close=true,bool fill=true) {
        RequireDrawing();
        if(Style.HasFill&&fill&&points.Count>=3) Draw(ProcessingGeometry.Triangulate(points).Select(i=>V(points[i],Style.Fill)).ToArray());
        if(Style.HasStroke) Polyline(points,close);
    }
    internal void Polyline(IReadOnlyList<Vector3> points,bool close=false) {
        if(!Style.HasStroke||points.Count<2) return;
        int cap=Style.Cap;Style.Cap=0;
        try{for(int i=0;i<points.Count-(close?0:1);i++) Segment(points[i],points[(i+1)%points.Count]);}finally{Style.Cap=cap;}
        Vector2 Direction(Vector3 a,Vector3 b) {
            if(!Is3D)return new(b.X-a.X,b.Y-a.Y);
            var matrix=Model*View*Projection;var p=Vector4.Transform(new Vector4(a,1),matrix);var q=Vector4.Transform(new Vector4(b,1),matrix);
            if(p.W<=0||q.W<=0)return Vector2.Zero;return new((q.X/q.W-p.X/p.W)*Width/2,(q.Y/q.W-p.Y/p.W)*Height/2);
        }
        Vector3 Offset(Vector3 p,Vector2 delta)=>Is3D?StrokeOffset(p,delta.X,delta.Y):p+new Vector3(delta,0);
        for(int i=close?0:1;i<(close?points.Count:points.Count-1);i++) {
            var p=points[i];if(Style.Join==1){Disk(p,Style.Weight/2,Style.Stroke);continue;}
            var a=Direction(points[(i+points.Count-1)%points.Count],p);var b=Direction(p,points[(i+1)%points.Count]);
            if(a.LengthSquared()<1e-12f||b.LengthSquared()<1e-12f)continue;a=Vector2.Normalize(a);b=Vector2.Normalize(b);
            float turn=a.X*b.Y-a.Y*b.X;if(Math.Abs(turn)<1e-6f)continue;float sign=turn>=0?-1:1;
            var n1=new Vector2(-a.Y,a.X)*sign;var n2=new Vector2(-b.Y,b.X)*sign;var edge1=n1*Style.Weight/2;var edge2=n2*Style.Weight/2;
            var middle=Vector2.Normalize(n1+n2);float denom=Vector2.Dot(middle,n2);var miter=middle*Style.Weight/2/denom;
            Vector3 center=Style.Join==0&&miter.Length()<=Style.Weight*2?Offset(p,miter):p;
            Draw(new[]{V(Offset(p,edge1),Style.Stroke),V(center,Style.Stroke),V(Offset(p,edge2),Style.Stroke)},lit:false);
        }
        if(!close&&cap==1){Disk(points[0],Style.Weight/2,Style.Stroke);Disk(points[^1],Style.Weight/2,Style.Stroke);}
        if(!close&&cap==2) {
            var first=Direction(points[0],points[1]);var last=Direction(points[^2],points[^1]);
            if(first.LengthSquared()>0)Segment(points[0],Offset(points[0],-Vector2.Normalize(first)*Style.Weight/2));
            if(last.LengthSquared()>0)Segment(points[^1],Offset(points[^1],Vector2.Normalize(last)*Style.Weight/2));
        }
    }
    internal void Segment(Vector3 a,Vector3 b) {
        if(!Style.HasStroke||Style.Weight<=0) return;
        if(Is3D) {
            var m=Model*View*Projection;var ca=Vector4.Transform(new Vector4(a,1),m);var cb=Vector4.Transform(new Vector4(b,1),m);
            if(ca.W<=0||cb.W<=0)return;
            Vector2 d2=new((cb.X/cb.W-ca.X/ca.W)*Width/2,(cb.Y/cb.W-ca.Y/ca.W)*Height/2);float length=d2.Length();
            if(length<1e-6f){Disk(a,Style.Weight/2,Style.Stroke);return;}
            var n2=new Vector2(-d2.Y,d2.X)/length*Style.Weight/2;
            var ap=StrokeOffset(a,n2.X,n2.Y);var am=StrokeOffset(a,-n2.X,-n2.Y);var bp=StrokeOffset(b,n2.X,n2.Y);var bm=StrokeOffset(b,-n2.X,-n2.Y);
            Draw(new[]{V(ap,Style.Stroke),V(am,Style.Stroke),V(bp,Style.Stroke),V(bp,Style.Stroke),V(am,Style.Stroke),V(bm,Style.Stroke)},lit:false);
            if(Style.Cap==1){Disk(a,Style.Weight/2,Style.Stroke);Disk(b,Style.Weight/2,Style.Stroke);}return;
        }
        Vector3 d=b-a; float len=new Vector2(d.X,d.Y).Length();
        if(len<1e-6f) { Disk(a,Style.Weight/2,Style.Stroke); return; }
        Vector3 n=new(-d.Y/len*Style.Weight/2,d.X/len*Style.Weight/2,0);
        if(Style.Cap==2) { Vector3 e=d/len*Style.Weight/2; a-=e; b+=e; }
        Draw(new[]{V(a+n,Style.Stroke),V(a-n,Style.Stroke),V(b+n,Style.Stroke),V(b+n,Style.Stroke),V(a-n,Style.Stroke),V(b-n,Style.Stroke)},lit:false);
        if(Style.Cap==1) { Disk(a,Style.Weight/2,Style.Stroke); Disk(b,Style.Weight/2,Style.Stroke); }
    }
    internal void Disk(Vector3 center,float radius,Vector4 color) {
        var v=new List<ProcessingVertex>();
        for(int i=0;i<16;i++) { float a=i*MathF.Tau/16,b=(i+1)*MathF.Tau/16;
            Vector3 Offset(float angle)=>Is3D?StrokeOffset(center,MathF.Cos(angle)*radius,MathF.Sin(angle)*radius):center+new Vector3(MathF.Cos(angle)*radius,MathF.Sin(angle)*radius,0);
            v.Add(V(center,color)); v.Add(V(Offset(a),color)); v.Add(V(Offset(b),color)); }
        Draw(v,lit:false);
    }
    private Vector3 StrokeOffset(Vector3 p,float x,float y) {
        var matrix=Model*View*Projection;if(!Matrix4x4.Invert(matrix,out var inverse))return p;
        var clip=Vector4.Transform(new Vector4(p,1),matrix);clip.X+=x*2/Width*clip.W;clip.Y+=y*2/Height*clip.W;
        var world=Vector4.Transform(clip,inverse);return new Vector3(world.X,world.Y,world.Z)/world.W;
    }
    internal void Ellipse(float x,float y,float w,float h,float start=0,float stop=MathF.Tau,int mode=2,bool arc=false) {
        RequireDrawing(); var b=ProcessingGeometry.Bounds(x,y,w,h,Style.EllipseMode);
        float rx=b.w/2,ry=b.h/2,cx=b.x+rx,cy=b.y+ry;
        if(stop<=start) return; stop=Math.Min(stop,start+MathF.Tau);
        int count=Math.Max(2,(int)MathF.Ceiling(Detail*(stop-start)/MathF.Tau));
        var pts=Enumerable.Range(0,count+1).Select(i=>new Vector3(cx+rx*MathF.Cos(start+(stop-start)*i/count),cy+ry*MathF.Sin(start+(stop-start)*i/count),0)).ToList();
        if(!arc||mode==2) {
            if(Style.HasFill) { var v=new List<ProcessingVertex>(); for(int i=0;i<count;i++) v.AddRange(new[]{V(new(cx,cy,0),Style.Fill),V(pts[i],Style.Fill),V(pts[i+1],Style.Fill)}); Draw(v); }
            if(Style.HasStroke) { if(arc) { var outline=new List<Vector3>{new(cx,cy,0)}; outline.AddRange(pts); Polyline(outline,true); } else Polyline(pts); }
        } else {
            if(Style.HasFill) Draw(ProcessingGeometry.Triangulate(pts).Select(i=>V(pts[i],Style.Fill)).ToArray());
            Polyline(pts,mode==1);
        }
    }
    internal void Image(ProcessingImage image,float x,float y,float? w=null,float? h=null) {
        RequireDrawing(); if(Surface==image) throw new ArgumentException("Cannot draw a canvas into itself.");
        var b=ProcessingGeometry.Bounds(x,y,w??image.Canvas?.Width??image.Width,h??image.Canvas?.Height??image.Height,Style.ImageMode);
        float t=image.Flipped?1:0,bt=1-t;
        var a=V(new(b.x,b.y,0),Style.Tint,new(0,t)); var c=V(new(b.x+b.w,b.y+b.h,0),Style.Tint,new(1,bt));
        Draw(new[]{a,V(new(b.x+b.w,b.y,0),Style.Tint,new(1,t)),c,a,c,V(new(b.x,b.y+b.h,0),Style.Tint,new(0,bt))},image:image,lit:false);
    }
    internal byte[] ReadPixels() {Bind();return Target.ReadPixels();}
    public void Dispose() {
        if(Disposed)return;Disposed=true;Target?.Dispose();program?.Dispose();Surface?.Dispose(this);
    }
}
internal sealed class ProcessingImage {
    internal readonly GraphicsRuntime Owner;
    internal int Width,Height;
    internal byte[] Bytes;
    internal WgpuTexture? GpuTexture;
    internal ProcessingCanvas? Canvas;
    internal bool Dirty=true,Flipped,Disposed,Premultiplied;
    internal ModuleObj Module;
    private ProcessingImage(GraphicsRuntime owner,int w,int h) {
        if(w<=0||h<=0||w>8192||h>8192||checked((long)w*h)>16*1024*1024) throw new ArgumentException("Image dimensions must be 1..8192 with at most 16 million pixels.");
        Owner=owner; Width=w; Height=h; Bytes=new byte[w*h*4]; Module=new ModuleObj(AquariusLang.Object.Environment.NewEnvironment());
        Module._Environment.Create("width",new IntegerObj(w)); Module._Environment.Create("height",new IntegerObj(h));
    }
    internal static ProcessingImage Create(GraphicsRuntime owner,int w,int h) => new(owner,w,h);
    internal void Upload(ProcessingCanvas canvas) {
        if(Disposed)throw new ArgumentException("Image has been disposed.");
        if(GpuTexture!=null&&(GpuTexture.Width!=Width||GpuTexture.Height!=Height)) {GpuTexture.Dispose();GpuTexture=null;}
        GpuTexture??=new WgpuTexture(canvas.Device,Width,Height);
        if(!Dirty)return;
        GpuTexture.Upload(Bytes);Dirty=false;
    }
    internal void Dispose(ProcessingCanvas canvas) {if(Disposed)return;Disposed=true;GpuTexture?.Dispose();GpuTexture=null;}
}
