using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.IO;

// ClipNotify —— 剪贴板变化提示工具（v4）
// 检测到剪贴板内容变化时，按设置弹出浮窗提示和/或发出提示音。
// v4 新增：托盘右键菜单可选择"提示音"和"浮窗提示"两个独立开关，
//         设置保存到 %LOCALAPPDATA%\ClipNotify\settings.ini，重启后仍生效。
namespace ClipNotify
{
    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int vKey);
        [DllImport("kernel32.dll")]
        internal static extern bool Beep(uint dwFreq, uint dwDuration);
        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

        // 低级键盘钩子：用于区分"真实按键"与"应用模拟注入的按键"
        internal delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        internal const int VK_CONTROL = 0x11;   // 通用 Ctrl
        internal const int VK_LCONTROL = 0xA2;  // 左 Ctrl（低级钩子实际报告的键码）
        internal const int VK_RCONTROL = 0xA3;  // 右 Ctrl
        internal const int WH_KEYBOARD_LL = 13;
        internal const int WM_KEYDOWN = 0x0100;
        internal const int WM_KEYUP = 0x0101;
        internal const int WM_SYSKEYDOWN = 0x0104;
        internal const int WM_SYSKEYUP = 0x0105;
        internal const int LLKHF_INJECTED = 0x10; // 模拟注入标志

        // Ctrl 键当前是否处于按下状态
        internal static bool IsCtrlDown()
        {
            return (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        }
    }

    // 用户设置：提示音开关、浮窗开关、精确模式（仅 Ctrl+C 时提示），持久化到本地配置文件
    static class Settings
    {
        public static bool Sound = true;
        public static bool Toast = true;
        public static bool Precise = false; // 精确模式：只有按下 Ctrl 键（真正按 Ctrl+C）才提示

        private static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClipNotify", "settings.ini");
            }
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        string[] kv = line.Split('=');
                        if (kv.Length == 2)
                        {
                            string key = kv[0].Trim();
                            bool val = kv[1].Trim().ToLowerInvariant() == "true";
                            if (key == "sound") Sound = val;
                            else if (key == "toast") Toast = val;
                            else if (key == "precise") Precise = val;
                        }
                    }
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath,
                    "sound=" + (Sound ? "true" : "false") + "\r\n" +
                    "toast=" + (Toast ? "true" : "false") + "\r\n" +
                    "precise=" + (Precise ? "true" : "false") + "\r\n");
            }
            catch { }
        }
    }

    static class Program
    {
        private static NotifyIcon _tray;
        private static Timer _pollTimer;
        private static uint _lastSeq;
        private static ToastForm _toast;
        private static bool _createdNew;

        // 键盘钩子相关：记录"用户真实按下"的 Ctrl 状态（排除应用模拟注入）
        private static NativeMethods.HookProc _hookProc;
        private static IntPtr _hookHandle = IntPtr.Zero;
        private static bool _userCtrlDown;

        private static readonly System.Threading.Mutex _mutex =
            new System.Threading.Mutex(true, @"Local\ClipNotify_OnlyOne_CSharp", out _createdNew);

        // 诊断日志：记录剪贴板变化时的关键状态，便于排查
        private static void Log(string msg)
        {
            try
            {
                string p = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClipNotify", "clipnotify.log");
                File.AppendAllText(p,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }

        [STAThread]
        static void Main()
        {
            if (!_createdNew) return; // 已有实例在运行

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Settings.Load();

            _tray = new NotifyIcon();
            // 使用 exe 自带的程序图标（编译时通过 /win32icon 嵌入）
            try
            {
                _tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                _tray.Icon = SystemIcons.Information;
            }
            _tray.Text = "ClipNotify 剪贴板提示工具";
            _tray.Visible = true;

            // 右键菜单：提示音 / 浮窗提示 两个独立开关
            ToolStripMenuItem mSound = new ToolStripMenuItem("提示音");
            mSound.CheckOnClick = true;
            mSound.Checked = Settings.Sound;
            mSound.CheckedChanged += (s, e) => { Settings.Sound = mSound.Checked; Settings.Save(); };

            ToolStripMenuItem mToast = new ToolStripMenuItem("浮窗提示");
            mToast.CheckOnClick = true;
            mToast.Checked = Settings.Toast;
            mToast.CheckedChanged += (s, e) => { Settings.Toast = mToast.Checked; Settings.Save(); };

            ToolStripMenuItem mPrecise = new ToolStripMenuItem("精确模式（仅Ctrl+C时提示）");
            mPrecise.CheckOnClick = true;
            mPrecise.Checked = Settings.Precise;
            mPrecise.CheckedChanged += (s, e) => { Settings.Precise = mPrecise.Checked; Settings.Save(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add(mSound);
            menu.Items.Add(mToast);
            menu.Items.Add(mPrecise);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("测试提示", null, (s, e) => Notify("手动测试"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => ExitApp());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => ExitApp();

            _toast = new ToastForm();

            InstallKeyboardHook();

            _lastSeq = NativeMethods.GetClipboardSequenceNumber();

            _pollTimer = new Timer();
            _pollTimer.Interval = 100;
            _pollTimer.Tick += (s, e) => CheckClipboard();
            _pollTimer.Start();

            // 自检：启动 1.5 秒后自动提示一次（UI 线程 Timer）
            Timer startupTimer = new Timer();
            startupTimer.Interval = 1500;
            startupTimer.Tick += (s, e) =>
            {
                startupTimer.Stop();
                Notify("ClipNotify 已运行，复制内容试试");
            };
            startupTimer.Start();

            Application.Run();
        }

        private static void ExitApp()
        {
            _tray.Visible = false;
            if (_toast != null) { _toast.Dispose(); _toast = null; }
            Application.Exit();
        }

        // 安装低级键盘钩子，监听真实 Ctrl 按键
        private static void InstallKeyboardHook()
        {
            try
            {
                _hookProc = HookCallback;
                _hookHandle = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);
                if (_hookHandle != IntPtr.Zero)
                    Log("键盘钩子安装成功");
                else
                    Log("键盘钩子安装失败 err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
            catch (Exception ex)
            {
                Log("键盘钩子安装异常: " + ex.Message);
            }
        }

        // 钩子回调：只记录真实（非注入）的 Ctrl 按下/释放
        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    NativeMethods.KBDLLHOOKSTRUCT kb =
                        (NativeMethods.KBDLLHOOKSTRUCT)System.Runtime.InteropServices.Marshal.PtrToStructure(
                            lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));
                    // 低级钩子报告的 Ctrl 是左右具体键码（0xA2/0xA3），0x11 也兼容判断
                    if (kb.vkCode == NativeMethods.VK_CONTROL ||
                        kb.vkCode == NativeMethods.VK_LCONTROL ||
                        kb.vkCode == NativeMethods.VK_RCONTROL)
                    {
                        bool injected = (kb.flags & NativeMethods.LLKHF_INJECTED) != 0;
                        uint msg = (uint)wParam;
                        bool down = (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN);
                        bool up = (msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP);
                        Log("  键盘事件 vk=0x" + kb.vkCode.ToString("X2") +
                            " " + (down ? "按下" : (up ? "释放" : "其他")) +
                            " 注入=" + injected);
                        if (!injected)
                        {
                            if (down) _userCtrlDown = true;
                            else if (up) _userCtrlDown = false;
                        }
                    }
                }
                catch { }
            }
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        // 精确模式下判断 Ctrl 是否被"用户真实按下"（钩子可用时优先，失败回退 GetAsyncKeyState）
        private static bool RealCtrlDown()
        {
            if (_hookHandle != IntPtr.Zero) return _userCtrlDown;
            return NativeMethods.IsCtrlDown();
        }

        private static void CheckClipboard()
        {
            try
            {
                uint cur = NativeMethods.GetClipboardSequenceNumber();
                if (cur != _lastSeq)
                {
                    _lastSeq = cur;
                    bool ctrl = RealCtrlDown();
                    Log("剪贴板变化 序号=" + cur + " 真实Ctrl=" + ctrl +
                        " (钩子=" + (_hookHandle != IntPtr.Zero) + ") 精确模式=" + Settings.Precise);

                    // 精确模式：只认"按下 Ctrl 键"的复制（真正按了 Ctrl+C），
                    // 过滤微信/豆包划词等"选中即写入剪贴板"的自动行为
                    if (Settings.Precise && !ctrl)
                    {
                        Log("  精确模式判定：Ctrl未按下，跳过提示");
                        return;
                    }

                    // 每次复制都提示：浮窗内容立即覆盖刷新（ToastForm 内部处理），不做去抖
                    Notify(DescribeClipboard());
                }
            }
            catch { }
        }

        private static string DescribeClipboard()
        {
            string text = "";
            try
            {
                if (Clipboard.ContainsText()) text = Clipboard.GetText();
            }
            catch { }

            text = text.Replace("\r", " ").Replace("\n", " ");
            if (text.Length > 60) text = text.Substring(0, 60) + "…";

            if (text.Length > 0) return "复制内容：" + text;

            try
            {
                if (Clipboard.ContainsFileDropList()) return "复制内容：文件";
                if (Clipboard.ContainsImage()) return "复制内容：图片";
            }
            catch { }
            return "检测到剪贴板内容变化";
        }

        private static void Notify(string body)
        {
            if (!Settings.Sound && !Settings.Toast) return; // 都关了就不提示

            try
            {
                if (Settings.Sound)
                {
                    // 单音清脆"叮"：E6，80ms（避免双音听起来像"响两下"）
                    NativeMethods.Beep(1318, 80);
                }
                if (Settings.Toast)
                {
                    _toast.ShowToast("剪贴板已更新", DateTime.Now.ToString("HH:mm:ss") + " " + body);
                }
            }
            catch { }
        }
    }

    // 右下角自绘浮窗：立即刷新、短时消失、不排队、不抢焦点
    class ToastForm : Form
    {
        private Label _title;
        private Label _body;
        private Timer _hideTimer;
        private const int DISPLAY_MS = 1400;

        public ToastForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(30, 36, 46);
            Width = 340;
            Height = 78;
            Region = Region.FromHrgn(NativeMethods.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 12, 12));

            _title = new Label();
            _title.Text = "剪贴板已更新";
            _title.ForeColor = Color.White;
            _title.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            _title.Location = new Point(16, 10);
            _title.AutoSize = true;

            _body = new Label();
            _body.ForeColor = Color.FromArgb(205, 210, 218);
            _body.Font = new Font("Microsoft YaHei UI", 9.5f);
            _body.Location = new Point(16, 33);
            _body.AutoSize = false;
            _body.Size = new Size(Width - 32, 36);
            _body.Text = "";

            Controls.Add(_title);
            Controls.Add(_body);

            _hideTimer = new Timer();
            _hideTimer.Interval = DISPLAY_MS;
            _hideTimer.Tick += (s, e) => { _hideTimer.Stop(); Hide(); };
        }

        // 不抢焦点、不打断用户输入
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        public void ShowToast(string title, string body)
        {
            // 跨线程保护：若从非 UI 线程调用，转交 UI 线程执行
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => ShowToast(title, body)));
                return;
            }
            _title.Text = title;
            _body.Text = body;
            PositionBottomRight();
            if (!Visible)
            {
                Opacity = 1;
                Show();
            }
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void PositionBottomRight()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        }
    }
}
