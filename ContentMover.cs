using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

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
        public static Task<string> Start(IntPtr hwnd, Point? point) {
            var result = new TaskCompletionSource<string>();
            if (!Workers.Wait(0)) { result.SetResult(""); return result.Task; }
            var thread = new Thread(delegate() {
                try { result.TrySetResult(Read(hwnd, point)); } catch { result.TrySetResult(""); } finally { Workers.Release(); }
            });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
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
    }
    public sealed class Settings {
        public string OutputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "内容迁移.txt");
        public string Token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        public bool RightClickEnabled = true;
    }
    public sealed class Clip {
        public string Text = "", Title = "", App = "", Source = "", Detail = "", Fidelity = "应用复制提供的纯文本";
        public bool RequiresConfirmation;
        public bool RequiresTitle;
        public DateTime CapturedAt = DateTime.Now;
    }
    static class Storage {
        static readonly object Gate = new object();
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
                byte[] bytes = new UTF8Encoding(false).GetBytes(Format(clip, note, source));
                // One append operation; never rewrite or truncate prior entries.
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) {
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
            }
        }
    }
    static class CapturePolicy {
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
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
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
    sealed class FloatingButton : Form {
        public event Action Chosen;
        public FloatingButton() {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            AutoScaleMode = AutoScaleMode.None; BackColor = Color.FromArgb(34, 95, 175);
            Font=new Font("Microsoft YaHei UI",10); ForeColor=Color.White; Cursor=Cursors.Hand; Text="内容迁移";
            DoubleBuffered=true;
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
        public void ShowAt(Point point) {
            Rectangle area = Screen.FromPoint(point).WorkingArea;
            int x = point.X - Width - 12, y = point.Y + 6;
            if (x < area.Left) { x = point.X + 12; y = point.Y - Height - 12; }
            Location = new Point(Math.Max(area.Left, Math.Min(x, area.Right - Width)), Math.Max(area.Top, Math.Min(y, area.Bottom - Height)));
            Show();
        }
    }
    sealed class NoteDialog : Form {
        public readonly TextBox NoteBox = new TextBox(), SourceBox = new TextBox();
        public readonly TextBox TitleBox = new TextBox();
        public CheckBox ConfirmationBox;
        public Button SaveButton;
        public NoteDialog(Clip clip, string output) {
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
            layout.Controls.Add(new Label { Text = "原文预览 · " + clip.Text.Length + " 个字符", Dock = DockStyle.Fill }, 0, 3);
            layout.Controls.Add(new TextBox { Text = clip.Text, ReadOnly = true, Multiline = true, WordWrap = false,
                ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, BackColor = Color.FromArgb(246, 248, 251) }, 0, 4);
            layout.Controls.Add(new Label { Text = "备注（可以留空；Ctrl + Enter 保存，Esc 取消）", Dock = DockStyle.Fill, Padding = new Padding(0, 7, 0, 0) }, 0, 5);
            NoteBox.Multiline = true; NoteBox.AcceptsReturn = true; NoteBox.ScrollBars = ScrollBars.Vertical; NoteBox.Dock = DockStyle.Fill;
            layout.Controls.Add(NoteBox, 0, 6);
            var sourcePanel = new Panel { Dock = DockStyle.Fill };
            sourcePanel.Controls.Add(new Label { Text = "来源窗口：" + clip.App + " · " + clip.Title + "\n" + clip.Fidelity,
                Dock = DockStyle.Top, Height = 39, AutoEllipsis = true, ForeColor = clip.RequiresConfirmation ? Color.FromArgb(161, 78, 14) : Color.DimGray });
            var confirm = new CheckBox { Text = "我已核对：上方原文就是本次要迁移的内容", Dock = DockStyle.Bottom, Height = 25, Visible = clip.RequiresConfirmation };
            ConfirmationBox = confirm;
            sourcePanel.Controls.Add(confirm); layout.Controls.Add(sourcePanel, 0, 7);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var save = new Button { Text = "保存到 TXT", AutoSize = true, Height = 32 };
            SaveButton = save;
            Action updateSave = delegate { save.Enabled = (!clip.RequiresConfirmation || confirm.Checked) && (!clip.RequiresTitle || ChatTitleReader.IsSpecific(TitleBox.Text)); };
            confirm.CheckedChanged += delegate { updateSave(); };
            TitleBox.TextChanged += delegate { updateSave(); }; updateSave();
            save.Click += delegate {
                try { clip.Title = TitleBox.Text.Trim(); Storage.Append(output, clip, NoteBox.Text, SourceBox.Text); DialogResult = DialogResult.OK; Close(); }
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
            Shown += delegate { if (clip.RequiresTitle) TitleBox.Focus(); else NoteBox.Focus(); };
        }
    }
    sealed class Controller : Form {
        readonly Settings settings;
        readonly FloatingButton floating = new FloatingButton();
        readonly NotifyIcon tray;
        readonly Native.HookProc hookProc;
        readonly System.Windows.Forms.Timer expiry = new System.Windows.Forms.Timer();
        IntPtr hook, sourceWindow;
        IntPtr selectionWindow;
        Task<string> selectionTask;
        int selectionGeneration;
        bool busy, shuttingDown;
        public bool TestMode;
        public Action<Clip> TestCaptured;
        public Action<string> TestError;
        readonly LocalBridge bridge;
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
            menu.Items.Add("浏览器扩展安装说明", null, delegate { Process.Start(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "使用说明.md")); });
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
            expiry.Interval = 14000; expiry.Tick += delegate { expiry.Stop(); floating.Hide(); };
            hookProc = Hook;
            hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, hookProc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) throw new InvalidOperationException("无法注册鼠标右键监听。");
            if (!Native.RegisterHotKey(Handle, 1, 0x4003, 0x4D) && !testMode) Notify("快捷键被占用", "右键入口仍可用；Ctrl+Alt+M 已被其他程序占用。");
            if (!testMode) {
                EnsureOutput(); SaveSettings(); PrepareExtension();
                bridge = new LocalBridge(settings.Token, delegate(Clip clip) { BeginInvoke(new Action(delegate { EditClip(clip); })); });
                try { bridge.Start(); } catch (Exception ex) { Notify("浏览器连接未启动", "通用右键仍可使用。" + ex.Message); }
                Notify("内容迁移已启动", "选中文字 → 右键 → 蓝色内容迁移按钮。也可按 Ctrl+Alt+M。");
            }
        }
        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }
        IntPtr Hook(int code, IntPtr wp, IntPtr lp) {
            if (code >= 0 && !shuttingDown) {
                int message = wp.ToInt32();
                if (message == Native.WM_RBUTTONDOWN && settings.RightClickEnabled && !busy) {
                    selectionGeneration++;
                    BeginInvoke(new Action(delegate { floating.Hide(); }));
                    IntPtr hwnd = Native.GetForegroundWindow();
                    var data = (Native.MOUSE)Marshal.PtrToStructure(lp, typeof(Native.MOUSE));
                    if (hwnd != IntPtr.Zero && Native.Pid(hwnd) != (uint)Process.GetCurrentProcess().Id) {
                        selectionWindow = hwnd; selectionTask = null;
                    }
                } else if (message == Native.WM_RBUTTONUP && settings.RightClickEnabled && !busy) {
                    IntPtr hwnd = Native.GetForegroundWindow();
                    if (TestMode) File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-results", "integration-debug.txt"), "\r\nRightUp foreground=" + hwnd + " title=" + Native.Title(hwnd) + " pid=" + Native.Pid(hwnd));
                    if (hwnd != IntPtr.Zero && Native.Pid(hwnd) != (uint)Process.GetCurrentProcess().Id) {
                        var data = (Native.MOUSE)Marshal.PtrToStructure(lp, typeof(Native.MOUSE));
                        var point = new Point(data.pt.X, data.pt.Y);
                        int generation=selectionGeneration;
                        Task<string> task=SelectionReader.Start(hwnd,point);
                        selectionWindow=hwnd; selectionTask=task;
                        BeginInvoke(new Action(delegate { ShowForSelection(hwnd,point,task,generation); }));
                    }
                } else if (message == Native.WM_LBUTTONDOWN && floating.Visible) {
                    var data = (Native.MOUSE)Marshal.PtrToStructure(lp, typeof(Native.MOUSE));
                    if (!floating.Bounds.Contains(data.pt.X, data.pt.Y)) { selectionGeneration++; BeginInvoke(new Action(delegate { floating.Hide(); })); }
                } else if(message==Native.WM_LBUTTONDOWN) {
                    selectionGeneration++;
                }
            }
            return Native.CallNextHookEx(hook, code, wp, lp);
        }
        protected override void WndProc(ref Message message) {
            if (message.Msg == Native.WM_HOTKEY && !busy) CaptureSelection(Native.GetForegroundWindow(), false);
            if ((uint)message.Msg == SettingsMessage && !busy) BeginInvoke(new Action(ShowSettings));
            base.WndProc(ref message);
        }
        public bool ButtonVisible { get { return floating.Visible; } }
        public void TestEntry(string text) { ShowForSelection(Handle,new Point(300,300),Task.FromResult(text),selectionGeneration); }
        async void ShowForSelection(IntPtr hwnd, Point point, Task<string> selection, int generation) {
            if(selection==null)return;
            try {
                if(await Task.WhenAny(selection,Task.Delay(900))!=selection)return;
                string text=await selection;
                if(String.IsNullOrEmpty(text) || generation!=selectionGeneration || busy || shuttingDown || !settings.RightClickEnabled)return;
                sourceWindow=hwnd; floating.ShowAt(point); expiry.Stop(); expiry.Start();
            } catch { }
        }
        public Rectangle ButtonBounds { get { return floating.Bounds; } }
        public void Stop() { Close(); Dispose(); Application.ExitThread(); }
        public void ClickCaptureForTest() { CaptureSelection(sourceWindow, true); }
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
            try {
                // Read before Escape can collapse a webview selection. Never block the mouse hook.
                Task<string> selection = dismissMenu && selectionWindow == hwnd && selectionTask != null
                    ? selectionTask : SelectionReader.Start(hwnd, null);
                if (await Task.WhenAny(selection, Task.Delay(650)) == selection) clip.Text = await selection;
                selectionTask = null;
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
                if (!String.IsNullOrEmpty(clip.Text)) {
                    clip.Fidelity = "Windows 辅助功能接口提供的选区原文";
                    if (dismissMenu && Native.GetForegroundWindow() == hwnd) Native.Keys(0x1B);
                } else {
                if (!Native.SetForegroundWindow(hwnd) && Native.GetForegroundWindow() != hwnd) throw new InvalidOperationException("无法切回来源窗口，请重新选中文字后按 Ctrl+Alt+M。");
                if (dismissMenu) { Native.Keys(0x1B); await Task.Delay(130); }
                if (Native.GetForegroundWindow() != hwnd) throw new InvalidOperationException("来源窗口已经改变，请重新选中文字。");
                uint before = Native.GetClipboardSequenceNumber();
                if (!Native.Keys(0x11, 0x43)) throw new InvalidOperationException("该应用可能以管理员权限运行，普通权限工具不能向它发送复制操作。");
                for (int i = 0; i < 30; i++) {
                    await Task.Delay(60);
                    if (Native.GetClipboardSequenceNumber() == before) continue;
                    try {
                        sequenceAfterCopy = Native.GetClipboardSequenceNumber();
                        if (Clipboard.ContainsText(TextDataFormat.UnicodeText)) {
                            clip.Text = Clipboard.GetText(TextDataFormat.UnicodeText);
                            sequenceAfterCopy = Native.GetClipboardSequenceNumber(); copied = !String.IsNullOrEmpty(clip.Text);
                            if (copied) break;
                        }
                    } catch (ExternalException) { }
                }
                if (!copied) {
                    if (String.IsNullOrEmpty(clipboardFallback)) throw new InvalidOperationException("这个应用未提供可读取的选区，剪贴板也没有文字。\n请选中文字后按 Ctrl+C，再点内容迁移；新版会显示已复制的文字供核对。");
                    CapturePolicy.UseClipboardFallback(clip, clipboardFallback);
                }
                }
            } catch (Exception ex) {
                if (CapturePolicy.UseClipboardFallback(clip, clipboardFallback)) { }
                else { Error(ex.Message); busy = false; return; }
            }
            finally {
                if (sequenceAfterCopy != 0 && previous != null && Native.GetClipboardSequenceNumber() == sequenceAfterCopy) {
                    try { Clipboard.SetDataObject(previous, true); } catch { Notify("剪贴板恢复失败", "选区已取得；原剪贴板暂时无法恢复。"); }
                }
            }
            try {
                // UI Automation or Office can block; give metadata its own STA worker and a deadline.
                var metadata = new TaskCompletionSource<Clip>();
                var worker = new Thread(delegate() { try { metadata.TrySetResult(ReadSource(hwnd, pid, clip)); } catch { metadata.TrySetResult(clip); } });
                worker.IsBackground = true; worker.SetApartmentState(ApartmentState.STA); worker.Start();
                if (await Task.WhenAny(metadata.Task, Task.Delay(1800)) == metadata.Task) clip = await metadata.Task;
                busy = false; EditClip(clip);
            } catch (Exception ex) { busy = false; Error(ex.Message); }
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
        void EditClip(Clip clip) {
            floating.Hide();
            if (busy) { Error("已有一条摘录正在处理，请完成或取消后再试。"); return; }
            if (String.IsNullOrEmpty(clip.Text)) { Error("没有选中文字，未保存任何内容。"); return; }
            if (TestMode) { if (TestCaptured != null) TestCaptured(clip); return; }
            busy = true;
            try { using (var editor = new NoteDialog(clip, settings.OutputPath)) {
                if (editor.ShowDialog() == DialogResult.OK) Notify("已保存到内容迁移 TXT", "原文、来源和备注已追加。点击此通知可打开文件。");
            } } finally { busy = false; }
        }
        void EnsureOutput() {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settings.OutputPath)));
            using (var file = new FileStream(settings.OutputPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) { }
        }
        void ChangeOutput() {
            using (var dialog = new SaveFileDialog { Title = "选择内容迁移保存文件（追加，不覆盖）", Filter = "文本文件 (*.txt)|*.txt", FileName = settings.OutputPath, OverwritePrompt = false }) {
                if (dialog.ShowDialog() == DialogResult.OK) { string old = settings.OutputPath; settings.OutputPath = dialog.FileName;
                    try { EnsureOutput(); SaveSettings(); Notify("保存位置已更改", settings.OutputPath); } catch (Exception ex) { settings.OutputPath = old; Error(ex.Message); }
                }
            }
        }
        void OpenFile() { try { EnsureOutput(); Process.Start(settings.OutputPath); } catch (Exception ex) { Error(ex.Message); } }
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
        void PrepareExtension() {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "browser-extension"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "pairing.js"), "const CONTENT_MOVER_TOKEN = \"" + settings.Token + "\";\n", new UTF8Encoding(false));
            string image = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "content-mover.png");
            if (File.Exists(image)) File.Copy(image, Path.Combine(folder, "icon.png"), true);
        }
        protected override void OnFormClosed(FormClosedEventArgs e) {
            shuttingDown = true; if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            Native.UnregisterHotKey(Handle, 1); if (bridge != null) bridge.Dispose();
            expiry.Dispose(); floating.Dispose(); tray.Visible = false; tray.Dispose(); base.OnFormClosed(e);
        }
        public static Settings LoadSettings() {
            if (File.Exists(ConfigPath)) {
                Settings config = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(ConfigPath));
                if (config == null || String.IsNullOrWhiteSpace(config.OutputPath) || !Path.IsPathRooted(config.OutputPath) || String.IsNullOrWhiteSpace(config.Token))
                    throw new InvalidDataException("config.json 中的保存路径或浏览器配对信息无效。");
                return config;
            }
            return new Settings();
        }
    }
    // A loopback-only HTTP bridge; no Internet listener, no browser-controlled file paths.
    sealed class LocalBridge : IDisposable {
        TcpListener listener; readonly string token; readonly Action<Clip> received;
        readonly int port;
        public LocalBridge(string secret, Action<Clip> callback, int listenPort = 19339) { token = secret; received = callback; port = listenPort; }
        public void Start() {
            listener = new TcpListener(IPAddress.Loopback, port); listener.Start(8);
            TcpListener server = listener;
            var worker = new Thread(delegate() { while (listener != null) {
                try { var client = server.AcceptTcpClient(); ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); }); }
                catch (SocketException) { break; } catch (ObjectDisposedException) { break; }
            } }); worker.IsBackground = true; worker.Start();
        }
        void HandleClient(TcpClient client) {
            using (client) {
                client.ReceiveTimeout = 2500; client.SendTimeout = 2500;
                try {
                    var stream = client.GetStream(); var headerBytes = new List<byte>(); int value;
                    while ((value = stream.ReadByte()) >= 0) {
                        headerBytes.Add((byte)value); int n = headerBytes.Count;
                        if (n > 16384) { Reply(stream, 431, "Headers too large", ""); return; }
                        if (n >= 4 && headerBytes[n-4] == 13 && headerBytes[n-3] == 10 && headerBytes[n-2] == 13 && headerBytes[n-1] == 10) break;
                    }
                    string[] lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split(new string[] { "\r\n" }, StringSplitOptions.None);
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length; i++) { int pos = lines[i].IndexOf(':'); if (pos > 0) headers[lines[i].Substring(0, pos)] = lines[i].Substring(pos + 1).Trim(); }
                    string origin; headers.TryGetValue("Origin", out origin); origin = origin ?? "";
                    if (origin.Length > 0 && !origin.StartsWith("chrome-extension://", StringComparison.Ordinal)) { Reply(stream, 403, "Origin rejected", ""); return; }
                    if (lines[0] == "OPTIONS /capture HTTP/1.1") { Reply(stream, 204, "No Content", origin); return; }
                    string authorization; headers.TryGetValue("Authorization", out authorization);
                    if (authorization != "Bearer " + token) { Reply(stream, 403, "Authentication required", origin); return; }
                    if (lines[0] != "POST /capture HTTP/1.1") { Reply(stream, 404, "Not Found", origin); return; }
                    int length; string lengthText; headers.TryGetValue("Content-Length", out lengthText);
                    if (!Int32.TryParse(lengthText, out length) || length < 1 || length > 2 * 1024 * 1024) { Reply(stream, 413, "Invalid length", origin); return; }
                    byte[] data = new byte[length]; int read = 0;
                    while (read < length) { int count = stream.Read(data, read, length-read); if (count == 0) throw new EndOfStreamException(); read += count; }
                    var json = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(data));
                    string text = Field(json, "text"); if (String.IsNullOrEmpty(text)) { Reply(stream, 400, "Empty selection", origin); return; }
                    var clip = new Clip { Text = text, Title = Field(json, "title"), Source = Field(json, "url"), App = "浏览器扩展", Fidelity = Field(json, "fidelity") };
                    string frame = Field(json, "frameUrl"); if (frame.Length > 0 && frame != clip.Source) clip.Detail = "选区所在框架：" + frame;
                    received(clip); Reply(stream, 202, "Accepted", origin);
                } catch { try { Reply(client.GetStream(), 400, "Bad Request", ""); } catch { } }
            }
        }
        static string Field(Dictionary<string, object> json, string key) { object value; return json.TryGetValue(key, out value) && value is string ? (string)value : ""; }
        static void Reply(Stream stream, int status, string reason, string origin) {
            string cors = origin.Length == 0 ? "" : "Access-Control-Allow-Origin: " + origin + "\r\nAccess-Control-Allow-Methods: POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type, Authorization\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + reason + "\r\n" + cors + "Content-Length: 0\r\nConnection: close\r\n\r\n"); stream.Write(bytes, 0, bytes.Length);
        }
        public void Dispose() { var current = listener; listener = null; if (current != null) current.Stop(); }
    }
    static class Program {
        public const string FixtureText = "  中文原文\r\n第二行\t空格  \r\nUnicode：😀 café\r\n尾部空格  ";
        [STAThread] static void Main(string[] args) {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try {
                if (args.Length > 0 && args[0] == "--fixture") { Fixture(); return; }
                if (args.Length > 0 && args[0] == "--self-test") { SelfTest(); return; }
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
            using (var dialog = new NoteDialog(clip, "桌面\\内容迁移.txt")) {
                dialog.NoteBox.Text = "这段内容解释了关键概念，准备在后续研究中引用。";
                dialog.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) {
                    dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                    bitmap.Save(Path.Combine(TestFolder(), "note-dialog.png"));
                }
                dialog.Close();
            }
            CapturePolicy.UseClipboardFallback(clip, FixtureText); clip.App = "ChatGPT"; clip.RequiresTitle=true;
            using (var dialog = new NoteDialog(clip, "桌面\\内容迁移.txt")) {
                dialog.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) {
                    dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                    bitmap.Save(Path.Combine(TestFolder(), "clipboard-confirmation.png"));
                }
                dialog.Close();
            }
        }
        static void Assert(bool condition, string message) { if (!condition) throw new Exception("TEST FAILED: " + message); }
        static void SelfTest() {
            string folder = TestFolder(), path = Path.Combine(folder, "storage-" + Guid.NewGuid().ToString("N") + ".txt");
            var clip = new Clip { Text = FixtureText, App = "测试", Title = "选区测试", Source = "D:\\文档\\原文.docx" };
            string first = Storage.Format(clip, "留下的原因\n第二行备注", clip.Source);
            Storage.Append(path, clip, "留下的原因\n第二行备注", clip.Source);
            Assert(File.ReadAllText(path) == first, "保存原文与备注字节内容");
            Storage.Append(path, clip, "", "");
            Assert(File.ReadAllText(path) == first + Storage.Format(clip, "", ""), "追加不覆盖");
            Assert(File.ReadAllText(path).Contains(FixtureText), "中文、空格、换行、制表符、表情原样保留");
            Assert(!first.Contains("文本来源："), "TXT 不再显示文本来源元信息");
            using(var entry=new Controller(new Settings(),true)) {
                entry.TestEntry("");Assert(!entry.ButtonVisible,"无选区时不显示右键入口");
                entry.TestEntry("选中文字");Assert(entry.ButtonVisible,"非空选区时显示右键入口");
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
            var done = new ManualResetEvent(false); Clip received = null;
            using (var bridge = new LocalBridge("test-secret", delegate(Clip entry) { received = entry; done.Set(); }, 19340)) {
                bridge.Start();
                string json = new JavaScriptSerializer().Serialize(new { text = FixtureText, title = "测试页面", url = "https://example.test/source", fidelity = "网页选区原文" });
                Assert(RequestBridge(json, "wrong", "chrome-extension://test") == 403, "拒绝错误令牌");
                Assert(RequestBridge(json, "test-secret", "https://evil.test") == 403, "拒绝网页跨域调用");
                Assert(RequestBridge("{\"text\":\"\"}", "test-secret", "chrome-extension://test") == 400, "拒绝空文本");
                Assert(RequestBridge(json, "test-secret", "chrome-extension://test") == 202, "浏览器桥接接收");
                Assert(done.WaitOne(2000) && received.Text == FixtureText && received.Source == "https://example.test/source", "桥接文本和出处完整");
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
            File.WriteAllText(Path.Combine(folder, "self-test.txt"), "PASS: UTF-8 fidelity; append; multiline notes; bridge auth/origin; clipboard fallback; confirmation blocks writes; confirmed save; startup enable/disable.\r\n", Encoding.UTF8);
        }
        static int RequestBridge(string json, string secret, string origin) {
            using (var client = new TcpClient("127.0.0.1", 19340)) {
                byte[] body = Encoding.UTF8.GetBytes(json); var stream = client.GetStream();
                byte[] header = Encoding.ASCII.GetBytes("POST /capture HTTP/1.1\r\nHost: 127.0.0.1\r\nOrigin: " + origin + "\r\nAuthorization: Bearer " + secret + "\r\nContent-Length: " + body.Length + "\r\n\r\n");
                stream.Write(header, 0, header.Length); stream.Write(body, 0, body.Length);
                string line = new StreamReader(stream).ReadLine(); return Int32.Parse(line.Split(' ')[1]);
            }
        }
        static void Fixture() {
            var form = new Form { Text = "内容迁移测试选区", Size = new Size(620, 330), StartPosition = FormStartPosition.CenterScreen, TopMost = true };
            var text = new TextBox { Multiline = true, Dock = DockStyle.Fill, Text = FixtureText, Font = new Font("Microsoft YaHei UI", 12) };
            form.Controls.Add(text); form.Shown += delegate { text.Focus(); text.SelectAll(); }; Application.Run(form);
        }
        static void IntegrationTest() {
            string folder = TestFolder();
            var inputDesktop = Native.OpenInputDesktop(0, false, 1);
            File.WriteAllText(Path.Combine(folder, "desktop-debug.txt"), "Thread desktop=" + Native.DesktopName(Native.GetThreadDesktop(Native.GetCurrentThreadId())) + " input desktop=" + Native.DesktopName(inputDesktop));
            if (inputDesktop != IntPtr.Zero) Native.CloseDesktop(inputDesktop);
            using (var controller = new Controller(new Settings(), true)) {
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
                        File.WriteAllText(Path.Combine(folder, "integration-test.txt"), "PASS: real right-click hook; floating entry; Ctrl+C fresh selection; exact Unicode/whitespace; clipboard restoration; source title; note dialog rendered.\r\n", Encoding.UTF8);
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
