using System.Numerics;
using AquariusLang.Object;
using AquaEnvironment=AquariusLang.Object.Environment;

namespace AquariusLang.Desktop.Graphics;

internal sealed partial class GraphicsRuntime {
    private sealed class RetainedShape {
        internal List<ProcessingVertex> Vertices=new();internal ProcessingStyle Style=new();internal Matrix4x4 Matrix=Matrix4x4.Identity;
        internal int Kind=9;internal bool Building,Closed;internal List<RetainedShape> Children=new();
    }
    private readonly Dictionary<ModuleObj,RetainedShape> retainedShapes=new();
    private RetainedShape ShapeObject(IObject value)=>value is ModuleObj module&&retainedShapes.TryGetValue(module,out var shape)?shape:throw new ArgumentException("Expected a PShape from this runtime.");
    private void RegisterRetainedShapes(AquaEnvironment env,ProcessingCanvas canvas) {
        PBind(env,"createShape",0,0,_=> {
            var shape=new RetainedShape();var e=AquaEnvironment.NewEnvironment();var module=new ModuleObj(e);retainedShapes.Add(module,shape);
            PAction(e,"beginShape",0,1,a=> {if(shape.Building)throw new InvalidOperationException("Shape is already being built.");shape.Kind=a.Length==0?9:Choice(a[0],0,1,4,5,6,7,8,9);shape.Vertices.Clear();shape.Building=true;});
            PAction(e,"vertex",2,3,a=> {if(!shape.Building)throw new InvalidOperationException("Call beginShape() first.");shape.Vertices.Add(new(new(F(a[0]),F(a[1]),a.Length==3?F(a[2]):0),Vector3.UnitZ,Vector2.Zero,shape.Style.Fill));});
            PAction(e,"endShape",0,1,a=> {if(!shape.Building)throw new InvalidOperationException("Call beginShape() first.");shape.Building=false;shape.Closed=a.Length==1&&Choice(a[0],0,1)==1;});
            PAction(e,"fill",1,4,a=>{shape.Style.Fill=Color(shape.Style,a);shape.Style.HasFill=true;});PAction(e,"stroke",1,4,a=>{shape.Style.Stroke=Color(shape.Style,a);shape.Style.HasStroke=true;});
            PAction(e,"noFill",0,0,_=>shape.Style.HasFill=false);PAction(e,"noStroke",0,0,_=>shape.Style.HasStroke=false);PAction(e,"strokeWeight",1,1,a=>shape.Style.Weight=Nonnegative(a[0]));
            PAction(e,"setFill",1,1,a=> {shape.Style.Fill=ProcessingGeometry.Unpack(Packed(a[0]));shape.Style.HasFill=true;shape.Vertices=shape.Vertices.Select(v=>v with{Color=shape.Style.Fill}).ToList();});
            PAction(e,"setVertex",3,4,a=> {int i=Int(a[0]);if(i<0||i>=shape.Vertices.Count)throw new ArgumentException("Vertex index out of range.");shape.Vertices[i]=shape.Vertices[i] with{Position=new(F(a[1]),F(a[2]),a.Length==4?F(a[3]):0)};});
            PBind(e,"getVertex",1,1,a=> {int i=Int(a[0]);if(i<0||i>=shape.Vertices.Count)throw new ArgumentException("Vertex index out of range.");var p=shape.Vertices[i].Position;return Numbers(p.X,p.Y,p.Z);});
            PBind(e,"getVertexCount",0,0,_=>N(shape.Vertices.Count));
            PAction(e,"translate",2,3,a=>shape.Matrix=Matrix4x4.CreateTranslation(F(a[0]),F(a[1]),a.Length==3?F(a[2]):0)*shape.Matrix);
            PAction(e,"rotate",1,1,a=>shape.Matrix=Matrix4x4.CreateRotationZ(F(a[0]))*shape.Matrix);PAction(e,"scale",1,1,a=>shape.Matrix=Matrix4x4.CreateScale(F(a[0]))*shape.Matrix);
            PAction(e,"resetMatrix",0,0,_=>shape.Matrix=Matrix4x4.Identity);
            PAction(e,"addChild",1,1,a=> {var child=ShapeObject(a[0]);bool Contains(RetainedShape s)=>s==shape||s.Children.Any(Contains);if(Contains(child))throw new ArgumentException("Shape groups cannot contain cycles.");shape.Children.Add(child);});
            return module;
        });
        PAction(env,"shape",1,3,a=> {RequireSketch();canvas.RequireDrawing();if(a.Length==2)throw new ArgumentException("shape() takes a shape[,x,y].");
            var old=canvas.Model;try{if(a.Length==3)canvas.Model=Matrix4x4.CreateTranslation(F(a[1]),F(a[2]),0)*canvas.Model;RenderShape(canvas,ShapeObject(a[0]));}finally{canvas.Model=old;}});
    }
    private static void RenderShape(ProcessingCanvas canvas,RetainedShape shape) {
        if(shape.Building)throw new InvalidOperationException("Finish the retained shape with endShape().");
        var oldStyle=canvas.Style;var oldModel=canvas.Model;
        try {canvas.Style=shape.Style.Copy();canvas.Style.Lit=oldStyle.Lit;canvas.Style.Ambient=oldStyle.Ambient;canvas.Style.Lights=new(oldStyle.Lights);canvas.Model=shape.Matrix*oldModel;canvas.Vertices.Clear();canvas.Vertices.AddRange(shape.Vertices);canvas.ShapeMode=shape.Kind;canvas.EndShape(shape.Closed);foreach(var child in shape.Children)RenderShape(canvas,child);}
        finally{canvas.Style=oldStyle;canvas.Model=oldModel;}
    }
}
