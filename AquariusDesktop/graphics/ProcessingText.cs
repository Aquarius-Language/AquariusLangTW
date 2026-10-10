using System.Numerics;
using System.Runtime.InteropServices;
using AquariusLang.Object;
using AquaEnvironment=AquariusLang.Object.Environment;

namespace AquariusLang.Desktop.Graphics;

internal sealed partial class GraphicsRuntime {
    private void RegisterCanvasText(AquaEnvironment env,ProcessingCanvas c) {
        PAction(env,"textSize",1,1,a=> {float size=Positive(a[0]);if(size>512)throw new ArgumentException("Text size must be <=512.");c.Style.TextSize=size;c.Style.Leading=size*1.2f;});
        PAction(env,"textLeading",1,1,a=>c.Style.Leading=Positive(a[0]));
        PAction(env,"textAlign",1,2,a=> {c.Style.TextAlign=Choice(a[0],0,1,2);if(a.Length==2)c.Style.TextVertical=Choice(a[1],2,101,102,103);});
        PAction(env,"textFont",1,2,a=> {
            if(a[0] is StringObj font)c.Style.Font=font.Value;
            else if(a[0] is ModuleObj module){c.Style.Font=Text(module._Environment.Get("name",out _));c.Style.TextSize=Positive(module._Environment.Get("size",out _));}
            else throw new ArgumentException("Expected createFont() result or system font name.");
            if(a.Length==2)c.Style.TextSize=Positive(a[1]);if(c.Style.TextSize>512)throw new ArgumentException("Text size must be <=512.");c.Style.Leading=c.Style.TextSize*1.2f;
        });
        PBind(env,"textWidth",1,1,a=>new DoubleObj(a[0].Inspect().Replace("\r","").Split('\n').Max(line=>TextRaster.Measure(line,c.Style.Font,c.Style.TextSize).w)));
        PBind(env,"textAscent",0,0,_=>new DoubleObj(c.Style.TextSize*.8));PBind(env,"textDescent",0,0,_=>new DoubleObj(c.Style.TextSize*.2));
        PAction(env,"text",3,5,a=> {
            RequireSketch();c.RequireDrawing();if(!c.Style.HasFill)return;
            string text=a[0] is StringObj s?s.Value:a[0].Inspect();float x=F(a[1]),y=F(a[2]);
            float? boxW=a.Length==5?Positive(a[3]):null,boxH=a.Length==5?Positive(a[4]):null;
            var lines=new List<string>();
            foreach(string line in text.Replace("\r","").Split('\n')) {
                if(boxW==null){lines.Add(line);continue;}
                string current="";foreach(string word in line.Split(' ')){string candidate=current.Length==0?word:current+" "+word;if(current.Length>0&&TextRaster.Measure(candidate,c.Style.Font,c.Style.TextSize).w>boxW){lines.Add(current);current=word;}else current=candidate;}lines.Add(current);
            }
            float total=(lines.Count-1)*c.Style.Leading+c.Style.TextSize;
            float top=boxW!=null?y:c.Style.TextVertical==101?y:c.Style.TextVertical==102?y-total:c.Style.TextVertical==2?y-total/2:y-c.Style.TextSize*.8f;
            var oldModel=c.Model; if(a.Length==4)c.Model=Matrix4x4.CreateTranslation(0,0,F(a[3]))*c.Model;
            try {for(int line=0;line<lines.Count;line++) {
                if(boxH!=null&&line*c.Style.Leading+c.Style.TextSize>boxH)break;
                if(lines[line].Length==0)continue;
                var logical=TextRaster.Measure(lines[line],c.Style.Font,c.Style.TextSize);
                float density=TextRasterScale(c,x,top+line*c.Style.Leading);
                var raster=TextRaster.Rasterize(lines[line],c.Style.Font,c.Style.TextSize*density);var image=ProcessingImage.Create(this,raster.w,raster.h);image.Bytes=raster.bytes;
                var oldStyle=c.Style;c.Style=oldStyle.Copy();c.Style.Tint=oldStyle.Fill;c.Style.ImageMode=0;
                float left=boxW!=null?x+(c.Style.TextAlign==2?(boxW.Value-logical.w)/2:c.Style.TextAlign==1?boxW.Value-logical.w:0):x-(c.Style.TextAlign==2?logical.w/2f:c.Style.TextAlign==1?logical.w:0);
                try{c.Image(image,left,top+line*c.Style.Leading,logical.w,logical.h);}finally{c.Style=oldStyle;image.Dispose(c);}
            }}finally{c.Model=oldModel;}
        });
    }
    internal static float TextRasterScale(ProcessingCanvas canvas,float x,float y) {
        var matrix=canvas.Model*canvas.View*canvas.Projection;
        Vector2 Project(float px,float py) {
            var p=Vector4.Transform(new Vector4(px,py,0,1),matrix);
            return new Vector2(p.X/p.W*canvas.PixelWidth/2,p.Y/p.W*canvas.PixelHeight/2);
        }
        var origin=Project(x,y);
        float scale=Math.Max((Project(x+1,y)-origin).Length(),(Project(x,y+1)-origin).Length());
        // Keep layout in logical coordinates while rasterizing at display resolution.
        return float.IsFinite(scale)?Math.Clamp(MathF.Round(scale,3),1,8):1;
    }
}

