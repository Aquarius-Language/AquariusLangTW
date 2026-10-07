using System.Numerics;
using AquariusLang.Object;
using AquaEnvironment=AquariusLang.Object.Environment;

namespace AquariusREPL.Graphics;

internal sealed partial class GraphicsRuntime {
    private void RegisterCanvas(AquaEnvironment env,ProcessingCanvas c) {
        void Action(string name,int min,int max,Action<IObject[]> fn)=>PAction(env,name,min,max,a=> {if(c.Disposed)throw new InvalidOperationException("Canvas is disposed.");fn(a);});
        void Draw(string name,int min,int max,Action<IObject[]> fn)=>Action(name,min,max,a=> {RequireSketch();c.RequireDrawing();fn(a);});
        void Transform(string name,int min,int max,Action<IObject[]> fn)=>Action(name,min,max,a=>{if(c.ShapeMode!=-1)throw new InvalidOperationException("Transforms are not allowed between beginShape() and endShape().");fn(a);});
        void Result(string name,int min,int max,Func<IObject[],IObject> fn)=>PBind(env,name,min,max,fn);
        Vector3 Point(IObject[] a,int i,int dimensions=2)=>new(F(a[i]),F(a[i+1]),dimensions==3?F(a[i+2]):0);
        Action("colorMode",1,5,a=> {
            if(a.Length==3)throw new ArgumentException("Use mode[,max] or mode,max1,max2,max3[,maxAlpha].");
            c.Style.ColorMode=Choice(a[0],0,1);
            if(a.Length==2)c.Style.ColorMax=new Vector4(Positive(a[1]));
            if(a.Length>=4)c.Style.ColorMax=new(Positive(a[1]),Positive(a[2]),Positive(a[3]),a.Length==5?Positive(a[4]):c.Style.ColorMax.W);
        });
        Result("color",1,4,a=>new DoubleObj(ProcessingGeometry.Pack(Color(c.Style,a))));
        foreach(var channel in new[]{"red","green","blue","alpha","hue","saturation","brightness"}) {
            string name=channel;Result(name,1,1,a=> {var v=ProcessingGeometry.Unpack(Packed(a[0]));var hsb=ProcessingGeometry.RgbToHsb(v);return new DoubleObj(name switch {
                "red"=>v.X*c.Style.ColorMax.X,"green"=>v.Y*c.Style.ColorMax.Y,"blue"=>v.Z*c.Style.ColorMax.Z,"alpha"=>v.W*c.Style.ColorMax.W,
                "hue"=>hsb.X*c.Style.ColorMax.X,"saturation"=>hsb.Y*c.Style.ColorMax.Y,_=>hsb.Z*c.Style.ColorMax.Z });});
        }
        Result("lerpColor",3,3,a=> {
            var x=ProcessingGeometry.Unpack(Packed(a[0]));var y=ProcessingGeometry.Unpack(Packed(a[1]));float t=Math.Clamp(F(a[2]),0,1);
            if(c.Style.ColorMode==1) { var hx=ProcessingGeometry.RgbToHsb(x);var hy=ProcessingGeometry.RgbToHsb(y);var h=Vector3.Lerp(hx,hy,t);return new DoubleObj(ProcessingGeometry.Pack(ProcessingGeometry.Hsb(h.X,h.Y,h.Z,x.W+(y.W-x.W)*t))); }
            return new DoubleObj(ProcessingGeometry.Pack(Vector4.Lerp(x,y,t)));
        });
        Action("fill",1,4,a=>{c.Style.Fill=Color(c.Style,a);c.Style.HasFill=true;});Action("stroke",1,4,a=>{c.Style.Stroke=Color(c.Style,a);c.Style.HasStroke=true;});
        Action("noFill",0,0,_=>c.Style.HasFill=false);Action("noStroke",0,0,_=>c.Style.HasStroke=false);
        Action("tint",1,4,a=>c.Style.Tint=Color(c.Style,a));Action("noTint",0,0,_=>c.Style.Tint=Vector4.One);
        Action("strokeWeight",1,1,a=>c.Style.Weight=Nonnegative(a[0]));Action("strokeCap",1,1,a=>c.Style.Cap=Choice(a[0],0,1,2));Action("strokeJoin",1,1,a=>c.Style.Join=Choice(a[0],0,1,2));
        Action("rectMode",1,1,a=>c.Style.RectMode=Choice(a[0],0,1,2,3));Action("ellipseMode",1,1,a=>c.Style.EllipseMode=Choice(a[0],0,1,2,3));Action("imageMode",1,1,a=>c.Style.ImageMode=Choice(a[0],0,1,2));
        Draw("background",1,4,a=>c.Background(Color(c.Style,a)));Draw("clear",0,0,_=>c.Background(Vector4.Zero));
        Draw("point",2,3,a=> {if(c.Style.HasStroke)c.Disk(Point(a,0,a.Length),c.Style.Weight/2,c.Style.Stroke);});
        Draw("line",4,6,a=> {if(a.Length!=4&&a.Length!=6)throw new ArgumentException("Use 4 (2D) or 6 (3D) coordinates.");int d=a.Length/2;c.Segment(Point(a,0,d),Point(a,d,d));});
        Draw("triangle",6,6,a=>c.Polygon(new[]{Point(a,0),Point(a,2),Point(a,4)}));
        Draw("quad",8,8,a=>c.Polygon(new[]{Point(a,0),Point(a,2),Point(a,4),Point(a,6)}));
        Draw("rect",4,5,a=> {
            var b=ProcessingGeometry.Bounds(F(a[0]),F(a[1]),F(a[2]),F(a[3]),c.Style.RectMode);
            if(a.Length==4) {c.Polygon(new[]{new Vector3(b.x,b.y,0),new(b.x+b.w,b.y,0),new(b.x+b.w,b.y+b.h,0),new(b.x,b.y+b.h,0)});return;}
            float r=Math.Clamp(F(a[4]),0,Math.Min(Math.Abs(b.w),Math.Abs(b.h))/2);var pts=new List<Vector3>();
            for(int corner=0;corner<4;corner++) {float cx=corner is 0 or 1?b.x+b.w-r:b.x+r,cy=corner is 1 or 2?b.y+b.h-r:b.y+r;
                for(int i=0;i<=8;i++){float angle=(-MathF.PI/2+corner*MathF.PI/2)+i*MathF.PI/16;pts.Add(new(cx+r*MathF.Cos(angle),cy+r*MathF.Sin(angle),0));}}
            c.Polygon(pts);
        });
        Draw("ellipse",4,4,a=>c.Ellipse(F(a[0]),F(a[1]),F(a[2]),F(a[3])));
        Draw("circle",3,3,a=>c.Ellipse(F(a[0]),F(a[1]),F(a[2]),F(a[2])));
        Draw("square",3,3,a=> {var b=ProcessingGeometry.Bounds(F(a[0]),F(a[1]),F(a[2]),F(a[2]),c.Style.RectMode);c.Polygon(new[]{new Vector3(b.x,b.y,0),new(b.x+b.w,b.y,0),new(b.x+b.w,b.y+b.h,0),new(b.x,b.y+b.h,0)});});
        Draw("arc",6,7,a=>c.Ellipse(F(a[0]),F(a[1]),F(a[2]),F(a[3]),F(a[4]),F(a[5]),a.Length==7?Choice(a[6],0,1,2):0,true));
        Action("bezierDetail",1,1,a=>c.Detail=Detail(a[0]));Action("curveDetail",1,1,a=>c.Detail=Detail(a[0]));Action("curveTightness",1,1,a=>c.Tightness=F(a[0]));
        foreach(string curve in new[]{"bezier","curve"}) {
            string name=curve;Draw(name,8,12,a=> {if(a.Length!=8&&a.Length!=12)throw new ArgumentException("Use 8 (2D) or 12 (3D) coordinates.");
                int d=a.Length/4;var p=Enumerable.Range(0,4).Select(i=>Point(a,i*d,d)).ToArray();var pts=new List<Vector3>();
                for(int i=0;i<=c.Detail;i++) {float t=(float)i/c.Detail;float Component(float x,float y,float z,float w)=>name=="bezier"?ProcessingGeometry.Bezier(x,y,z,w,t):ProcessingGeometry.Curve(x,y,z,w,t,c.Tightness);
                    pts.Add(new(Component(p[0].X,p[1].X,p[2].X,p[3].X),Component(p[0].Y,p[1].Y,p[2].Y,p[3].Y),Component(p[0].Z,p[1].Z,p[2].Z,p[3].Z)));}
                c.Polyline(pts);
            });
        }
        Action("pushMatrix",0,0,_=>c.Matrices.Push(c.Model));Action("popMatrix",0,0,_=> {if(c.Matrices.Count==0)throw new InvalidOperationException("Matrix stack is empty.");c.Model=c.Matrices.Pop();});
        Action("resetMatrix",0,0,_=>c.Model=Matrix4x4.Identity);
        Action("pushStyle",0,0,_=>c.Styles.Push(c.Style.Copy()));Action("popStyle",0,0,_=> {if(c.Styles.Count==0)throw new InvalidOperationException("Style stack is empty.");c.Style=c.Styles.Pop();});
        Action("push",0,0,_=>{c.Matrices.Push(c.Model);c.Styles.Push(c.Style.Copy());});Action("pop",0,0,_=> {if(c.Matrices.Count==0||c.Styles.Count==0)throw new InvalidOperationException("push()/pop() stack is empty.");c.Model=c.Matrices.Pop();c.Style=c.Styles.Pop();});
        Transform("translate",2,3,a=>c.Model=Matrix4x4.CreateTranslation(Point(a,0,a.Length))*c.Model);
        Transform("scale",1,3,a=>c.Model=Matrix4x4.CreateScale(a.Length==1?new Vector3(F(a[0])):new Vector3(F(a[0]),F(a[1]),a.Length==3?F(a[2]):1))*c.Model);
        Transform("rotate",1,1,a=>c.Model=Matrix4x4.CreateRotationZ(F(a[0]))*c.Model);
        Transform("rotateX",1,1,a=>c.Model=Matrix4x4.CreateRotationX(F(a[0]))*c.Model);Transform("rotateY",1,1,a=>c.Model=Matrix4x4.CreateRotationY(F(a[0]))*c.Model);Transform("rotateZ",1,1,a=>c.Model=Matrix4x4.CreateRotationZ(F(a[0]))*c.Model);
        Transform("shearX",1,1,a=> {var m=Matrix4x4.Identity;m.M21=MathF.Tan(F(a[0]));c.Model=m*c.Model;});Transform("shearY",1,1,a=> {var m=Matrix4x4.Identity;m.M12=MathF.Tan(F(a[0]));c.Model=m*c.Model;});
        Transform("applyMatrix",1,1,a=>c.Model=Mat(a[0])*c.Model);Result("getMatrix",0,0,_=>Matrix(c.Model));
        foreach(string axis in new[]{"X","Y","Z"}) {string dim=axis;
            Result("model"+dim,2,3,a=> {var p=Vector3.Transform(Point(a,0,a.Length),c.Model);return new DoubleObj(dim=="X"?p.X:dim=="Y"?p.Y:p.Z);});
            Result("screen"+dim,2,3,a=> {var p=Vector4.Transform(new Vector4(Point(a,0,a.Length),1),c.Model*c.View*c.Projection);if(Math.Abs(p.W)<1e-10)throw new ArgumentException("Point projects to infinity.");return new DoubleObj(dim=="X"?(p.X/p.W+1)*c.Width/2:dim=="Y"?(1-p.Y/p.W)*c.Height/2:(p.Z/p.W+1)/2);});
        }
        Draw("camera",0,9,a=> {if(a.Length==0){c.DefaultCamera();return;}if(a.Length!=9)throw new ArgumentException("camera() takes zero or nine values.");var eye=Point(a,0,3);var center=Point(a,3,3);var up=Point(a,6,3);
            if(Vector3.Cross(center-eye,up).LengthSquared()<1e-12)throw new ArgumentException("Camera vectors are degenerate.");c.View=Matrix4x4.CreateLookAt(eye,center,up);});
        Draw("perspective",0,4,a=> {if(a.Length==0){c.Projection=ProcessingCanvas.Perspective(MathF.PI/3,(float)c.Width/c.Height,1,10000);c.Projection.M22=-c.Projection.M22;return;}
            if(a.Length!=4)throw new ArgumentException("perspective() takes zero or four values.");c.Projection=ProcessingCanvas.Perspective(F(a[0]),F(a[1]),F(a[2]),F(a[3]));c.Projection.M22=-c.Projection.M22;});
        Draw("ortho",0,6,a=> {if(a.Length==0)c.Projection=ProcessingCanvas.Ortho(0,c.Width,c.Height,0,-10000,10000);
            else if(a.Length is 4 or 6)c.Projection=ProcessingCanvas.Ortho(F(a[0]),F(a[1]),F(a[2]),F(a[3]),a.Length==6?F(a[4]):-10000,a.Length==6?F(a[5]):10000);
            else throw new ArgumentException("ortho() takes zero, four, or six values.");});
        Draw("lights",0,0,_=> {c.Style.Lit=true;c.Style.Ambient=new(.5f);c.Style.Lights.Clear();c.Style.Lights.Add(new(0,new(.5f),Vector3.Zero,new(0,0,-1),new(1,0,0),Vector3.Zero,0,0));});
        Action("noLights",0,0,_=> {c.Style.Lit=false;c.Style.Lights.Clear();c.Style.Ambient=Vector3.Zero;});
        Action("ambientLight",3,3,a=> {var color=Color(c.Style,a);if(!c.Style.Lit)c.Style.Ambient=Vector3.Zero;c.Style.Ambient+=new Vector3(color.X,color.Y,color.Z);c.Style.Lit=true;});
        void AddLight(IObject[] a,int kind) {if(c.Style.Lights.Count==8)throw new ArgumentException("A canvas supports at most eight directional, point, or spot lights.");var color=Color(c.Style,a.Take(3).ToArray());
            var position=kind==0?Vector3.Zero:Vector3.Transform(Point(a,3,3),c.Model);var direction=kind==0?Normalized(Vector3.TransformNormal(Point(a,3,3),c.Model)):kind==2?Normalized(Vector3.TransformNormal(Point(a,6,3),c.Model)):Vector3.UnitZ;
            if(!c.Style.Lit)c.Style.Ambient=Vector3.Zero;c.Style.Lit=true;c.Style.Lights.Add(new(kind,new(color.X,color.Y,color.Z),position,direction,c.Style.Falloff,c.Style.LightSpecular,kind==2?MathF.Cos(F(a[9])):0,kind==2?Positive(a[10]):0));}
        Action("directionalLight",6,6,a=>AddLight(a,0));Action("pointLight",6,6,a=>AddLight(a,1));Action("spotLight",11,11,a=>AddLight(a,2));
        Action("lightFalloff",3,3,a=> {var v=Point(a,0,3);if(v.X<0||v.Y<0||v.Z<0||v.LengthSquared()==0)throw new ArgumentException("Light falloff coefficients must be nonnegative and not all zero.");c.Style.Falloff=v;});
        Action("lightSpecular",3,3,a=> {var v=Color(c.Style,a);c.Style.LightSpecular=new(v.X,v.Y,v.Z);});
        Action("ambient",1,3,a=> {var v=Color(c.Style,a);c.Style.MaterialAmbient=new(v.X,v.Y,v.Z);});
        Action("specular",1,3,a=> {var v=Color(c.Style,a);c.Style.Specular=new(v.X,v.Y,v.Z);});Action("emissive",1,3,a=> {var v=Color(c.Style,a);c.Style.Emissive=new(v.X,v.Y,v.Z);});Action("shininess",1,1,a=>c.Style.Shininess=Positive(a[0]));
        Draw("box",1,3,a=> {if(a.Length!=1&&a.Length!=3)throw new ArgumentException("box() takes one or three dimensions.");c.Box(F(a[0]),a.Length==3?F(a[1]):F(a[0]),a.Length==3?F(a[2]):F(a[0]));});
        Draw("sphere",1,1,a=>c.Sphere(Positive(a[0])));Action("sphereDetail",1,2,a=> {c.SphereU=Detail(a[0]);c.SphereV=a.Length==2?Detail(a[1]):c.SphereU;});
        Action("beginShape",0,1,a=> {RequireSketch();c.RequireDrawing();c.ShapeMode=a.Length==0?9:Choice(a[0],0,1,4,5,6,7,8,9);c.Vertices.Clear();c.Contours.Clear();c.CurveVertices.Clear();c.InContour=false;c.Texture=null;});
        Action("beginContour",0,0,_=> {if(c.ShapeMode!=9||c.InContour)throw new InvalidOperationException("beginContour() requires a polygon and cannot be nested.");c.Contours.Add(new());c.InContour=true;});
        Action("endContour",0,0,_=> {if(!c.InContour)throw new InvalidOperationException("Call beginContour() first.");if(c.ActiveVertices.Count<3)throw new ArgumentException("Contour needs at least three vertices.");c.InContour=false;});
        Action("vertex",2,5,a=> {
            if(c.ShapeMode==-1)throw new InvalidOperationException("Call beginShape() first.");
            if(a.Length is not (2 or 3 or 4 or 5))throw new ArgumentException("Use x,y[,z][,u,v].");
            bool uv=a.Length>=4;Vector2 tex=uv?new(F(a[^2]),F(a[^1])):Vector2.Zero;
            if(uv&&c.TextureMode==1&&c.Texture!=null)tex/=new Vector2(c.Texture.Width,c.Texture.Height);
            c.ActiveVertices.Add(c.V(Point(a,0,a.Length is 3 or 5?3:2),c.Texture==null?c.Style.Fill:c.Style.Tint,tex));
        });
        Action("normal",3,3,a=>c.Normal=Normalized(Point(a,0,3)));
        Action("texture",1,1,a=> {if(c.ShapeMode==-1)throw new InvalidOperationException("texture() belongs inside beginShape().");c.Texture=ImageObject(a[0]);});
        Action("textureMode",1,1,a=>c.TextureMode=Choice(a[0],0,1));
        Action("bezierVertex",6,9,a=>c.BezierVertex(a.Select(F).ToArray()));
        Action("quadraticVertex",4,6,a=>c.QuadraticVertex(a.Select(F).ToArray()));
        Action("curveVertex",2,3,a=> {if(c.ShapeMode!=9)throw new InvalidOperationException("curveVertex() belongs in a polygon.");c.CurveVertices.Add(Point(a,0,a.Length));if(c.CurveVertices.Count>=4){var p=c.CurveVertices.TakeLast(4).ToArray();for(int i=c.CurveVertices.Count==4?0:1;i<=c.Detail;i++){float t=(float)i/c.Detail;c.ActiveVertices.Add(c.V(new(ProcessingGeometry.Curve(p[0].X,p[1].X,p[2].X,p[3].X,t,c.Tightness),ProcessingGeometry.Curve(p[0].Y,p[1].Y,p[2].Y,p[3].Y,t,c.Tightness),ProcessingGeometry.Curve(p[0].Z,p[1].Z,p[2].Z,p[3].Z,t,c.Tightness)),c.Style.Fill));}}});
        Action("endShape",0,1,a=> {if(c.ShapeMode==-1||c.InContour)throw new InvalidOperationException("Finish any contour and call beginShape() first.");c.EndShape(a.Length==1&&Choice(a[0],0,1)==1);});
        Draw("image",3,5,a=> {if(a.Length!=3&&a.Length!=5)throw new ArgumentException("image() takes image,x,y[,width,height].");c.Image(ImageObject(a[0]),F(a[1]),F(a[2]),a.Length==5?F(a[3]):null,a.Length==5?F(a[4]):null);});
        RegisterCanvasPixels(env,c);RegisterCanvasText(env,c);RegisterRetainedShapes(env,c);RegisterCanvasShaders(env,c);
        Draw("smooth",0,1,a=> {if(a.Length==1)Choice(a[0],2,4,8);c.Smooth=true;c.Bind();});Draw("noSmooth",0,0,_=> {c.Smooth=false;c.Bind();});
        Draw("clip",4,4,a=> {c.Clip=new(F(a[0]),F(a[1]),Positive(a[2]),Positive(a[3]));c.Bind();});
        Draw("noClip",0,0,_=> {c.Clip=null;c.Bind();});
        Draw("blendMode",1,1,a=>c.BlendMode=Choice(a[0],0,1,2,3,4));
    }
    private static int Detail(IObject o) {int d=Int(o);if(d<3||d>512)throw new ArgumentException("Detail must be 3..512.");return d;}
}

