using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
class GenerateIcon {
    static GraphicsPath Rounded(float x, float y, float w, float h, float r) {
        var path = new GraphicsPath(); float d=r*2;
        path.AddArc(x,y,d,d,180,90); path.AddArc(x+w-d,y,d,d,270,90);
        path.AddArc(x+w-d,y+h-d,d,d,0,90); path.AddArc(x,y+h-d,d,d,90,90); path.CloseFigure(); return path;
    }
    static Bitmap Draw(int size) {
        var bitmap = new Bitmap(size,size,PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(bitmap)) {
            g.SmoothingMode=SmoothingMode.AntiAlias; g.ScaleTransform(size/256f,size/256f);
            using(var page=Rounded(26,41,83,159,12)) using(var shade=new SolidBrush(Color.FromArgb(26,20,40,65))) g.FillPath(shade,page);
            using(var page=Rounded(23,36,83,159,12)) using(var brush=new LinearGradientBrush(new Point(23,36),new Point(106,195),ColorTranslator.FromHtml("#edf4fc"),ColorTranslator.FromHtml("#c9d7e9"))) {
                g.FillPath(brush,page); using(var edge=new Pen(ColorTranslator.FromHtml("#91a6bf"),2))g.DrawPath(edge,page);
            }
            using(var spine=new Pen(ColorTranslator.FromHtml("#758eac"),3))g.DrawLine(spine,36,49,36,182);
            using(var pen=new Pen(ColorTranslator.FromHtml("#768eac"),4.5f)) {
                pen.StartCap=pen.EndCap=LineCap.Round;
                g.DrawLine(pen,49,66,86,66);g.DrawLine(pen,49,86,86,86);g.DrawLine(pen,49,106,75,106);
            }
            using(var page=Rounded(151,59,83,159,12))using(var shade=new SolidBrush(Color.FromArgb(26,20,40,65)))g.FillPath(shade,page);
            using(var page=Rounded(148,54,83,159,12))using(var brush=new LinearGradientBrush(new Point(148,54),new Point(231,213),Color.White,ColorTranslator.FromHtml("#e7eef7"))) {
                g.FillPath(brush,page);using(var edge=new Pen(ColorTranslator.FromHtml("#adbdd0"),2))g.DrawPath(edge,page);
            }
            using(var spine=new Pen(ColorTranslator.FromHtml("#bbc9da"),3))g.DrawLine(spine,161,67,161,200);
            using(var pen=new Pen(ColorTranslator.FromHtml("#9aaec6"),4.5f)) {
                pen.StartCap=pen.EndCap=LineCap.Round;g.DrawLine(pen,174,84,211,84);g.DrawLine(pen,174,104,211,104);g.DrawLine(pen,174,124,200,124);
            }
            using(var pen=new Pen(ColorTranslator.FromHtml("#3478db"),10)) {
                pen.StartCap=pen.EndCap=LineCap.Round; pen.LineJoin=LineJoin.Round;
                g.DrawLine(pen,86,157,174,157);g.DrawLines(pen,new Point[]{new Point(155,138),new Point(174,157),new Point(155,176)});
            }
        } return bitmap;
    }
    static void Main(string[] args) {
        string folder=args[0]; Directory.CreateDirectory(folder);
        using(var image=Draw(256)) image.Save(Path.Combine(folder,"content-mover.png"),ImageFormat.Png);
        int[] sizes={16,24,32,48,64,128,256}; byte[][] png=new byte[sizes.Length][];
        for(int i=0;i<sizes.Length;i++) using(var image=Draw(sizes[i])) using(var stream=new MemoryStream()) {image.Save(stream,ImageFormat.Png);png[i]=stream.ToArray();}
        using(var writer=new BinaryWriter(File.Create(Path.Combine(folder,"content-mover.ico")))) {
            writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;
            for(int i=0;i<sizes.Length;i++) {writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(png[i].Length);writer.Write(offset);offset+=png[i].Length;}
            foreach(byte[] bytes in png)writer.Write(bytes);
        }
    }
}
