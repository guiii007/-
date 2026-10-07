using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;
[assembly: System.Reflection.AssemblyProduct("内容迁移")]
[assembly: System.Reflection.AssemblyVersion("1.2.5.0")]

namespace ContentMover {
    static class AppIcon {
        public static Icon Load() {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "content-mover.ico");
            if (File.Exists(path)) using (var icon = new Icon(path, 32, 32)) return (Icon)icon.Clone();
            return (Icon)SystemIcons.Application.Clone();
        }
    }
    static class StartupManager {
        public static void Set(string startupPath, string executable, bool enabled) {
            if (!enabled) { if (File.Exists(startupPath)) File.Delete(startupPath); return; }
            string escaped = executable.Replace("\"", "\"\"");
            Directory.CreateDirectory(Path.GetDirectoryName(startupPath));
            File.WriteAllText(startupPath, "CreateObject(\"WScript.Shell\").Run Chr(34) & \"" + escaped + "\" & Chr(34), 0, False\r\n", Encoding.Unicode);
        }
    }
    static class SelectionReader {
        static readonly SemaphoreSlim Workers = new SemaphoreSlim(2, 2);
        public static async Task<SelectionResult> Start(IntPtr hwnd, Point? point, CancellationToken cancellation = default(CancellationToken),bool menuOnly=false) {
            try { await Workers.WaitAsync(cancellation); } catch(OperationCanceledException) {return new SelectionResult();}
            try {
                return await Task.Run(delegate {
                    if(cancellation.IsCancellationRequested)return new SelectionResult();
                    var info=new ProcessStartInfo(Application.ExecutablePath,"--selection-probe " + hwnd.ToInt64() + " " +
                        (point.HasValue ? point.Value.X + " " + point.Value.Y : "none")+(menuOnly ? " menu" : "")) {
                        UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                        RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8
                    };
                    using(var worker=Process.Start(info)) {
                        var output=worker.StandardOutput.ReadToEndAsync();
                        using(cancellation.Register(delegate {try{if(!worker.HasExited)worker.Kill();}catch{}})) {
                            if(!worker.WaitForExit(2200)){try{worker.Kill();}catch{}worker.WaitForExit();return new SelectionResult{MenuDiagnostics="worker timeout"};}
                            if(cancellation.IsCancellationRequested || worker.ExitCode!=0)return new SelectionResult();
                            try{return new JavaScriptSerializer().Deserialize<SelectionResult>(output.GetAwaiter().GetResult()) ?? new SelectionResult();}catch{return new SelectionResult();}
                        }
                    }
                });
            } catch {return new SelectionResult();} finally {Workers.Release();}
        }
        static string Selected(AutomationElement element, uint pid) {
            try {
                if (element == null || element.Current.IsPassword) return "";
                object pattern;
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return "";
                var parts = new StringBuilder();
                foreach (var range in ((TextPattern)pattern).GetSelection()) parts.Append(range.GetText(-1));
                return parts.ToString();
            } catch { return ""; }
        }
        static string Read(IntPtr hwnd, Point? point) {
            uint pid = Native.Pid(hwnd); if (pid == 0) return "";
            string native=Native.SelectedEditText(hwnd);if(native.Length>0)return native;
            var candidates = new List<AutomationElement>();
            if (point.HasValue) try { candidates.Add(AutomationElement.FromPoint(new System.Windows.Point(point.Value.X, point.Value.Y))); } catch { }
            try { candidates.Add(AutomationElement.FocusedElement); } catch { }
            var timer = Stopwatch.StartNew();
            foreach (var initial in candidates) {
                // Webviews may expose descendants from a renderer process. Scope by window ancestry, not PID.
                var ancestor=initial; bool belongs=false;
                for(int i=0;ancestor!=null && i<16;i++) {
                    if(ancestor.Current.NativeWindowHandle==hwnd.ToInt64()){belongs=true;break;}
                    ancestor=TreeWalker.RawViewWalker.GetParent(ancestor);
                }
                if(!belongs)continue;
                var element = initial;
                for (int i = 0; element != null && i < 12 && timer.ElapsedMilliseconds < 550; i++) {
                    string text = Selected(element, pid); if (text.Length > 0) return text;
                    if (element.Current.NativeWindowHandle == hwnd.ToInt64()) break;
                    element = TreeWalker.RawViewWalker.GetParent(element);
                }
            }
            // Webviews frequently expose the selection on their document rather than the focused element.
            var root = AutomationElement.FromHandle(hwnd);
            var queue = new Queue<AutomationElement>(); queue.Enqueue(root); int visited = 0;
            while (queue.Count > 0 && ++visited < 700 && timer.ElapsedMilliseconds < 550) {
                var element = queue.Dequeue(); string text = Selected(element, pid); if (text.Length > 0) return text;
                var child = TreeWalker.RawViewWalker.GetFirstChild(element);
                for (int i = 0; child != null && i < 80; i++) { queue.Enqueue(child); child = TreeWalker.RawViewWalker.GetNextSibling(child); }
            }
            return "";
        }
        public static SelectionResult Probe(IntPtr hwnd,Point? point,bool menuOnly=false) {
            var result=new SelectionResult();
            if(!menuOnly)try {result.Text=Read(hwnd,point);if(result.Text.Length>0){result.HasSelection=true;return result;}}catch{}
            Rectangle? menu;string diagnostics;result.HasSelection=ContextCopyEnabled(hwnd,point,out menu,out diagnostics);result.MenuDiagnostics=diagnostics;
            if(menu.HasValue){result.MenuLeft=menu.Value.Left;result.MenuTop=menu.Value.Top;result.MenuWidth=menu.Value.Width;result.MenuHeight=menu.Value.Height;}
            return result;
        }
        static bool ContextCopyEnabled(IntPtr hwnd,Point? point,out Rectangle? menuBounds,out string diagnostics) {
            menuBounds=null;
            diagnostics="";
            uint pid=Native.Pid(hwnd);if(pid==0)return false;
            string app="";try{app=Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();}catch{return false;}
            if(app=="explorer")return false;
            bool textApp=app=="msedge" || app=="chrome" || app=="firefox" || app=="brave" || app=="winword" ||
                app=="notepad" || app.Contains("deepseek") || app.Contains("chatgpt") || app.Contains("codex");
            if(!textApp)return false;
            try {
                var roots=new List<AutomationElement>();
                var sourceRoot=AutomationElement.FromHandle(hwnd);
                var localMenus=sourceRoot.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Menu));
                diagnostics="source menus="+localMenus.Count;
                foreach(AutomationElement localMenu in localMenus)if(!localMenu.Current.IsOffscreen)roots.Add(localMenu);
                Rectangle? popupBounds=point.HasValue ? Native.ContextMenuBounds(hwnd,point.Value) : (Rectangle?)null;
                if(popupBounds.HasValue){var bounds=popupBounds.Value;var hit=AutomationElement.FromPoint(new System.Windows.Point(bounds.Left+10,bounds.Top+10));
                    for(int i=0;hit!=null && i<12;i++){var rect=hit.Current.BoundingRectangle;if(rect.Width>=bounds.Width-10 && rect.Height>=bounds.Height-10){roots.Add(hit);break;}hit=TreeWalker.RawViewWalker.GetParent(hit);}}
                var menus=AutomationElement.RootElement.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Menu));
                diagnostics+="; desktop menus="+menus.Count+"; native="+popupBounds;
                foreach(AutomationElement menu in menus)if(!menu.Current.IsOffscreen && (menu.Current.ProcessId==(int)pid || (point.HasValue && menu.Current.BoundingRectangle.Contains(new System.Windows.Point(point.Value.X,point.Value.Y)))))roots.Insert(0,menu);
                if(point.HasValue) {
                    var hit=AutomationElement.FromPoint(new System.Windows.Point(point.Value.X+20,point.Value.Y+20));
                    for(int i=0;hit!=null && i<8;i++){if(hit.Current.ControlType==ControlType.Menu){roots.Insert(0,hit);break;}hit=TreeWalker.RawViewWalker.GetParent(hit);}
                }
                foreach(var root in roots) {
                    diagnostics+="; root="+root.Current.ControlType.ProgrammaticName+"/"+root.Current.ClassName;
                    var items=root.FindAll(TreeScope.Descendants,new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuItem),new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text)));
                    foreach(AutomationElement item in items) {
                        string name=item.Current.Name.Trim();int tab=name.IndexOf('\t');if(tab>=0)name=name.Substring(0,tab).Trim();
                        bool copy=System.Text.RegularExpressions.Regex.IsMatch(name.Replace("&",""),@"^(复制|Copy)\s*(\([cC]\))?(\s+Ctrl\+C)?$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if(copy && item.Current.IsEnabled && !item.Current.IsOffscreen){
                            var parent=item;
                            for(int i=0;parent!=null && i<12;i++){if(parent.Current.ControlType==ControlType.Menu){menuBounds=VisibleMenuBounds(parent,popupBounds);return true;}if(parent==root)break;parent=TreeWalker.RawViewWalker.GetParent(parent);}
                            if(item.Current.ControlType==ControlType.MenuItem){menuBounds=popupBounds;return true;}
                            var rect=item.Current.BoundingRectangle;
                            if(popupBounds.HasValue && popupBounds.Value.Contains(new Point((int)(rect.Left+rect.Width/2),(int)(rect.Top+rect.Height/2)))){menuBounds=popupBounds;return true;}
                            var ancestor=TreeWalker.ControlViewWalker.GetParent(item);
                            for(int i=0;ancestor!=null && i<8;i++){if(ancestor.Current.ControlType==ControlType.Menu)return true;if(ancestor==root)break;ancestor=TreeWalker.ControlViewWalker.GetParent(ancestor);}
                        }
                    }
                }
            }catch(Exception ex){diagnostics+="; "+ex.GetType().Name;}
            return false;
        }
        static Rectangle? VisibleMenuBounds(AutomationElement menu,Rectangle? fallback){
            Rectangle? visible=null;
            try{var items=menu.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuItem));
                foreach(AutomationElement item in items){if(item.Current.IsOffscreen)continue;var rect=item.Current.BoundingRectangle;if(rect.Width<40 || rect.Height<8)continue;
                    var bounds=Rectangle.FromLTRB((int)rect.Left,(int)rect.Top,(int)rect.Right,(int)rect.Bottom);visible=visible.HasValue ? Rectangle.Union(visible.Value,bounds) : bounds;}
                if(visible.HasValue)return visible;
                var outer=menu.Current.BoundingRectangle;if(outer.Width>40 && outer.Height>20)return Rectangle.FromLTRB((int)outer.Left,(int)outer.Top,(int)outer.Right,(int)outer.Bottom);
            }catch{}return fallback;
        }
    }
    public sealed class SelectionResult { public string Text="",MenuDiagnostics=""; public bool HasSelection;public int MenuLeft,MenuTop,MenuWidth,MenuHeight;public Rectangle? MenuBounds{get{return MenuWidth>0 && MenuHeight>0 ? new Rectangle(MenuLeft,MenuTop,MenuWidth,MenuHeight) : (Rectangle?)null;}} }
    public sealed class Settings {
        public string OutputPath = OutputFiles.DatedPath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),DateTime.Today);
        public bool CustomOutput;
        public bool RightClickEnabled = true;
    }
    static class OutputFiles {
        public static string DatedPath(string desktop,DateTime date){return Path.Combine(desktop,"内容迁移"+date.ToString("yyyy-M-d",System.Globalization.CultureInfo.InvariantCulture)+".md");}
        public static string Resolve(string path,bool custom,string desktop,DateTime date) {
            if(Path.GetExtension(path).Equals(".txt",StringComparison.OrdinalIgnoreCase))return custom ? Path.ChangeExtension(path,".md") : DatedPath(desktop,date);
            if(custom || File.Exists(path))return path;
            bool onDesktop=String.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),Path.GetFullPath(desktop).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase);
            bool defaultName=System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path),@"^内容迁移(\d{4}-\d{1,2}-\d{1,2})?\.md$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return onDesktop && defaultName ? DatedPath(desktop,date) : path;
        }
    }
    public sealed class Clip {
        public string Text = "", Title = "", App = "", Source = "", Detail = "", Fidelity = "应用复制提供的纯文本";
        public bool RequiresConfirmation;
        public bool RequiresTitle;
        public DateTime CapturedAt = DateTime.Now;
    }
    static class Storage {
        static readonly object Gate = new object();
        static string Metadata(string text){return (text ?? "").Replace("\\","\\\\").Replace("*","\\*").Replace("_","\\_").Replace("[","\\[").Replace("]","\\]").Replace("<","&lt;").Replace(">","&gt;").Replace("\r"," ").Replace("\n"," ");}
        public static string FormatMarkdown(Clip clip,string note,string source) {
            return "\r\n## "+Metadata(String.IsNullOrWhiteSpace(clip.Title) ? "内容迁移" : clip.Title)+"\r\n\r\n"+
                "- **采集时间：** "+clip.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz")+"\r\n- **应用：** "+Metadata(clip.App)+"\r\n"+
                "- **出处：** "+Metadata(String.IsNullOrEmpty(source) ? "未取得网址或文件路径，请补充出处。" : source)+"\r\n"+
                (String.IsNullOrEmpty(clip.Detail) ? "" : "- **定位信息：** "+Metadata(clip.Detail)+"\r\n")+
                "\r\n### 备注\r\n\r\n"+note+"\r\n\r\n### 原文\r\n\r\n"+clip.Text+"\r\n\r\n---\r\n";
        }
        public static string Format(Clip clip, string note, string source) {
            return "\r\n================ 内容迁移 ================\r\n" +
                "采集时间：" + clip.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz") + "\r\n" +
                "应用：" + clip.App + "\r\n标题：" + clip.Title + "\r\n" +
                "出处：" + (String.IsNullOrEmpty(source) ? "未取得网址或文件路径；请用应用和标题查找，或补充出处。" : source) + "\r\n" +
                (String.IsNullOrEmpty(clip.Detail) ? "" : "定位信息：" + clip.Detail + "\r\n") +
                "备注：\r\n" + note + "\r\n" +
                "---------------- 原文开始 ----------------\r\n" + clip.Text +
                "\r\n---------------- 原文结束 ----------------\r\n";
        }
        public static void Append(string path, Clip clip, string note, string source) {
            lock (Gate) {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                byte[] bytes = new UTF8Encoding(false).GetBytes(Path.GetExtension(path).Equals(".md",StringComparison.OrdinalIgnoreCase) ? FormatMarkdown(clip,note,source) : Format(clip, note, source));
                // One append operation; never rewrite or truncate prior entries.
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) {
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
            }
        }
    }
    static class CapturePolicy {
        public static bool UseFreshCopy(Clip clip,string text) {
            if(String.IsNullOrEmpty(text))return false;
            clip.Text=text;clip.RequiresConfirmation=false;clip.Fidelity="应用复制提供的纯文本";return true;
        }
        public static bool UseAccessibilityFallback(Clip clip,string text) {
            if(String.IsNullOrEmpty(text))return false;
            clip.Text=text;clip.RequiresConfirmation=true;clip.Fidelity="未取得本次复制文本；辅助功能读取可能与原文不同，请逐字核对。";return true;
        }
        public static bool UseClipboardFallback(Clip clip, string text) {
            if (String.IsNullOrEmpty(text)) return false;
            clip.Text = text; clip.RequiresConfirmation = true;
            clip.Fidelity = "兼容模式：已复制的剪贴板文本，请核对原文和实际出处后保存"; return true;
        }
    }
    static class ChatTitleReader {
        public static bool IsSpecific(string value) {
            if (String.IsNullOrWhiteSpace(value)) return false;
            string title=value.Trim();
            foreach (string generic in new string[]{"ChatGPT","ChatGPT - ChatGPT","聊天","Chats","Chat history","聊天记录","New chat","新聊天","新建聊天","主页","Home","设置","Settings"})
                if (title.Equals(generic,StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        public static int CandidateScore(string name, bool selected, string status, bool titleControl, bool navigation) {
            if (!IsSpecific(name) || name.Length > 200) return 0;
            if (navigation && selected) return 100;
            string state=(status??"").ToLowerInvariant();
            if (navigation && (state=="current" || state=="page" || state=="当前" || state=="当前页面")) return 90;
            return titleControl ? 80 : 0;
        }
        public static string Read(IntPtr hwnd) {
            try {
                var queue=new Queue<AutomationElement>(); queue.Enqueue(AutomationElement.FromHandle(hwnd));
                var timer=Stopwatch.StartNew(); string best=""; int score=0, visited=0;
                while(queue.Count>0 && ++visited<1400 && timer.ElapsedMilliseconds<1000) {
                    var element=queue.Dequeue();
                    try {
                        var current=element.Current; var type=current.ControlType;
                        bool navigation=type==ControlType.Hyperlink || type==ControlType.TabItem || type==ControlType.ListItem || type==ControlType.TreeItem;
                        bool selected=false; object pattern;
                        if(navigation && element.TryGetCurrentPattern(SelectionItemPattern.Pattern,out pattern)) selected=((SelectionItemPattern)pattern).Current.IsSelected;
                        string id=current.AutomationId.ToLowerInvariant();
                        bool titleControl=id=="conversation-title" || id=="chat-title" || id=="conversationtitle";
                        int candidate=CandidateScore(current.Name,selected,current.ItemStatus,titleControl,navigation);
                        if(candidate>score) {best=current.Name.Trim();score=candidate;}
                        var child=TreeWalker.RawViewWalker.GetFirstChild(element);
                        for(int i=0;child!=null && i<100;i++){queue.Enqueue(child);child=TreeWalker.RawViewWalker.GetNextSibling(child);}
                    } catch {}
                }
                return best;
            } catch {return "";}
        }
    }
    static class Native {
        public const int WH_MOUSE_LL = 14, WM_RBUTTONUP = 0x205, WM_LBUTTONDOWN = 0x201, WM_HOTKEY = 0x312;
        public const int WM_RBUTTONDOWN = 0x204;
        public delegate IntPtr HookProc(int code, IntPtr wp, IntPtr lp);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct GUIINFO {public uint size,flags;public IntPtr active,focus,capture,menu,move,caret;public RECT caretRect;}
        [StructLayout(LayoutKind.Sequential)] public struct MSG {public IntPtr hwnd;public uint message;public UIntPtr wp;public IntPtr lp;public uint time;public POINT point;}
        [StructLayout(LayoutKind.Sequential)] public struct MOUSE { public POINT pt; public uint data, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
        [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION {
            [FieldOffset(0)] public KEYBOARD ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
        }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBOARD { public ushort key, scan; public uint flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int x, y; public uint data, flags, time; public UIntPtr extra; }
        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wp, IntPtr lp);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        public delegate bool EnumWindowProc(IntPtr window,IntPtr parameter);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowProc callback,IntPtr parameter);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window,uint command);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out RECT bounds,int size);
        public static Rectangle? ContextMenuBounds(IntPtr source,Point anchor) {
            uint sourcePid=Pid(source);Rectangle? best=null;int bestDistance=Int32.MaxValue;
            EnumWindows(delegate(IntPtr window,IntPtr ignored){
                if(window==source || !IsWindowVisible(window))return true;
                if(Pid(window)!=sourcePid){IntPtr owner=GetWindow(window,4);bool belongs=false;for(int i=0;owner!=IntPtr.Zero && i<8;i++){if(owner==source || Pid(owner)==sourcePid){belongs=true;break;}owner=GetWindow(owner,4);}if(!belongs)return true;}
                var className=new StringBuilder(100);GetClassName(window,className,className.Capacity);string name=className.ToString();
                bool nativeMenu=name=="#32768";
                bool popup=(GetWindowLong(window,-16)&unchecked((int)0x80000000))!=0;
                if(!nativeMenu && !(popup && (name.StartsWith("Chrome_WidgetWin",StringComparison.Ordinal) || name.StartsWith("WindowsForms10",StringComparison.Ordinal) || name.IndexOf("NetUI",StringComparison.OrdinalIgnoreCase)>=0)))return true;
                RECT rect;if(!GetWindowRect(window,out rect))return true;var bounds=Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom);
                RECT frame;try{if(DwmGetWindowAttribute(window,9,out frame,Marshal.SizeOf(typeof(RECT)))==0 && frame.Right>frame.Left && frame.Bottom>frame.Top)bounds=Rectangle.FromLTRB(frame.Left,frame.Top,frame.Right,frame.Bottom);}catch{}
                if(bounds.Width<70 || bounds.Height<80)return true;
                if(!nativeMenu && bounds.Width>bounds.Height*3)return true;
                int distance=Math.Max(0,Math.Max(bounds.Left-anchor.X,anchor.X-bounds.Right))+Math.Max(0,Math.Max(bounds.Top-anchor.Y,anchor.Y-bounds.Bottom));
                if(distance>90)return true;
                if(distance<bestDistance || (distance==bestDistance && (!best.HasValue || (long)bounds.Width*bounds.Height>(long)best.Value.Width*best.Value.Height))){best=bounds;bestDistance=distance;}
                return true;
            },IntPtr.Zero);return best;
        }
        public static bool ActivateSource(IntPtr window) {
            uint unused;uint own=GetCurrentThreadId(),foreground=GetWindowThreadProcessId(GetForegroundWindow(),out unused),target=GetWindowThreadProcessId(window,out unused);
            bool joinedForeground=false,joinedTarget=false;
            try {
                if(foreground!=0 && foreground!=own)joinedForeground=AttachThreadInput(own,foreground,true);
                if(target!=0 && target!=own && target!=foreground)joinedTarget=AttachThreadInput(own,target,true);
                if(GetForegroundWindow()!=window)SetForegroundWindow(window);return GetForegroundWindow()==window;
            }finally{if(joinedTarget)AttachThreadInput(own,target,false);if(joinedForeground)AttachThreadInput(own,foreground,false);}
        }
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int awareness);
        public static void InitializeDpi(){try{if(SetProcessDpiAwarenessContext(new IntPtr(-4)))return;}catch{}try{if(SetProcessDpiAwareness(2)==0)return;}catch{}SetProcessDPIAware();}
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
        public static string DesktopName(IntPtr desktop) { var text = new StringBuilder(256); int needed; GetUserObjectInformation(desktop, 2, text, 512, out needed); return text.ToString(); }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint thread,uint message,IntPtr wp,IntPtr lp);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
        [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG message,IntPtr window,uint min,uint max,uint flags);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread,ref GUIINFO info);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr window,StringBuilder name,int max);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr window,int index);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr wp,IntPtr lp,uint flags,uint timeout,out UIntPtr result);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageTimeoutW")] static extern IntPtr SendMessageText(IntPtr window,uint message,IntPtr wp,StringBuilder text,uint flags,uint timeout,out UIntPtr result);
        public static string SelectedEditText(IntPtr hwnd) {
            uint pid;uint thread=GetWindowThreadProcessId(hwnd,out pid);var info=new GUIINFO{size=(uint)Marshal.SizeOf(typeof(GUIINFO))};
            var inputClass=new StringBuilder(128);GetClassName(hwnd,inputClass,inputClass.Capacity);
            if(inputClass.ToString().IndexOf("Edit",StringComparison.OrdinalIgnoreCase)>=0)info.focus=hwnd;
            else if(!GetGUIThreadInfo(thread,ref info) || info.focus==IntPtr.Zero)return "";
            var name=new StringBuilder(128);GetClassName(info.focus,name,name.Capacity);
            if(name.ToString().IndexOf("Edit",StringComparison.OrdinalIgnoreCase)<0 || (GetWindowLong(info.focus,-16)&0x20)!=0)return "";
            IntPtr start=Marshal.AllocHGlobal(4),end=Marshal.AllocHGlobal(4);
            try {
                UIntPtr result;Marshal.WriteInt32(start,0);Marshal.WriteInt32(end,0);
                if(SendMessageTimeout(info.focus,0xB0,start,end,2,180,out result)==IntPtr.Zero)return "";
                int a=Marshal.ReadInt32(start),b=Marshal.ReadInt32(end);if(b<=a)return "";
                if(SendMessageTimeout(info.focus,0xE,IntPtr.Zero,IntPtr.Zero,2,180,out result)==IntPtr.Zero)return "";
                int count=(int)result.ToUInt64();if(count<=0 || count>2000000)return "";
                var text=new StringBuilder(count+1);
                if(SendMessageText(info.focus,0xD,new IntPtr(count+1),text,2,180,out result)==IntPtr.Zero)return "";
                string full=text.ToString();return b<=full.Length ? full.Substring(a,b-a) : "";
            }finally{Marshal.FreeHGlobal(start);Marshal.FreeHGlobal(end);}
        }
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
        public static string Title(IntPtr hwnd) { var text = new StringBuilder(2048); GetWindowText(hwnd, text, text.Capacity); return text.ToString(); }
        public static uint Pid(IntPtr hwnd) { uint pid; GetWindowThreadProcessId(hwnd, out pid); return pid; }
        public static bool Keys(params ushort[] keys) {
            var inputs = new List<INPUT>();
            foreach (ushort key in keys) inputs.Add(new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBOARD { key = key } } });
            for (int i = keys.Length - 1; i >= 0; i--) inputs.Add(new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBOARD { key = keys[i], flags = 2 } } });
            return SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT))) == inputs.Count;
        }
    }
    sealed class MouseMonitor : IDisposable {
        readonly Thread thread;readonly Action<int,Native.MOUSE,IntPtr> receive;
        Native.HookProc callback;IntPtr hook;uint threadId;
        int renewals;public int Renewals{get{return Volatile.Read(ref renewals);}}
        public MouseMonitor(Action<int,Native.MOUSE,IntPtr> handler,int renewalInterval=10000) {
            receive=handler;var ready=new ManualResetEvent(false);
            thread=new Thread(delegate(){
                threadId=Native.GetCurrentThreadId();callback=OnMouse;Native.MSG ignored;Native.PeekMessage(out ignored,IntPtr.Zero,0,0,0);
                hook=Native.SetWindowsHookEx(Native.WH_MOUSE_LL,callback,Native.GetModuleHandle(null),0);ready.Set();
                if(hook!=IntPtr.Zero)using(var refresh=new System.Windows.Forms.Timer{Interval=renewalInterval}) {
                    refresh.Tick+=delegate{
                        IntPtr replacement=Native.SetWindowsHookEx(Native.WH_MOUSE_LL,callback,Native.GetModuleHandle(null),0);
                        if(replacement!=IntPtr.Zero){IntPtr previous=hook;hook=replacement;Native.UnhookWindowsHookEx(previous);Interlocked.Increment(ref renewals);}
                    };
                    refresh.Start();try{Application.Run();}finally{refresh.Stop();Native.UnhookWindowsHookEx(hook);}
                }
            });thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();
            if(!ready.WaitOne(2000) || hook==IntPtr.Zero)throw new InvalidOperationException("无法注册鼠标监听。");
        }
        IntPtr OnMouse(int code,IntPtr wp,IntPtr lp) {
            if(code>=0) {
                int message=wp.ToInt32();
                if(message==0x201 || message==0x202 || message==0x204 || message==0x205) {
                    var data=(Native.MOUSE)Marshal.PtrToStructure(lp,typeof(Native.MOUSE));
                    // Keep the low-level callback short. Resolving controls under the pointer can
                    // block on another application and make Windows silently remove the hook.
                    IntPtr window=Native.GetForegroundWindow();
                    try{receive(message,data,window);}catch{}
                }
            }
            return Native.CallNextHookEx(hook,code,wp,lp);
        }
        public void Dispose(){if(threadId!=0)Native.PostThreadMessage(threadId,0x12,IntPtr.Zero,IntPtr.Zero);}
    }
    sealed class FloatingButton : Form {
        public event Action Chosen;
        public FloatingButton() {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            AutoScaleMode = AutoScaleMode.None; BackColor = Color.FromArgb(34, 95, 175);
            Font=new Font("Microsoft YaHei UI",10); ForeColor=Color.White; Cursor=Cursors.Hand; Text="内容迁移";
            DoubleBuffered=true;
            MeasureCaption();
        }
        void MeasureCaption(){
            using(var graphics=CreateGraphics()) using(var caption=CaptionPath(graphics)) {
                var bounds=caption.GetBounds(); float scale=graphics.DpiY/96f;
                // Measure visible glyphs, not a fixed button width or invisible font side bearings.
                ClientSize=new Size((int)Math.Ceiling(bounds.Width+8*scale+11.6f*scale+20*scale),
                    (int)Math.Ceiling(Math.Max(bounds.Height,11.6f*scale)+16*scale));
            }
        }
        GraphicsPath CaptionPath(Graphics graphics) {
            var path=new GraphicsPath(); path.AddString("内容迁移",Font.FontFamily,(int)Font.Style,
                Font.SizeInPoints*graphics.DpiY/72f,PointF.Empty,StringFormat.GenericTypographic); return path;
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e); var graphics=e.Graphics; graphics.SmoothingMode=SmoothingMode.AntiAlias;
            float scale=graphics.DpiY/96f;
            using(var path=CaptionPath(graphics)) {
                var bounds=path.GetBounds();float groupWidth=bounds.Width+19.6f*scale;
                float padding=(ClientSize.Width-groupWidth)/2f;
                using(var matrix=new Matrix()){matrix.Translate(padding-bounds.Left,(ClientSize.Height-bounds.Height)/2f-bounds.Top);path.Transform(matrix);}
                using(var brush=new SolidBrush(ForeColor))graphics.FillPath(brush,path);
                float x=padding+bounds.Width+8.8f*scale, y=(ClientSize.Height-10*scale)/2f;
                using(var pen=new Pen(ForeColor,1.6f*scale)) {
                    pen.StartCap=pen.EndCap=LineCap.Round;pen.LineJoin=LineJoin.Round;
                    graphics.DrawLine(pen,x,y+10*scale,x+10*scale,y);
                    graphics.DrawLines(pen,new PointF[]{new PointF(x+4*scale,y),new PointF(x+10*scale,y),new PointF(x+10*scale,y+6*scale)});
                }
            }
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if(e.Button==MouseButtons.Left && Chosen!=null)Chosen(); }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; } }
        protected override void WndProc(ref Message message) {
            if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref message);
        }
        public static Point Placement(Point point,Size size,Rectangle area,Rectangle? menu,int gap) {
            int x,y;
            if(menu.HasValue) {
                var bounds=menu.Value;x=bounds.Left-size.Width-gap;y=bounds.Height<=size.Height*6 ? bounds.Top : Math.Max(bounds.Top,Math.Min(point.Y-size.Height/2,bounds.Bottom-size.Height));
                if(x<area.Left){x=bounds.Right+gap;if(x+size.Width>area.Right){x=bounds.Left;y=bounds.Top-size.Height-gap;if(y<area.Top)y=bounds.Bottom+gap;}}
            }else{x=point.X-size.Width-gap;y=point.Y;if(x<area.Left)x=point.X+gap;}
            return new Point(Math.Max(area.Left,Math.Min(x,area.Right-size.Width)),Math.Max(area.Top,Math.Min(y,area.Bottom-size.Height)));
        }
        public void ShowAt(Point point,Rectangle? menu=null) {
            Hide();
            // Move the hidden window to the target monitor before measuring its DPI-dependent caption.
            Native.SetWindowPos(Handle,IntPtr.Zero,point.X,point.Y,0,0,0x1|0x4|0x10);
            MeasureCaption();
            Rectangle area = Screen.FromPoint(point).WorkingArea;
            int gap;using(var graphics=CreateGraphics())gap=Math.Max(6,(int)Math.Round(8*graphics.DpiX/96f));
            Location=Placement(point,Size,area,menu,gap);
            Show();
            // Reassert topmost order after the source's native context menu has been created.
            Native.SetWindowPos(Handle,new IntPtr(-1),Left,Top,Width,Height,0x10|0x40);
            Native.ShowWindow(Handle,4);
        }
    }
    sealed class NoteDialog : Form {
        public readonly TextBox NoteBox = new TextBox(), SourceBox = new TextBox();
        public readonly TextBox TitleBox = new TextBox();
        public CheckBox ConfirmationBox;
        public Button SaveButton;
        public TextBox PreviewBox;
        readonly System.Windows.Forms.Timer focusRetry=new System.Windows.Forms.Timer{Interval=80};
        readonly bool needsTitle;
        bool inputReady;int focusAttempts;
        public NoteDialog(Clip clip, string output,IntPtr sourceWindow=default(IntPtr),Task<Clip> metadata=null) {
            needsTitle=clip.RequiresTitle;
            Icon = AppIcon.Load();
            Text = "内容迁移 · 添加备注"; Font = new Font("Microsoft YaHei UI", 9); AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(650, 645); MinimumSize = new Size(560, 570); StartPosition = FormStartPosition.CenterScreen;
            TopMost = true; MaximizeBox = false; MinimizeBox = false; BackColor = Color.White;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 12 };
            float[] heights = { 29, 25, 33, 24, 120, 29, 100, 67, 44, 24, 25, 33 };
            foreach (float h in heights) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            layout.RowStyles[4] = new RowStyle(SizeType.Percent, 60); layout.RowStyles[6] = new RowStyle(SizeType.Percent, 40);
            layout.Controls.Add(new Label { Text = "这段内容为什么值得留下？", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
            layout.Controls.Add(new Label { Text = "出处（自动取得的信息可在这里补充或修正）", Dock = DockStyle.Fill }, 0, 1);
            SourceBox.Text = clip.Source; SourceBox.Dock = DockStyle.Fill; layout.Controls.Add(SourceBox, 0, 2);
            // Insert the editable conversation/document title above the source and preview.
            var titleLabel = new Label { Text = clip.RequiresTitle ? "聊天标题（未能自动读取，请填写当前聊天的名称）" : "标题（可修改，便于以后回溯）", Dock = DockStyle.Fill,
                ForeColor = clip.RequiresTitle ? Color.FromArgb(161,78,14) : ForeColor };
            TitleBox.Text = clip.RequiresTitle ? "" : clip.Title; TitleBox.Dock = DockStyle.Fill;
            var previewHeading=new Panel{Dock=DockStyle.Fill};
            var countLabel=new Label{Text="原文预览 · "+clip.Text.Length+" 个字符",Dock=DockStyle.Fill};previewHeading.Controls.Add(countLabel);
            layout.Controls.Add(previewHeading,0,3);
            var preview=new TextBox { Text = clip.Text, ReadOnly = true, Multiline = true, WordWrap = false,
                ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, BackColor = Color.FromArgb(246, 248, 251) };
            PreviewBox=preview;
            layout.Controls.Add(preview,0,4);
            var attachments=new Dictionary<string,string>();
            var noteHeading=new Panel{Dock=DockStyle.Fill};
            var addImage=new Button{Text="插入图片…",Dock=DockStyle.Right,Width=100};
            noteHeading.Controls.Add(new Label{Text="备注（支持 Markdown；Ctrl + Enter 保存）",Dock=DockStyle.Fill,Padding=new Padding(0,7,0,0)});
            noteHeading.Controls.Add(addImage);layout.Controls.Add(noteHeading,0,5);
            addImage.Click+=delegate{using(var picker=new OpenFileDialog{Filter="图片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp"}){if(picker.ShowDialog(this)!=DialogResult.OK)return;
                string relative="assets/"+Guid.NewGuid().ToString("N")+Path.GetExtension(picker.FileName).ToLowerInvariant();attachments.Add(relative,picker.FileName);
                NoteBox.SelectedText="\r\n![图片]("+relative+")\r\n";NoteBox.Focus();}};
            NoteBox.Multiline = true; NoteBox.AcceptsReturn = true; NoteBox.ScrollBars = ScrollBars.Vertical; NoteBox.Dock = DockStyle.Fill;
            layout.Controls.Add(NoteBox, 0, 6);
            var sourcePanel = new Panel { Dock = DockStyle.Fill };
            var sourceInfo=new Label { Text = "来源窗口：" + clip.App + " · " + clip.Title + "\n" + clip.Fidelity,
                Dock = DockStyle.Top, Height = 39, AutoEllipsis = true, ForeColor = clip.RequiresConfirmation ? Color.FromArgb(161, 78, 14) : Color.DimGray };
            sourcePanel.Controls.Add(sourceInfo);
            var confirm = new CheckBox { Text = "我已核对：上方原文就是本次要迁移的内容", Dock = DockStyle.Bottom, Height = 25, Visible = clip.RequiresConfirmation };
            ConfirmationBox = confirm;
            sourcePanel.Controls.Add(confirm); layout.Controls.Add(sourcePanel, 0, 7);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var save = new Button { Text = "保存到 Markdown", AutoSize = true, Height = 32 };
            SaveButton = save;
            Action updateSave = delegate { save.Enabled = preview.Text.Length>0 && (!clip.RequiresConfirmation || confirm.Checked) && (!clip.RequiresTitle || ChatTitleReader.IsSpecific(TitleBox.Text)); };
            preview.TextChanged+=delegate{countLabel.Text="原文预览 · "+preview.Text.Length+" 个字符";updateSave();};
            confirm.CheckedChanged += delegate { updateSave(); };
            TitleBox.TextChanged += delegate { updateSave(); }; updateSave();
            save.Click += delegate {
                try { clip.Title = TitleBox.Text.Trim();if(!preview.ReadOnly)clip.Text=preview.Text;
                    foreach(var attachment in attachments){string destination=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)),attachment.Key.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(destination));if(!File.Exists(destination))File.Copy(attachment.Value,destination);}
                    Storage.Append(output, clip, NoteBox.Text, SourceBox.Text); DialogResult = DialogResult.OK; Close(); }
                catch (Exception error) { MessageBox.Show(this, "保存失败，原文和备注仍保留在这里。\n" + error.Message, "内容迁移", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            var cancel = new Button { Text = "取消", AutoSize = true, Height = 32, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(save); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 8);
            layout.Controls.Add(new Label { Text = "追加到：" + output, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.DimGray }, 0, 9);
            // Move existing rows down; preserve their height definitions and flexible preview rows.
            var originalControls = new List<Control>(); foreach (Control control in layout.Controls) originalControls.Add(control);
            foreach (Control control in originalControls) { int row = layout.GetRow(control); if (row >= 1) layout.SetRow(control, row + 2); }
            layout.RowStyles.Clear();
            float[] finalHeights = { 29, 25, 33, 25, 33, 24, 120, 29, 100, 67, 44, 24 };
            for (int i=0;i<finalHeights.Length;i++) layout.RowStyles.Add(new RowStyle(i==6 || i==8 ? SizeType.Percent : SizeType.Absolute, i==6 ? 60 : i==8 ? 40 : finalHeights[i]));
            layout.Controls.Add(titleLabel,0,1); layout.Controls.Add(TitleBox,0,2);
            Controls.Add(layout); CancelButton = cancel; KeyPreview = true;
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; save.PerformClick(); } };
            Shown += delegate { FocusInitialInput();if(!inputReady)focusRetry.Start(); };
            if(metadata!=null){string initialTitle=TitleBox.Text,initialSource=SourceBox.Text;
                Shown+=async delegate{try{if(await Task.WhenAny(metadata,Task.Delay(1800))!=metadata)return;var information=await metadata;
                    if(IsDisposed || !Visible || DialogResult!=DialogResult.None)return;
                    if(SourceBox.Text==initialSource)SourceBox.Text=information.Source;
                    if(TitleBox.Text==initialTitle && !information.RequiresTitle){TitleBox.Text=information.Title;clip.RequiresTitle=false;titleLabel.Text="标题（可修改，便于以后回溯）";titleLabel.ForeColor=ForeColor;}
                    clip.Detail=information.Detail;updateSave();
                }catch{}};
            }
            focusRetry.Tick+=delegate{FocusInitialInput();if(inputReady || ++focusAttempts>=3)focusRetry.Stop();};
            FormClosed+=delegate{focusRetry.Dispose();};
        }
        public void FocusInitialInput() {
            if(inputReady || IsDisposed || !Visible)return;
            TextBox target=needsTitle ? TitleBox : NoteBox;
            uint unusedPid;uint foregroundThread=Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out unusedPid);
            uint ownThread=Native.GetCurrentThreadId();bool attached=false;
            try {
                // The floating entry deliberately leaves the source active. Transfer actual Windows
                // keyboard ownership as well as WinForms' logical focus when opening the editor.
                if(foregroundThread!=0 && foregroundThread!=ownThread)attached=Native.AttachThreadInput(ownThread,foregroundThread,true);
                Native.SetForegroundWindow(Handle);Activate();ActiveControl=target;target.Focus();target.SelectionStart=target.Text.Length;target.SelectionLength=0;
                if(Native.GetForegroundWindow()==Handle)Native.SetFocus(target.Handle);
            }finally{if(attached)Native.AttachThreadInput(ownThread,foregroundThread,false);}
            inputReady=Native.GetForegroundWindow()==Handle && target.Focused;
        }
    }
    sealed class Controller : Form {
        readonly Settings settings;
        readonly FloatingButton floating = new FloatingButton();
        readonly NotifyIcon tray;
        readonly MouseMonitor mouseMonitor;
        readonly System.Windows.Forms.Timer expiry = new System.Windows.Forms.Timer();
        IntPtr sourceWindow;
        IntPtr selectionWindow;
        Task<SelectionResult> selectionTask;
        CancellationTokenSource probeCancellation=new CancellationTokenSource();
        Point leftDown;IntPtr leftWindow;DateTime lastSelectionGesture;
        int selectionGeneration;
        bool busy, shuttingDown;
        public bool TestMode;
        public bool AllowTestMouse;
        public Action<Clip> TestCaptured;
        public Action<string> TestError;
        static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        public static readonly uint SettingsMessage = Native.RegisterWindowMessage("ContentMover.ShowSettings.v2");
        public Controller(Settings config, bool testMode) {
            settings = config; TestMode = testMode; ShowInTaskbar = false; Opacity = 0; Size = new Size(1, 1);
            Text = "内容迁移后台服务";
            var unused = Handle;
            Icon = AppIcon.Load();
            tray = new NotifyIcon { Icon = Icon, Text = "内容迁移 · 右键摘录 / Ctrl+Alt+M", Visible = !testMode };
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开摘录文件", null, delegate { OpenFile(); });
            menu.Items.Add("更改保存文件…", null, delegate { ChangeOutput(); });
            var right = new ToolStripMenuItem("右键显示内容迁移") { Checked = settings.RightClickEnabled, CheckOnClick = true };
            right.CheckedChanged += delegate { settings.RightClickEnabled = right.Checked; floating.Hide(); SaveSettings(); }; menu.Items.Add(right);
            menu.Items.Add("设置 / 开机自启…", null, delegate { ShowSettings(); });
            var startup = new ToolStripMenuItem("开机自动启动") { Checked = File.Exists(StartupPath()) };
            startup.Click += delegate { try { StartupManager.Set(StartupPath(), Application.ExecutablePath, !startup.Checked); startup.Checked = File.Exists(StartupPath());
                    Notify("开机自启", startup.Checked ? "已开启，下次登录 Windows 自动启动。" : "已关闭。"); }
                catch (Exception ex) { MessageBox.Show("无法修改开机启动：" + ex.Message); } }; menu.Items.Add(startup);
            menu.Opening += delegate { startup.Checked = File.Exists(StartupPath()); };
            menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("退出内容迁移", null, delegate { Stop(); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { OpenFile(); };
            tray.BalloonTipClicked += delegate { OpenFile(); };
            floating.Chosen += delegate { CaptureSelection(sourceWindow, true); };
            expiry.Interval = 14000; expiry.Tick += delegate { expiry.Stop(); floating.Hide();RecordEntryStatus("entry-expired",false); };
            mouseMonitor=new MouseMonitor(delegate(int message,Native.MOUSE data,IntPtr window){
                if(!shuttingDown && (!TestMode || AllowTestMouse))try{BeginInvoke(new Action(delegate{OnMouse(message,data,window);}));}catch{}
            });
            if (!Native.RegisterHotKey(Handle, 1, 0x4003, 0x4D) && !testMode) Notify("快捷键被占用", "右键入口仍可用；Ctrl+Alt+M 已被其他程序占用。");
            if (!testMode) {
                EnsureOutput(); SaveSettings();
                Notify("内容迁移已启动", "选中文字 → 右键 → 蓝色内容迁移按钮。也可按 Ctrl+Alt+M。");
            }
        }
        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }
        void ResetProbe() {
            probeCancellation.Cancel();probeCancellation=new CancellationTokenSource();selectionTask=null;
        }
        void OnMouse(int message,Native.MOUSE data,IntPtr window) {
            if(shuttingDown || busy)return;
            var point=new Point(data.pt.X,data.pt.Y);
            if(message==Native.WM_LBUTTONDOWN) {
                if(floating.Visible && floating.Bounds.Contains(point))return;
                selectionGeneration++;floating.Hide();ResetProbe();leftDown=point;leftWindow=window;
            } else if(message==0x202 && window==leftWindow && Native.Pid(window)!=(uint)Process.GetCurrentProcess().Id) {
                if(Math.Abs(point.X-leftDown.X)+Math.Abs(point.Y-leftDown.Y)>4) {
                    selectionWindow=window;lastSelectionGesture=DateTime.UtcNow;
                    selectionTask=SelectionReader.Start(window,point,probeCancellation.Token);
                }
            } else if(message==Native.WM_RBUTTONDOWN && settings.RightClickEnabled) {
                RecordEntryStatus("right-click",false);
                floating.Hide();
                if(window==IntPtr.Zero || Native.Pid(window)==(uint)Process.GetCurrentProcess().Id){selectionGeneration++;ResetProbe();selectionWindow=IntPtr.Zero;return;}
                if(selectionWindow!=window || DateTime.UtcNow-lastSelectionGesture>TimeSpan.FromSeconds(3)) {
                    selectionGeneration++;ResetProbe();selectionWindow=window;
                    selectionTask=SelectionReader.Start(window,point,probeCancellation.Token);
                }
            } else if(message==Native.WM_RBUTTONUP && settings.RightClickEnabled) {
                // Use the source captured before the menu opens, rather than its changed foreground window.
                IntPtr source=selectionWindow;
                if(source==IntPtr.Zero || Native.Pid(source)==(uint)Process.GetCurrentProcess().Id)return;
                if(selectionTask==null)selectionTask=SelectionReader.Start(source,point,probeCancellation.Token);
                ShowForSelection(source,point,selectionTask,selectionGeneration);
            }
        }
        protected override void WndProc(ref Message message) {
            if (message.Msg == Native.WM_HOTKEY && !busy) CaptureSelection(Native.GetForegroundWindow(), false);
            if ((uint)message.Msg == SettingsMessage && !busy) BeginInvoke(new Action(ShowSettings));
            base.WndProc(ref message);
        }
        public bool ButtonVisible { get { return floating.Visible; } }
        public void TestEntry(string text) { ShowForSelection(Handle,new Point(300,300),Task.FromResult(new SelectionResult{Text=text,HasSelection=text.Length>0}),selectionGeneration); }
        public void TestDelayedEntry(Task<SelectionResult> result){floating.Hide();ShowForSelection(Handle,new Point(300,300),result,++selectionGeneration);}
        public void TestInvalidateEntry(){selectionGeneration++;floating.Hide();}
        async void ShowForSelection(IntPtr hwnd, Point point, Task<SelectionResult> selection, int generation) {
            if(selection==null)return;
            try {
                var result=await selection;
                RecordEntryStatus("selection-read",result.HasSelection);
                if(!TestMode && !result.HasSelection && generation==selectionGeneration && !shuttingDown && !busy) {
                    await Task.Delay(100);
                    if(generation!=selectionGeneration || shuttingDown || busy)return;
                    selection=SelectionReader.Start(hwnd,point,probeCancellation.Token,true);selectionTask=selection;
                    result=await selection;
                }
                if(!result.HasSelection || generation!=selectionGeneration || busy || shuttingDown || !settings.RightClickEnabled)return;
                Rectangle? menu=Native.ContextMenuBounds(hwnd,point);
                Task<SelectionResult> pendingMenu=null;
                if(result.MenuBounds.HasValue)menu=result.MenuBounds;
                if(!TestMode){pendingMenu=SelectionReader.Start(hwnd,point,probeCancellation.Token,true);
                    var menuResult=await pendingMenu;RecordMenuProbe(hwnd,point,menuResult);var precise=menuResult.MenuBounds;if(precise.HasValue)menu=precise;
                    if(generation!=selectionGeneration || shuttingDown || busy)return;
                }
                if(!menu.HasValue && !TestMode){await Task.Delay(80);if(generation!=selectionGeneration || shuttingDown || busy)return;menu=Native.ContextMenuBounds(hwnd,point);}
                sourceWindow=hwnd; floating.ShowAt(point,menu); expiry.Stop(); expiry.Start();
                RecordEntryStatus("entry-shown",true);
            } catch { }
        }
        void RecordMenuProbe(IntPtr source,Point point,SelectionResult result){if(TestMode)return;try{File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"menu-probe.status.json"),new JavaScriptSerializer().Serialize(new{Time=DateTimeOffset.Now.ToString("o"),Source=source.ToInt64(),Point=point,Menu=result.MenuBounds,Diagnostics=result.MenuDiagnostics}));}catch{}}
        void RecordEntryStatus(string stage,bool hasSelection){if(TestMode)return;try{File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"right-click.status.json"),new JavaScriptSerializer().Serialize(new{Time=DateTimeOffset.Now.ToString("o"),Stage=stage,HasSelection=hasSelection,HookRenewals=mouseMonitor.Renewals,Window=floating.Handle.ToInt64(),Visible=Native.IsWindowVisible(floating.Handle),Left=floating.Left,Top=floating.Top,Width=floating.Width,Height=floating.Height,Desktop=Native.DesktopName(Native.GetThreadDesktop(Native.GetCurrentThreadId()))}));}catch{}}
        public Rectangle ButtonBounds { get { return floating.Bounds; } }
        public void Stop() { if(!shuttingDown){shuttingDown=true;probeCancellation.Cancel();mouseMonitor.Dispose();}Close();Dispose();Application.ExitThread(); }
        public void ClickCaptureForTest() { CaptureSelection(sourceWindow, true); }
        public void SetSelectionForCopyTest(IntPtr window,string text){selectionWindow=window;selectionTask=Task.FromResult(new SelectionResult{Text=text,HasSelection=true});}
        public async void CaptureSelection(IntPtr hwnd, bool dismissMenu) {
            if (busy || hwnd == IntPtr.Zero || Native.Pid(hwnd) == (uint)Process.GetCurrentProcess().Id) return;
            busy = true; expiry.Stop(); floating.Hide();
            var clip = new Clip { Title = Native.Title(hwnd) };
            uint pid = Native.Pid(hwnd);
            try { clip.App = Process.GetProcessById((int)pid).ProcessName; } catch { clip.App = "未知应用"; }
            clip.RequiresTitle=clip.App.Equals("ChatGPT",StringComparison.OrdinalIgnoreCase) && !ChatTitleReader.IsSpecific(clip.Title);
            IDataObject previous = null;
            uint sequenceAfterCopy = 0;
            bool copied = false;
            string clipboardFallback = "";
            string accessibilityFallback="";
            try {
                // Read before Escape can collapse a webview selection. Never block the mouse hook.
                Task<SelectionResult> selection = dismissMenu && selectionWindow == hwnd && selectionTask != null
                    ? selectionTask : SelectionReader.Start(hwnd, null);
                accessibilityFallback=(await selection).Text;
            selectionTask = null;
                if(!String.IsNullOrEmpty(accessibilityFallback)) {
                    clip.Text=accessibilityFallback;clip.Fidelity="应用提供的选区文本";
                    if(dismissMenu)Native.PostMessage(hwnd,0x1F,IntPtr.Zero,IntPtr.Zero);
                    busy=false;EditClip(clip,hwnd,StartMetadata(hwnd,pid,clip));return;
                }
                try { if (Clipboard.ContainsText(TextDataFormat.UnicodeText)) clipboardFallback = Clipboard.GetText(TextDataFormat.UnicodeText); } catch (ExternalException) { }
                try {
                    // Materialize all clipboard formats while their owner is still available.
                    var original = Clipboard.GetDataObject();
                    if (original != null) { var snapshot = new DataObject();
                        foreach (string format in original.GetFormats(false)) {
                            try { object data = original.GetData(format, false); if (data != null) snapshot.SetData(format, false, data); } catch { }
                        } previous = snapshot;
                    }
                } catch { /* Capturing must still work when another app temporarily locks the clipboard. */ }
                // Accessibility text is only evidence of selection: PDF providers can return
                // substituted glyphs. Always request the application's fresh copy as the original.
                if (!Native.ActivateSource(hwnd)) throw new InvalidOperationException("无法切回来源窗口，请重新选中文字后按 Ctrl+Alt+M。");
                if (dismissMenu) { Native.PostMessage(hwnd,0x1F,IntPtr.Zero,IntPtr.Zero); await Task.Delay(30); }
                if (Native.GetForegroundWindow() != hwnd) throw new InvalidOperationException("来源窗口已经改变，请重新选中文字。");
                uint before = Native.GetClipboardSequenceNumber();
                if (!Native.Keys(0x11, 0x43)) throw new InvalidOperationException("该应用可能以管理员权限运行，普通权限工具不能向它发送复制操作。");
                for (int i = 0; i < 12; i++) {
                    await Task.Delay(25);
                    if (Native.GetClipboardSequenceNumber() == before) continue;
                    try {
                        sequenceAfterCopy = Native.GetClipboardSequenceNumber();
                        if (Clipboard.ContainsText(TextDataFormat.UnicodeText)) {
                            string fresh=Clipboard.GetText(TextDataFormat.UnicodeText);
                            sequenceAfterCopy = Native.GetClipboardSequenceNumber(); copied = CapturePolicy.UseFreshCopy(clip,fresh);
                            if (copied) break;
                        }
                    } catch (ExternalException) { }
                }
                if (!copied) {
                    if (!CapturePolicy.UseClipboardFallback(clip, clipboardFallback) && !CapturePolicy.UseAccessibilityFallback(clip,accessibilityFallback))
                        throw new InvalidOperationException("未取得本次复制文本。\n请选中文字后按 Ctrl+C，再点内容迁移并核对预览。");
                }
            } catch (Exception ex) {
                if (CapturePolicy.UseClipboardFallback(clip, clipboardFallback) || CapturePolicy.UseAccessibilityFallback(clip,accessibilityFallback)) { }
                else { Error(ex.Message); busy = false; return; }
            }
            finally {
                if (sequenceAfterCopy != 0 && previous != null && Native.GetClipboardSequenceNumber() == sequenceAfterCopy) {
                    try { Clipboard.SetDataObject(previous, true); } catch { Notify("剪贴板恢复失败", "选区已取得；原剪贴板暂时无法恢复。"); }
                }
            }
            try {
                busy = false; EditClip(clip,hwnd,StartMetadata(hwnd,pid,clip));
            } catch (Exception ex) { busy = false; Error(ex.Message); }
        }
        static Task<Clip> StartMetadata(IntPtr hwnd,uint pid,Clip clip) {
            var metadata=new TaskCompletionSource<Clip>();
            var worker=new Thread(delegate(){try{metadata.TrySetResult(ReadSource(hwnd,pid,clip));}catch{metadata.TrySetResult(clip);}});
            worker.IsBackground=true;worker.SetApartmentState(ApartmentState.STA);worker.Start();return metadata.Task;
        }
        static Clip ReadSource(IntPtr hwnd, uint pid, Clip clip) {
            // Build a separate result so a timed-out worker cannot mutate a displayed clip.
            var result = new Clip { Text = clip.Text, Title = clip.Title, App = clip.App, CapturedAt = clip.CapturedAt, Fidelity = clip.Fidelity, RequiresConfirmation = clip.RequiresConfirmation, RequiresTitle = clip.RequiresTitle };
            if (clip.App.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) && !ChatTitleReader.IsSpecific(clip.Title)) {
                string title = ChatTitleReader.Read(hwnd);
                if (ChatTitleReader.IsSpecific(title)) { result.Title = title; result.RequiresTitle=false; }
                else result.RequiresTitle = true;
            }
            if (String.Equals(clip.App, "WINWORD", StringComparison.OrdinalIgnoreCase)) {
                object com = null, docObject = null, selectionObject = null, windowObject = null;
                try {
                    com = Marshal.GetActiveObject("Word.Application"); dynamic word = com;
                    windowObject = word.ActiveWindow; dynamic window = windowObject;
                    if (Native.Pid(new IntPtr((int)window.Hwnd)) == pid) {
                        docObject = word.ActiveDocument; dynamic doc = docObject;
                        string fullName = (string)doc.FullName;
                        if (Path.IsPathRooted(fullName)) result.Source = fullName;
                        else result.Detail = "Word 文档尚未保存：" + (string)doc.Name;
                        selectionObject = word.Selection; dynamic selection = selectionObject;
                        result.Detail += " 页码 " + selection.Information[3] + "；选区字符位置 " + selection.Start + "–" + selection.End;
                    }
                } catch { }
                finally { foreach (object obj in new object[] { selectionObject, docObject, windowObject, com }) if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }
            }
            if (clip.App.Equals("msedge", StringComparison.OrdinalIgnoreCase) || clip.App.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                clip.App.Equals("firefox", StringComparison.OrdinalIgnoreCase) || clip.App.Equals("brave", StringComparison.OrdinalIgnoreCase)) {
                try {
                    AutomationElement root = AutomationElement.FromHandle(hwnd);
                    AutomationElementCollection edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    foreach (AutomationElement edit in edits) {
                        string name = edit.Current.Name.ToLowerInvariant(), id = edit.Current.AutomationId.ToLowerInvariant();
                        if (!(name.Contains("address") || name.Contains("地址") || name.Contains("网址") || id.Contains("urlbar") || id.Contains("address"))) continue;
                        object pattern; if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) continue;
                        string value = ((ValuePattern)pattern).Current.Value;
                        Uri uri; if (Uri.TryCreate(value, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "file")) {
                            result.Source = value; result.Detail = "浏览器地址栏；在页面内搜索原文以定位。"; break;
                        }
                        // Do not guess an omitted URL scheme or invent a link.
                        if (!String.IsNullOrWhiteSpace(value)) result.Detail = "地址栏显示值（不是已验证的完整链接）：" + value;
                    }
                } catch { }
            }
            return result;
        }
        void EditClip(Clip clip,IntPtr sourceWindow,Task<Clip> metadata=null) {
            floating.Hide();
            if (busy) { Error("已有一条摘录正在处理，请完成或取消后再试。"); return; }
            if (String.IsNullOrEmpty(clip.Text)) { Error("没有选中文字，未保存任何内容。"); return; }
            if (TestMode) { if (TestCaptured != null) TestCaptured(clip); return; }
            busy = true;
            try { EnsureOutput(); using (var editor = new NoteDialog(clip, settings.OutputPath,sourceWindow,metadata)) {
                if (editor.ShowDialog() == DialogResult.OK) Notify("已保存到内容迁移 Markdown", "原文、来源和备注已追加。点击可用 VS Code 打开文件。");
            } } finally { busy = false; }
        }
        void EnsureOutput() {
            string resolved=OutputFiles.Resolve(settings.OutputPath,settings.CustomOutput,Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),DateTime.Today);
            bool changed=resolved!=settings.OutputPath;settings.OutputPath=resolved;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settings.OutputPath)));
            using (var file = new FileStream(settings.OutputPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) { }
            if(changed)SaveSettings();
        }
        void ChangeOutput() {
            using (var dialog = new SaveFileDialog { Title = "选择 Markdown 保存文件（追加，不覆盖）", Filter = "Markdown 文件 (*.md)|*.md",DefaultExt="md",AddExtension=true, FileName = settings.OutputPath, OverwritePrompt = false }) {
                if (dialog.ShowDialog() == DialogResult.OK) { string old = settings.OutputPath;bool oldCustom=settings.CustomOutput; settings.OutputPath = dialog.FileName;settings.CustomOutput=true;
                    try { EnsureOutput(); SaveSettings(); Notify("保存位置已更改", settings.OutputPath); } catch (Exception ex) { settings.OutputPath = old;settings.CustomOutput=oldCustom; Error(ex.Message); }
                }
            }
        }
        void OpenFile() { try { EnsureOutput();string code=null;
            foreach(string candidate in new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Microsoft VS Code","Code.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Microsoft VS Code","Code.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Microsoft VS Code","Code.exe")})if(File.Exists(candidate)){code=candidate;break;}
            if(code!=null)Process.Start(new ProcessStartInfo(code,"\""+settings.OutputPath+"\""){UseShellExecute=true});else Process.Start(settings.OutputPath);
        } catch (Exception ex) { Error(ex.Message); } }
        void Error(string message) { if (TestMode) { if (TestError != null) TestError(message); } else MessageBox.Show(message, "内容迁移", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        void Notify(string title, string message) { if (!TestMode) { tray.BalloonTipTitle = title; tray.BalloonTipText = message; tray.ShowBalloonTip(4000); } }
        void SaveSettings() { File.WriteAllText(ConfigPath, new JavaScriptSerializer().Serialize(settings), new UTF8Encoding(false)); }
        static string StartupPath() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "内容迁移.vbs"); }
        public void ShowSettings() {
            using (var form = new Form { Text = "内容迁移 · 设置", Icon = Icon, Font = new Font("Microsoft YaHei UI", 10),
                ClientSize = new Size(475, 235), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterScreen, TopMost = true }) {
                var toggle = new CheckBox { Text = "开机自动启动内容迁移", Checked = File.Exists(StartupPath()), Location = new Point(24, 27), AutoSize = true };
                var status = new Label { Text = toggle.Checked ? "当前已开启" : "当前已关闭", Location = new Point(25, 62), AutoSize = true, ForeColor = Color.DimGray };
                toggle.Click += delegate { try { StartupManager.Set(StartupPath(), Application.ExecutablePath, toggle.Checked); }
                    catch (Exception ex) { MessageBox.Show(form, "设置失败：" + ex.Message); }
                    toggle.Checked = File.Exists(StartupPath()); status.Text = toggle.Checked ? "已开启，下次登录 Windows 时自动启动。" : "已关闭，下次登录 Windows 时不会自动启动。"; };
                form.Controls.Add(toggle); form.Controls.Add(status);
                form.Controls.Add(new Label { Text = "兼容 ChatGPT 等桌面应用：\n自动读取失败时，先 Ctrl+C，再迁移并核对原文。\n快捷键：Ctrl+Alt+M", Location = new Point(24, 105), Size = new Size(430, 75) });
                var close = new Button { Text = "完成", Location = new Point(365, 187), Size = new Size(85, 32), DialogResult = DialogResult.OK };
                form.Controls.Add(close); form.AcceptButton = close; form.ShowDialog();
            }
        }
        protected override void OnFormClosed(FormClosedEventArgs e) {
            shuttingDown = true; probeCancellation.Cancel();mouseMonitor.Dispose();
            Native.UnregisterHotKey(Handle, 1);
            expiry.Dispose(); floating.Dispose(); tray.Visible = false; tray.Dispose(); base.OnFormClosed(e);
        }
        public static Settings LoadSettings() {
            if (File.Exists(ConfigPath)) {
                Settings config = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(ConfigPath));
                if (config == null || String.IsNullOrWhiteSpace(config.OutputPath) || !Path.IsPathRooted(config.OutputPath))
                    throw new InvalidDataException("config.json 中的保存路径无效。");
                return config;
            }
            return new Settings();
        }
    }
    static class Program {
        public const string FixtureText = "  中文原文\r\n第二行\t空格  \r\nUnicode：😀 café\r\n尾部空格  ";
        [STAThread] static void Main(string[] args) {
            Native.InitializeDpi();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try {
                if (args.Length > 0 && args[0] == "--fixture") { Fixture(); return; }
                if (args.Length > 1 && args[0] == "--selection-probe") {
                    using(var deadline=new System.Threading.Timer(delegate{Environment.Exit(3);},null,2600,Timeout.Infinite)) {
                        Point? point=args.Length>3 && args[2]!="none" ? new Point(Int32.Parse(args[2]),Int32.Parse(args[3])) : (Point?)null;
                        using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false))) {
                            output.Write(new JavaScriptSerializer().Serialize(SelectionReader.Probe(new IntPtr(Int64.Parse(args[1])),point,Array.IndexOf(args,"menu")>=0)));
                        }return;
                    }
                }
                if (args.Length > 0 && args[0] == "--self-test") { SelfTest(); return; }
                if (args.Length > 0 && args[0] == "--focus-test") { FocusTest(); return; }
                if (args.Length > 0 && args[0] == "--copy-test") { CopyTest(); return; }
                if (args.Length > 0 && args[0] == "--render-test") { RenderTest(); return; }
                if (args.Length > 0 && args[0] == "--integration-test") { IntegrationTest(); return; }
                bool created;
                using (var mutex = new Mutex(true, "Local\\ContentMover.Desktop.v1", out created)) {
                    if (!created) {
                        IntPtr window = Native.FindWindow(null, "内容迁移后台服务");
                        if (window != IntPtr.Zero) Native.PostMessage(window, Controller.SettingsMessage, IntPtr.Zero, IntPtr.Zero);
                        else MessageBox.Show("内容迁移已经在后台运行，请查看任务栏通知区域。", "内容迁移");
                        return;
                    }
                    using (var controller = new Controller(Controller.LoadSettings(), false)) {
                        if (args.Length > 0 && args[0] == "--settings") controller.BeginInvoke(new Action(controller.ShowSettings));
                        Application.Run(controller);
                    }
                }
            } catch (Exception ex) {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"), ex.ToString());
                if (args.Length == 0) MessageBox.Show("内容迁移启动失败：\n" + ex.Message, "内容迁移");
                Environment.ExitCode = 1;
            }
        }
        static string TestFolder() { string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-results"); Directory.CreateDirectory(folder); return folder; }
        static void RenderTest() {
            using(var entry=new FloatingButton()) {
                using(var bitmap=new Bitmap(entry.Width,entry.Height)){entry.DrawToBitmap(bitmap,new Rectangle(Point.Empty,entry.Size));bitmap.Save(Path.Combine(TestFolder(),"floating-entry.png"));}
            }
            var clip = new Clip { Text = FixtureText, App = "示例应用", Title = "原文和备注预览", Source = "D:\\文档\\示例.docx" };
            using (var dialog = new NoteDialog(clip, "桌面\\内容迁移.md")) {
                dialog.NoteBox.Text = "这段内容解释了关键概念，准备在后续研究中引用。";
                dialog.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) {
                    dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                    bitmap.Save(Path.Combine(TestFolder(), "note-dialog.png"));
                }
                dialog.Close();
            }
            CapturePolicy.UseClipboardFallback(clip, FixtureText); clip.App = "ChatGPT"; clip.RequiresTitle=true;
            using (var dialog = new NoteDialog(clip, "桌面\\内容迁移.md")) {
                dialog.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) {
                    dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                    bitmap.Save(Path.Combine(TestFolder(), "clipboard-confirmation.png"));
                }
                dialog.Close();
            }
        }
        static void Assert(bool condition, string message) { if (!condition) throw new Exception("TEST FAILED: " + message); }
        static void CopyTest() {
            Process fixture=null;IDataObject original=Clipboard.GetDataObject();
            var captureTime=new Stopwatch();
            using(var controller=new Controller(new Settings(),true))using(var start=new System.Windows.Forms.Timer{Interval=200})using(var deadline=new System.Windows.Forms.Timer{Interval=12000}) {
                Action<string> fail=delegate(string message){File.WriteAllText(Path.Combine(TestFolder(),"copy-test.txt"),"FAIL: "+message);Environment.ExitCode=1;controller.Stop();};
                controller.TestError=fail;
                controller.TestCaptured=delegate(Clip clip){
                    try {
                        if(clip.Text!=FixtureText)File.WriteAllText(Path.Combine(TestFolder(),"copy-debug.txt"),clip.Fidelity+"\n"+Convert.ToBase64String(Encoding.UTF8.GetBytes(clip.Text)));
                        Assert(clip.Text==FixtureText,"缓存选区直接保留原文");
                        Assert(!clip.RequiresConfirmation && clip.Fidelity=="应用提供的选区文本","确认直接使用缓存选区");
                        Assert(Clipboard.GetText()=="测试前的剪贴板内容","复制后恢复之前的剪贴板");
                        Assert(!Native.IsIconic(fixture.MainWindowHandle) && Native.IsZoomed(fixture.MainWindowHandle),"迁移不最小化或还原来源窗口");
                        Assert(captureTime.ElapsedMilliseconds<500,"缓存选区没有等待复制或出处");
                        string path=Path.Combine(TestFolder(),"copy-fidelity.txt");Storage.Append(path,clip,"验证复制优先","测试窗口");
                        Assert(File.ReadAllText(path).Contains(FixtureText),"保存原样保留复制文本");
                        File.WriteAllText(Path.Combine(TestFolder(),"copy-test.txt"),"PASS: cached capture "+captureTime.ElapsedMilliseconds+"ms; source stays maximized; Unicode/whitespace preserved; clipboard unchanged; stored text matches selection.");
                        controller.Stop();
                    }catch(Exception ex){fail(ex.Message);}
                };
                start.Tick+=async delegate {
                    start.Stop();
                    try {
                        fixture=Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--fixture"){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Normal});
                        await Task.Delay(600);fixture.Refresh();IntPtr source=fixture.MainWindowHandle;Assert(source!=IntPtr.Zero,"复制测试窗口启动");
                        Native.ShowWindow(source,3);
                        uint unusedPid;uint other=Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out unusedPid),own=Native.GetCurrentThreadId();
                        bool attached=other!=own && Native.AttachThreadInput(own,other,true);
                        try{Native.ActivateSource(source);}finally{if(attached)Native.AttachThreadInput(own,other,false);}
                        await Task.Delay(100);Assert(Native.GetForegroundWindow()==source,"仅向测试来源窗口发送复制");
                        Native.Keys(0x11,0x41);await Task.Delay(80);Clipboard.SetText("测试前的剪贴板内容");
                        controller.SetSelectionForCopyTest(source,FixtureText);captureTime.Start();controller.CaptureSelection(source,true);
                    }catch(Exception ex){fail(ex.Message);}
                };
                deadline.Tick+=delegate{fail("复制测试超时");};
                try{start.Start();deadline.Start();Application.Run(controller);}finally{
                    if(fixture!=null && !fixture.HasExited)fixture.CloseMainWindow();
                    if(original!=null)Clipboard.SetDataObject(original,true);
                }
            }
        }
        static void FocusTest() {
            Process fixture=null;
            try {
                fixture=Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--fixture"){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Normal});
                var wait=Stopwatch.StartNew();while(fixture.MainWindowHandle==IntPtr.Zero && wait.ElapsedMilliseconds<3000){Application.DoEvents();Thread.Sleep(40);fixture.Refresh();}
                IntPtr source=fixture.MainWindowHandle;Assert(source!=IntPtr.Zero,"测试来源窗口启动");
                foreach(bool title in new[]{false,true}) {
                    uint unusedPid;uint other=Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out unusedPid),own=Native.GetCurrentThreadId();
                    bool attached=other!=own && Native.AttachThreadInput(own,other,true);
                    try{Native.SetForegroundWindow(source);}finally{if(attached)Native.AttachThreadInput(own,other,false);}
                    using(var editor=new NoteDialog(new Clip{Text="测试原文",RequiresTitle=title},Path.Combine(TestFolder(),"focus-output.txt"))) {
                        editor.Show();var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<400){Application.DoEvents();Thread.Sleep(10);}
                        TextBox target=title ? editor.TitleBox : editor.NoteBox;
                        target.ImeMode=ImeMode.Disable;
                        Assert(Native.GetForegroundWindow()==editor.Handle,"弹窗取得前台键盘所有权");
                        var info=new Native.GUIINFO{size=(uint)Marshal.SizeOf(typeof(Native.GUIINFO))};Native.GetGUIThreadInfo(Native.GetCurrentThreadId(),ref info);
                        Assert(info.focus==target.Handle,"系统焦点位于正确的输入框");
                        var inputs=new[]{new Native.INPUT{type=1,u=new Native.INPUTUNION{ki=new Native.KEYBOARD{scan=0x78,flags=4}}},new Native.INPUT{type=1,u=new Native.INPUTUNION{ki=new Native.KEYBOARD{scan=0x78,flags=6}}}};
                        Assert(Native.SendInput(2,inputs,Marshal.SizeOf(typeof(Native.INPUT)))==2,"发送测试字符");
                        clock.Restart();while(clock.ElapsedMilliseconds<120){Application.DoEvents();Thread.Sleep(10);}
                        Assert(target.Text=="x","直接键入进入弹窗输入框");editor.Close();
                    }
                }
                File.WriteAllText(Path.Combine(TestFolder(),"focus-test.txt"),"PASS: foreground transfer from separate process; OS focus; typed character reaches note and required-title fields.");
            }finally{if(fixture!=null && !fixture.HasExited)fixture.CloseMainWindow();}
        }
        static void SelfTest() {
            string folder = TestFolder(), path = Path.Combine(folder, "storage-" + Guid.NewGuid().ToString("N") + ".txt");
            string outputTest=Path.Combine(folder,"dated-output-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(outputTest);
            string legacy=Path.Combine(outputTest,"内容迁移.txt"),today=OutputFiles.DatedPath(outputTest,new DateTime(2026,10,4));
            Assert(Path.GetFileName(today)=="内容迁移2026-10-4.md","日期文件名有效且不含斜杠");
            Assert(OutputFiles.Resolve(legacy,false,outputTest,new DateTime(2026,10,4))==today,"缺失默认文件时使用当天日期");
            File.WriteAllText(legacy,"保留旧摘录");Assert(OutputFiles.Resolve(legacy,false,outputTest,new DateTime(2026,10,4))==today && File.ReadAllText(legacy)=="保留旧摘录","改用 Markdown 并保留已有 TXT");
            Assert(OutputFiles.Resolve(today,true,outputTest,new DateTime(2026,10,5))==today,"自定义保存文件不改名");
            Assert(OutputFiles.Resolve(today,false,outputTest,new DateTime(2026,10,5))==OutputFiles.DatedPath(outputTest,new DateTime(2026,10,5)),"缺失带日期默认文件时采用新的创建日期");
            var clip = new Clip { Text = FixtureText, App = "测试", Title = "选区测试", Source = "D:\\文档\\原文.docx" };
            string first = Storage.Format(clip, "留下的原因\n第二行备注", clip.Source);
            Storage.Append(path, clip, "留下的原因\n第二行备注", clip.Source);
            Assert(File.ReadAllText(path) == first, "保存原文与备注字节内容");
            Storage.Append(path, clip, "", "");
            Assert(File.ReadAllText(path) == first + Storage.Format(clip, "", ""), "追加不覆盖");
            Assert(File.ReadAllText(path).Contains(FixtureText), "中文、空格、换行、制表符、表情原样保留");
            Assert(!first.Contains("文本来源："), "TXT 不再显示文本来源元信息");
            string markdownPath=Path.Combine(outputTest,"markdown.md");
            clip.Text=FixtureText+"\r\n**加粗**\r\n![图片](assets/test.png)";
            Storage.Append(markdownPath,clip,"# 手写备注\n**重要**",clip.Source);
            string markdown=File.ReadAllText(markdownPath);
            Assert(markdown.Contains(clip.Text) && markdown.Contains("### 原文") && markdown.Contains("# 手写备注"),"Markdown 原文与格式语法原样保留");
            Assert(OutputFiles.Resolve(legacy,true,outputTest,DateTime.Today)==Path.ChangeExtension(legacy,".md"),"自定义 TXT 路径切换为 MD");
            var pending=new TaskCompletionSource<Clip>();
            using(var editor=new NoteDialog(new Clip{Text=FixtureText,Title="初始标题"},markdownPath,IntPtr.Zero,pending.Task)){
                editor.Show();Application.DoEvents();Assert(editor.Visible && !pending.Task.IsCompleted,"出处未完成时立即显示备注窗口");
                editor.TitleBox.Text="用户标题";editor.SourceBox.Text="用户出处";editor.NoteBox.Text="正在输入";
                pending.SetResult(new Clip{Title="自动标题",Source="自动出处"});Application.DoEvents();
                Assert(editor.TitleBox.Text=="用户标题" && editor.SourceBox.Text=="用户出处" && editor.NoteBox.Text=="正在输入","后台出处不覆盖用户输入");editor.Close();
            }
            var screen=new Rectangle(0,0,1920,1080);var button=new Size(92,30);
            var compact=new Rectangle(1173,374,221,131);Point compactLocation=FloatingButton.Placement(new Point(1173,450),button,screen,compact,8);
            Assert(compactLocation==new Point(1073,374) && !new Rectangle(compactLocation,button).IntersectsWith(compact),"Codex 短菜单入口放在左侧并对齐顶部");
            var upwards=new Rectangle(742,344,890,534);Point location=FloatingButton.Placement(new Point(742,870),button,screen,upwards,8);
            Assert(location==new Point(642,848),"向上展开菜单时入口贴近鼠标而非菜单顶部");
            Assert(!new Rectangle(location,button).IntersectsWith(upwards),"入口不被菜单遮挡");
            Assert(FloatingButton.Placement(new Point(434,720),button,screen,new Rectangle(434,449,636,675),8)==new Point(334,705),"长菜单入口与鼠标所在高度对齐");
            var leftMenu=new Rectangle(2,200,400,500);location=FloatingButton.Placement(new Point(2,200),button,screen,leftMenu,8);
            Assert(location==new Point(410,200) && !new Rectangle(location,button).IntersectsWith(leftMenu),"屏幕左边缘改放菜单右侧");
            var wideMenu=new Rectangle(0,300,1920,500);location=FloatingButton.Placement(new Point(500,300),button,screen,wideMenu,8);
            Assert(location==new Point(0,262) && screen.Contains(new Rectangle(location,button)),"左右空间都不足时放到菜单上方");
            using(var owner=new Form{Location=new Point(250,200),StartPosition=FormStartPosition.Manual})using(var popup=new ContextMenuStrip()) {
                for(int i=0;i<8;i++)popup.Items.Add("菜单测试 "+i);owner.Show();var anchor=owner.PointToScreen(new Point(40,40));popup.Show(anchor);Application.DoEvents();
                var found=Native.ContextMenuBounds(owner.Handle,anchor);Assert(found.HasValue && found.Value.IntersectsWith(popup.Bounds),"识别实际弹出菜单位置");
                popup.Close();owner.Close();
            }
            foreach(var monitor in Screen.AllScreens)using(var owner=new Form{Size=new Size(300,180),StartPosition=FormStartPosition.Manual,Location=new Point(monitor.WorkingArea.Left+100,monitor.WorkingArea.Top+100)})using(var popup=new ContextMenuStrip()){
                popup.Items.Add("Copy");popup.Items.Add("Select All");owner.Show();Application.DoEvents();
                Native.RECT rectangle;Native.GetWindowRect(owner.Handle,out rectangle);
                Assert(rectangle.Left==owner.Left && rectangle.Top==owner.Top,"各屏幕 WinForms 和 Win32 使用同一套物理坐标");
                var anchor=owner.PointToScreen(new Point(80,60));popup.Show(anchor);Application.DoEvents();
                var detected=Native.ContextMenuBounds(owner.Handle,anchor);
                // Small test menus can be shorter than the native discovery threshold; compare coordinates directly.
                Native.GetWindowRect(popup.Handle,out rectangle);Assert(rectangle.Left==popup.Left && rectangle.Top==popup.Top,"不同缩放屏幕上的菜单坐标一致");
                popup.Close();owner.Close();
            }
            using(var monitor=new MouseMonitor(delegate{},80)) {
                var wait=Stopwatch.StartNew();while(monitor.Renewals<2 && wait.ElapsedMilliseconds<1500){Application.DoEvents();Thread.Sleep(10);}
                Assert(monitor.Renewals>=2,"鼠标监听定期重新注册成功");
            }
            var pdfCopy=new Clip{Text="两电平TP7组成的DQR&个低压"};string paper="其主要由 3 个两电平 VSC 组成的 FID";
            Assert(CapturePolicy.UseFreshCopy(pdfCopy,paper) && pdfCopy.Text==paper && !pdfCopy.RequiresConfirmation,"应用复制覆盖PDF接口错误字符");
            Assert(CapturePolicy.UseAccessibilityFallback(pdfCopy,"未验证选区") && pdfCopy.RequiresConfirmation,"仅辅助功能读取时必须核对");
            using(var fixture=new Form())using(var edit=new TextBox{Multiline=true,Text=FixtureText,Dock=DockStyle.Fill}) {
                fixture.Controls.Add(edit);fixture.Show();edit.Focus();edit.SelectAll();Application.DoEvents();
                Assert(Native.SelectedEditText(edit.Handle)==FixtureText,"原生编辑器选区保留Unicode和换行");
                var probe=SelectionReader.Start(edit.Handle,null);var clock=Stopwatch.StartNew();
                while(!probe.IsCompleted && clock.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(10);}
                Assert(probe.IsCompleted && probe.Result.Text==FixtureText,"独立读取进程取得真实编辑器选区");
                edit.Select(0,0);Application.DoEvents();Assert(Native.SelectedEditText(edit.Handle)=="","空选区不会读取整篇文档");
                fixture.Close();
            }
            using(var entry=new Controller(new Settings(),true)) {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                entry.TestEntry("");Assert(!entry.ButtonVisible,"无选区时不显示右键入口");
                entry.TestEntry("选中文字");Assert(entry.ButtonVisible,"非空选区时显示右键入口");
                var delayed=new TaskCompletionSource<SelectionResult>();entry.TestDelayedEntry(delayed.Task);
                var wait=Stopwatch.StartNew();while(wait.ElapsedMilliseconds<1150){Application.DoEvents();Thread.Sleep(10);}
                delayed.SetResult(new SelectionResult{Text="缓慢应用的选区",HasSelection=true});wait.Restart();while(wait.ElapsedMilliseconds<300){Application.DoEvents();Thread.Sleep(10);}
                Assert(entry.ButtonVisible,"超过旧版900ms截止时间的选区仍显示入口");
                var obsolete=new TaskCompletionSource<SelectionResult>();entry.TestDelayedEntry(obsolete.Task);entry.TestInvalidateEntry();
                obsolete.SetResult(new SelectionResult{Text="过期选区",HasSelection=true});Application.DoEvents();
                Assert(!entry.ButtonVisible,"已取消的选区任务不能重新显示入口");
                entry.Stop();
            }
            Assert(ChatTitleReader.CandidateScore("ChatGPT",true,"page",true,true)==0, "不把应用名当成聊天标题");
            Assert(ChatTitleReader.CandidateScore("历史聊天",false,"",false,true)==0, "不把其他历史聊天当成当前标题");
            Assert(ChatTitleReader.CandidateScore("当前聊天标题",true,"",false,true)==100, "接受已选中的聊天标题");
            Assert(ChatTitleReader.CandidateScore("当前聊天标题",false,"page",false,true)==90, "接受当前页导航标题");
            string titleTestPath=Path.Combine(folder,"title-test-"+Guid.NewGuid().ToString("N")+".txt");
            var unnamed=new Clip{App="ChatGPT",Title="ChatGPT",Text=FixtureText,RequiresTitle=true};
            using(var editor=new NoteDialog(unnamed,titleTestPath)) {
                Assert(!editor.SaveButton.Enabled,"聊天标题未取得时必须补充");
                editor.TitleBox.Text="ChatGPT";Assert(!editor.SaveButton.Enabled,"不能用应用名替代聊天标题");
                editor.TitleBox.Text="内容迁移软件优化";Assert(editor.SaveButton.Enabled,"填写具体聊天标题后允许保存");
                editor.Show();Application.DoEvents();editor.SaveButton.PerformClick();
                Assert(File.ReadAllText(titleTestPath).Contains("标题：内容迁移软件优化"),"记录手填聊天标题");
            }
            string startupFile = Path.Combine(folder, "startup-test.vbs");
            StartupManager.Set(startupFile, "D:\\带空格的 目录\\内容迁移.exe", true);
            Assert(File.Exists(startupFile) && File.ReadAllText(startupFile).Contains("D:\\带空格的 目录\\内容迁移.exe"), "开启自启，含空格的路径保留");
            StartupManager.Set(startupFile, "", false); Assert(!File.Exists(startupFile), "关闭自启");
            StartupManager.Set(startupFile, "", false);
            var fallbackClip = new Clip { App = "ChatGPT", Title = "示例聊天" };
            Assert(!CapturePolicy.UseClipboardFallback(fallbackClip, ""), "空剪贴板不会被接受");
            Assert(CapturePolicy.UseClipboardFallback(fallbackClip, FixtureText), "手动复制原文进入兼容模式");
            string fallbackFile = Path.Combine(folder, "fallback-" + Guid.NewGuid().ToString("N") + ".txt");
            using (var editor = new NoteDialog(fallbackClip, fallbackFile)) {
                Assert(!editor.SaveButton.Enabled, "未经核对不允许保存剪贴板文本");
                editor.SaveButton.PerformClick(); Assert(!File.Exists(fallbackFile), "未确认不写入");
                editor.ConfirmationBox.Checked = true;
                Assert(editor.SaveButton.Enabled, "核对后允许保存");
                editor.NoteBox.Text = "ChatGPT 手动复制兼容测试";
                editor.Show(); Application.DoEvents(); editor.SaveButton.PerformClick();
                Assert(File.Exists(fallbackFile) && File.ReadAllText(fallbackFile).Contains(FixtureText), "兼容模式保存完整原文");
            }
            File.WriteAllText(Path.Combine(folder, "self-test.txt"), "PASS: Markdown and UTF-8 fidelity; TXT preserved; append; background metadata does not overwrite edits; selection hook; confirmation; startup.\r\n", Encoding.UTF8);
        }
        static void Fixture() {
            var form = new Form { Text = "内容迁移测试选区", Size = new Size(620, 330), StartPosition = FormStartPosition.CenterScreen, TopMost = true };
            var text = new TextBox { Multiline = true, Dock = DockStyle.Fill, Text = FixtureText, Font = new Font("Microsoft YaHei UI", 12) };
            form.Controls.Add(text); form.Shown += delegate { text.Focus(); text.SelectAll(); };form.Activated+=delegate{text.Focus();text.SelectAll();}; Application.Run(form);
        }
        static void IntegrationTest() {
            string folder = TestFolder();
            var inputDesktop = Native.OpenInputDesktop(0, false, 1);
            File.WriteAllText(Path.Combine(folder, "desktop-debug.txt"), "Thread desktop=" + Native.DesktopName(Native.GetThreadDesktop(Native.GetCurrentThreadId())) + " input desktop=" + Native.DesktopName(inputDesktop));
            if (inputDesktop != IntPtr.Zero) Native.CloseDesktop(inputDesktop);
            using (var controller = new Controller(new Settings(), true)) {
                controller.AllowTestMouse=true;
                Process fixture = null; var timeout = new System.Windows.Forms.Timer { Interval = 12000 };
                var start = new System.Windows.Forms.Timer { Interval = 500 };
                bool completed = false;
                controller.TestError = delegate(string message) { File.WriteAllText(Path.Combine(folder, "integration-test.txt"), "FAIL: " + message); Environment.ExitCode = 1; completed = true; controller.Stop(); };
                controller.TestCaptured = delegate(Clip clip) {
                    try {
                        Assert(clip.Text == FixtureText, "真实复制保持换行、空格和 Unicode");
                        Assert(Clipboard.GetText() == "剪贴板恢复验证", "恢复原剪贴板");
                        string path = Path.Combine(folder, "integration-output.txt");
                        Storage.Append(path, clip, "通过真实右键触发的测试备注", "测试窗口");
                        using (var dialog = new NoteDialog(clip, path)) {
                            dialog.Show(); Application.DoEvents();
                            using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size)); bitmap.Save(Path.Combine(folder, "note-dialog.png")); }
                            dialog.Close();
                        }
                        File.WriteAllText(Path.Combine(folder, "integration-test.txt"), "PASS: real right-click hook; floating entry; cached selection; exact Unicode/whitespace; clipboard unchanged; note dialog rendered.\r\n", Encoding.UTF8);
                    } catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "integration-test.txt"), "FAIL: " + ex.Message); Environment.ExitCode = 1; }
                    completed = true; controller.Stop();
                };
                start.Tick += async delegate {
                    start.Stop(); Clipboard.SetText("剪贴板恢复验证");
                    fixture = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--fixture") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Normal }); await Task.Delay(900); fixture.Refresh();
                    IntPtr hwnd = fixture.MainWindowHandle; File.WriteAllText(Path.Combine(folder, "integration-debug.txt"), "Fixture PID=" + fixture.Id + " HWND=" + hwnd + " title=" + Native.Title(hwnd) + " foreground=" + Native.GetForegroundWindow()); Native.ShowWindow(hwnd, 5);
                    uint unusedPid; uint foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out unusedPid);
                    uint testThread = Native.GetCurrentThreadId();
                    bool attached = Native.AttachThreadInput(testThread, foregroundThread, true);
                    Native.SetForegroundWindow(hwnd);
                    if (attached) Native.AttachThreadInput(testThread, foregroundThread, false);
                    await Task.Delay(200);
                    File.AppendAllText(Path.Combine(folder, "integration-debug.txt"), "\r\nBefore right click=" + Native.GetForegroundWindow() + " title=" + Native.Title(Native.GetForegroundWindow()));
                    Native.RECT rect; Native.GetWindowRect(hwnd, out rect);
                    bool moved = Native.SetCursorPos(rect.Left + 70, rect.Top + 75);
                    Native.POINT actualPoint; Native.GetCursorPos(out actualPoint);
                    File.AppendAllText(Path.Combine(folder, "integration-debug.txt"), "\r\nRect=" + rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom + " cursor=" + actualPoint.X + "," + actualPoint.Y + " moved=" + moved);
                    Native.mouse_event(2, 0, 0, 0, UIntPtr.Zero); Native.mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(150); Native.Keys(0x11, 0x41); await Task.Delay(150);
                    Native.mouse_event(8, 0, 0, 0, UIntPtr.Zero); Native.mouse_event(16, 0, 0, 0, UIntPtr.Zero); await Task.Delay(250);
                    if (!controller.ButtonVisible) { controller.TestError("右键按钮未显示"); return; }
                    Rectangle bounds = controller.ButtonBounds;
                    Native.SetCursorPos(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                    Native.mouse_event(2, 0, 0, 0, UIntPtr.Zero); Native.mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                };
                timeout.Tick += delegate { if (!completed) controller.TestError("集成测试超时"); };
                start.Start(); timeout.Start(); Application.Run(controller);
                start.Dispose(); timeout.Dispose(); if (fixture != null && !fixture.HasExited) fixture.CloseMainWindow();
            }
        }
    }
}