internal sealed partial class ProcessingCanvas {
    internal int BlendMode;
    internal void Box(float w,float h,float d) {
        var p=new[]{new Vector3(-w/2,-h/2,-d/2),new(w/2,-h/2,-d/2),new(w/2,h/2,-d/2),new(-w/2,h/2,-d/2),new(-w/2,-h/2,d/2),new(w/2,-h/2,d/2),new(w/2,h/2,d/2),new(-w/2,h/2,d/2)};
        int[][] faces={new[]{4,5,6,7},new[]{1,0,3,2},new[]{0,4,7,3},new[]{5,1,2,6},new[]{3,7,6,2},new[]{0,1,5,4}};
        var normals=new[]{Vector3.UnitZ,-Vector3.UnitZ,-Vector3.UnitX,Vector3.UnitX,Vector3.UnitY,-Vector3.UnitY};var mesh=new List<ProcessingVertex>();
        for(int f=0;f<6;f++)foreach(int i in new[]{0,1,2,0,2,3})mesh.Add(V(p[faces[f][i]],Style.Fill,normal:normals[f]));
        if(Style.HasFill)Draw(mesh);
        if(Style.HasStroke)foreach(var face in faces)Polyline(face.Select(i=>p[i]).ToArray(),true);
    }
    internal void Sphere(float radius) {
        Vector3 P(int u,int v) {float a=MathF.Tau*u/SphereU,b=MathF.PI*v/SphereV;return new(MathF.Sin(b)*MathF.Cos(a),MathF.Cos(b),MathF.Sin(b)*MathF.Sin(a));}
        var mesh=new List<ProcessingVertex>();
        for(int v=0;v<SphereV;v++)for(int u=0;u<SphereU;u++) {
            var a=P(u,v);var b=P(u+1,v);var c=P(u+1,v+1);var d=P(u,v+1);
            foreach(var n in new[]{a,b,c,a,c,d})mesh.Add(V(n*radius,Style.Fill,normal:n));
            if(Style.HasStroke){Segment(a*radius,b*radius);Segment(a*radius,d*radius);}
        }
        if(Style.HasFill)Draw(mesh);
    }
    internal void BezierVertex(float[] a) {
        if(ShapeMode!=9||ActiveVertices.Count==0||a.Length is not (6 or 9))throw new ArgumentException("bezierVertex() needs a POLYGON with an initial vertex and 6 or 9 coordinates.");
        int dim=a.Length/3;Vector3 P(int i)=>new(a[i],a[i+1],dim==3?a[i+2]:0);var start=ActiveVertices[^1].Position;var b=P(0);var c=P(dim);var d=P(dim*2);
        for(int i=1;i<=Detail;i++){float t=(float)i/Detail;ActiveVertices.Add(V(new(ProcessingGeometry.Bezier(start.X,b.X,c.X,d.X,t),ProcessingGeometry.Bezier(start.Y,b.Y,c.Y,d.Y,t),ProcessingGeometry.Bezier(start.Z,b.Z,c.Z,d.Z,t)),Style.Fill));}
    }
    internal void QuadraticVertex(float[] a) {
        if(ShapeMode!=9||ActiveVertices.Count==0||a.Length is not (4 or 6))throw new ArgumentException("quadraticVertex() needs an initial polygon vertex and 4 or 6 coordinates.");
        int dim=a.Length/2;var start=ActiveVertices[^1].Position;var control=new Vector3(a[0],a[1],dim==3?a[2]:0);var end=new Vector3(a[dim],a[dim+1],dim==3?a[dim+2]:0);
        var b=start+(control-start)*2/3;var c=end+(control-end)*2/3;BezierVertex(dim==2?new[]{b.X,b.Y,c.X,c.Y,end.X,end.Y}:new[]{b.X,b.Y,b.Z,c.X,c.Y,c.Z,end.X,end.Y,end.Z});
    }
    internal void EndShape(bool close) {
        int kind=ShapeMode;ShapeMode=-1;
        var triangles=new List<ProcessingVertex>();
        void Tri(int a,int b,int c) {triangles.Add(Vertices[a]);triangles.Add(Vertices[b]);triangles.Add(Vertices[c]);}
        if(kind==0){foreach(var v in Vertices)if(Style.HasStroke)Disk(v.Position,Style.Weight/2,Style.Stroke);return;}
        if(kind==1){if(Vertices.Count%2!=0)throw new ArgumentException("LINES requires pairs of vertices.");for(int i=0;i<Vertices.Count;i+=2)Segment(Vertices[i].Position,Vertices[i+1].Position);return;}
        if(kind==4){if(Vertices.Count%3!=0)throw new ArgumentException("TRIANGLES requires groups of three vertices.");for(int i=0;i<Vertices.Count;i+=3)Tri(i,i+1,i+2);}
        if(kind==5)for(int i=2;i<Vertices.Count;i++) {if(i%2==0)Tri(i-2,i-1,i);else Tri(i-1,i-2,i);}
        if(kind==6)for(int i=2;i<Vertices.Count;i++)Tri(0,i-1,i);
        if(kind==7){if(Vertices.Count%4!=0)throw new ArgumentException("QUADS requires groups of four vertices.");for(int i=0;i<Vertices.Count;i+=4){Tri(i,i+1,i+2);Tri(i,i+2,i+3);}}
        if(kind==8){if(Vertices.Count%2!=0)throw new ArgumentException("QUAD_STRIP requires pairs of vertices.");for(int i=2;i+1<Vertices.Count;i+=2){Tri(i-2,i-1,i+1);Tri(i-2,i+1,i);}}
        if(kind==9&&Vertices.Count>=3)foreach(int i in ProcessingGeometry.Triangulate(Vertices.Select(v=>v.Position).ToArray()))triangles.Add(Vertices[i]);
        if(Style.HasFill&&Contours.Count>0) {
            Bind();Target.ClearStencil();Draw(triangles,lit:false,stencilMode:1);
            foreach(var contour in Contours){var mesh=ProcessingGeometry.Triangulate(contour.Select(v=>v.Position).ToArray()).Select(i=>contour[i]).ToArray();Draw(mesh,lit:false,stencilMode:2);}
            Draw(triangles,image:Texture,stencilMode:3);
        } else if(Style.HasFill)Draw(triangles,image:Texture);
        if(Style.HasStroke) {
            if(kind==9)Polyline(Vertices.Select(v=>v.Position).ToArray(),close);
            else if(kind==7)for(int i=0;i<Vertices.Count;i+=4)Polyline(Vertices.Skip(i).Take(4).Select(v=>v.Position).ToArray(),true);
            else for(int i=0;i<triangles.Count;i+=3)Polyline(triangles.Skip(i).Take(3).Select(v=>v.Position).ToArray(),true);
        }
        if(Style.HasStroke)foreach(var contour in Contours)Polyline(contour.Select(v=>v.Position).ToArray(),true);
        Vertices.Clear();Contours.Clear();Texture=null;
    }
}
