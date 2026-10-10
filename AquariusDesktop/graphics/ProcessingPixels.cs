using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using AquariusLang.Object;
using AquaEnvironment=AquariusLang.Object.Environment;

namespace AquariusLang.Desktop.Graphics;

internal sealed partial class GraphicsRuntime {
    private ProcessingImage ImageObject(IObject value) {
        if(value is not ModuleObj module||!images.TryGetValue(module,out var image)||image.Owner!=this||image.Disposed)throw new ArgumentException("Expected a live PImage or PGraphics from this runtime.");
        return image;
    }
    private byte[] ImagePixels(ProcessingImage image) {
        var canvas=canvases.FirstOrDefault(c=>c.Surface==image);
        return canvas==null?image.Bytes:canvas.ReadPixels();
    }
    private static uint ImagePixelAt(ProcessingImage image,byte[] bytes,int x,int y) {
        int width=image.Canvas?.Width??image.Width,height=image.Canvas?.Height??image.Height;
        if(x<0||y<0||x>=width||y>=height)return 0;
        return PixelAt(bytes,image.Width,image.Height,x*image.Width/width,y*image.Height/height);
    }
    private ProcessingImage RegisterImage(ProcessingImage image) {
        images.Add(image.Module,image);var env=image.Module._Environment;
        PAction(env,"loadPixels",0,0,_=>env.Create("pixels",PixelArray(image.Bytes)));
        PAction(env,"updatePixels",0,0,a=> {image.Bytes=PixelBytes(Array(env.Get("pixels",out _)),image.Width*image.Height);image.Dirty=true;});
        PBind(env,"get",0,4,a=> {
            if(a.Length==2)return new DoubleObj(PixelAt(image.Bytes,image.Width,image.Height,Int(a[0]),Int(a[1])));
            if(a.Length!=0&&a.Length!=4)throw new ArgumentException("get() takes zero, two, or four arguments.");
            int x=a.Length==0?0:Int(a[0]),y=a.Length==0?0:Int(a[1]),w=a.Length==0?image.Width:Dimension(a[2]),h=a.Length==0?image.Height:Dimension(a[3]);
            var copy=ProcessingImage.Create(this,w,h);for(int j=0;j<h;j++)for(int i=0;i<w;i++)SetPixel(copy.Bytes,w,h,i,j,PixelAt(image.Bytes,image.Width,image.Height,x+i,y+j));return RegisterImage(copy).Module;
        });
        PAction(env,"set",3,3,a=> {SetPixel(image.Bytes,image.Width,image.Height,Int(a[0]),Int(a[1]),Packed(a[2]));image.Dirty=true;});
        PAction(env,"save",1,1,a=>WriteImage(Text(a[0]),image.Width,image.Height,image.Bytes));
        PAction(env,"filter",1,2,a=> {Filter(image.Bytes,image.Width,image.Height,Choice(a[0],0,1,2,3,4,5),a.Length==2?F(a[1]):.5f);image.Dirty=true;});
        PAction(env,"mask",1,1,a=> {var mask=ImageObject(a[0]);if(mask.Width!=image.Width||mask.Height!=image.Height)throw new ArgumentException("Mask dimensions must match.");var bytes=ImagePixels(mask);for(int i=0;i<image.Bytes.Length;i+=4)image.Bytes[i+3]=bytes[i+2];image.Dirty=true;});
        PAction(env,"resize",2,2,a=> {
            int w=Int(a[0]),h=Int(a[1]);if(w==0&&h>0)w=Math.Max(1,(int)Math.Round((double)image.Width*h/image.Height));if(h==0&&w>0)h=Math.Max(1,(int)Math.Round((double)image.Height*w/image.Width));
            var resized=ProcessingImage.Create(this,w,h);for(int y=0;y<h;y++)for(int x=0;x<w;x++)SetPixel(resized.Bytes,w,h,x,y,PixelAt(image.Bytes,image.Width,image.Height,x*image.Width/w,y*image.Height/h));
            image.Bytes=resized.Bytes;image.Width=w;image.Height=h;image.Dirty=true;env.Create("width",N(w));env.Create("height",N(h));env.Create("pixels",PixelArray(image.Bytes));
        });
        PAction(env,"copy",9,9,a=> {
            var src=ImageObject(a[0]);int sx=Int(a[1]),sy=Int(a[2]),sw=Dimension(a[3]),sh=Dimension(a[4]),dx=Int(a[5]),dy=Int(a[6]),dw=Dimension(a[7]),dh=Dimension(a[8]);
            byte[] source=ImagePixels(src);byte[] snapshot=src==image?(byte[])source.Clone():source;
            for(int y=0;y<dh;y++)for(int x=0;x<dw;x++)SetPixel(image.Bytes,image.Width,image.Height,dx+x,dy+y,ImagePixelAt(src,snapshot,sx+x*sw/dw,sy+y*sh/dh));image.Dirty=true;
        });
        return image;
    }
    private ProcessingImage LoadProcessingImage(string path) {
        IntPtr p=Native.aqua_image(ResourcePath(path),0,out int w,out int h);
        if(p==IntPtr.Zero)throw new IOException(Marshal.PtrToStringUTF8(Native.aqua_image_error())??"Image decode failed.");
        try {var image=ProcessingImage.Create(this,w,h);Marshal.Copy(p,image.Bytes,0,image.Bytes.Length);return RegisterImage(image);}finally{Native.aqua_image_free(p);}
    }
    private void RegisterCanvasPixels(AquaEnvironment env,ProcessingCanvas c) {
        void Action(string name,int min,int max,ProcessingAction fn)=>PAction(env,name,min,max,a=>{RequireSketch();c.RequireDrawing();fn(a);});
        void ReadAction(string name,int min,int max,ProcessingAction fn)=>PAction(env,name,min,max,a=>{RequireSketch();c.RequireReadable();fn(a);});
        ReadAction("loadPixels",0,0,_=>env.Create("pixels",PixelArray(c.ReadPixels())));
        Action("updatePixels",0,0,a=> {var pixels=env.Get("pixels",out _);if(pixels is not ArrayObj array)throw new InvalidOperationException("Call loadPixels() first.");WriteCanvasPixels(c,PixelBytes(array,c.PixelWidth*c.PixelHeight));});
        PBind(env,"get",0,4,a=> {
            RequireSketch();c.RequireReadable();byte[] bytes=c.ReadPixels();
            if(a.Length==2)return new DoubleObj(PixelAt(bytes,c.PixelWidth,c.PixelHeight,(int)(F(a[0])*c.PixelWidth/c.Width),(int)(F(a[1])*c.PixelHeight/c.Height)));
            if(a.Length!=0&&a.Length!=4)throw new ArgumentException("get() takes zero, two, or four arguments.");
            int x=a.Length==0?0:Int(a[0]),y=a.Length==0?0:Int(a[1]),w=a.Length==0?c.Width:Dimension(a[2]),h=a.Length==0?c.Height:Dimension(a[3]);
            var image=ProcessingImage.Create(this,w,h);
            for(int j=0;j<h;j++)for(int i=0;i<w;i++)SetPixel(image.Bytes,w,h,i,j,PixelAt(bytes,c.PixelWidth,c.PixelHeight,(x+i)*c.PixelWidth/c.Width,(y+j)*c.PixelHeight/c.Height));
            return RegisterImage(image).Module;
        });
        Action("set",3,3,a=> {
            if(a[2] is ModuleObj) {c.Image(ImageObject(a[2]),F(a[0]),F(a[1]));return;}
            var bytes=c.ReadPixels();SetPixel(bytes,c.PixelWidth,c.PixelHeight,Int(a[0])*c.PixelWidth/c.Width,Int(a[1])*c.PixelHeight/c.Height,Packed(a[2]));WriteCanvasPixels(c,bytes);
        });
        ReadAction("save",1,1,a=>SaveCanvas(c,Text(a[0])));
        ReadAction("saveFrame",1,1,a=> {string path=Text(a[0]);int start=path.IndexOf('#');if(start>=0){int count=0;while(start+count<path.Length&&path[start+count]=='#')count++;path=path[..start]+frameCount.ToString(new string('0',count))+path[(start+count)..];}SaveCanvas(c,path);});
        Action("filter",1,2,a=> {var bytes=c.ReadPixels();Filter(bytes,c.PixelWidth,c.PixelHeight,Choice(a[0],0,1,2,3,4,5),a.Length==2?F(a[1]):.5f);WriteCanvasPixels(c,bytes);});
        Action("copy",9,9,a=> {
            var src=ImageObject(a[0]);int sx=Int(a[1]),sy=Int(a[2]),sw=Dimension(a[3]),sh=Dimension(a[4]);
            var source=ImagePixels(src);
            var cropped=ProcessingImage.Create(this,sw,sh);try {
                for(int y=0;y<sh;y++)for(int x=0;x<sw;x++)SetPixel(cropped.Bytes,sw,sh,x,y,ImagePixelAt(src,source,sx+x,sy+y));
                c.Image(cropped,F(a[5]),F(a[6]),F(a[7]),F(a[8]));
            }finally{cropped.Dispose(c);}
        });
    }
    private static ArrayObj PixelArray(byte[] bytes) {var array=new IObject[bytes.Length/4];for(int i=0;i<array.Length;i++)array[i]=new DoubleObj((uint)bytes[i*4+3]<<24|(uint)bytes[i*4]<<16|(uint)bytes[i*4+1]<<8|bytes[i*4+2]);return new ArrayObj(array);}
    private static byte[] PixelBytes(ArrayObj array,int length) {
        if(array.Elements.Length!=length)throw new ArgumentException($"pixels must contain {length} packed ARGB colors.");
        var bytes=new byte[length*4];for(int i=0;i<length;i++){uint c=Packed(array.Elements[i]);bytes[i*4]=(byte)(c>>16);bytes[i*4+1]=(byte)(c>>8);bytes[i*4+2]=(byte)c;bytes[i*4+3]=(byte)(c>>24);}return bytes;
    }
    private static uint PixelAt(byte[] bytes,int w,int h,int x,int y) {if(x<0||y<0||x>=w||y>=h)return 0;int i=(y*w+x)*4;return (uint)bytes[i+3]<<24|(uint)bytes[i]<<16|(uint)bytes[i+1]<<8|bytes[i+2];}
    private static void SetPixel(byte[] bytes,int w,int h,int x,int y,uint c) {if(x<0||y<0||x>=w||y>=h)return;int i=(y*w+x)*4;bytes[i]=(byte)(c>>16);bytes[i+1]=(byte)(c>>8);bytes[i+2]=(byte)c;bytes[i+3]=(byte)(c>>24);}
    private void WriteCanvasPixels(ProcessingCanvas c,byte[] bytes) {
        var image=ProcessingImage.Create(this,c.PixelWidth,c.PixelHeight);image.Bytes=bytes;
        var oldModel=c.Model;var oldView=c.View;var oldProjection=c.Projection;var oldStyle=c.Style;bool old3d=c.Is3D;int blend=c.BlendMode;
        try {
            c.Model=c.View=Matrix4x4.Identity;c.Projection=ProcessingCanvas.Ortho(0,c.Width,c.Height,0,-10000,10000);c.Style=new ProcessingStyle();c.Is3D=false;c.BlendMode=4;
            c.Image(image,0,0,c.Width,c.Height);
        } finally {c.Model=oldModel;c.View=oldView;c.Projection=oldProjection;c.Style=oldStyle;c.Is3D=old3d;c.BlendMode=blend;image.Dispose(c);c.Bind();}
    }
    private void SaveCanvas(ProcessingCanvas c,string path)=>WriteImage(path,c.PixelWidth,c.PixelHeight,c.ReadPixels());
    internal static void Filter(byte[] bytes,int w,int h,int mode,float param) {
        if(mode==5) {
            int radius=Math.Clamp((int)MathF.Ceiling(param),1,32);var source=(byte[])bytes.Clone();var tmp=new byte[bytes.Length];
            for(int pass=0;pass<2;pass++) {
                var from=pass==0?source:tmp;var to=pass==0?tmp:bytes;
                for(int y=0;y<h;y++)for(int x=0;x<w;x++)for(int channel=0;channel<4;channel++) {int sum=0;for(int d=-radius;d<=radius;d++)sum+=from[(Math.Clamp(y+(pass==1?d:0),0,h-1)*w+Math.Clamp(x+(pass==0?d:0),0,w-1))*4+channel];to[(y*w+x)*4+channel]=(byte)(sum/(2*radius+1));}
            }return;
        }
        if(mode==3&&(param<2||param>255))throw new ArgumentException("POSTERIZE levels must be 2..255.");
        for(int i=0;i<bytes.Length;i+=4) {
            byte gray=(byte)Math.Clamp((int)Math.Round(bytes[i]*.299+bytes[i+1]*.587+bytes[i+2]*.114),0,255);
            if(mode==0)bytes[i]=bytes[i+1]=bytes[i+2]=gray;
            if(mode==1)for(int k=0;k<3;k++)bytes[i+k]=(byte)(255-bytes[i+k]);
            if(mode==2)bytes[i]=bytes[i+1]=bytes[i+2]=gray>=param*255?(byte)255:(byte)0;
            if(mode==3)for(int k=0;k<3;k++)bytes[i+k]=(byte)(Math.Round(bytes[i+k]/255.0*(Math.Floor(param)-1))*255/(Math.Floor(param)-1));
            if(mode==4)bytes[i+3]=255;
        }
    }
    // PNG encoder uses only the .NET runtime; RGBA and transparency are preserved.
    internal static void WriteImage(string path,int w,int h,byte[] bytes) {
        string ext=Path.GetExtension(path).ToLowerInvariant();if(ext!=".png"&&ext!=".ppm")throw new ArgumentException("save supports .png and .ppm.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output=File.Create(path);
        if(ext==".ppm") {output.Write(Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n"));for(int i=0;i<bytes.Length;i+=4)output.Write(bytes,i,3);return;}
        output.Write(AquariusLang.Application.RgbaImageEncoding.Png(new(w,h,bytes)));
    }
}
