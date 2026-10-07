using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
namespace ContentMover {
    sealed class RegionSelector:Form {
        Point start,current;bool dragging;
        public Rectangle SelectedRegion;
        public RegionSelector(){FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;Bounds=SystemInformation.VirtualScreen;StartPosition=FormStartPosition.Manual;BackColor=Color.Black;Opacity=.25;Cursor=Cursors.Cross;KeyPreview=true;DoubleBuffered=true;}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Escape){DialogResult=DialogResult.Cancel;Close();}base.OnKeyDown(e);}
        protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left){start=current=e.Location;dragging=true;Capture=true;}base.OnMouseDown(e);}
        protected override void OnMouseMove(MouseEventArgs e){if(dragging){current=e.Location;Invalidate();}base.OnMouseMove(e);}
        protected override void OnMouseUp(MouseEventArgs e){if(dragging && e.Button==MouseButtons.Left){current=e.Location;dragging=false;Capture=false;Rectangle local=SelectedRect();if(local.Width>=8 && local.Height>=8){SelectedRegion=new Rectangle(PointToScreen(local.Location),local.Size);DialogResult=DialogResult.OK;Close();}else Invalidate();}base.OnMouseUp(e);}
        Rectangle SelectedRect(){return Rectangle.FromLTRB(Math.Min(start.X,current.X),Math.Min(start.Y,current.Y),Math.Max(start.X,current.X),Math.Max(start.Y,current.Y));}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using(var font=new Font("Microsoft YaHei UI",18))e.Graphics.DrawString("拖动框选论文原文 · Esc 取消",font,Brushes.White,24,24);if(dragging)using(var pen=new Pen(Color.Cyan,4))e.Graphics.DrawRectangle(pen,SelectedRect());}
    }
    sealed class OcrResult { public string Text="",Error="",Language=""; }
    static class LocalOcr {
        public static async Task<string> Read(string imagePath) {
            string script=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ocr.ps1");
            string powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
            return await Task.Run(delegate{
                using(var process=Process.Start(new ProcessStartInfo(powershell,"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""+script+"\" -ImagePath \""+imagePath+"\""){
                    UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8})) {
                    var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
                    if(!process.WaitForExit(15000)){try{process.Kill();}catch{}throw new Exception("文字识别超时，请缩小框选范围后重试。");}
                    var result=new JavaScriptSerializer().Deserialize<OcrResult>(output.GetAwaiter().GetResult());
                    if(result==null || process.ExitCode!=0 || !String.IsNullOrEmpty(result.Error))throw new Exception(result==null ? "无法启动 Windows 文字识别。" : result.Error);
                    if(String.IsNullOrWhiteSpace(result.Text))throw new Exception("没有识别出文字，请放大页面并框选清晰的原文。");
                    return result.Text;
                }
            });
        }
        public static Bitmap CaptureRegion(Rectangle region) {
            Rectangle allowed=Rectangle.Intersect(region,SystemInformation.VirtualScreen);
            if(allowed!=region || region.Width<8 || region.Height<8)throw new Exception("框选范围无效。");
            var image=new Bitmap(region.Width,region.Height);
            try{using(var graphics=Graphics.FromImage(image))graphics.CopyFromScreen(region.Location,Point.Empty,region.Size);return image;}catch{image.Dispose();throw;}
        }
    }
}
