using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.IO;

// ClipNotify —— 剪贴板变化提示工具
// 检测到剪贴板内容变化时，按设置弹出浮窗提示和/或发出提示音。
// v11 新增：托盘右键菜单增加"日志记录"开关，关闭后不再写 clipnotify.log。
// v12 新增：提示音可调节音量（小/中/大），并提供 3 种音色选择（清脆叮/柔和叮/叮咚两声）。
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
        // MCI：播放 WAV 并可控制音量（winmm.dll）
        [DllImport("winmm.dll")]
        internal static extern int mciSendString(string command, System.Text.StringBuilder ret, int retLen, IntPtr hwnd);
        // waveOut 设备音量：0xFFFFFFFF = 左右声道均满幅；WAVE_MAPPER = -1
        [DllImport("winmm.dll")]
        internal static extern uint waveOutSetVolume(IntPtr uDeviceID, uint dwVolume);
        [DllImport("winmm.dll")]
        internal static extern uint waveOutGetVolume(IntPtr uDeviceID, out uint dwVolume);

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
        public static bool Log = true;      // 日志记录开关：关闭后不再写 clipnotify.log
        public static int SoundType = 0;    // 提示音音色：0..7 见菜单
        public static int SoundVolume = 6;  // 提示音音量：1-10（默认 6）

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
                            else if (key == "log") Log = val;
                            else if (key == "soundtype") { try { SoundType = int.Parse(kv[1].Trim()); } catch { } }
                            else if (key == "soundvolume") { try { SoundVolume = int.Parse(kv[1].Trim()); } catch { } }
                        }
                    }
                }
                // 防御：数值越界时回落到默认值
                if (SoundType < 0 || SoundType > 7) SoundType = 0;
                // 旧版本 volume 是 0-100（30/60/100 三档），迁移到 1-10
                if (SoundVolume > 10) { SoundVolume = (int)Math.Round(SoundVolume / 10.0); }
                if (SoundVolume < 1) SoundVolume = 1;
                if (SoundVolume > 10) SoundVolume = 10;
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
                    "precise=" + (Precise ? "true" : "false") + "\r\n" +
                    "log=" + (Log ? "true" : "false") + "\r\n" +
                    "soundtype=" + SoundType + "\r\n" +
                    "soundvolume=" + SoundVolume + "\r\n");
            }
            catch { }
        }
    }

    // 提示音：程序内置生成 8 种 WAV 音色（无需附带音频文件），用 MCI 播放并可控音量
    static class SoundFiles
    {
        public const int TYPE_COUNT = 8;
        // 音量档位：1-10，波形幅度按档位缩放（MCI 的 setaudio 对 WAV 无效，用幅度才是可靠音量控制）
        public static readonly int[] Volumes = { 1,2,3,4,5,6,7,8,9,10 };
        private static readonly string[] Bases = {
            "ding_clear", "ding_soft", "ding_dong",   // 0..2 原 3 种
            "tri_tone", "glass", "hero", "chime", "marimba"  // 3..7 经典风格
        };
        private static string _dir;
        private static int _curType = -1;   // 当前已打开的 MCI 别名对应音色，-1 表示未打开
        private static int _curVolume = -1; // 当前已打开的 MCI 别名对应音量档位

        private static string DirPath
        {
            get
            {
                if (_dir == null)
                {
                    _dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ClipNotify", "sounds");
                }
                return _dir;
            }
        }

        // 文件路径：音色_音量.wav，例如 ding_clear_v60.wav
        private static string FilePath(int type, int volume)
        {
            return Path.Combine(DirPath, Bases[type] + "_v" + volume + ".wav");
        }

        // 启动时只确保"当前选中音色+当前音量"的文件存在（其他按需生成）
        public static void EnsureFiles()
        {
            EnsureFor(Settings.SoundType, Settings.SoundVolume);
        }

        // 确保指定音色+音量的文件存在，不存在才生成
        public static void EnsureFor(int type, int volume)
        {
            try
            {
                Directory.CreateDirectory(DirPath);
                if (type < 0 || type >= TYPE_COUNT) type = 0;
                if (volume < 1) volume = 1;
                if (volume > 10) volume = 10;
                string p = FilePath(type, volume);
                if (!File.Exists(p) || new FileInfo(p).Length < 1000)
                    Generate(type, volume, p);
            }
            catch { }
        }

        // 生成 WAV：16bit 单声道 44.1kHz，正弦波 + 指数衰减包络，幅度按音量档缩放
        private static void Generate(int type, int volume, string path)
        {
            const int rate = 44100;
            // 各音色总时长
            double total = 0.10;
            if (type == 0) total = 0.10;      // 清脆叮：高频短促
            else if (type == 1) total = 0.16; // 柔和叮：中频舒缓
            else if (type == 2) total = 0.26; // 叮咚两声：先叮后咚
            else if (type == 3) total = 0.36; // Tri-tone：三音上行
            else if (type == 4) total = 0.09;  // Glass：玻璃短音
            else if (type == 5) total = 0.32;  // Hero：双音升调
            else if (type == 6) total = 0.40;  // Chime：风铃长尾
            else total = 0.28;                 // Marimba：短琶音

            double amp = volume / 10.0; // 音量 1-10 → 0.1..1.0

            int n = (int)(rate * total);
            short[] data = new short[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate;
                double v;
                if (type == 0)
                {
                    v = Math.Sin(2 * Math.PI * 1318 * t) * Math.Exp(-t / 0.030) * amp * 0.9;
                }
                else if (type == 1)
                {
                    v = Math.Sin(2 * Math.PI * 880 * t) * Math.Exp(-t / 0.060) * amp * 0.9;
                }
                else if (type == 2)
                {
                    if (t < 0.09)
                        v = Math.Sin(2 * Math.PI * 1318 * t) * Math.Exp(-t / 0.028) * amp * 0.9;
                    else if (t >= 0.11)
                    {
                        double t2 = t - 0.11;
                        v = Math.Sin(2 * Math.PI * 990 * t2) * Math.Exp(-t2 / 0.045) * amp * 0.9;
                    }
                    else v = 0;
                }
                else if (type == 3) // Tri-tone：C-E-G 上行（iPhone 经典短信音风格）
                {
                    double f;
                    if (t < 0.12) f = 1318;
                    else if (t < 0.24) f = 1661;
                    else f = 1975;
                    v = Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t / 0.04) * amp * 0.85;
                }
                else if (type == 4) // Glass：高频玻璃单音（macOS 风格）
                {
                    v = Math.Sin(2 * Math.PI * 2093 * t) * Math.Exp(-t / 0.018) * amp * 0.85;
                }
                else if (type == 5) // Hero：双音升调（macOS iChat 风格）
                {
                    double f; double t0;
                    if (t < 0.14) { f = 1047; t0 = t; }
                    else { f = 1568; t0 = t - 0.16; }
                    v = Math.Sin(2 * Math.PI * f * t0) * Math.Exp(-t0 / 0.05) * amp * 0.85;
                }
                else if (type == 6) // Chime：风铃两音（Windows XP 风格）
                {
                    double f; double t0;
                    if (t < 0.16) { f = 880; t0 = t; }
                    else { f = 1175; t0 = t - 0.18; }
                    v = Math.Sin(2 * Math.PI * f * t0) * Math.Exp(-t0 / 0.08) * amp * 0.8;
                }
                else // Marimba：快速三音琶音
                {
                    double f; double t0;
                    if (t < 0.08) { f = 1568; t0 = t; }
                    else if (t < 0.16) { f = 1975; t0 = t - 0.08; }
                    else { f = 2637; t0 = t - 0.16; }
                    v = Math.Sin(2 * Math.PI * f * t0) * Math.Exp(-t0 / 0.03) * amp * 0.85;
                }
                // 首尾 2ms 淡入淡出，避免爆音
                double edge = Math.Min(t, total - t);
                if (edge < 0.002) v *= edge / 0.002;

                if (v > 1) v = 1; if (v < -1) v = -1;
                data[i] = (short)(v * 32767);
            }

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                int byteRate = rate * 2;
                int dataLen = n * 2;
                fs.Write(new byte[] { 0x52, 0x49, 0x46, 0x46 }, 0, 4); // "RIFF"
                WriteInt(fs, 36 + dataLen);
                fs.Write(new byte[] { 0x57, 0x41, 0x56, 0x45 }, 0, 4); // "WAVE"
                fs.Write(new byte[] { 0x66, 0x6D, 0x74, 0x20 }, 0, 4); // "fmt "
                WriteInt(fs, 16);
                WriteShort(fs, 1);        // PCM
                WriteShort(fs, 1);        // 单声道
                WriteInt(fs, rate);
                WriteInt(fs, byteRate);
                WriteShort(fs, 2);        // blockAlign
                WriteShort(fs, 16);       // 16bit
                fs.Write(new byte[] { 0x64, 0x61, 0x74, 0x61 }, 0, 4); // "data"
                WriteInt(fs, dataLen);
                for (int i = 0; i < n; i++) WriteShort(fs, data[i]);
            }
        }

        private static void WriteInt(FileStream fs, int v)
        {
            fs.Write(BitConverter.GetBytes(v), 0, 4);
        }

        private static void WriteShort(FileStream fs, int v)
        {
            fs.Write(BitConverter.GetBytes((short)v), 0, 2);
        }

        // 播放指定音色与音量档位（音量通过波形幅度实现），失败时静默回退系统蜂鸣
        public static void Play(int type, int volume)
        {
            try
            {
                if (type < 0 || type >= TYPE_COUNT) type = 0;
                int vol = volume;
                if (vol < 1) vol = 1; if (vol > 10) vol = 10;

                string p = FilePath(type, vol);
                if (!File.Exists(p)) EnsureFor(type, vol);

                // 音色或音量变化时重新打开对应文件
                if (_curType != type || _curVolume != vol)
                {
                    if (_curType >= 0)
                        NativeMethods.mciSendString("close cnSnd", null, 0, IntPtr.Zero);
                    _curType = -1;
                    _curVolume = -1;
                    var sb = new System.Text.StringBuilder();
                    if (NativeMethods.mciSendString("open \"" + p + "\" type waveaudio alias cnSnd", sb, 0, IntPtr.Zero) == 0)
                    {
                        _curType = type;
                        _curVolume = vol;
                        // 强制把默认 wave 输出设备音量拉满（其他程序可能临时调低过）
                        NativeMethods.waveOutSetVolume((IntPtr)(-1), 0xFFFFFFFF);
                    }
                }
                if (_curType < 0)
                {
                    NativeMethods.Beep(1318, 80);
                    return;
                }

                // 每次播放前再确认一次设备音量（防止被其他程序抢占改小）
                NativeMethods.waveOutSetVolume((IntPtr)(-1), 0xFFFFFFFF);
                // 先停止上一次（快速连续复制时新音立即覆盖旧音）
                NativeMethods.mciSendString("stop cnSnd", null, 0, IntPtr.Zero);
                if (NativeMethods.mciSendString("play cnSnd from 0", null, 0, IntPtr.Zero) != 0)
                    NativeMethods.Beep(1318, 80);
            }
            catch
            {
                try { NativeMethods.Beep(1318, 80); } catch { }
            }
        }

        // 把任意音量值归一到最近的音量档位
        public static int NearestVolume(int volume)
        {
            int best = Volumes[0];
            int bestDiff = Math.Abs(volume - Volumes[0]);
            for (int i = 1; i < Volumes.Length; i++)
            {
                int d = Math.Abs(volume - Volumes[i]);
                if (d < bestDiff) { bestDiff = d; best = Volumes[i]; }
            }
            return best;
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

        // 诊断日志：记录剪贴板变化时的关键状态，便于排查（可在右键菜单关闭）
        private static void Log(string msg)
        {
            if (!Settings.Log) return; // 日志开关关闭时不写入
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
            SoundFiles.EnsureFiles(); // 确保当前音色+音量的提示音文件就绪

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

            // 提示音音色（三选一）
            ToolStripMenuItem mSoundType = new ToolStripMenuItem("提示音音色");
            string[] typeNames = { "清脆叮", "柔和叮", "叮咚两声", "三音上行", "玻璃音", "双音升调", "风铃", "马林巴琶音" };
            ToolStripMenuItem[] typeItems = new ToolStripMenuItem[typeNames.Length];
            for (int i = 0; i < typeNames.Length; i++)
            {
                int idx = i;
                typeItems[i] = new ToolStripMenuItem(typeNames[i]);
                typeItems[i].Checked = (Settings.SoundType == idx);
                typeItems[i].Click += (s, e) =>
                {
                    Settings.SoundType = idx;
                    for (int j = 0; j < typeItems.Length; j++) typeItems[j].Checked = (j == idx);
                    Settings.Save();
                    SoundFiles.Play(Settings.SoundType, Settings.SoundVolume); // 选中即试听
                };
                mSoundType.DropDownItems.Add(typeItems[i]);
            }

            // 提示音音量（1-10）
            ToolStripMenuItem mVolume = new ToolStripMenuItem("提示音音量");
            int[] volVals = { 1,2,3,4,5,6,7,8,9,10 };
            ToolStripMenuItem[] volItems = new ToolStripMenuItem[volVals.Length];
            for (int i = 0; i < volVals.Length; i++)
            {
                int idx = i;
                int vval = volVals[i];
                volItems[i] = new ToolStripMenuItem("音量 " + vval);
                volItems[i].Checked = (Settings.SoundVolume == vval);
                volItems[i].Click += (s, e) =>
                {
                    Settings.SoundVolume = vval;
                    for (int j = 0; j < volItems.Length; j++) volItems[j].Checked = (volVals[j] == Settings.SoundVolume);
                    Settings.Save();
                    SoundFiles.Play(Settings.SoundType, Settings.SoundVolume); // 选中即试听
                };
                mVolume.DropDownItems.Add(volItems[i]);
            }

            ToolStripMenuItem mToast = new ToolStripMenuItem("浮窗提示");
            mToast.CheckOnClick = true;
            mToast.Checked = Settings.Toast;
            mToast.CheckedChanged += (s, e) => { Settings.Toast = mToast.Checked; Settings.Save(); };

            ToolStripMenuItem mPrecise = new ToolStripMenuItem("精确模式（仅Ctrl+C时提示）");
            mPrecise.CheckOnClick = true;
            mPrecise.Checked = Settings.Precise;
            mPrecise.CheckedChanged += (s, e) => { Settings.Precise = mPrecise.Checked; Settings.Save(); };

            ToolStripMenuItem mLog = new ToolStripMenuItem("日志记录");
            mLog.CheckOnClick = true;
            mLog.Checked = Settings.Log;
            mLog.CheckedChanged += (s, e) => { Settings.Log = mLog.Checked; Settings.Save(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add(mSound);
            menu.Items.Add(mSoundType);
            menu.Items.Add(mVolume);
            menu.Items.Add(mToast);
            menu.Items.Add(mPrecise);
            menu.Items.Add(mLog);
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
                    // 按用户选择的音色与音量播放（程序内置生成的 WAV，音量实时可控）
                    SoundFiles.Play(Settings.SoundType, Settings.SoundVolume);
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