// Use the OS font collection on Windows (including CJK fallback). Other hosts
// have a dependency-free small bitmap font rather than a platform font dependency.
internal static class TextRaster {
    internal static (int w,int h) Measure(string text,string font,float size) {
        if(text.Length>4096)throw new ArgumentException("Text is limited to 4096 characters per call.");
        if(!OperatingSystem.IsWindows())return (Math.Max(1,(int)Math.Ceiling(text.Length*size*6/8)),Math.Max(1,(int)Math.Ceiling(size)));
        IntPtr dc=CreateCompatibleDC(IntPtr.Zero),f=Font(font,size),old=SelectObject(dc,f);
        try {if(!GetTextExtentPoint32W(dc,text,text.Length,out var s))throw new InvalidOperationException("Font measurement failed.");return (Math.Max(1,s.X+2),Math.Max(1,s.Y));}
        finally{SelectObject(dc,old);DeleteObject(f);DeleteDC(dc);}
    }
    internal static (int w,int h,byte[] bytes) Rasterize(string text,string font,float size) {
        var (w,h)=Measure(text,font,size);if(w>8192||h>8192)throw new ArgumentException("Rendered text is too large.");var bytes=new byte[w*h*4];
        if(!OperatingSystem.IsWindows()) {
            float scale=size/8;for(int i=0;i<text.Length;i++){char key=char.ToUpperInvariant(text[i]);string pattern=Glyphs.TryGetValue(key,out var value)?value:"11111100011010110001101011000111111";
                if(key==' ')continue;for(int row=0;row<7;row++)for(int col=0;col<5;col++)if(pattern[row*5+col]=='1')
                    for(int y=(int)(row*scale);y<(int)Math.Ceiling((row+1)*scale)&&y<h;y++)for(int x=(int)((i*6+col)*scale);x<(int)Math.Ceiling((i*6+col+1)*scale)&&x<w;x++){int p=(y*w+x)*4;bytes[p]=bytes[p+1]=bytes[p+2]=bytes[p+3]=255;}}
            return(w,h,bytes);
        }
        IntPtr dc=CreateCompatibleDC(IntPtr.Zero),f=Font(font,size),oldFont=SelectObject(dc,f);IntPtr bitmap=IntPtr.Zero,oldBitmap=IntPtr.Zero;
        try {
            var info=new BitmapInfo{Size=40,Width=w,Height=-h,Planes=1,Bits=32};bitmap=CreateDIBSection(dc,ref info,0,out var pixels,IntPtr.Zero,0);
            if(bitmap==IntPtr.Zero)throw new InvalidOperationException("Font bitmap allocation failed.");oldBitmap=SelectObject(dc,bitmap);
            Marshal.Copy(new byte[bytes.Length],0,pixels,bytes.Length);SetBkMode(dc,1);SetTextColor(dc,0xFFFFFF);
            if(!TextOutW(dc,0,0,text,text.Length))throw new InvalidOperationException("Text rendering failed.");GdiFlush();Marshal.Copy(pixels,bytes,0,bytes.Length);
            for(int i=0;i<bytes.Length;i+=4){byte alpha=Math.Max(bytes[i],Math.Max(bytes[i+1],bytes[i+2]));bytes[i]=bytes[i+1]=bytes[i+2]=255;bytes[i+3]=alpha;}
            return(w,h,bytes);
        } finally {if(oldBitmap!=IntPtr.Zero)SelectObject(dc,oldBitmap);if(bitmap!=IntPtr.Zero)DeleteObject(bitmap);SelectObject(dc,oldFont);DeleteObject(f);DeleteDC(dc);}
    }
    private static IntPtr Font(string name,float size)=>CreateFontW(-(int)Math.Ceiling(size),0,0,0,400,0,0,0,1,0,0,4,0,name=="sans-serif"?"Segoe UI":name=="monospace"?"Consolas":name);
    [StructLayout(LayoutKind.Sequential)] private struct Size {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo {public uint Size;public int Width,Height;public ushort Planes,Bits;public uint Compression,ImageSize;public int Xppm,Yppm;public uint Used,Important,Color;}
    [DllImport("gdi32.dll")]private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")]private static extern IntPtr SelectObject(IntPtr dc,IntPtr o);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr CreateFontW(int h,int w,int e,int o,int weight,uint italic,uint underline,uint strike,uint charset,uint output,uint clip,uint quality,uint pitch,string name);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode)]private static extern bool GetTextExtentPoint32W(IntPtr dc,string text,int count,out Size size);
    [DllImport("gdi32.dll")]private static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfo info,uint usage,out IntPtr pixels,IntPtr section,uint offset);
    [DllImport("gdi32.dll")]private static extern int SetBkMode(IntPtr dc,int mode);
    [DllImport("gdi32.dll")]private static extern uint SetTextColor(IntPtr dc,uint color);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode)]private static extern bool TextOutW(IntPtr dc,int x,int y,string text,int count);
    [DllImport("gdi32.dll")]private static extern bool GdiFlush();
    private static readonly Dictionary<char,string> Glyphs=new() {
        ['A']="01110100011000111111100011000110001",['B']="11110100011000111110100011000111110",['C']="01111100001000010000100001000001111",
        ['D']="11110100011000110001100011000111110",['E']="11111100001000011110100001000011111",['F']="11111100001000011110100001000010000",
        ['G']="01111100001000010111100011000101111",['H']="10001100011000111111100011000110001",['I']="11111001000010000100001000010011111",
        ['J']="00111000100001000010100101001001100",['K']="10001100101010011000101001001010001",['L']="10000100001000010000100001000011111",
        ['M']="10001110111010110101100011000110001",['N']="10001110011010110011100011000110001",['O']="01110100011000110001100011000101110",
        ['P']="11110100011000111110100001000010000",['Q']="01110100011000110001101011001001101",['R']="11110100011000111110101001001010001",
        ['S']="01111100001000001110000010000111110",['T']="11111001000010000100001000010000100",['U']="10001100011000110001100011000101110",
        ['V']="10001100011000110001100010101000100",['W']="10001100011000110101101011101110001",['X']="10001100010101000100010101000110001",
        ['Y']="10001100010101000100001000010000100",['Z']="11111000010001000100010001000011111",['0']="01110100011001110101110011000101110",
        ['1']="00100011000010000100001000010001110",['2']="01110100010000100010001000100011111",['3']="11110000010000101110000010000111110",
        ['4']="00010001100101010010111110001000010",['5']="11111100001000011110000010000111110",['6']="01110100001000011110100011000101110",
        ['7']="11111000010001000100010000100001000",['8']="01110100011000101110100011000101110",['9']="01110100011000101111000010000101110",
        ['.']="00000000000000000000000000011000110",['-']="00000000000000011111000000000000000",[':']="00000001100011000000001100011000000",
        ['/']="00001000100001000100010000100010000",['|']="00100001000010000100001000010000100",['+']="00000001000010011111001000010000000"
    };
}
