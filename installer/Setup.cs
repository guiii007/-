using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
[assembly: AssemblyProduct("内容迁移安装程序")]
[assembly: AssemblyVersion("1.1.3.0")]
static class InstallCore {
    public const string RegistryPath=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\ContentMover";
    public static readonly string DefaultPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","ContentMover");
    public static string StartupPath{get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),"内容迁移.vbs");}}
    public static string ChildPath(string root,string relative) {
        string full=Path.GetFullPath(Path.Combine(root,relative));
        if(Path.IsPathRooted(relative) || !full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装包中的文件路径无效。");
        return full;
    }
    public static List<string> Extract(string root) {
        var files=new List<string>();Directory.CreateDirectory(root);
        using(var payload=Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))using(var zip=new ZipArchive(payload,ZipArchiveMode.Read)) {
            foreach(var entry in zip.Entries) {
                int slash=entry.FullName.IndexOf('/');if(slash<0)continue;
                string relative=entry.FullName.Substring(slash+1).Replace('/',Path.DirectorySeparatorChar);
                if(relative.Length==0 || entry.FullName.EndsWith("/"))continue;
                string path=ChildPath(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path));
                using(var input=entry.Open())using(var output=File.Create(path))input.CopyTo(output);
                files.Add(relative);
            }
        }
        return files;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string c,string t);
    public static void StopProduct() {
        if(FindWindow(null,"内容迁移 · 添加备注")!=IntPtr.Zero)throw new InvalidOperationException("请先保存或取消正在编辑的摘录，再继续安装。");
        foreach(var process in Process.GetProcessesByName("内容迁移"))try {
            if(FileVersionInfo.GetVersionInfo(process.MainModule.FileName).ProductName=="内容迁移"){process.Kill();process.WaitForExit(3000);}
        }catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
    }
    public static void ValidateRoot(string path) {
        string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        foreach(string forbidden in new[]{Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar),Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),Environment.GetFolderPath(Environment.SpecialFolder.Windows)})
            if(full.Equals(forbidden.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请选择独立的安装文件夹。");
    }
    static void Shortcut(string path,string executable,string root) {
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic link=shell.CreateShortcut(path);
        try{link.TargetPath=executable;link.WorkingDirectory=root;link.IconLocation=Path.Combine(root,"assets","content-mover.ico")+",0";link.Description="摘录原文与备注；运行中双击打开设置";link.Save();}finally{Marshal.FinalReleaseComObject(link);Marshal.FinalReleaseComObject(shell);}
    }
    public static void Install(string root,bool desktop,bool startup) {
        ValidateRoot(root);StopProduct();var files=Extract(root);
        string app=Path.Combine(root,"内容迁移.exe"),uninstall=Path.Combine(root,"卸载内容迁移.exe");
        File.Copy(Application.ExecutablePath,uninstall,true);files.Add("卸载内容迁移.exe");
        File.WriteAllLines(Path.Combine(root,"installed-files.txt"),files.ToArray(),Encoding.UTF8);
        if(desktop)Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"内容迁移.lnk"),app,root);
        string menu=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"内容迁移");Directory.CreateDirectory(menu);Shortcut(Path.Combine(menu,"内容迁移.lnk"),app,root);
        if(startup)File.WriteAllText(StartupPath,"CreateObject(\"WScript.Shell\").Run Chr(34) & \""+app.Replace("\"","\"\"")+"\" & Chr(34), 0, False\r\n",Encoding.Unicode);
        else if(File.Exists(StartupPath))File.Delete(StartupPath);
        using(var key=Registry.CurrentUser.CreateSubKey(RegistryPath)) {
            key.SetValue("DisplayName","内容迁移");key.SetValue("DisplayVersion","1.1.3");key.SetValue("Publisher","guiii007");key.SetValue("InstallLocation",root);
            key.SetValue("UninstallString","\""+uninstall+"\" --uninstall");key.SetValue("DisplayIcon",app+",0");key.SetValue("NoModify",1);key.SetValue("NoRepair",1);
        }
    }
    static void RemoveShortcut(string path,string root) {
        if(!File.Exists(path))return;
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic link=shell.CreateShortcut(path);
        bool owned=false;try{owned=String.Equals((string)link.TargetPath,Path.Combine(root,"内容迁移.exe"),StringComparison.OrdinalIgnoreCase);}finally{Marshal.FinalReleaseComObject(link);Marshal.FinalReleaseComObject(shell);}
        if(owned)File.Delete(path);
    }
    public static void Remove(string root) {
        using(var key=Registry.CurrentUser.OpenSubKey(RegistryPath))if(key==null || !String.Equals((string)key.GetValue("InstallLocation"),root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("安装信息不匹配，未删除任何文件。");
        StopProduct();
        RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"内容迁移.lnk"),root);
        string menu=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"内容迁移");RemoveShortcut(Path.Combine(menu,"内容迁移.lnk"),root);
        if(Directory.Exists(menu) && Directory.GetFileSystemEntries(menu).Length==0)Directory.Delete(menu);
        if(File.Exists(StartupPath) && File.ReadAllText(StartupPath).Contains(Path.Combine(root,"内容迁移.exe")))File.Delete(StartupPath);
        // Only known product files; leave config.json, personal files and desktop TXT untouched.
        foreach(string file in new[]{"内容迁移.exe","启动内容迁移.vbs","README.md","使用说明.md","ocr.ps1","assets\\content-mover.ico","assets\\content-mover.png","卸载内容迁移.exe","installed-files.txt"}) {
            string path=ChildPath(root,file);if(File.Exists(path))File.Delete(path);
        }
        string assets=ChildPath(root,"assets");if(Directory.Exists(assets) && Directory.GetFileSystemEntries(assets).Length==0)Directory.Delete(assets);
        if(Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length==0)Directory.Delete(root);
        Registry.CurrentUser.DeleteSubKeyTree(RegistryPath,false);
    }
}
sealed class SetupWizard:Form {
    int step;bool installing;
    readonly Panel body=new Panel();readonly Button next=new Button(),back=new Button();
    readonly TextBox folder=new TextBox();readonly CheckBox desktop=new CheckBox(),startup=new CheckBox(),launch=new CheckBox();
    readonly Label heading=new Label(),detail=new Label();readonly ProgressBar progress=new ProgressBar();
    public SetupWizard() {
        Text="内容迁移 · 安装向导";Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);Font=new Font("Microsoft YaHei UI",10);
        AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(570,390);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;BackColor=Color.White;
        heading.SetBounds(28,25,510,42);heading.Font=new Font(Font.FontFamily,17,FontStyle.Bold);Controls.Add(heading);
        body.SetBounds(28,83,510,232);Controls.Add(body);
        detail.SetBounds(0,0,505,92);body.Controls.Add(detail);
        folder.SetBounds(0,110,410,28);folder.Text=InstallCore.DefaultPath;body.Controls.Add(folder);
        var browse=new Button{Text="浏览…",Location=new Point(420,108),Size=new Size(85,32)};browse.Click+=delegate{using(var dialog=new FolderBrowserDialog{SelectedPath=folder.Text})if(dialog.ShowDialog()==DialogResult.OK)folder.Text=Path.Combine(dialog.SelectedPath,"ContentMover");};body.Controls.Add(browse);browse.Tag="options";
        desktop.Text="创建桌面快捷方式";desktop.Checked=true;desktop.SetBounds(0,158,350,28);body.Controls.Add(desktop);
        startup.Text="开机自动启动（安装后也可以随时开关）";startup.SetBounds(0,190,480,28);body.Controls.Add(startup);
        launch.Text="立即运行内容迁移";launch.Checked=true;launch.SetBounds(0,145,350,28);body.Controls.Add(launch);
        progress.SetBounds(0,120,505,22);body.Controls.Add(progress);
        back.Text="上一步";back.SetBounds(342,337,90,34);back.Click+=delegate{step=0;Render();};Controls.Add(back);
        next.SetBounds(444,337,98,34);next.Click+=async delegate{
            if(step==0){step=1;Render();return;}
            if(step==1){try{string path=folder.Text;bool makeDesktop=desktop.Checked,autoStart=startup.Checked;InstallCore.ValidateRoot(path);installing=true;step=2;Render();await Task.Run(delegate{InstallCore.Install(path,makeDesktop,autoStart);});installing=false;step=3;Render();}
                catch(Exception ex){installing=false;step=1;Render();MessageBox.Show(this,"安装未完成：\n"+ex.Message,"内容迁移",MessageBoxButtons.OK,MessageBoxIcon.Error);}return;}
            if(step==3){if(launch.Checked)Process.Start(new ProcessStartInfo(Path.Combine(folder.Text,"内容迁移.exe")){WorkingDirectory=folder.Text,WindowStyle=ProcessWindowStyle.Hidden});Close();}
        };Controls.Add(next);
        FormClosing+=delegate(object sender,FormClosingEventArgs e){if(installing)e.Cancel=true;};Render();
    }
    void Render() {
        foreach(Control control in body.Controls)control.Visible=false;detail.Visible=true;
        back.Visible=step==1;next.Enabled=step!=2;
        if(step==0){heading.Text="欢迎安装内容迁移";detail.Text="把选中的文字、出处和备注，保存到桌面的一个文本文件。\n\n支持浏览器、Word、AI 客户端等应用。\n无需安装浏览器扩展。";next.Text="下一步";}
        if(step==1){heading.Text="选择安装选项";detail.Text="安装到当前 Windows 用户，无需管理员权限。\n安装前请保存或取消正在编辑的摘录。\n安装位置：";folder.Visible=true;desktop.Visible=true;startup.Visible=true;foreach(Control c in body.Controls)if((string)c.Tag=="options")c.Visible=true;next.Text="安装";}
        if(step==2){heading.Text="正在安装";detail.Text="正在复制程序并创建快捷方式，请稍候…";progress.Visible=true;progress.Style=ProgressBarStyle.Marquee;next.Text="安装中";}
        if(step==3){heading.Text="安装完成";detail.Text="选中文字 → 鼠标右键 → 内容迁移 → 添加备注并保存。\n\n桌面快捷方式可打开软件设置。\n也可使用快捷键 Ctrl+Alt+M。";launch.Visible=true;next.Text="完成";}
    }
    public void RenderStep(int value){step=value;Render();}
}
static class SetupProgram {
    [STAThread]static void Main(string[] args) {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        try {
            if(args.Length>1 && args[0]=="--self-test") {
                string root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"config.json"),"preserve-settings");
                var files=InstallCore.Extract(root);
                if(!File.Exists(Path.Combine(root,"内容迁移.exe")) || File.ReadAllText(Path.Combine(root,"config.json"))!="preserve-settings" || files.Exists(x=>x.Contains("browser-extension")))throw new Exception("安装包提取或设置保留测试失败。");
                bool rejected=false;try{InstallCore.ChildPath(root,"..\\escape.txt");}catch(InvalidDataException){rejected=true;}if(!rejected)throw new Exception("路径越界保护失败。");
                File.WriteAllText(Path.Combine(root,"installer-test.txt"),"PASS: embedded payload; executable; preserved config; no extension; path traversal rejected.");return;
            }
            if(args.Length>1 && args[0]=="--render-test") {
                Directory.CreateDirectory(args[1]);using(var wizard=new SetupWizard()){wizard.Show();Application.DoEvents();for(int i=0;i<4;i++){wizard.RenderStep(i);Application.DoEvents();using(var bmp=new Bitmap(wizard.Width,wizard.Height)){wizard.DrawToBitmap(bmp,new Rectangle(Point.Empty,wizard.Size));bmp.Save(Path.Combine(args[1],"setup-"+i+".png"));}}wizard.Close();}return;
            }
            if(args.Length>0 && args[0]=="--uninstall") {
                if(MessageBox.Show("卸载内容迁移？桌面上的摘录文件和个人设置会保留。","内容迁移",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                string temporary=Path.Combine(Path.GetTempPath(),"ContentMoverUninstall-"+Guid.NewGuid().ToString("N")+".exe");File.Copy(Application.ExecutablePath,temporary);
                Process.Start(new ProcessStartInfo(temporary,"--remove \""+Path.GetDirectoryName(Application.ExecutablePath)+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});return;
            }
            if(args.Length>1 && args[0]=="--remove"){System.Threading.Thread.Sleep(900);InstallCore.Remove(args[1]);MessageBox.Show("内容迁移已卸载。个人摘录仍保留。","内容迁移");return;}
            Application.Run(new SetupWizard());
        }catch(Exception ex){if(args.Length>0 && args[0].StartsWith("--") && args[0]!="--uninstall" && args[0]!="--remove"){Console.Error.WriteLine(ex.Message);Environment.ExitCode=1;}else MessageBox.Show(ex.Message,"内容迁移",MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }
}
