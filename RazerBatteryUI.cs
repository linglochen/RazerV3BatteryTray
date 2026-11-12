// 毒蝰 V3 Pro 任务栏电量显示（带设置界面）
//
// 电量协议（OpenRazer 交叉验证，本机实测）：
//   91 字节 HID 特征报告：[0]=ReportID 0x00，随后 90 字节雷蛇报告
//   电量查询 TID=0x1F / class=0x07 / cmd=0x80 / dataSize=2，CRC=字节[3..88]异或
//   充电查询 cmd=0x84；响应 status=0x02 时偏移10=电量原始值(0..255)
//   接口：VID 0x1532 / PID 0x00C1(无线) 0x00C0(有线)，鼠标 collection(UsagePage=1,Usage=2)，
//   特征报告 91 字节；CreateFile dwDesiredAccess=0 绕过系统鼠标接口独占锁，无需管理员。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml.Serialization;
using Microsoft.Win32;

[assembly: AssemblyTitle("RazerV3BatteryTray")]
[assembly: AssemblyProduct("雷蛇毒蝰 V3 Pro 电量托盘")]
[assembly: AssemblyDescription("雷蛇毒蝰 V3 Pro 任务栏电量显示（HID 直连，无需 Synapse/管理员）")]
[assembly: AssemblyCompany("岚乔")]
[assembly: AssemblyCopyright("Copyright © 岚乔 2026")]
[assembly: AssemblyVersion("1.8.0.0")]
[assembly: AssemblyFileVersion("1.8.0.0")]

public class ColorRange {
    public int Min = 0;
    public int Max = 100;
    public string ColorHex = "#00E676";
    public Color ToColor() { try { return ColorTranslator.FromHtml(ColorHex); } catch { return Color.Red; } }
}

// 渲染参数：电量/充电/样式 + 全部外观设置（从 Settings 换算而来，动画相位由程序循环递增）
public class RenderOpts {
    public int Pct = -1;                  // -1 = 未连接（画 ?）
    public bool Charging = false;
    public string Style = "plain";        // plain|ring|mouse|batH|batV|barH|barV|bolt|circle|gauge|hex|drop
    public bool ShowNumber = true;
    public int FontScale = 100;           // 文字缩放 60-160 (%)
    public int Thick = 4;                 // 环/条粗度等级 1-8
    public bool ShowPercent = false;      // 数字后追加 % 号
    public int AnimPhase = 0;             // 充电动画相位 0-7（填充上涨 + 闪电闪动）
    public int NumberOutline = 2;         // 数字描边粗细 0-4（0=无）
    public string NumberColorHex = "#FFFFFF";   // 数字颜色
}

// 任务栏浅色/深色：Win11 原生托盘图标在浅色任务栏是黑、深色任务栏是白。
// 缓存 5 秒，避免充电动画每 150ms 读一次注册表
static class Theme {
    static bool _light;
    static DateTime _stamp = DateTime.MinValue;
    public static bool LightTaskbar {
        get {
            if ((DateTime.Now - _stamp).TotalSeconds > 5) {
                _stamp = DateTime.Now;
                try {
                    using (var k = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
                        object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                        _light = v != null && Convert.ToInt32(v) == 1;
                    }
                } catch { _light = false; }
            }
            return _light;
        }
    }
    public static Color Foreground {
        get { return LightTaskbar ? Color.FromArgb(22, 22, 26) : Color.White; }
    }
}

static class ColorExt {
    public static bool IsAuto(string hex) {
        return string.Equals(hex, "auto", StringComparison.OrdinalIgnoreCase);
    }
    public static Color ParseSafe(string hex) {
        if (IsAuto(hex)) return Theme.Foreground;
        try { return ColorTranslator.FromHtml(hex); } catch { return Color.Empty; }
    }
}

public class Settings {
    public bool AutoStart = false;
    public string IconStyle = "plain";      // plain|ring|mouse|batH|batV|barH|barV|bolt|circle|gauge|hex|drop
    public int RefreshSeconds = 60;         // 轮询间隔 1-3600 秒
    public bool ShowNumber = true;          // 是否绘制数字
    public bool ShowPercent = false;        // 数字后追加 % 号
    public int FontScale = 100;             // 文字缩放 60-160 (%)
    public int RingThickness = 6;           // 环/条粗度等级 1-8
    public int PollRate = 1000;             // 常规轮询率（智能切换退出游戏时恢复）
    public bool SmartPoll = false;          // 全屏游戏时自动切换轮询率
    public int GamePollRate = 2000;         // 游戏轮询率
    public bool AdvDyn = true;              // 高级：动态灵敏度预览
    public int AdvCurve = 1;                // 0 经典 1 自然 2 跳跃 3 自定义
    public bool AdvRotate = false;          // 高级：旋转预览
    public int AdvRotateDeg = 0;            // -44..44
    public float[] CustomCurve = { 1.00f, 1.15f, 1.30f, 1.42f, 1.50f };  // 自定义曲线 5 个点
    public List<string> Samples = new List<string>();   // 电量采样 "ISO时间|电量|充电"（用于估算可用时长）
    public List<ColorRange> Ranges = new List<ColorRange> {
        new ColorRange { Min = 20, Max = 100, ColorHex = "#00E676" },
        new ColorRange { Min = 10, Max = 20,  ColorHex = "#FF9800" },
        new ColorRange { Min = 0,  Max = 10,  ColorHex = "#F44336" },
    };
    public List<string> History = new List<string>();   // 事件记录（新事件 append）
    public string LastFullTime = "";                    // 最近一次充满电时间
    public string LastEmptyTime = "";                   // 最近一次没电时间
    public bool LowBatteryNotify = true;                 // 低电量弹窗提醒开关
    public int LowBatteryThreshold = 20;                 // 低电量提醒阈值（%）
    public bool FullBatteryNotify = true;                // 充满电弹窗提醒开关
    public int NumberOutline = 2;                          // 数字描边粗细 0-4（0=无）
    public string NumberColorHex = "auto";                 // 数字颜色（auto=跟随任务栏深浅色）
    public bool EstimateEnabled = true;                   // 预估可用时长开关
    public int EstimateSegments = 3;                      // 用最近几段掉电样本平均估算（1-10）
    public List<string> ChargeSamples = new List<string>();  // 充电采样 "ISO时间|电量"（估充满时间）
    public List<string> ChargeStats = new List<string>();    // 充电耗时 "开始时刻|充满时刻|秒数|起电量"（上限 100）
    public List<string> SleepRecords = new List<string>();   // 休眠记录 "休眠时刻|休眠电量|唤醒时刻|唤醒电量|时长秒"（上限 100）
    public bool DailyReport = false;                      // 每日定时电量播报开关
    public int DailyReportMax = 3;                        // 每日最多播报次数
    public List<string> DailyTimes = new List<string>();  // 播报时间 "HH:mm" 列表
    public int LogKeepDays = 30;                          // 运行日志归档保留天数（0=永久）
    public List<string> RunStats = new List<string>();    // 续航统计 "满电时刻|没电时刻|秒数"（上限 100 条）
    public string CurrentFullStart = "";                  // 当前续航区间起点（满电时刻，未闭合）
    public string SleepOpen = "";                         // 未闭合入睡 "时刻|电量"（跨程序重启/关机持久）
    public bool AutoCheckUpdate = true;                    // 启动后自动检查 GitHub 新版本
    public string LastUpdateCheck = "";                    // 最近一次检查时间（ISO）
    public string SkippedUpdate = "";                      // 用户点了「稍后」的版本，自动检查不再重复提示

    public RenderOpts ToOpts(int phase) {
        return new RenderOpts {
            Style = IconStyle, ShowNumber = ShowNumber, FontScale = FontScale,
            Thick = RingThickness,
            ShowPercent = ShowPercent, AnimPhase = phase,
            NumberOutline = NumberOutline, NumberColorHex = NumberColorHex,
        };
    }
}

static class Util {
    // Icon 实例不能跨控件共享：窗体/托盘 Dispose 时会连带释放底层句柄，
    // 其他控件再用就会 ObjectDisposedException。每次都克隆一份新的。
    public static Icon AppIcon() {
        // 1) exe 自带图标（编译参数 /win32icon:mouse.ico）。这条路径能正确取到
        //    32×32 非 PNG 帧，窗口标题栏、任务栏按钮、Alt-Tab 都显示鼠标图标。
        try {
            using (var src = Icon.ExtractAssociatedIcon(Application.ExecutablePath)) {
                if (src != null) return (Icon)src.Clone();
            }
        } catch { }
        // 2) 嵌入资源兜底（编译参数 /resource:mouse.ico,mouse.ico）。
        //    指定 32×32，避免 256×256 PNG 压缩帧在 Icon.ToBitmap() 下解出花屏。
        try {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("mouse.ico")) {
                if (s != null) using (var ico = new Icon(s, 32, 32)) return (Icon)ico.Clone();
            }
        } catch { }
        // 3) exe 同目录 mouse.ico
        try {
            string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mouse.ico");
            if (File.Exists(p)) using (var ico = new Icon(p, 32, 32)) return (Icon)ico.Clone();
        } catch { }
        return (Icon)SystemIcons.Application.Clone();
    }
}

static class NativeHid {
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, int Flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr data, ref Guid guid, int idx, ref IFDATA ifData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref IFDATA ifData, IntPtr detail, int size, ref int required, IntPtr data);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr prep);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_FreePreparsedData(IntPtr prep);
    [DllImport("hid.dll", SetLastError = true)]
    static extern int HidP_GetCaps(IntPtr prep, ref CAPS caps);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetFeature(IntPtr h, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetFeature(IntPtr h, byte[] buf, uint len);
    [DllImport("hid.dll")]
    static extern void HidD_GetHidGuid(out Guid g);

    [StructLayout(LayoutKind.Sequential)]
    struct IFDATA { public int cbSize; public Guid guid; public int flags; public IntPtr reserved; }
    [StructLayout(LayoutKind.Sequential)]
    struct CAPS {
        public ushort Usage, UsagePage, InLen, OutLen, FeatLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] R;
        public ushort N1, N2, N3, N4, N5, N6, N7, N8, N9, N10;
    }

    const int HIDP_STATUS_SUCCESS = 0x00110000;

    static byte[] BuildRequest(byte cmd) {
        var r = new byte[91];
        r[0] = 0; r[2] = 0x1F; r[6] = 0x02; r[7] = 0x07; r[8] = cmd;
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= r[i];
        r[89] = crc;
        return r;
    }

    static int? Query(IntPtr h, byte cmd) {
        var req = BuildRequest(cmd);
        int[] waits = { 80, 150, 250 };
        foreach (int w in waits) {
            if (!HidD_SetFeature(h, req, 91)) return null;
            Thread.Sleep(w);
            var resp = new byte[91];
            if (!HidD_GetFeature(h, resp, 91)) return null;
            if (resp[1] == 0x02) return resp[10];
            if (resp[1] != 0x01) return null;
        }
        return null;
    }

    // 连接类型：鼠标同一时刻只有一种链路在SystemService里枚举出来——
    // 无线接收器模式 PID=0x00C1，插线切有线模式 PID=0x00C0（USB 重新枚举）
    public enum Link { None, Wireless, Wired }

    public class BatteryResult {
        public bool Online; public int Percent; public bool Charging;
        public Link Link = Link.None;
        public int Pid;                       // 命中的产品 ID（调试用）
        public string RawBattery = "";        // 电量响应十六进制（调试用）
        public string RawCharging = "";       // 充电响应十六进制（调试用）
    }

    // 调试日志回调：把枚举/查询细节写进主日志，插线问题远程可判断
    public static Action<string> DebugLog;

    static int? Query(IntPtr h, byte cmd, out string raw) {
        raw = "";
        var req = BuildRequest(cmd);
        int[] waits = { 80, 150, 250 };
        foreach (int w in waits) {
            if (!HidD_SetFeature(h, req, 91)) return null;
            Thread.Sleep(w);
            var resp = new byte[91];
            if (!HidD_GetFeature(h, resp, 91)) return null;
            raw = BitConverter.ToString(resp, 0, 12).Replace("-", " ");
            // 响应必须与请求匹配（OpenRazer razer_send_payload 同款校验）：
            // 接收器会异步推 0x1C/0x93 状态报告（插拔瞬间及每 ~2.5 分钟一次），
            // status 同样是 0x02，不校验会把报告里的无关字节当电量（曾致 5% 误读+连环误弹）
            if (resp[2] != 0x1F || resp[7] != 0x07 || resp[8] != cmd) {
                if (DebugLog != null) DebugLog(string.Format("丢弃不匹配响应(期望 1F/07/{0:X2}): {1}", cmd, raw));
                continue;
            }
            if (resp[1] == 0x02) return resp[10];
            if (resp[1] != 0x01) return null;
        }
        return null;
    }

    // 通用特征报告查询（诊断/扩展用）：class/cmd/dataSize 可变，返回完整 91 字节响应
    // 用于 DPI（04/85）、轮询率（00/C0）等扩展命令；失败返回 null
    public static byte[] QueryRaw(byte cmdClass, byte cmd, byte dataSize, byte[] args, byte tid) {
        foreach (var kv in FindAllViperHandles()) {
            IntPtr h = kv.Value;
            try {
                var req = new byte[91];
                req[0] = 0; req[2] = tid; req[6] = dataSize; req[7] = cmdClass; req[8] = cmd;
                if (args != null) Array.Copy(args, 0, req, 9, Math.Min(args.Length, 80));
                byte crc = 0;
                for (int i = 3; i <= 88; i++) crc ^= req[i];
                req[89] = crc;
                int[] waits = { 80, 150, 250 };
                foreach (int w in waits) {
                    if (!HidD_SetFeature(h, req, 91)) break;
                    Thread.Sleep(w);
                    var resp = new byte[91];
                    if (!HidD_GetFeature(h, resp, 91)) break;
                    if (resp[2] != tid || resp[7] != cmdClass || resp[8] != cmd) continue;
                    if (resp[1] == 0x02) return resp;
                    if (resp[1] == 0x01) continue;
                    return resp;
                }
            } catch { } finally { CloseHandle(h); }
        }
        return null;
    }

    // ---- 鼠标 DPI / 轮询率（Viper V3 Pro 实测协议） ----
    // DPI 当前值：04/85（args[1..4] X/Y 高低位）
    // DPI 档位：  04/86 读、04/06 写；响应 args[1]=当前档位、args[2]=档位数，
    //             每档 7 字节 [档号, Xh, Xl, Yh, Yl, 0, 0]
    // 轮询率：    00/C0 读、00/40 写；args[1] 0x01=8000 0x02=4000 0x04=2000
    //              0x08=1000 0x10=500 0x40=125；HyperPolling 需连发 0x00/0x01 两次
    public class MouseInfo {
        public bool Online;
        public int DpiX = -1, DpiY = -1;
        public int PollHz = -1;
        public int ActiveStage = 1;
        public int StageCount;
        public int[] StageX = new int[0];
        public int[] StageY = new int[0];
    }

    public static int PollCodeToHz(int code) {
        switch (code) {
            case 0x01: return 8000;
            case 0x02: return 4000;
            case 0x04: return 2000;
            case 0x08: return 1000;
            case 0x10: return 500;
            case 0x40: return 125;
            default: return -1;
        }
    }
    public static int HzToPollCode(int hz) {
        switch (hz) {
            case 8000: return 0x01;
            case 4000: return 0x02;
            case 2000: return 0x04;
            case 1000: return 0x08;
            case 500: return 0x10;
            case 125: return 0x40;
            default: return -1;
        }
    }

    public static MouseInfo ReadDpiPoll() {
        var m = new MouseInfo();
        var d = QueryRaw(0x04, 0x85, 7, new byte[7], 0x1F);
        if (d != null && d[1] == 0x02) {
            m.Online = true;
            m.DpiX = (d[10] << 8) | d[11];
            m.DpiY = (d[12] << 8) | d[13];
        }
        var p = QueryRaw(0x00, 0xC0, 2, new byte[] { 0, 0 }, 0x1F);
        if (p != null && p[1] == 0x02) {
            m.Online = true;
            m.PollHz = PollCodeToHz(p[10]);
        }
        return m;
    }

    public static MouseInfo ReadDpiStages() {
        var m = new MouseInfo();
        var r = QueryRaw(0x04, 0x86, 1, new byte[1], 0x1F);
        if (r == null || r[1] != 0x02) return m;
        m.Online = true;
        m.ActiveStage = r[10];
        m.StageCount = Math.Max(0, Math.Min(5, (int)r[11]));
        m.StageX = new int[m.StageCount];
        m.StageY = new int[m.StageCount];
        for (int i = 0; i < m.StageCount; i++) {
            int b = 12 + i * 7;   // args[3] = resp[12]
            m.StageX[i] = (r[b + 1] << 8) | r[b + 2];
            m.StageY[i] = (r[b + 3] << 8) | r[b + 4];
        }
        return m;
    }

    public static bool SetDpiStages(int activeStage, int[] xs, int[] ys) {
        int n = Math.Min(5, Math.Min(xs.Length, ys.Length));
        if (n <= 0) return false;
        var args = new byte[3 + n * 7];
        args[0] = 0;
        args[1] = (byte)Math.Max(1, Math.Min(n, activeStage));
        args[2] = (byte)n;
        for (int i = 0; i < n; i++) {
            int x = Math.Max(100, Math.Min(35000, xs[i]));
            int y = Math.Max(100, Math.Min(35000, ys[i]));
            int b = 3 + i * 7;
            args[b] = (byte)(i + 1);
            args[b + 1] = (byte)(x >> 8); args[b + 2] = (byte)x;
            args[b + 3] = (byte)(y >> 8); args[b + 4] = (byte)y;
        }
        var r = QueryRaw(0x04, 0x06, (byte)args.Length, args, 0x1F);
        return r != null && r[1] == 0x02;
    }

    public static bool SetDpi(int x, int y) {
        x = Math.Max(100, Math.Min(35000, x));
        y = Math.Max(100, Math.Min(35000, y));
        var args = new byte[] { 0, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, 0, 0 };
        var r = QueryRaw(0x04, 0x05, 7, args, 0x1F);
        return r != null && r[1] == 0x02;
    }

    public static bool SetPollRate(int hz) {
        int code = HzToPollCode(hz);
        if (code < 0) return false;
        var a = QueryRaw(0x00, 0x40, 2, new byte[] { 0, (byte)code }, 0x1F);
        var b = QueryRaw(0x00, 0x40, 2, new byte[] { 1, (byte)code }, 0x1F);
        return (a != null && a[1] == 0x02) || (b != null && b[1] == 0x02);
    }

    // ---- 电源：闲置睡眠时间 / 低电量阈值 ----
    public class PowerInfo {
        public bool Online;
        public int IdleSeconds = -1;
        public int LowRaw = -1;
        public int LowPercent = -1;
    }
    static int Round5(int v) { return ((v + 2) / 5) * 5; }

    public static PowerInfo ReadPower() {
        var p = new PowerInfo();
        var idle = QueryRaw(0x07, 0x83, 2, new byte[2], 0x1F);
        if (idle != null && idle[1] == 0x02) {
            p.Online = true;
            p.IdleSeconds = (idle[9] << 8) | idle[10];
        }
        var low = QueryRaw(0x07, 0x81, 1, new byte[1], 0x1F);
        if (low != null && low[1] == 0x02) {
            p.Online = true;
            p.LowRaw = low[9];
            p.LowPercent = Math.Max(5, Math.Min(100, Round5(p.LowRaw * 100 / 255)));
        }
        return p;
    }

    public static bool SetIdleSeconds(int seconds) {
        seconds = Math.Max(60, Math.Min(900, seconds));
        var r = QueryRaw(0x07, 0x03, 2, new byte[] { (byte)(seconds >> 8), (byte)seconds }, 0x1F);
        return r != null && r[1] == 0x02;
    }

    public static bool SetLowBatteryPercent(int pct) {
        pct = Math.Max(5, Math.Min(100, pct));
        int raw = Round5(pct * 255 / 100);
        raw = Math.Max(0x0C, Math.Min(0xFF, raw));
        var r = QueryRaw(0x07, 0x01, 1, new byte[] { (byte)raw }, 0x1F);
        return r != null && r[1] == 0x02;
    }

    // ---- 校准：非对称中止（抬升/着陆距离） ----
    // Viper V3 Pro 实测：0B/85 返回 00 00 01 12 0E => 抬升 0x12+1=19、着陆 0x0E+1=15
    // 开启写入：0B/03 [00,04,01] -> 0B/0B [00,04,04,00] -> 0B/05 [00,04,lift-1,land-1]
    public class LodInfo {
        public bool Online;
        public bool Async;
        public int Lift = 19;
        public int Land = 15;
        public int FixedLod = 1;
    }

    public static LodInfo ReadLod() {
        var l = new LodInfo();
        var r = QueryRaw(0x0B, 0x85, 1, new byte[1], 0x1F);
        if (r == null || r[1] != 0x02) return l;
        l.Online = true;
        int mode = r[11];
        int a = r[12], b = r[13];
        if (mode == 0x04 || a > 2) {
            l.Async = true;
            l.Lift = Math.Max(2, Math.Min(26, a + 1));
            l.Land = Math.Max(1, Math.Min(25, b + 1));
        } else {
            l.Async = false;
            l.FixedLod = Math.Max(0, Math.Min(2, a));
        }
        return l;
    }

    public static bool SetAsyncLod(int lift, int land) {
        lift = Math.Max(2, Math.Min(26, lift));
        land = Math.Max(1, Math.Min(25, land));
        var a = QueryRaw(0x0B, 0x03, 3, new byte[] { 0, 4, 1 }, 0x1F);
        var b = QueryRaw(0x0B, 0x0B, 4, new byte[] { 0, 4, 4, 0 }, 0x1F);
        var c = QueryRaw(0x0B, 0x05, 4, new byte[] { 0, 4, (byte)(lift - 1), (byte)(land - 1) }, 0x1F);
        return a != null && b != null && c != null &&
               a[1] == 0x02 && b[1] == 0x02 && c[1] == 0x02;
    }

    public static bool SetFixedLod(int lod) {
        lod = Math.Max(0, Math.Min(2, lod));
        var a = QueryRaw(0x0B, 0x03, 3, new byte[] { 0, 4, 0x40 }, 0x1F);
        var b = QueryRaw(0x0B, 0x0B, 4, new byte[] { 0, 4, 1, (byte)lod }, 0x1F);
        return a != null && b != null && a[1] == 0x02 && b[1] == 0x02;
    }

    public static BatteryResult Read() {
        var res = new BatteryResult();
        // 插线时接收器(00C1)与有线模式(00C0)同时枚举且都存在，但鼠标本体只在
        // 其中一条链路上响应。两个都查，谁返回有效电量用谁；00C1 优先（无线更常用）
        foreach (var kv in FindAllViperHandles()) {
            var r = TryReadOne(kv.Key, kv.Value);
            if (r != null) return r;
        }
        // 全部无响应但设备枚举还在（插线切换中）：报在线-未知，避免"插线却显示未连接+误弹低电量"
        if (ViperPresent()) {
            if (DebugLog != null) DebugLog("全部链路无响应但设备存在，标记在线-未知");
            res.Online = true; res.Percent = -1;
        }
        return res;
    }

    // 是否枚举到任一毒蝰 VID/PID（不做开句柄查询，只看路径存在）
    static bool ViperPresent() {
        Guid hidGuid; HidD_GetHidGuid(out hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
        var ifData = new IFDATA(); ifData.cbSize = Marshal.SizeOf(ifData);
        bool present = false;
        for (int i = 0; !present; i++) {
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref ifData)) break;
            int req = 0;
            SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, ref req, IntPtr.Zero);
            IntPtr detail = Marshal.AllocHGlobal(req);
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            string path = null;
            if (SetupDiGetDeviceInterfaceDetail(set, ref ifData, detail, req, ref req, IntPtr.Zero))
                path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
            Marshal.FreeHGlobal(detail);
            string lower = (path ?? "").ToLower();
            if (lower.Contains("vid_1532") && (lower.Contains("pid_00c1") || lower.Contains("pid_00c0")))
                present = true;
        }
        SetupDiDestroyDeviceInfoList(set);
        return present;
    }

    static BatteryResult TryReadOne(ushort pid, IntPtr h) {
        var res = new BatteryResult();
        try {
            res.Pid = pid;
            res.Link = pid == 0x00C0 ? Link.Wired : Link.Wireless;
            string rawB;
            int? batt = Query(h, 0x80, out rawB);
            res.RawBattery = rawB;
            if (DebugLog != null) DebugLog(string.Format("PID={0:X4} link={1} 电量响应: {2} => raw={3}", pid, res.Link, rawB, batt.HasValue ? batt.Value.ToString() : "null"));
            if (batt.HasValue && batt.Value > 0) {
                res.Online = true;
                // 四舍五入 + 钳 100：整数截断会让 96.08% 永远显示 96（差 1 度永远到不了充满判定）
                res.Percent = Math.Min(100, (batt.Value * 100 + 127) / 255);
                string rawC;
                int? chg = Query(h, 0x84, out rawC);
                res.RawCharging = rawC;
                if (DebugLog != null) DebugLog(string.Format("充电响应: {0} => raw={1}", rawC, chg.HasValue ? chg.Value.ToString() : "null"));
                res.Charging = (chg == 1);
                // 有线插线即充电：官方 Synapse 在有线模式下直接显示充电中，
                // 部分固件此时 0x84 返回 0/无响应，按物理事实判定
                if (res.Link == Link.Wired && !res.Charging) {
                    res.Charging = true;
                    if (DebugLog != null) DebugLog("有线模式：0x84 未报充电，按插线即充电处理");
                }
                return res;
            }
            if (batt == 0 && res.Link == Link.Wired) {
                // 有线插线瞬间固件切换中，电量暂报 0：视为在线但未知，等下一轮
                res.Online = true;
                res.Percent = -1;
                if (DebugLog != null) DebugLog("有线模式电量暂为 0（切换中），标记在线-未知");
                return res;
            }
        } finally { CloseHandle(h); }
        return null;   // 此链路无有效数据，让上层试下一个
    }

    // 返回所有可打开的毒蝰控制句柄（00C1 在前，00C0 在后）
    static List<KeyValuePair<ushort, IntPtr>> FindAllViperHandles() {
        var result = new List<KeyValuePair<ushort, IntPtr>>();
        Guid hidGuid; HidD_GetHidGuid(out hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
        var ifData = new IFDATA(); ifData.cbSize = Marshal.SizeOf(ifData);
        var wireless = new List<KeyValuePair<string, ushort>>();
        var wired = new List<KeyValuePair<string, ushort>>();
        for (int i = 0; ; i++) {
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref ifData)) break;
            int req = 0;
            SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, ref req, IntPtr.Zero);
            IntPtr detail = Marshal.AllocHGlobal(req);
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            string path = null;
            if (SetupDiGetDeviceInterfaceDetail(set, ref ifData, detail, req, ref req, IntPtr.Zero))
                path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
            Marshal.FreeHGlobal(detail);
            if (path == null) continue;
            string lower = path.ToLower();
            if (lower.Contains("rzvirtual") || !lower.Contains("vid_1532")) continue;
            if (lower.Contains("pid_00c1")) wireless.Add(new KeyValuePair<string, ushort>(path, 0x00C1));
            else if (lower.Contains("pid_00c0")) wired.Add(new KeyValuePair<string, ushort>(path, 0x00C0));
        }
        SetupDiDestroyDeviceInfoList(set);
        if (DebugLog != null && wireless.Count + wired.Count > 0)
            DebugLog(string.Format("枚举候选: 无线x{0} 有线x{1}", wireless.Count, wired.Count));
        // 同类 PID 只取第一个可用句柄（多接口同报 candidate 是同一设备的多个 collection，
        // 只有鼠标 collection 能开+特征 91；多余候选开句柄无谓），无线优先
        bool gotWireless = false, gotWired = false;
        foreach (var kv in wireless.Concat(wired)) {
            if (kv.Value == 0x00C1 && gotWireless) continue;
            if (kv.Value == 0x00C0 && gotWired) continue;
            // access=0 绕过系统对鼠标 collection 的独占锁定
            IntPtr h = CreateFile(kv.Key, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == (IntPtr)(-1)) continue;
            var caps = new CAPS(); IntPtr prep;
            if (HidD_GetPreparsedData(h, out prep)) {
                int st = HidP_GetCaps(prep, ref caps);
                HidD_FreePreparsedData(prep);
                if (st == HIDP_STATUS_SUCCESS && caps.UsagePage == 0x01 && caps.Usage == 0x02 && caps.FeatLen >= 91) {
                    result.Add(new KeyValuePair<ushort, IntPtr>(kv.Value, h));
                    if (kv.Value == 0x00C1) gotWireless = true; else gotWired = true;
                    continue;
                }
            }
            CloseHandle(h);
        }
        return result;
    }
}

static class IconRenderer {
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int n);

    // 托盘图标实际像素：SM_CXSMICON（100%=16、150%=24、200%=32）。
    // 电池样式按它 1:1 渲染，系统不再缩放，描边/正极帽/数字都更清晰；宽图超出该正方形会被系统等比缩小
    public static int TrayIconPx {
        get {
            try {
                int v = GetSystemMetrics(49);   // SM_CXSMICON
                return v >= 16 && v <= 64 ? v : 24;
            } catch { return 24; }
        }
    }

    // 逻辑画布 32px：GDI 托盘图标实际支持的最大精细尺寸。
    // 实际按 SSA 倍超采样绘制再高质量降采样（见 Render），边缘圆润无锯齿
    public const int Canvas = 32;
    const int SSA = 8;   // 8 倍超采样（256px 中间画布→32px）：边缘 alpha 过渡级数比 4 倍翻倍，
                         // 圆环外/内缘呈连续线性渐变（1.4.3 复验仍见 2-3 级跳变中夹孤立档，此为最终光滑手段）

    // 全部样式都画在一个 32×32 逻辑画布里（托盘图标是正方形，宽图会被系统等比缩小）。
    // 电池的宽高比在画布内部解决，不再用 64 宽双格画布
    public static int CanvasW(string style) { return 32; }
    public static int CanvasH(string style) { return 32; }

    // DPI 预案：进程 DPI 感知后按系统缩放选托盘画布倍率——
    // 100%→1(32px)、125-175%→1.5(48px)、200%+→2(64px)。SSA 超采样固定再加 4 倍
    public static float DpiScale = 1f;
    public static void InitDpi() {
        try {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) {
                float dpi = g.DpiX;   // DPI aware 进程拿到的是真实值（96=100%）
                if (dpi >= 192) DpiScale = 2f;
                else if (dpi >= 120) DpiScale = 1.5f;
                else DpiScale = 1f;
            }
        } catch { DpiScale = 1f; }
    }

    public static Color ColorFor(int pct, List<ColorRange> ranges) {
        foreach (var r in ranges)
            if (pct >= r.Min && pct <= r.Max) return r.ToColor();
        return Color.FromArgb(244, 67, 54);
    }

    static Color ChargeColor { get { return Color.FromArgb(255, 187, 0); } }

    // 鼠鼠造型轮廓：大圆角双肩（左右键）+ 掌区圆底。rT 用 0.48w 使顶部呈两个圆肩
    static GraphicsPath MouseBody(RectangleF r) {
        float rT = r.Width * 0.48f, rB = r.Width * 0.17f;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, 2 * rT, 2 * rT, 180, 90);
        p.AddArc(r.Right - 2 * rT, r.Y, 2 * rT, 2 * rT, 270, 90);
        p.AddLine(r.Right, r.Y + rT, r.Right, r.Bottom - rB);
        p.AddArc(r.Right - 2 * rB, r.Bottom - 2 * rB, 2 * rB, 2 * rB, 0, 90);
        p.AddArc(r.X, r.Bottom - 2 * rB, 2 * rB, 2 * rB, 90, 90);
        p.AddLine(r.X, r.Bottom - rB, r.X, r.Y + rT);
        p.CloseFigure();
        return p;
    }

    // 鼠标细节：上半部左右键分割线 + 滚轮胶囊（画在填充之上）
    static void DrawMouseDetails(Graphics g, RectangleF body) {
        float cx = body.X + body.Width / 2f;
        float splitTop = body.Y + body.Height * 0.06f;
        float splitBottom = body.Y + body.Height * 0.40f;
        using (var pen = new Pen(Color.FromArgb(15, 15, 18), Math.Max(1.0f, body.Width * 0.05f)))
            g.DrawLine(pen, cx, splitTop, cx, splitBottom);
        // 滚轮：中央小胶囊
        float ww = body.Width * 0.115f, wh = body.Height * 0.155f;
        var wr = new RectangleF(cx - ww / 2, body.Y + body.Height * 0.10f, ww, wh);
        using (var p = RoundedRect(wr, ww * 0.45f)) {
            using (var b = new SolidBrush(Color.FromArgb(230, 230, 235)))
                g.FillPath(b, p);
            using (var pen = new Pen(Color.FromArgb(15, 15, 18), 1.0f))
                g.DrawPath(pen, p);
        }
    }

    public static bool IsBatteryStyle(string style) {
        return style == "batH" || style == "batV";
    }

    // Win11 笔记本电池布局：outer=主体外沿（含描边）、cap=正极帽、stroke=描边宽。
    // 帽与主体之间留出真实缝隙，24px 托盘格里也能一眼看到正极；竖版帽在顶部
    static void BatteryLayout(float S, bool vertical, out RectangleF outer, out RectangleF cap, out float stroke) {
        stroke = Math.Max(1.2f, S * 0.070f);
        if (vertical) {
            float capW = S * 0.36f, capH = S * 0.095f, gap = S * 0.05f;
            float bodyW = S * 0.72f;
            float top = S * 0.02f;
            cap = new RectangleF((S - capW) / 2f, top, capW, capH);
            float bodyTop = top + capH + gap;
            outer = new RectangleF((S - bodyW) / 2f, bodyTop, bodyW, S - bodyTop - S * 0.02f);
        } else {
            float capW = S * 0.11f, capH = S * 0.30f, gap = S * 0.05f;
            float left = S * 0.015f, totalW = S * 0.97f;
            float bodyW = totalW - capW - gap;
            float bodyH = Math.Min(S * 0.74f, bodyW / 1.1f);
            outer = new RectangleF(left, (S - bodyH) / 2f, bodyW, bodyH);
            cap = new RectangleF(outer.Right + gap, outer.Y + (bodyH - capH) / 2f, capW, capH);
        }
    }

    static RectangleF BatteryNumberZone(float S, string style) {
        RectangleF outer, cap; float stroke;
        BatteryLayout(S, style == "batV", out outer, out cap, out stroke);
        // 数字区用整格高度量字号（墨迹仍落在电池体内），数字更大更清楚；
        // 宽度收进描边内侧并留 0.8px 呼吸位，数字不贴框；垂直中心对齐电池主体中心
        float cy = outer.Y + outer.Height / 2f;
        return new RectangleF(outer.X + stroke + 0.8f, cy - S / 2f,
                              outer.Width - stroke * 2f - 1.6f, S);
    }

    // Win11 风格电池主体：主题色描边 + 独立正极帽 + 阈值色填充（充电时逐段扫描）
    static void DrawBatteryGlyph(Graphics g, float S, bool vertical, int fillPct, int sweepPct,
                                 bool charging, Color fillCol, List<ColorRange> ranges, Color strokeCol) {
        RectangleF outer, cap; float stroke;
        BatteryLayout(S, vertical, out outer, out cap, out stroke);
        float rad = Math.Min(outer.Width, outer.Height) * 0.22f;
        var pathRect = RectangleF.Inflate(outer, -stroke / 2f, -stroke / 2f);
        using (var bodyPath = RoundedRect(pathRect, rad)) {
            var interior = RectangleF.Inflate(outer, -(stroke + 0.9f), -(stroke + 0.9f));
            int tgt = charging ? Math.Max(fillPct, sweepPct) : fillPct;
            if (tgt > 0) {
                g.SetClip(bodyPath);
                if (charging) FillByThreshold(g, interior, 0, tgt, ranges, !vertical);
                else if (vertical) {
                    float fh = interior.Height * tgt / 100f;
                    using (var b = new SolidBrush(fillCol))
                        g.FillRectangle(b, interior.X, interior.Bottom - fh, interior.Width, fh);
                } else {
                    float fw = interior.Width * tgt / 100f;
                    using (var b = new SolidBrush(fillCol))
                        g.FillRectangle(b, interior.X, interior.Y, fw, interior.Height);
                }
                g.ResetClip();
            }
            using (var pen = new Pen(strokeCol, stroke))
                g.DrawPath(pen, bodyPath);
        }
        using (var b = new SolidBrush(strokeCol))
        using (var p = RoundedRect(cap, Math.Min(cap.Width, cap.Height) * 0.32f))
            g.FillPath(b, p);
    }

    static GraphicsPath BoltPath(RectangleF r) {
        float[,] pts = {
            {0.62f,0.02f},{0.14f,0.58f},{0.40f,0.58f},{0.28f,0.98f},
            {0.82f,0.40f},{0.55f,0.40f}
        };
        var p = new GraphicsPath();
        p.StartFigure();
        var last = new PointF(r.X + r.Width * pts[0, 0], r.Y + r.Height * pts[0, 1]);
        p.AddLine(last.X, last.Y, last.X, last.Y);
        for (int i = 1; i < pts.GetLength(0); i++) {
            last = new PointF(r.X + r.Width * pts[i, 0], r.Y + r.Height * pts[i, 1]);
            p.AddLine(p.GetLastPoint().X, p.GetLastPoint().Y, last.X, last.Y);
        }
        p.CloseFigure();
        return p;
    }

    // 数字：手动漫量文字尺寸 + 严格居中（水平+垂直），
    // 8 方向偏移描边（outline 0-4 级可调）+ 颜色可调。
    // 不用 GraphicsPath.AddString（坏字号坑）；不用 DrawString 矩形对齐（垂直偏上不可控）
    static void DrawNumber(Graphics g, RectangleF area, string s, Color c, int fontScale, int outlineLv, Color numCol, bool leftAlign = false, float outlinePx = 0f) {
        // numCol 有效（非 Empty）用自定义色，否则用 c（plain 样式=阈值色，其余=白）
        Color fill = (numCol.A == 0 || numCol == Color.Empty) ? c : numCol;
        float em = area.Height * (fontScale / 100f) * 0.74f;
        if (em < 5) em = 5;
        var fmt = new StringFormat(StringFormat.GenericTypographic) {
            Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near
        };
        SizeF sz;
        using (var probe = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel)) {
            sz = g.MeasureString(s, probe, new PointF(0, 0), fmt);
            float maxW = area.Width * 0.98f;
            if (sz.Width > maxW && sz.Width > 0) {
                em *= maxW / sz.Width;
                using (var f2 = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel))
                    sz = g.MeasureString(s, f2, new PointF(0, 0), fmt);
            }
        }
        using (var font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel)) {
            // 墨迹中心居中：Typographic 行框含上伸/下伸留白，数字墨迹在行框内偏上——
            // 用 em 顶隙差（Ascent-墨迹顶）把行框中心换成字形墨迹中心，数字视觉中心=区域中心
            float ascent = font.FontFamily.GetCellAscent(FontStyle.Bold) * em /
                           font.FontFamily.GetEmHeight(FontStyle.Bold);
            float descent = font.FontFamily.GetCellDescent(FontStyle.Bold) * em /
                            font.FontFamily.GetEmHeight(FontStyle.Bold);
            // 数字实际墨迹高 ≈ ascent 的 70%（数字顶部平齐 cap 高度，无下伸部）
            float inkH = ascent * 0.72f;
            float inkTopInLine = ascent - inkH;     // 墨迹顶在行框内的偏移
            float lineH = sz.Height;                 // MeasureString 的行高（Typographic≈ascent+descent）
            float ty = area.Y + (area.Height - inkH) / 2f - inkTopInLine;   // 墨迹中心对齐区域中心
            float tx = leftAlign ? area.X : area.X + (area.Width - sz.Width) / 2f;
            float o = outlinePx > 0f ? outlinePx : (outlineLv <= 0 ? 0f : Math.Max(0.8f, em * 0.07f * outlineLv / 2f));
            if (o > 0) {
                // 描边用半透明深灰（纯黑在浅色任务栏上一圈死黑太重）
                using (var dark = new SolidBrush(Color.FromArgb(150, 10, 10, 12))) {
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++) {
                            if (dx == 0 && dy == 0) continue;
                            g.DrawString(s, font, dark, tx + dx * o, ty + dy * o, fmt);
                        }
                }
            }
            using (var fillB = new SolidBrush(fill))
                g.DrawString(s, font, fillB, tx, ty, fmt);
        }
        fmt.Dispose();
    }

    static void DrawBolt(Graphics g, RectangleF badge) { DrawBolt(g, badge, 255); }

    static void DrawBolt(Graphics g, RectangleF badge, int alpha) {
        // 闪电徽标（带深色描边，任何底色可读），描边随徽章尺寸缩放
        using (var path = BoltPath(badge)) {
            using (var pen = new Pen(Color.FromArgb(8, 8, 8), Math.Max(1.0f, badge.Width * 0.09f)) { LineJoin = LineJoin.Round }) {
                using (var b = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), 255, 215, 0)))
                    g.FillPath(b, path);
                g.DrawPath(pen, path);
            }
        }
    }

    static RectangleF NumberZone(int size, string style, float tPx) {
        // 数字可绘区域：大区域（数字可占满环内空间，字号 FontScale 用户自调 60-160%）；
        // DrawNumber 用字形墨迹中心对齐区域中心 → 数字视觉中心 = 图形几何中心
        if (style == "mouse") return new RectangleF(size * 0.16f, size * 0.36f, size * 0.68f, size * 0.56f);
        if (style == "gauge") return new RectangleF(0, size * 0.52f, size, size * 0.46f);
        return new RectangleF(0, 0, size, size);
    }

    static GraphicsPath HexPath(RectangleF r) {
        // 横向六边形：上顶点、右上、右下、下顶点、左下、左上
        var p = new GraphicsPath();
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        p.AddLine(x + w * 0.5f, y, x + w * 0.96f, y + h * 0.30f);
        p.AddLine(x + w * 0.96f, y + h * 0.70f, x + w * 0.5f, y + h);
        p.AddLine(x + w * 0.04f, y + h * 0.70f, x + w * 0.04f, y + h * 0.30f);
        p.CloseFigure();
        return p;
    }

    static GraphicsPath DropPath(RectangleF r) {
        // 水滴：顶部尖 + 下方大圆；底部圆弧经 AddArc 逆时针（负 sweep）
        var p = new GraphicsPath();
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        float cx = x + w * 0.5f, tipY = y + h * 0.04f;
        float circleR = w * 0.37f, circleCy = y + h * 0.62f;
        p.StartFigure();
        p.AddLine(cx, tipY, cx, tipY);
        // 左曲线：尖 -> 圆左点
        p.AddBezier(cx, tipY, x + w * 0.34f, y + h * 0.22f, cx - circleR, y + h * 0.44f, cx - circleR, circleCy);
        // 底部半圆：左点 -> 底 -> 右点（GDI 角度: 180=左，90=下，0=右；负 sweep=逆时针）
        p.AddArc(cx - circleR, circleCy - circleR, 2 * circleR, 2 * circleR, 180, -180);
        // 右曲线：圆右点 -> 尖
        p.AddBezier(cx + circleR, circleCy, cx + circleR, y + h * 0.44f, x + w * 0.66f, y + h * 0.22f, cx, tipY);
        p.CloseFigure();
        return p;
    }

    public static Bitmap Render(int size, RenderOpts o, List<ColorRange> ranges) {
        return Render(size, size, o, ranges);
    }

    // w x h 画布：逻辑 32 系（方形，按 DpiScale 整体放大传入）。
    // 高精细：SSA 倍超采样绘制 → 高质量双三次降采样，圆/弧/圆角边缘光滑无锯齿；
    // 所有"像素级"参数（描边/粗细/描边厚度）按 画布高/32 归一，画布放大时同步放大
    public static Bitmap Render(int w, int h, RenderOpts o, List<ColorRange> ranges) {
        var hi = new Bitmap(w * SSA, h * SSA, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(hi)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.ScaleTransform(SSA, SSA);   // 之后全部按逻辑 w/h 坐标绘制
            float S = h;   // 以高度为基准度量（宽画布时图形画在左格 0..S）
            float unit = h / 32f;         // 逻辑像素→实际画布像素（DPI 放大后 >1）
            Color col = ColorFor(o.Pct < 0 ? 0 : o.Pct, ranges);
            Color fillCol = o.Charging ? ChargeColor : col;
            float tPx = (2f + o.Thick) * unit;

            // 充电填充动画：0..100% 循环扫描填充，不同样式用各自方式呈现(见 DrawStyle)
            int fillPct = o.Pct;
            int sweepPct = -1;
            if (o.Charging && o.Pct >= 0) {
                sweepPct = (int)Math.Min(100, o.Pct + (100 - o.Pct) * ((o.AnimPhase + 0.5f) / 8f));
            }

            DrawStyle(g, S, fillPct, sweepPct, o.Charging, o.Style, fillCol, ranges, tPx);

            if (o.ShowNumber) {
                string num = o.Pct < 0 ? "?" : o.Pct.ToString() + (o.ShowPercent ? "%" : "");
                bool autoNum = ColorExt.IsAuto(o.NumberColorHex);
                bool battery = IsBatteryStyle(o.Style);
                // 电池：数字画在电池体内（BatteryNumberZone）；其他样式保持原有内部布局
                var zone = battery ? BatteryNumberZone(S, o.Style)
                                   : NumberZone(h, o.Style, (2f + o.Thick) * unit);
                // 自动配色只作用于电池（电池描边已跟随任务栏黑/白）；
                // 圆环/鼠标等保持原来的白字+描边，不因主题变化而变黑
                Color numCol = battery
                    ? (autoNum ? Theme.Foreground : ColorExt.ParseSafe(o.NumberColorHex))
                    : (autoNum ? Color.Empty : ColorExt.ParseSafe(o.NumberColorHex));
                Color fallback = o.Style == "plain" ? fillCol : Color.White;
                // 浅色任务栏：黑字直接压在绿色填充上也清楚，不用描边；
                // 深色任务栏：白字在绿色填充上对比不足，用 0.6px 细描边托一下（比默认 0.8px 更保字形）
                int outline = (battery && autoNum) ? (Theme.LightTaskbar ? 0 : 1) : o.NumberOutline;
                float outlinePx = (battery && autoNum && !Theme.LightTaskbar) ? 0.6f : 0f;
                DrawNumber(g, zone, num, fallback, o.FontScale, outline, numCol, false, outlinePx);
            }

            // 充电闪电：非电池样式画在图形右上角；电池用填充扫描表达充电，不再叠闪电
            // 相位 0..4 从 255 渐隐到 115，5..7 熄灭 → 闪动节奏
            if (o.Charging && !IsBatteryStyle(o.Style) && o.AnimPhase % 8 < 5) {
                int alpha = 255 - (o.AnimPhase % 8) * 35;
                var badge = new RectangleF(S * 0.56f, S * 0.02f, S * 0.44f, S * 0.44f);
                DrawBolt(g, badge, alpha);
            }
        }
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g2 = Graphics.FromImage(bmp)) {
            g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // WrapModeTileFlipXY：消除降采样时的半像素偏移与边缘跳变，alpha 过渡对齐到整像素
            using (var ia = new System.Drawing.Imaging.ImageAttributes()) {
                ia.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                g2.DrawImage(hi, new Rectangle(0, 0, w, h), 0, 0, hi.Width, hi.Height, GraphicsUnit.Pixel, ia);
            }
        }
        hi.Dispose();
        return bmp;
    }

    // 阈值分段填充：area 是 0..100% 的完整填充区，从 from% 画到 to%，
    // 每个百分比带用其所属阈值的颜色（充电扫描时颜色逐段切换，随设定变）
    static void FillByThreshold(Graphics g, RectangleF area, int fromPct, int toPct,
                                List<ColorRange> ranges, bool horizontal) {
        if (toPct <= fromPct) return;
        var stops = new List<ColorRange>(ranges);
        stops.Sort((a, b) => a.Min.CompareTo(b.Min));
        foreach (var r in stops) {
            int lo = Math.Max(fromPct, r.Min), hi = Math.Min(toPct, r.Max + 1);
            if (hi <= lo) continue;
            using (var b = new SolidBrush(r.ToColor()))
                g.FillRectangle(b, horizontal
                    ? new RectangleF(area.X + area.Width * lo / 100f, area.Y, area.Width * (hi - lo) / 100f, area.Height)
                    : new RectangleF(area.X, area.Bottom - area.Height * hi / 100f, area.Width, area.Height * (hi - lo) / 100f));
        }
    }

    static float math2rad(float deg) { return (float)(deg * Math.PI / 180.0); }

    // 环带路径（圆头端）：外弧顺时针 → 终端半圆凸弧 → 内弧逆时针 → 起端半圆凸弧闭合。
    // 端面是真正的半圆封口（圆心=带宽中点、半径=半带宽），不会有"平弦端面"的凹角
    static GraphicsPath RingBandPath(float cx, float cy, float rOut, float rIn, float startDeg, float sweepDeg) {
        var p = new GraphicsPath();
        if (sweepDeg <= 0) { p.AddEllipse(cx - rOut, cy - rOut, 2 * rOut, 2 * rOut); return p; }
        float end = startDeg + sweepDeg;
        var ro = new RectangleF(cx - rOut, cy - rOut, 2 * rOut, 2 * rOut);
        var ri = new RectangleF(cx - rIn, cy - rIn, 2 * rIn, 2 * rIn);
        if (sweepDeg >= 359.5f) {
            p.AddEllipse(ro);
            p.AddEllipse(ri);
            return p;
        }
        float rm = (rOut + rIn) / 2f;          // 带宽中点半径
        float hw = (rOut - rIn) / 2f;          // 半带宽（圆头半径）
        p.AddArc(ro, startDeg, sweepDeg);
        // 终端半圆：圆心=终点带心(ex,ey) 半径=hw，从外缘点绕凸面 180° 到内缘点
        float ex = cx + (float)Math.Cos(math2rad(end)) * rm;
        float ey = cy + (float)Math.Sin(math2rad(end)) * rm;
        p.AddArc(ex - hw, ey - hw, hw * 2, hw * 2, end, 180);
        p.AddArc(ri, end, -sweepDeg);
        // 起端半圆同理
        float sx = cx + (float)Math.Cos(math2rad(startDeg)) * rm;
        float sy = cy + (float)Math.Sin(math2rad(startDeg)) * rm;
        p.AddArc(sx - hw, sy - hw, hw * 2, hw * 2, startDeg + 180, 180);
        p.CloseFigure();
        return p;
    }

    // 各样式的图形主体（不含数字），统一以 (0,0,W,S) 画布坐标绘制；
    // outside 模式外部已用坐标变换缩进左侧。fillPct=真实电量，sweepPct=充电扫描位（-1=非充电）。
    static void DrawStyle(Graphics g, float S, int fillPct, int sweepPct, bool charging, string style,
                          Color fillCol, List<ColorRange> ranges, float tPx) {
        if (style == "mouse") {
            // 重新设计：现代造型——窄肩流线壳、上下渐变壳体、细描边、
            // 高光电平线 + 滚轮胶囊 + 底部指示灯点，填充按电量自底向上
            var body = new RectangleF(S * 0.09f, S * 0.02f, S * 0.80f, S * 0.96f);
            using (var path = MouseBody(body)) {
                using (var grad = new LinearGradientBrush(body, Color.FromArgb(72, 76, 84), Color.FromArgb(40, 42, 47), 90f))
                    g.FillPath(grad, path);
                if (fillPct > 0) {
                    g.SetClip(path);
                    float fillH = body.Height * fillPct / 100f;
                    using (var b = new SolidBrush(Color.FromArgb(170, fillCol)))
                        g.FillRectangle(b, body.X, body.Bottom - fillH, body.Width, fillH);
                    g.ResetClip();
                }
                using (var pen = new Pen(Color.FromArgb(235, 238, 242), Math.Max(1.0f, S * 0.035f)))
                    g.DrawPath(pen, path);
            }
            DrawMouseDetails(g, body);
        }
        else if (style == "ring") {
            // 圆环（环带填充法，端部真圆头）：闭环带路径（外弧→端部半圆凸弧→内弧→起端半圆）
            // 整体 Fill。旧粗 pen 画弧的斜段离散暗点、平弦端面的凹角都不存在。
            // rOut=0.5S-0.5px：外缘恰好内切——突破内切版(=0.5S)四极点的抗锯齿像素被画布
            // 硬裁，极点区从平滑过渡变切边（实测圆滑度回归的根因）；留半像素给极点抗锯齿，
            // 尺寸损失 1px 视觉无感，圆滑保住
            float cx2 = S * 0.5f, cy2 = S * 0.5f;
            float rOut = S * 0.5f - 0.5f;
            float rIn = rOut - tPx;                       // 内半径
            int tgt = charging ? Math.Max(fillPct, sweepPct) : fillPct;
            float fillSweep = 360f * Math.Max(0, Math.Min(100, tgt)) / 100f;
            // 1) 底槽环带（缺口段，端部也是圆头）
            if (fillSweep < 359.5f) {
                using (var band = RingBandPath(cx2, cy2, rOut, rIn, -90 + fillSweep, 360f - fillSweep))
                using (var bg = new SolidBrush(Color.FromArgb(116, 120, 128)))
                    g.FillPath(bg, band);
            }
            // 2) 电量环带（12 点起顺时针）
            if (tgt > 0 && fillSweep > 0.5f) {
                using (var band = RingBandPath(cx2, cy2, rOut, rIn, -90, fillSweep))
                using (var fg = new SolidBrush(fillCol))
                    g.FillPath(fg, band);
            }
        }
        else if (IsBatteryStyle(style)) {
            // 三种电池统一 Win11 外观：主题色粗描边 + 独立正极帽 + 阈值色填充。
            // 数字由 Render 主流程画在电池体内（BatteryNumberZone）
            DrawBatteryGlyph(g, S, style == "batV", fillPct, sweepPct, charging, fillCol, ranges,
                Theme.Foreground);
        }
        else if (style == "barH" || style == "barV") {
            // 进度条：圆角底槽 + 电量填充（横：左→右；竖：下→上），无帽
            bool hor = style == "barH";
            var slot = hor
                ? new RectangleF(S * 0.06f, S * 0.36f, S * 0.88f, S * 0.28f)
                : new RectangleF(S * 0.30f, S * 0.02f, S * 0.38f, S * 0.96f);
            using (var slotPath = RoundedRect(slot, Math.Min(slot.Width, slot.Height) * 0.35f)) {
                using (var bg = new SolidBrush(Color.FromArgb(55, 58, 62)))
                    g.FillPath(bg, slotPath);
                int tgt = charging ? Math.Max(fillPct, sweepPct) : fillPct;
                if (tgt > 0) {
                    g.SetClip(slotPath);
                    if (charging) FillByThreshold(g, slot, 0, tgt, ranges, hor);
                    else if (hor) {
                        float w = slot.Width * tgt / 100f;
                        using (var b = new SolidBrush(fillCol))
                            g.FillRectangle(b, slot.X, slot.Y, w, slot.Height);
                    } else {
                        float h = slot.Height * tgt / 100f;
                        using (var b = new SolidBrush(fillCol))
                            g.FillRectangle(b, slot.X, slot.Bottom - h, slot.Width, h);
                    }
                    g.ResetClip();
                }
                float pw = Math.Max(1.0f, S * 0.04f);
                using (var pen = new Pen(Color.White, pw))
                    g.DrawPath(pen, slotPath);
            }
        }
        else if (style == "bolt") {
            // 大闪电：形状本身从底部按电量填充
            var br = new RectangleF(S * 0.09f, S * 0.01f, S * 0.80f, S * 0.98f);
            using (var path = BoltPath(br)) {
                using (var bg = new SolidBrush(Color.FromArgb(55, 58, 62)))
                    g.FillPath(bg, path);
                int tgt = charging ? Math.Max(fillPct, sweepPct) : fillPct;
                if (tgt > 0) {
                    g.SetClip(path);
                    if (charging) FillByThreshold(g, br, 0, tgt, ranges, false);
                    else {
                        float h = br.Height * tgt / 100f;
                        using (var b = new SolidBrush(fillCol))
                            g.FillRectangle(b, br.X, br.Bottom - h, br.Width, h);
                    }
                    g.ResetClip();
                }
                float pw = Math.Max(1.0f, S * 0.04f);
                using (var pen = new Pen(Color.White, pw))
                    g.DrawPath(pen, path);
            }
        }
        else if (style == "circle") {
            // 圆形：底圆 + 自底向上水位填充
            var rc = new RectangleF(S * 0.02f, S * 0.02f, S * 0.96f, S * 0.96f);
            using (var path = new GraphicsPath()) {
                path.AddEllipse(rc);
                using (var bg = new SolidBrush(Color.FromArgb(55, 58, 62)))
                    g.FillPath(bg, path);
                int tgt = charging ? Math.Max(fillPct, sweepPct) : fillPct;
                if (tgt > 0) {
                    g.SetClip(path);
                    if (charging) FillByThreshold(g, rc, 0, tgt, ranges, false);
                    else {
                        float h = rc.Height * tgt / 100f;
                        using (var b = new SolidBrush(fillCol))
                            g.FillRectangle(b, rc.X, rc.Bottom - h, rc.Width, h);
                    }
                    g.ResetClip();
                }
                float pw = Math.Max(1.0f, S * 0.045f);
                using (var pen = new Pen(Color.White, pw))
                    g.DrawPath(pen, path);
            }
        }
        else if (style == "gauge") {
            // 仪表盘：270° 弧（135° 起），环带填充法 + 圆头端（同 ring，无叠画无暗点无凹角）
            float gcx = S * 0.5f, gcy = S * 0.5f;
            float grOut = S * 0.5f - tPx * 0.5f;   // 外缘贴画布边（同 ring 视觉放大）
            float grIn = grOut - tPx;
            const float sweep = 270f, start = 135f;
            int tgt = charging ? Math.Max(fillPct, sweepPct) : fillPct;
            float fillSweep = sweep * Math.Max(0, Math.Min(100, tgt)) / 100f;
            if (fillSweep < sweep - 0.5f) {
                using (var band = RingBandPath(gcx, gcy, grOut, grIn, start + fillSweep, sweep - fillSweep))
                using (var bg = new SolidBrush(Color.FromArgb(116, 120, 128)))
                    g.FillPath(bg, band);
            }
            if (tgt > 0 && fillSweep > 0.5f) {
                using (var band = RingBandPath(gcx, gcy, grOut, grIn, start, fillSweep))
                using (var fg = new SolidBrush(fillCol))
                    g.FillPath(fg, band);
            }
        }
        else if (style == "hex") {
            var rc = new RectangleF(S * 0.02f, S * 0.03f, S * 0.96f, S * 0.94f);
            using (var path = HexPath(rc)) {
                using (var bg = new SolidBrush(Color.FromArgb(55, 58, 62)))
                    g.FillPath(bg, path);
                int tgt = charging ? Math.Max(fillPct, sweepPct) : fillPct;
                if (tgt > 0) {
                    g.SetClip(path);
                    if (charging) FillByThreshold(g, rc, 0, tgt, ranges, false);
                    else {
                        float h = rc.Height * tgt / 100f;
                        using (var b = new SolidBrush(fillCol))
                            g.FillRectangle(b, rc.X, rc.Bottom - h, rc.Width, h);
                    }
                    g.ResetClip();
                }
                float pw = Math.Max(1.0f, S * 0.045f);
                using (var pen = new Pen(Color.White, pw))
                    g.DrawPath(pen, path);
            }
        }
        else if (style == "drop") {
            var rc = new RectangleF(S * 0.04f, S * 0.02f, S * 0.92f, S * 0.96f);
            using (var path = DropPath(rc)) {
                using (var bg = new SolidBrush(Color.FromArgb(55, 58, 62)))
                    g.FillPath(bg, path);
                int tgt = charging ? Math.Max(fillPct, sweepPct) : fillPct;
                if (tgt > 0) {
                    g.SetClip(path);
                    if (charging) FillByThreshold(g, rc, 0, tgt, ranges, false);
                    else {
                        float h = rc.Height * tgt / 100f;
                        using (var b = new SolidBrush(fillCol))
                            g.FillRectangle(b, rc.X, rc.Bottom - h, rc.Width, h);
                    }
                    g.ResetClip();
                }
                float pw = Math.Max(1.0f, S * 0.045f);
                using (var pen = new Pen(Color.White, pw))
                    g.DrawPath(pen, path);
            }
        }
        // plain：无图形，仅数字（Render 主流程绘制）
    }

    static GraphicsPath RoundedRect(RectangleF r, float rad) {
        var p = new GraphicsPath();
        rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
        p.AddArc(r.X, r.Y, 2 * rad, 2 * rad, 180, 90);
        p.AddArc(r.Right - 2 * rad, r.Y, 2 * rad, 2 * rad, 270, 90);
        p.AddArc(r.Right - 2 * rad, r.Bottom - 2 * rad, 2 * rad, 2 * rad, 0, 90);
        p.AddArc(r.X, r.Bottom - 2 * rad, 2 * rad, 2 * rad, 90, 90);
        p.CloseFigure();
        return p;
    }

    // 不用了：见 Program.RenderTray —— HICON 必须显式 DestroyIcon 回收，
    // 否则每次换图标泄漏一个 GDI 句柄（上限1万），充电动画 150ms/帧约 25 分钟耗尽 → GetHicon 抛错、程序僵死
}

// ======================= 现代化 UI 基础组件 =======================
// 统一配色 / 字体 / 圆角卡片绘制，让设置窗与记录弹窗保持同一套视觉语言。
static class Ui {
    public static readonly Color Bg          = Color.FromArgb(244, 246, 248);
    public static readonly Color Card        = Color.White;
    public static readonly Color Border      = Color.FromArgb(228, 232, 236);
    public static readonly Color Text        = Color.FromArgb(32, 36, 42);
    public static readonly Color Muted       = Color.FromArgb(122, 130, 140);
    public static readonly Color Faint       = Color.FromArgb(162, 170, 180);
    public static readonly Color Accent      = Color.FromArgb(0, 190, 118);
    public static readonly Color AccentDark  = Color.FromArgb(0, 148, 88);
    public static readonly Color AccentSoft  = Color.FromArgb(232, 250, 242);
    public static readonly Color Sidebar     = Color.FromArgb(26, 28, 34);
    public static readonly Color SidebarHover= Color.FromArgb(38, 41, 49);
    public static readonly Color SidebarOn   = Color.FromArgb(45, 49, 59);
    public static readonly Color SidebarText = Color.FromArgb(174, 181, 192);
    public static readonly Color Red         = Color.FromArgb(220, 68, 68);
    public static readonly Color RedSoft     = Color.FromArgb(253, 237, 237);
    public static readonly Color Amber       = Color.FromArgb(219, 146, 20);
    public static readonly Color Blue        = Color.FromArgb(52, 132, 220);
    public static readonly Color Purple      = Color.FromArgb(132, 96, 210);
    public static readonly Color Orange      = Color.FromArgb(232, 118, 42);

    public static readonly Font Body       = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font BodyBold   = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font Small      = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font SmallBold  = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font CardTitle  = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font Big        = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font Stat       = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font H1         = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font Nav        = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);

    public static void Prepare(Graphics g) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    public static GraphicsPath Round(Rectangle r, int radius) {
        var p = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) { p.AddRectangle(new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height))); return p; }
        int d = Math.Max(2, radius * 2);
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void RoundControl(Control c, int radius) {
        if (c == null || c.Width <= 0 || c.Height <= 0) return;
        using (var p = Round(new Rectangle(0, 0, c.Width, c.Height), radius)) {
            var old = c.Region;
            c.Region = new Region(p);
            if (old != null) old.Dispose();
        }
    }
}

// 圆角白卡片：标题 / 副标题由卡片自己绘制，内容控件仍按绝对坐标摆放。
class CardPanel : Panel {
    public string Title = "";
    public string Hint = "";
    public int Radius = 12;

    public CardPanel() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Ui.Bg;   // 圆角外的底色与所在区域一致
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        Ui.Prepare(g);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Ui.Round(r, Radius)) {
            using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
            using (var p = new Pen(Ui.Border)) g.DrawPath(p, path);
        }
        if (Title != null && Title.Length > 0) {
            using (var b = new SolidBrush(Ui.Text))
                g.DrawString(Title, Ui.CardTitle, b, 24, 18);
        }
        if (Hint != null && Hint.Length > 0) {
            using (var b = new SolidBrush(Ui.Muted))
                g.DrawString(Hint, Ui.Small, b, 24, 43);
        }
    }
}

// 左侧导航项：选中态用绿色竖条 + 更亮的底色提示。
class NavButton : Control {
    bool _selected, _hover;
    public bool Selected {
        get { return _selected; }
        set { if (_selected != value) { _selected = value; Invalidate(); } }
    }
    public NavButton() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        BackColor = Ui.Sidebar;
        Font = Ui.Nav;
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.Clear(Ui.Sidebar);
        if (_selected || _hover)
            using (var b = new SolidBrush(_selected ? Ui.SidebarOn : Ui.SidebarHover))
                g.FillRectangle(b, 0, 0, Width, Height);
        if (_selected)
            using (var b = new SolidBrush(Ui.Accent))
                g.FillRectangle(b, 0, 12, 3, Height - 24);
        Color fg = _selected ? Color.White : Ui.SidebarText;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(24, 0, Width - 34, Height), fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

// 扁平现代按钮：Primary 绿色实心 / Secondary 白底描边 / Danger 红色警示。
class ModernButton : Button {
    public enum Kind { Primary, Secondary, Danger }
    public Kind StyleKind = Kind.Secondary;
    bool _hover, _down;

    public ModernButton() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        Font = Ui.Body;
        BackColor = Color.White;
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        Ui.Prepare(g);
        g.Clear(BackColor);
        Color bg, border, fg;
        if (!Enabled) {
            bg = Color.FromArgb(246, 247, 249); border = Ui.Border; fg = Ui.Faint;
        } else if (StyleKind == Kind.Primary) {
            bg = _down ? Ui.AccentDark : (_hover ? Color.FromArgb(0, 205, 128) : Ui.Accent);
            border = bg; fg = Color.White;
        } else if (StyleKind == Kind.Danger) {
            bg = _down ? Color.FromArgb(250, 228, 228) : (_hover ? Ui.RedSoft : Color.White);
            border = Color.FromArgb(240, 202, 202); fg = Ui.Red;
        } else {
            bg = _down ? Color.FromArgb(236, 239, 243) : (_hover ? Color.FromArgb(247, 249, 251) : Color.White);
            border = Ui.Border; fg = Ui.Text;
        }
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Ui.Round(r, 8)) {
            using (var b = new SolidBrush(bg)) g.FillPath(b, path);
            using (var p = new Pen(border)) g.DrawPath(p, path);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

// 自绘复选框：18px 圆角方框 + 绿色勾选，避免系统蓝色默认样式。
class ToggleCheck : CheckBox {
    bool _hover;
    public ToggleCheck() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        AutoSize = false;
        Height = 26;
        BackColor = Color.White;
        ForeColor = Ui.Text;
        Font = Ui.Body;
        Cursor = Cursors.Hand;
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        Ui.Prepare(g);
        g.Clear(BackColor);
        int box = 18;
        int by = (Height - box) / 2;
        var r = new Rectangle(0, by, box, box);
        using (var path = Ui.Round(r, 5)) {
            if (Checked) {
                using (var b = new SolidBrush(_hover ? Ui.AccentDark : Ui.Accent)) g.FillPath(b, path);
            } else {
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                using (var p = new Pen(_hover ? Ui.Accent : Color.FromArgb(198, 205, 214))) g.DrawPath(p, path);
            }
        }
        if (Checked) {
            using (var p = new Pen(Color.White, 2f)) {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawLines(p, new PointF[] {
                    new PointF(4.5f, by + 9f), new PointF(7.8f, by + 12.5f), new PointF(13.5f, by + 5.5f) });
            }
        }
        Color fg = Enabled ? Ui.Text : Ui.Faint;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(box + 10, 0, Width - box - 12, Height), fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

// 现代化 DPI 滑杆：自绘绿色轨道 + 圆形滑块，避免原生 TrackBar 在白色卡片上轨道不可见
class StageDot : Control {
    public int Number = 1;
    public Color DotColor = Ui.Accent;
    bool _selected;
    public bool Selected {
        get { return _selected; }
        set { if (_selected == value) return; _selected = value; Invalidate(); }
    }
    public event EventHandler Picked;
    public StageDot() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Size = new Size(38, 32);
        BackColor = Color.White;
        Cursor = Cursors.Hand;
    }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        Ui.Prepare(g);
        g.Clear(BackColor);
        int r = 13, cx = Width / 2, cy = Height / 2;
        using (var b = new SolidBrush(_selected ? DotColor : Color.White))
            g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
        using (var p = new Pen(DotColor, 2f))
            g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
        using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
            TextRenderer.DrawText(g, Number.ToString(), f,
                new Rectangle(0, 0, Width, Height), _selected ? Color.White : DotColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left && Picked != null) Picked(this, EventArgs.Empty);
        base.OnMouseDown(e);
    }
}

class DpiSlider : Control {
    public int Minimum = 100, Maximum = 35000;
    public Color SliderColor = Ui.Accent;
    int _value = 1000;
    public event EventHandler ValueChanged;
    public int Value {
        get { return _value; }
        set {
            int v = Math.Max(Minimum, Math.Min(Maximum, value));
            if (_value == v) return;
            _value = v;
            Invalidate();
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }
    }
    public DpiSlider() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Height = 30;
        BackColor = Color.White;
        Cursor = Cursors.Hand;
    }
    float Ratio { get { return Maximum <= Minimum ? 0f : (_value - Minimum) / (float)(Maximum - Minimum); } }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        Ui.Prepare(g);
        g.Clear(BackColor);
        int pad = 9, cy = Height / 2, w = Math.Max(1, Width - pad * 2);
        using (var b = new SolidBrush(Color.FromArgb(226, 231, 236)))
            g.FillRectangle(b, pad, cy - 2, w, 4);
        int tw = (int)(w * Ratio);
        using (var b = new SolidBrush(SliderColor))
            g.FillRectangle(b, pad, cy - 2, tw, 4);
        int tx = pad + tw;
        using (var b = new SolidBrush(Color.White))
            g.FillEllipse(b, tx - 7, cy - 7, 14, 14);
        using (var p = new Pen(SliderColor, 2f))
            g.DrawEllipse(p, tx - 7, cy - 7, 14, 14);
    }
    void SetFromX(int x) {
        int pad = 9, w = Math.Max(1, Width - pad * 2);
        float r = (float)(x - pad) / w;
        r = Math.Max(0f, Math.Min(1f, r));
        Value = Minimum + (int)Math.Round(r * (Maximum - Minimum));
    }
    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left) { SetFromX(e.X); Focus(); }
        base.OnMouseDown(e);
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left) SetFromX(e.X);
        base.OnMouseMove(e);
    }
}

// 动态灵敏度曲线预览（Synapse 软件功能，本程序只做界面/曲线展示）
class CurvePanel : Control {
    int _kind = 1;
    int _speed;
    int _drag = -1;
    public float[] CustomY = { 1.00f, 1.15f, 1.30f, 1.42f, 1.50f };
    public event EventHandler CurveChanged;

    public int Kind {
        get { return _kind; }
        set { if (_kind == value) return; _kind = value; Cursor = value == 3 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }
    public int MouseSpeed {
        get { return _speed; }
        set { int v = Math.Max(0, Math.Min(70, value)); if (_speed == v) return; _speed = v; Invalidate(); }
    }
    public CurvePanel() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Color.FromArgb(248, 250, 252);
        Height = 170;
    }

    float RatioAt(float x) {
        if (_kind == 3) {
            float pos = x / 70f * 4f;
            int i = Math.Min(3, (int)pos);
            float f = pos - i;
            return CustomY[i] * (1f - f) + CustomY[i + 1] * f;
        }
        switch (_kind) {
            case 0: return 1.0f + 0.22f * (x / 70f);
            case 2: return 1.0f + 0.5f * Math.Min(1f, x / 12f);
            default: return (float)(1.5 - 0.5 * Math.Exp(-x / 7.0));
        }
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        Ui.Prepare(g);
        g.Clear(BackColor);
        int l = 46, t = 26, r = Width - 14, b = Height - 38;
        int w = Math.Max(1, r - l), h = Math.Max(1, b - t);
        using (var p = new Pen(Color.FromArgb(228, 232, 236)))
            g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        using (var p = new Pen(Color.FromArgb(232, 236, 240)))
        using (var f = new Font("Segoe UI", 6.5f))
        using (var br = new SolidBrush(Ui.Faint)) {
            for (int i = 0; i <= 4; i++) {
                int y = b - h * i / 4;
                g.DrawLine(p, l, y, r, y);
                g.DrawString((1.0 + i * 0.2).ToString("0.0"), f, br, 6, y - 7);
            }
            for (int i = 0; i <= 7; i++) {
                int x = l + w * i / 7;
                g.DrawLine(p, x, t, x, b);
                g.DrawString((i * 10).ToString(), f, br, x - 6, b + 4);
            }
        }
        var pts = new PointF[141];
        for (int i = 0; i <= 140; i++) {
            float x = i / 140f * 70f;
            float y = RatioAt(x);
            pts[i] = new PointF(l + w * (x / 70f), b - h * ((y - 1.0f) / 0.8f));
        }
        // 鼠标移动速度填充：从 0 到当前速度的曲线下方区域（Synapse 同款）
        if (_speed > 0) {
            using (var fill = new GraphicsPath()) {
                fill.AddLine(new PointF(l, b), pts[0]);
                int last = 0;
                for (int i = 1; i <= 140; i++) {
                    float x = i / 140f * 70f;
                    if (x > _speed) break;
                    fill.AddLine(pts[i - 1], pts[i]);
                    last = i;
                }
                fill.AddLine(pts[last], new PointF(pts[last].X, b));
                fill.CloseFigure();
                using (var bf = new SolidBrush(Color.FromArgb(55, Ui.Accent)))
                    g.FillPath(bf, fill);
            }
            float sx = l + w * (_speed / 70f);
            using (var p = new Pen(Ui.Accent, 1f))
                g.DrawLine(p, sx, t, sx, b);
        }
        using (var gp = new Pen(Ui.Accent, 2f))
            g.DrawLines(gp, pts);
        if (_kind == 3) {
            for (int i = 0; i < 5; i++) {
                float x = l + w * (i / 4f);
                float y = b - h * ((CustomY[i] - 1.0f) / 0.8f);
                using (var bf = new SolidBrush(Color.White))
                    g.FillEllipse(bf, x - 5, y - 5, 10, 10);
                using (var p = new Pen(Ui.Accent, 2f))
                    g.DrawEllipse(p, x - 5, y - 5, 10, 10);
            }
        }
        using (var f = new Font("Microsoft YaHei UI", 7f))
        using (var br = new SolidBrush(Ui.Muted)) {
            g.DrawString("输出与输入比率", f, br, 4, 2);
            g.DrawString("输入速度（计数/毫秒）", f, br, l + w / 2 - 50, Height - 15);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (_kind == 3) {
            int l = 46, r = Width - 14;
            int w = Math.Max(1, r - l);
            int best = -1; float bestDx = 18f;
            for (int i = 0; i < 5; i++) {
                float px = l + w * (i / 4f);
                float dx = Math.Abs(e.X - px);
                if (dx < bestDx) { bestDx = dx; best = i; }
            }
            if (best >= 0) { _drag = best; Capture = true; }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        if (_drag >= 0) {
            int t = 26, b = Height - 38;
            int h = Math.Max(1, b - t);
            float ratio = 1.0f + (b - e.Y) / (float)h * 0.8f;
            ratio = Math.Max(1.0f, Math.Min(1.75f, ratio));
            CustomY[_drag] = ratio;
            Invalidate();
            if (CurveChanged != null) CurveChanged(this, EventArgs.Empty);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        _drag = -1;
        Capture = false;
        base.OnMouseUp(e);
    }
}


// 顶部连接状态胶囊：彩色圆点 + 文字，颜色随有线/无线/离线切换。
class Pill : Control {
    Color _accent = Ui.Muted;
    public Pill() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Ui.Bg;
        Font = Ui.SmallBold;
        Height = 34;
    }
    public void SetText(string text, Color accent) {
        Text = text;
        _accent = accent;
        Width = TextRenderer.MeasureText(text, Font).Width + 46;
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        Ui.Prepare(g);
        g.Clear(BackColor);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Ui.Round(r, Height / 2)) {
            using (var b = new SolidBrush(Color.FromArgb(24, _accent))) g.FillPath(b, path);
            using (var p = new Pen(Color.FromArgb(70, _accent))) g.DrawPath(p, path);
        }
        using (var b = new SolidBrush(_accent)) g.FillEllipse(b, 15, Height / 2 - 4, 8, 8);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(30, 0, Width - 38, Height), _accent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

// 使用记录中的一条事件卡片（整行自绘，避免大量子控件）。
class HistoryRow : Control {
    public DateTime Time = DateTime.MinValue;
    public string Message = "";
    public string Badge = "事件";
    public Color BadgeColor = Ui.Muted;

    public HistoryRow() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Ui.Bg;
        Height = 68;
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        Ui.Prepare(g);
        g.Clear(BackColor);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Ui.Round(r, 10)) {
            using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
            using (var p = new Pen(Ui.Border)) g.DrawPath(p, path);
        }
        string timeText = Time == DateTime.MinValue ? "--:--" : Time.ToString("HH:mm:ss");
        string dateText = Time == DateTime.MinValue ? "未知日期" : Time.ToString("yyyy-MM-dd");
        using (var b = new SolidBrush(Ui.Text)) g.DrawString(timeText, Ui.Big, b, 18, 11);
        using (var b = new SolidBrush(Ui.Faint)) g.DrawString(dateText, Ui.Small, b, 18, 37);

        int badgeX = 124;
        int bw = TextRenderer.MeasureText(Badge, Ui.SmallBold).Width + 22;
        var br = new Rectangle(badgeX, 22, bw, 24);
        using (var path = Ui.Round(br, 12)) {
            using (var b = new SolidBrush(Color.FromArgb(28, BadgeColor))) g.FillPath(b, path);
        }
        TextRenderer.DrawText(g, Badge, Ui.SmallBold, br, BadgeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        int msgX = badgeX + bw + 16;
        var msgRect = new Rectangle(msgX, 12, Width - msgX - 18, Height - 20);
        TextRenderer.DrawText(g, Message, Ui.Body, msgRect, Ui.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

// ======================= 设置窗体 =======================
class SettingsForm : Form {
    ComboBox cmbStyle;
    Panel pnlNumColor;
    FlowLayoutPanel dailyPickersPanel;
    NumericUpDown numRefresh, numFont, numThick, numLow, numOutline, numSeg, numDailyMax, numLogKeep;
    CheckBox chkAuto, chkNumber, chkPercent, chkLow, chkFull, chkDaily, chkEst, chkAutoNum, chkUpdate;
    ModernButton btnUpdate;
    DataGridView dgv;
    PictureBox picLive, picSim;
    System.Windows.Forms.Timer previewTimer;
    System.Windows.Forms.Timer mouseTimer;
    System.Windows.Forms.Timer speedTimer;
    Point lastSpeedPoint;
    DateTime lastSpeedTime, lastMoveTime;
    int previewPhase;
    Label lblFull, lblEmpty, lblEst;
    Label lblMouseDpi, lblMousePoll;
    StageDot[] dpiDots;
    NumericUpDown[] dpiNums, dpiNumsY;
    ToggleCheck[] dpiLinks;
    DpiSlider[] dpiBars;
    Label lblDpiStatus, lblPollLive, lblSmartState;
    ModernButton[] pollBtns, gamePollBtns;
    ToggleCheck chkSmartPoll;
    int pollRateSel, gameRateSel, dpiActiveStage = 1;
    bool syncingDpiUi;
    bool mousePageLoaded;
    DateTime toastUntil;
    int toastSeq;
    // 电源 / 校准 / 高级
    DpiSlider idleSlider, lowSlider, liftSlider, landSlider, rotateSlider;
    Label lblIdleVal, lblLowVal, lblPowerStatus, lblLiftVal, lblLandVal, lblCalStatus, lblRotVal;
    ToggleCheck chkAdvDyn, chkAdvRotate;
    CurvePanel curvePanel;
    ModernButton[] curveBtns;
    ListBox lstRun, lstEvents;
    Label lblHist, lblAvgRun, lblLogSize, lblArchInfo;
    Pill linkBar;
    Settings cfg;
    Action onApply;

    Panel sidebar, mainPanel, header, contentHost;
    Panel[] sections;
    NavButton[] navs;
    Label lblSectionTitle, lblSectionSub;
    ModernButton btnHistory;

    const int CardW = 720;
    const int ContentW = 740;
    const int R1 = 64;      // 卡片内容第一行
    const int R2 = 110;
    const int R3 = 156;

    static readonly string[] SectionTitles = { "外观", "电量配色", "鼠标设置", "提醒", "预估与播报", "记录与统计", "日志管理" };
    static readonly string[] SectionSubs = {
        "图标样式、数字外观与刷新频率",
        "按电量区间自定义颜色与阈值",
        "DPI 档位、轮询率与全屏游戏智能切换",
        "低电量、充满提醒与开机启动",
        "续航估算与每日定时播报",
        "使用记录、续航区间与充电统计",
        "运行日志与归档文件清理"
    };

    static readonly string[] StyleIds = { "plain", "ring", "mouse", "batH", "batV",
                                           "barH", "barV", "bolt", "circle", "gauge", "hex", "drop" };
    static int StyleToIndex(string id) {
        // 旧版"电池-双格(Win)"/"电池+百分比(Win)"并入电池-横向（双格方案已移除）
        if (id == "batWin" || id == "batPct") id = "batH";
        int i = Array.IndexOf(StyleIds, id);
        return i < 0 ? 0 : i;
    }
    static string IndexToStyle(int i) { return StyleIds[Math.Max(0, Math.Min(StyleIds.Length - 1, i))]; }

    public SettingsForm(Settings cfg, Action onApply) {
        this.cfg = cfg;
        this.onApply = onApply;
        Text = "毒蝰 V3 Pro 电量 · 设置中心";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Util.AppIcon();
        BackColor = Ui.Bg;
        // 字体先于 AutoScale 设置，确保 DPI 缩放基于统一字体
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Font;

        BuildSidebar();
        BuildHeader();
        BuildBottomBar();
        BuildContent();

        // Dock 顺序：内容先加，再底栏/顶栏/侧栏，保证布局区域正确
        mainPanel = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
        mainPanel.Controls.Add(contentHost);
        mainPanel.Controls.Add(bottomBar);
        mainPanel.Controls.Add(header);
        Controls.Add(mainPanel);
        Controls.Add(sidebar);

        // 窗体尺寸：优先 1000x760，屏幕过小时压缩并让内容区滚动
        int targetW = 1000, targetH = 760;
        try {
            var wa = Screen.PrimaryScreen.WorkingArea;
            targetW = Math.Min(targetW, Math.Max(860, wa.Width - 40));
            targetH = Math.Min(targetH, Math.Max(620, wa.Height - 40));
        } catch { }
        ClientSize = new Size(targetW, targetH);

        // 默认位置：水平居中，垂直略偏上（可用区域约 40% 处）
        StartPosition = FormStartPosition.Manual;
        try {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int left = wa.X + Math.Max(0, (wa.Width - Width) / 2);
            int top = wa.Y + Math.Max(0, (int)((wa.Height - Height) * 0.4));
            Location = new Point(left, top);
        } catch { StartPosition = FormStartPosition.CenterScreen; }

        // 侧栏底部版本 / 作者（跟随侧栏高度）
        var lblVersion = new Label {
            Text = "v1.8.0", AutoSize = true, ForeColor = Color.FromArgb(124, 131, 143),
            Font = Ui.Small, BackColor = Ui.Sidebar
        };
        var lblAuthor = new Label {
            Text = "作者：岚乔", AutoSize = true, ForeColor = Color.FromArgb(92, 99, 111),
            Font = Ui.Small, BackColor = Ui.Sidebar
        };
        btnUpdate = new ModernButton {
            Text = "检查更新", Bounds = new Rectangle(24, 10, 120, 30),
            StyleKind = ModernButton.Kind.Secondary, BackColor = Ui.Sidebar
        };
        btnUpdate.Click += (s, e) => Program.CheckForUpdates(true);
        sidebar.Controls.Add(btnUpdate);
        sidebar.Controls.Add(lblVersion);
        sidebar.Controls.Add(lblAuthor);
        sidebar.Resize += (s, e) => {
            btnUpdate.Location = new Point(24, Math.Max(10, sidebar.ClientSize.Height - 96));
            lblVersion.Location = new Point(24, Math.Max(10, sidebar.ClientSize.Height - 54));
            lblAuthor.Location = new Point(24, Math.Max(10, sidebar.ClientSize.Height - 32));
        };
        btnUpdate.Location = new Point(24, Math.Max(10, sidebar.ClientSize.Height - 96));
        lblVersion.Location = new Point(24, Math.Max(10, sidebar.ClientSize.Height - 54));
        lblAuthor.Location = new Point(24, Math.Max(10, sidebar.ClientSize.Height - 32));

        // 预览动画定时器：150ms/帧
        previewTimer = new System.Windows.Forms.Timer { Interval = 150 };
        previewTimer.Tick += (s, e) => { previewPhase = (previewPhase + 1) % 8; RenderPreviews(); };
        previewTimer.Start();

        // 顶栏 DPI/轮询率 + 鼠标设置页实时值：1 秒刷新（HID 读取在 Program 内 3 秒节流）
        mouseTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        mouseTimer.Tick += (s, e) => {
            Program.RefreshMouseInfo();
            UpdateMouseHeader();
            UpdateMouseLive();
        };
        mouseTimer.Start();
        FormClosed += (s, e) => {
            try { previewTimer.Stop(); previewTimer.Dispose(); } catch { }
            try { mouseTimer.Stop(); mouseTimer.Dispose(); } catch { }
            try { speedTimer.Stop(); speedTimer.Dispose(); } catch { }
        };

        Program.RefreshMouseInfo(true);
        UpdateMouseHeader();

        // 高级曲线填充动画：30ms 采样鼠标移动速度（像素/毫秒 -> 计数/毫秒）
        lastSpeedPoint = Cursor.Position;
        lastSpeedTime = DateTime.Now;
        speedTimer = new System.Windows.Forms.Timer { Interval = 30 };
        speedTimer.Tick += (s, e) => {
            var p = Cursor.Position;
            var now = DateTime.Now;
            double dt = (now - lastSpeedTime).TotalMilliseconds;
            if (dt < 20) return;
            double dx = p.X - lastSpeedPoint.X, dy = p.Y - lastSpeedPoint.Y;
            double pxPerMs = Math.Sqrt(dx * dx + dy * dy) / dt;
            double dpi = Program.mouseInfo.DpiX > 0 ? Program.mouseInfo.DpiX : 1600;
            double ppi = 96.0 * Math.Max(0.5, IconRenderer.DpiScale);
            double cpm = pxPerMs * dpi / ppi;
            if (curvePanel != null) {
                if (cpm > 0.4) {
                    curvePanel.MouseSpeed = (int)Math.Min(70, Math.Round(cpm));
                    lastMoveTime = now;
                } else if ((now - lastMoveTime).TotalMilliseconds > 120) {
                    curvePanel.MouseSpeed = 0;
                }
            }
            lastSpeedPoint = p;
            lastSpeedTime = now;
        };
        speedTimer.Start();

        SelectSection(0);
        RefreshHistoryLists();
        RebuildDailyPickers();
        RenderPreviews();
        LayoutHeader();
        CenterSections();
    }

    // ---- 侧栏 ----
    void BuildSidebar() {
        sidebar = new Panel { Dock = DockStyle.Left, Width = 216, BackColor = Ui.Sidebar };

        var logo = new PictureBox {
            Bounds = new Rectangle(22, 26, 38, 38), SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Ui.Sidebar
        };
        try { using (var ic = Util.AppIcon()) logo.Image = ic.ToBitmap(); } catch { }
        var lblBrand = new Label {
            Text = "毒蝰 V3 Pro", Bounds = new Rectangle(72, 24, 130, 22),
            Font = Ui.BodyBold, ForeColor = Color.White, BackColor = Ui.Sidebar
        };
        var lblBrandSub = new Label {
            Text = "电量托盘 · Razer", Bounds = new Rectangle(72, 46, 130, 18),
            Font = Ui.Small, ForeColor = Color.FromArgb(116, 123, 135), BackColor = Ui.Sidebar
        };
        sidebar.Controls.Add(logo);
        sidebar.Controls.Add(lblBrand);
        sidebar.Controls.Add(lblBrandSub);

        navs = new NavButton[SectionTitles.Length];
        for (int i = 0; i < SectionTitles.Length; i++) {
            var n = new NavButton {
                Text = SectionTitles[i],
                Bounds = new Rectangle(16, 112 + i * 54, 184, 46),
                Tag = i
            };
            n.Click += (s, e) => SelectSection((int)((Control)s).Tag);
            sidebar.Controls.Add(n);
            navs[i] = n;
        }
    }

    // ---- 顶栏：标题 + 状态胶囊 + 使用记录按钮 ----
    void BuildHeader() {
        header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Ui.Bg };
        lblSectionTitle = new Label {
            Text = SectionTitles[0], Bounds = new Rectangle(32, 22, 340, 32),
            Font = Ui.H1, ForeColor = Ui.Text, BackColor = Ui.Bg, AutoEllipsis = true,
            Visible = false   // 顶部红色区域改放 DPI / 轮询率，侧栏已高亮当前页
        };
        lblSectionSub = new Label {
            Text = SectionSubs[0], Bounds = new Rectangle(34, 54, 360, 22),
            Font = Ui.Body, ForeColor = Ui.Muted, BackColor = Ui.Bg, AutoEllipsis = true,
            Visible = false   // 红色区域改放 DPI / 轮询率两行
        };
        lblMouseDpi = new Label {
            Text = "DPI —", Bounds = new Rectangle(32, 40, 340, 24),
            Font = Ui.SmallBold, ForeColor = Ui.Text, BackColor = Ui.Bg,
            TextAlign = ContentAlignment.MiddleLeft
        };
        lblMousePoll = new Label {
            Text = "轮询率 —", Bounds = new Rectangle(32, 66, 340, 22),
            Font = Ui.Small, ForeColor = Ui.Muted, BackColor = Ui.Bg,
            TextAlign = ContentAlignment.MiddleLeft
        };
        lblMouseDpi.Cursor = Cursors.Hand;
        lblMousePoll.Cursor = Cursors.Hand;
        lblMouseDpi.Click += (s, e) => SelectSection(2);
        lblMousePoll.Click += (s, e) => SelectSection(2);
        linkBar = new Pill { BackColor = Ui.Bg };
        linkBar.SetText("连接方式：检测中…", Ui.Muted);
        linkBar.Location = new Point(0, 30);

        btnHistory = new ModernButton {
            Text = "使用记录", Bounds = new Rectangle(0, 28, 120, 38),
            StyleKind = ModernButton.Kind.Secondary, BackColor = Ui.Bg
        };
        btnHistory.Click += (s, e) => OpenHistory();

        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Ui.Border };
        header.Controls.Add(lblSectionTitle);
        header.Controls.Add(lblSectionSub);
        header.Controls.Add(lblMouseDpi);
        header.Controls.Add(lblMousePoll);
        header.Controls.Add(linkBar);
        header.Controls.Add(btnHistory);
        header.Controls.Add(line);
        // 顶栏右侧控件置前：即使左标题较长也不会盖住状态胶囊 / 使用记录按钮
        btnHistory.BringToFront();
        linkBar.BringToFront();
        header.Resize += (s, e) => LayoutHeader();
    }

    // ---- 底栏：保存 / 关闭 ----
    Panel bottomBar;
    void BuildBottomBar() {
        bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 72, BackColor = Color.White };
        var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Ui.Border };
        var hint = new Label {
            Text = "修改设置后点击「保存并应用」生效", Bounds = new Rectangle(32, 25, 420, 24),
            Font = Ui.Body, ForeColor = Ui.Muted, BackColor = Color.White
        };
        var btnSave = new ModernButton {
            Text = "保存并应用", Bounds = new Rectangle(0, 16, 132, 40),
            StyleKind = ModernButton.Kind.Primary, BackColor = Color.White,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        var btnClose = new ModernButton {
            Text = "关闭", Bounds = new Rectangle(0, 16, 96, 40),
            StyleKind = ModernButton.Kind.Secondary, BackColor = Color.White,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        btnSave.Click += (s, e) => Apply();
        btnClose.Click += (s, e) => Close();
        btnSave.Left = 784 - 32 - btnSave.Width;
        btnClose.Left = btnSave.Left - 12 - btnClose.Width;
        bottomBar.Controls.Add(line);
        bottomBar.Controls.Add(hint);
        bottomBar.Controls.Add(btnSave);
        bottomBar.Controls.Add(btnClose);
        bottomBar.Resize += (s, e) => {
            btnSave.Left = bottomBar.ClientSize.Width - 32 - btnSave.Width;
            btnClose.Left = btnSave.Left - 12 - btnClose.Width;
        };
    }

    void LayoutHeader() {
        if (header == null || linkBar == null) return;
        if (header.ClientSize.Width <= 0) return;
        int right = header.ClientSize.Width - 32;
        linkBar.Left = right - linkBar.Width;
        linkBar.Top = 30;
        if (btnHistory != null) {
            btnHistory.Left = linkBar.Left - 14 - btnHistory.Width;
            btnHistory.Top = 28;
        }
    }

    void UpdateMouseHeader() {
        if (lblMouseDpi == null || DateTime.Now < toastUntil) return;
        var m = Program.mouseInfo;
        if (m.Online && m.DpiX > 0) {
            lblMouseDpi.Text = m.DpiX == m.DpiY ? "DPI " + m.DpiX : "DPI " + m.DpiX + " × " + m.DpiY;
            lblMousePoll.Text = m.PollHz > 0 ? "轮询率 " + m.PollHz + " Hz" : "轮询率 读取中…";
        } else {
            lblMouseDpi.Text = "DPI —";
            lblMousePoll.Text = "轮询率 —";
        }
    }

    void UpdateMouseLive() {
        var m = Program.mouseInfo;
        if (lblPollLive != null)
            lblPollLive.Text = m.Online && m.PollHz > 0 ? "当前轮询率：" + m.PollHz + " Hz" : "当前轮询率：读取中…";
        if (lblSmartState != null) {
            string state = Program.LastFullscreen ? "游戏全屏" : "桌面";
            string rate = m.Online && m.PollHz > 0 ? m.PollHz + " Hz" : "—";
            lblSmartState.Text = "当前状态：" + state + " · " + rate;
        }
    }

    static Color StageColor(int i) {
        Color[] colors = {
            Color.FromArgb(0, 230, 118), Color.FromArgb(0, 191, 165),
            Color.FromArgb(255, 179, 0), Color.FromArgb(244, 81, 30),
            Color.FromArgb(171, 71, 188)
        };
        return colors[Math.Max(0, Math.Min(colors.Length - 1, i))];
    }

    void UpdateStageDots() {
        if (dpiDots == null) return;
        for (int i = 0; i < dpiDots.Length; i++)
            dpiDots[i].Selected = (i + 1 == dpiActiveStage);
    }

    public void ShowToast(string text, bool ok) { Toast(text, ok); }

    public void SetUpdateBusy(bool busy) {
        if (btnUpdate == null) return;
        btnUpdate.Enabled = !busy;
        btnUpdate.Text = busy ? "检查中…" : "检查更新";
    }

    void Toast(string text, bool ok) {
        if (lblMouseDpi == null) return;
        int seq = ++toastSeq;
        toastUntil = DateTime.Now.AddMilliseconds(1600);
        lblMouseDpi.Text = text;
        lblMouseDpi.ForeColor = ok ? Ui.AccentDark : Ui.Red;
        lblMousePoll.Text = "";
        var t = new System.Windows.Forms.Timer { Interval = 1600 };
        t.Tick += (s, e) => {
            t.Stop();
            if (seq == toastSeq) {
                toastUntil = DateTime.MinValue;
                lblMouseDpi.ForeColor = Ui.Text;
                UpdateMouseHeader();
            }
            t.Dispose();
        };
        t.Start();
    }

    // ---- 内容区：5 个可切换页面 ----
    void BuildContent() {
        contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, AutoScroll = true };
        contentHost.Resize += (s, e) => CenterSections();

        sections = new Panel[SectionTitles.Length];
        sections[0] = BuildAppearanceSection();
        sections[1] = BuildThresholdSection();
        sections[2] = BuildMouseSection();
        sections[3] = BuildNotifySection();
        sections[4] = BuildEstimateSection();
        sections[5] = BuildRecordsSection();
        sections[6] = BuildLogSection();

        foreach (var sec in sections) {
            sec.Location = new Point(0, 20);
            sec.Visible = false;
            contentHost.Controls.Add(sec);
        }
    }

    void CenterSections() {
        if (sections == null || contentHost == null) return;
        int avail = contentHost.ClientSize.Width;
        // 当前页高于可视区时会出现竖向滚动条，先预留其宽度，避免多出横向滚动条
        for (int i = 0; i < sections.Length; i++) {
            if (sections[i] != null && sections[i].Visible && sections[i].Height > contentHost.ClientSize.Height) {
                avail -= SystemInformation.VerticalScrollBarWidth;
                break;
            }
        }
        int x = Math.Max(0, (avail - ContentW) / 2);
        foreach (var sec in sections) if (sec != null) sec.Left = x;
    }

    void SelectSection(int index) {
        if (sections == null || index < 0 || index >= sections.Length) return;
        for (int i = 0; i < sections.Length; i++) {
            // 先移到最左侧再显示：高页面会引出竖向滚动条，
            // 若仍停留在居中位置会多出 4px 横向滚动条
            if (i == index) sections[i].Left = 0;
            sections[i].Visible = i == index;
            navs[i].Selected = i == index;
        }
        lblSectionTitle.Text = SectionTitles[index];
        lblSectionSub.Text = SectionSubs[index];
        if (index == 2 && !mousePageLoaded) {
            mousePageLoaded = true;
            LoadMousePage();
        }
        UpdateMouseHeader();
        UpdateMouseLive();
        contentHost.AutoScrollPosition = new Point(0, 0);
        CenterSections();
    }

    // ---- 小控件工厂 ----
    CardPanel AddCard(Panel sec, ref int y, string title, string hint, int height) {
        var c = new CardPanel {
            Title = title, Hint = hint,
            Bounds = new Rectangle(10, y, CardW, height), BackColor = Ui.Bg
        };
        sec.Controls.Add(c);
        y += height + 16;
        return c;
    }

    Label Lbl(Control parent, string text, int x, int y, int w, int h) {
        var l = new Label {
            Text = text, Bounds = new Rectangle(x, y, w, h), AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.Text, BackColor = Color.White
        };
        parent.Controls.Add(l);
        return l;
    }
    Label Muted(Control parent, string text, int x, int y, int w, int h) {
        var l = Lbl(parent, text, x, y, w, h);
        l.ForeColor = Ui.Muted;
        return l;
    }
    Label BoldLbl(Control parent, string text, int x, int y, int w, int h) {
        var l = Lbl(parent, text, x, y, w, h);
        l.Font = Ui.BodyBold;
        return l;
    }
    NumericUpDown Num(Control parent, int x, int y, int w, int min, int max, int val) {
        var n = new NumericUpDown {
            Bounds = new Rectangle(x, y, w, 28), Minimum = min, Maximum = max,
            BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, Font = Ui.Body
        };
        n.Value = Math.Max(min, Math.Min(max, val));
        parent.Controls.Add(n);
        return n;
    }
    ComboBox Cmb(Control parent, int x, int y, int w) {
        var c = new ComboBox {
            Bounds = new Rectangle(x, y, w, 30), DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Standard, BackColor = Color.White, Font = Ui.Body
        };
        parent.Controls.Add(c);
        return c;
    }
    ToggleCheck Tgl(Control parent, string text, int x, int y, int w, bool on) {
        var c = new ToggleCheck { Text = text, Bounds = new Rectangle(x, y, w, 26), Checked = on };
        parent.Controls.Add(c);
        return c;
    }
    ModernButton Btn(Control parent, string text, int x, int y, int w, int h, ModernButton.Kind kind, Color surface) {
        var b = new ModernButton {
            Text = text, Bounds = new Rectangle(x, y, w, h), StyleKind = kind,
            BackColor = surface, Font = Ui.Body
        };
        parent.Controls.Add(b);
        return b;
    }
    ListBox ListBoxAt(Control parent, int x, int y, int w, int h) {
        var l = new ListBox {
            Bounds = new Rectangle(x, y, w, h), BorderStyle = BorderStyle.None,
            IntegralHeight = false, Font = Ui.Body, BackColor = Color.FromArgb(248, 250, 252),
            ForeColor = Ui.Text, HorizontalScrollbar = false
        };
        parent.Controls.Add(l);
        return l;
    }

    // ---- 页面 1：外观 ----
    Panel BuildAppearanceSection() {
        var sec = new Panel { Width = ContentW, Height = 10, BackColor = Ui.Bg };
        int y = 0;

        var c1 = AddCard(sec, ref y, "图标样式", "选择托盘图标样式、数字与刷新频率", 208);
        Lbl(c1, "样式", 24, R1, 48, 28);
        cmbStyle = Cmb(c1, 78, R1 - 1, 182);
        cmbStyle.Items.AddRange(new object[] {
            "纯文字", "圆环", "鼠标造型", "电池-横向", "电池-竖向",
            "进度条-横向", "进度条-竖向", "闪电形", "圆形水位", "仪表盘", "六边形", "水滴" });
        cmbStyle.SelectedIndex = StyleToIndex(cfg.IconStyle);
        cmbStyle.SelectedIndexChanged += (s, e) => RenderPreviews();

        Lbl(c1, "环 / 条粗度", 278, R1, 110, 28);
        numThick = Num(c1, 394, R1 - 2, 60, 1, 8, cfg.RingThickness);
        numThick.ValueChanged += (s, e) => RenderPreviews();

        Lbl(c1, "刷新间隔", 468, R1, 92, 28);
        numRefresh = Num(c1, 566, R1 - 2, 66, 1, 3600, cfg.RefreshSeconds);
        Lbl(c1, "秒", 638, R1, 24, 28);

        chkNumber = Tgl(c1, "显示数字", 24, R2 - 1, 110, cfg.ShowNumber);
        chkNumber.CheckedChanged += (s, e) => RenderPreviews();
        chkPercent = Tgl(c1, "带 % 号", 140, R2 - 1, 96, cfg.ShowPercent);
        chkPercent.CheckedChanged += (s, e) => RenderPreviews();
        Muted(c1, "电池内显示数字", 246, R2, 300, 28);

        Lbl(c1, "文字大小", 24, R3, 90, 28);
        numFont = Num(c1, 120, R3 - 2, 60, 60, 160, cfg.FontScale);
        numFont.ValueChanged += (s, e) => { cfg.FontScale = (int)numFont.Value; RenderPreviews(); Program.RefreshTrayIcon(); };
        Lbl(c1, "%", 184, R3, 18, 28);
        Lbl(c1, "描边粗细", 220, R3, 90, 28);
        numOutline = Num(c1, 316, R3 - 2, 50, 0, 4, cfg.NumberOutline);
        numOutline.ValueChanged += (s, e) => { cfg.NumberOutline = (int)numOutline.Value; RenderPreviews(); Program.RefreshTrayIcon(); };
        Lbl(c1, "数字颜色", 378, R3, 90, 28);
        Color ncInit = ColorExt.ParseSafe(cfg.NumberColorHex);
        pnlNumColor = new Panel {
            Bounds = new Rectangle(472, R3 + 1, 44, 24),
            BackColor = ncInit == Color.Empty ? Color.White : ncInit,
            BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand
        };
        pnlNumColor.Click += (s, e) => {
            using (var dlg = new ColorDialog()) {
                dlg.Color = pnlNumColor.BackColor;
                if (dlg.ShowDialog(this) == DialogResult.OK) {
                    pnlNumColor.BackColor = dlg.Color;
                    if (chkAutoNum.Checked) chkAutoNum.Checked = false;   // 选具体颜色=关自动
                    cfg.NumberColorHex = ColorTranslator.ToHtml(dlg.Color);
                    RenderPreviews();
                    Program.RefreshTrayIcon();
                }
            }
        };
        c1.Controls.Add(pnlNumColor);
        // 自动配色：浅色任务栏用近黑、深色任务栏用白（与 Win11 原生电池一致），且自动去掉描边
        chkAutoNum = Tgl(c1, "电池自动", 520, R3 - 1, 124, ColorExt.IsAuto(cfg.NumberColorHex));
        chkAutoNum.CheckedChanged += (s, e) => {
            cfg.NumberColorHex = chkAutoNum.Checked
                ? "auto" : ColorTranslator.ToHtml(pnlNumColor.BackColor);
            if (chkAutoNum.Checked) pnlNumColor.BackColor = Theme.Foreground;
            RenderPreviews();
            Program.RefreshTrayIcon();
        };

        var c2 = AddCard(sec, ref y, "实时预览", "左侧为当前状态，右侧循环演示充电效果", 196);
        Muted(c2, "当前状态（实时）", 24, 62, 200, 24);
        picLive = new PictureBox {
            Bounds = new Rectangle(24, 88, 322, 84), SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(35, 37, 43)
        };
        picLive.Resize += (s, e) => Ui.RoundControl(picLive, 9);
        Ui.RoundControl(picLive, 9);
        Muted(c2, "充电效果（动画）", 374, 62, 200, 24);
        picSim = new PictureBox {
            Bounds = new Rectangle(374, 88, 322, 84), SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(35, 37, 43)
        };
        picSim.Resize += (s, e) => Ui.RoundControl(picSim, 9);
        Ui.RoundControl(picSim, 9);
        c2.Controls.Add(picLive);
        c2.Controls.Add(picSim);

        sec.Height = y + 2;
        return sec;
    }

    // ---- 页面 2：电量配色 ----
    Panel BuildThresholdSection() {
        var sec = new Panel { Width = ContentW, Height = 10, BackColor = Ui.Bg };
        int y = 0;

        var c3 = AddCard(sec, ref y, "电量阈值与颜色", "双击颜色格修改颜色，按电量区间自动配色", 330);
        dgv = new DataGridView {
            Bounds = new Rectangle(24, R1, 480, 210),
            AllowUserToAddRows = false, AllowUserToDeleteRows = true,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            BorderStyle = BorderStyle.None, BackgroundColor = Color.White,
            GridColor = Color.FromArgb(236, 239, 243), EnableHeadersVisualStyles = false
        };
        dgv.ColumnHeadersHeight = 36;
        dgv.RowTemplate.Height = 32;
        dgv.Font = Ui.Body;
        dgv.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(247, 248, 250);
        dgv.ColumnHeadersDefaultCellStyle.ForeColor = Ui.Muted;
        dgv.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(247, 248, 250);
        dgv.ColumnHeadersDefaultCellStyle.Font = Ui.BodyBold;
        dgv.DefaultCellStyle.BackColor = Color.White;
        dgv.DefaultCellStyle.ForeColor = Ui.Text;
        dgv.DefaultCellStyle.SelectionBackColor = Ui.AccentSoft;
        dgv.DefaultCellStyle.SelectionForeColor = Ui.Text;
        dgv.DefaultCellStyle.Font = Ui.Body;
        dgv.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(252, 253, 254);
        dgv.Columns.Add("min", "下限(%)");
        dgv.Columns.Add("max", "上限(%)");
        dgv.Columns.Add("hex", "颜色");
        foreach (var r in cfg.Ranges) dgv.Rows.Add(r.Min, r.Max, r.ColorHex);
        dgv.CellDoubleClick += DgvCellDoubleClick;
        c3.Controls.Add(dgv);

        var btnAdd = Btn(c3, "添加区间", 520, R1, 176, 38, ModernButton.Kind.Secondary, Color.White);
        btnAdd.Click += (s, e) => dgv.Rows.Add(0, 100, "#FFFFFF");
        var btnDel = Btn(c3, "删除选中", 520, R1 + 48, 176, 38, ModernButton.Kind.Secondary, Color.White);
        btnDel.Click += (s, e) => { if (dgv.CurrentRow != null) dgv.Rows.Remove(dgv.CurrentRow); };
        Muted(c3, "提示：区间 0–100，下限不能大于上限；颜色会实时应用到托盘图标。",
            24, R1 + 222, 672, 26);

        sec.Height = y + 2;
        return sec;
    }

    // ---- 页面 3：鼠标设置（DPI / 轮询率 / 智能轮询） ----
    static readonly int[] PollRates = { 125, 500, 1000, 2000, 4000, 8000 };

    Panel BuildMouseSection() {
        var sec = new Panel { Width = ContentW, Height = 10, BackColor = Ui.Bg };
        int y = 0;

        var c1 = AddCard(sec, ref y, "DPI 档位", "5 个档位，圆圈为当前档位；X/Y 可分开设置；点「应用 DPI」写入鼠标", 400);
        dpiDots = new StageDot[5];
        dpiNums = new NumericUpDown[5];
        dpiNumsY = new NumericUpDown[5];
        dpiLinks = new ToggleCheck[5];
        dpiBars = new DpiSlider[5];
        for (int i = 0; i < 5; i++) {
            int idx = i;
            int ry = 80 + i * 46;
            var dot = new StageDot {
                Number = i + 1, DotColor = StageColor(i),
                Bounds = new Rectangle(24, ry, 38, 32)
            };
            dot.Picked += (s, e) => {
                dpiActiveStage = idx + 1;
                UpdateStageDots();
            };
            c1.Controls.Add(dot); dpiDots[i] = dot;
            if (i == 0) dot.Selected = true;

            var numX = Num(c1, 76, ry - 2, 86, 100, 35000, 1000);
            var numY = Num(c1, 170, ry - 2, 86, 100, 35000, 1000);
            dpiNums[i] = numX; dpiNumsY[i] = numY;
            var link = Tgl(c1, "X=Y", 264, ry - 1, 74, true);
            dpiLinks[i] = link;
            numY.Enabled = false;

            var bar = new DpiSlider {
                Bounds = new Rectangle(348, ry - 1, 282, 30),
                Minimum = 100, Maximum = 35000,
                SliderColor = StageColor(i)
            };
            c1.Controls.Add(bar); dpiBars[i] = bar;
            numX.ValueChanged += (s, e) => {
                if (syncingDpiUi) return;
                int v = (int)numX.Value;
                if (bar.Value != v) bar.Value = v;
                if (link.Checked && numY.Value != v) numY.Value = v;
            };
            numY.ValueChanged += (s, e) => {
                if (syncingDpiUi) return;
                if (link.Checked && numX.Value != numY.Value) numX.Value = numY.Value;
            };
            link.CheckedChanged += (s, e) => {
                if (syncingDpiUi) return;
                if (link.Checked) { numY.Value = numX.Value; numY.Enabled = false; }
                else numY.Enabled = true;
            };
            bar.ValueChanged += (s, e) => {
                if (syncingDpiUi) return;
                if (numX.Value != bar.Value) numX.Value = bar.Value;
                if (link.Checked && numY.Value != bar.Value) numY.Value = bar.Value;
            };
        }
        var btnReadDpi = Btn(c1, "从鼠标读取", 24, 324, 124, 36, ModernButton.Kind.Secondary, Color.White);
        btnReadDpi.Click += (s, e) => LoadMousePage();
        var btnApplyDpi = Btn(c1, "应用 DPI", 160, 324, 124, 36, ModernButton.Kind.Primary, Color.White);
        btnApplyDpi.Click += (s, e) => ApplyDpi();
        lblDpiStatus = Muted(c1, "", 296, 328, 400, 26);

        var c2 = AddCard(sec, ref y, "轮询率", "常规轮询率；点击立即写入鼠标", 168);
        pollRateSel = cfg.PollRate;
        gameRateSel = cfg.GamePollRate;
        pollBtns = new ModernButton[PollRates.Length];
        for (int i = 0; i < PollRates.Length; i++) {
            int hz = PollRates[i];
            var b = Btn(c2, hz.ToString(), 24 + i * 108, 64, 100, 36, ModernButton.Kind.Secondary, Color.White);
            b.Click += (s, e) => SetNormalPollRate(hz);
            pollBtns[i] = b;
        }
        lblPollLive = Muted(c2, "当前轮询率：读取中…", 24, 112, 340, 26);
        Muted(c2, "单位 Hz；8000 需要 HyperPolling 接收器", 380, 112, 320, 26);

        var c3 = AddCard(sec, ref y, "智能轮询率（游戏模式）", "全屏游戏时自动切到游戏轮询率，退出后恢复常规轮询率", 250);
        chkSmartPoll = Tgl(c3, "全屏游戏时智能切换", 24, 60, 320, cfg.SmartPoll);
        chkSmartPoll.CheckedChanged += (s, e) => {
            cfg.SmartPoll = chkSmartPoll.Checked;
            if (!cfg.SmartPoll) {
                if (NativeHid.SetPollRate(cfg.PollRate)) Program.NotifyManualPoll(cfg.PollRate);
                Toast("已关闭智能轮询", true);
            } else {
                Program.CheckSmartPolling(true);
                Toast("已开启智能轮询", true);
            }
            UpdateMouseLive();
        };
        Lbl(c3, "游戏轮询率", 24, 102, 100, 26);
        gamePollBtns = new ModernButton[PollRates.Length];
        for (int i = 0; i < PollRates.Length; i++) {
            int hz = PollRates[i];
            var b = Btn(c3, hz.ToString(), 24 + i * 108, 130, 100, 36, ModernButton.Kind.Secondary, Color.White);
            b.Click += (s, e) => SetGamePollRate(hz);
            gamePollBtns[i] = b;
        }
        Muted(c3, "与 Synapse 的智能切换二选一，避免两边抢设置；本程序运行期间生效。", 24, 176, 660, 24);
        lblSmartState = Muted(c3, "当前状态：桌面 · —", 24, 202, 400, 26);

        // ---- 电源 ----
        var c4 = AddCard(sec, ref y, "电源", "无线节能睡眠时间与低电量阈值；写入鼠标", 340);
        Lbl(c4, "闲置进入睡眠", 24, 64, 160, 26);
        idleSlider = new DpiSlider {
            Bounds = new Rectangle(24, 96, 560, 30), Minimum = 1, Maximum = 15, Value = 15
        };
        c4.Controls.Add(idleSlider);
        lblIdleVal = new Label {
            Text = "15 分钟", Bounds = new Rectangle(596, 94, 100, 26),
            Font = Ui.BodyBold, ForeColor = Ui.AccentDark, BackColor = Color.White
        };
        c4.Controls.Add(lblIdleVal);
        Muted(c4, "1 分钟", 24, 128, 80, 22);
        Muted(c4, "15 分钟", 540, 128, 80, 22);
        Lbl(c4, "低电量阈值", 24, 160, 160, 26);
        lowSlider = new DpiSlider {
            Bounds = new Rectangle(24, 192, 560, 30), Minimum = 5, Maximum = 100, Value = 30
        };
        c4.Controls.Add(lowSlider);
        lblLowVal = new Label {
            Text = "30%", Bounds = new Rectangle(596, 190, 100, 26),
            Font = Ui.BodyBold, ForeColor = Ui.AccentDark, BackColor = Color.White
        };
        c4.Controls.Add(lblLowVal);
        Muted(c4, "5%", 24, 224, 60, 22);
        Muted(c4, "100%", 580, 224, 80, 22);
        var btnReadPower = Btn(c4, "从鼠标读取", 24, 258, 124, 36, ModernButton.Kind.Secondary, Color.White);
        btnReadPower.Click += (s, e) => LoadPower();
        var btnApplyPower = Btn(c4, "应用电源设置", 160, 258, 140, 36, ModernButton.Kind.Primary, Color.White);
        btnApplyPower.Click += (s, e) => ApplyPower();
        lblPowerStatus = Muted(c4, "", 316, 262, 380, 26);
        idleSlider.ValueChanged += (s, e) => lblIdleVal.Text = idleSlider.Value + " 分钟";
        lowSlider.ValueChanged += (s, e) => lblLowVal.Text = lowSlider.Value + "%";

        // ---- 校准 ----
        var c5 = AddCard(sec, ref y, "校准", "非对称中止与抬升/着陆距离（表面校准向导需 Synapse）", 330);
        Lbl(c5, "抬升距离", 24, 64, 160, 26);
        liftSlider = new DpiSlider {
            Bounds = new Rectangle(24, 96, 560, 30), Minimum = 2, Maximum = 26, Value = 19
        };
        c5.Controls.Add(liftSlider);
        lblLiftVal = new Label {
            Text = "19", Bounds = new Rectangle(596, 94, 100, 26),
            Font = Ui.BodyBold, ForeColor = Ui.AccentDark, BackColor = Color.White
        };
        c5.Controls.Add(lblLiftVal);
        Muted(c5, "低 2", 24, 128, 60, 22);
        Muted(c5, "高 26", 580, 128, 80, 22);
        Lbl(c5, "着陆距离", 24, 160, 160, 26);
        landSlider = new DpiSlider {
            Bounds = new Rectangle(24, 192, 560, 30), Minimum = 1, Maximum = 25, Value = 15
        };
        c5.Controls.Add(landSlider);
        lblLandVal = new Label {
            Text = "15", Bounds = new Rectangle(596, 190, 100, 26),
            Font = Ui.BodyBold, ForeColor = Ui.AccentDark, BackColor = Color.White
        };
        c5.Controls.Add(lblLandVal);
        Muted(c5, "低 1", 24, 224, 60, 22);
        Muted(c5, "高 25", 580, 224, 80, 22);
        var btnReadCal = Btn(c5, "从鼠标读取", 24, 258, 124, 36, ModernButton.Kind.Secondary, Color.White);
        btnReadCal.Click += (s, e) => LoadCalibration();
        var btnApplyCal = Btn(c5, "应用校准", 160, 258, 124, 36, ModernButton.Kind.Primary, Color.White);
        btnApplyCal.Click += (s, e) => ApplyCalibration();
        lblCalStatus = Muted(c5, "", 296, 262, 400, 26);
        Muted(c5, "按住左键+右键+滚轮 7 秒可重置鼠标；表面校准向导需 Synapse。",
            24, 300, 660, 22);
        liftSlider.ValueChanged += (s, e) => lblLiftVal.Text = liftSlider.Value.ToString();
        landSlider.ValueChanged += (s, e) => lblLandVal.Text = landSlider.Value.ToString();

        // ---- 高级 ----
        var c6 = AddCard(sec, ref y, "高级", "动态灵敏度与旋转（Synapse 软件功能，仅预览）", 480);
        chkAdvDyn = Tgl(c6, "动态灵敏度", 24, 60, 200, cfg.AdvDyn);
        string[] curveNames = { "经典", "自然", "跳跃", "自定义" };
        curveBtns = new ModernButton[curveNames.Length];
        for (int i = 0; i < curveNames.Length; i++) {
            int idx = i;
            var b = Btn(c6, curveNames[i], 24 + i * 158, 96, 150, 34, ModernButton.Kind.Secondary, Color.White);
            b.Click += (s, e) => { cfg.AdvCurve = idx; if (curvePanel != null) curvePanel.Kind = idx; UpdateCurveButtons(); };
            curveBtns[i] = b;
        }
        curvePanel = new CurvePanel {
            Bounds = new Rectangle(24, 140, 660, 170), Kind = cfg.AdvCurve
        };
        if (cfg.CustomCurve != null && cfg.CustomCurve.Length >= 5)
            curvePanel.CustomY = (float[])cfg.CustomCurve.Clone();
        curvePanel.CurveChanged += (s, e) => cfg.CustomCurve = (float[])curvePanel.CustomY.Clone();
        c6.Controls.Add(curvePanel);
        chkAdvRotate = Tgl(c6, "旋转", 24, 334, 160, cfg.AdvRotate);
        rotateSlider = new DpiSlider {
            Bounds = new Rectangle(24, 366, 500, 30), Minimum = -44, Maximum = 44, Value = cfg.AdvRotateDeg
        };
        c6.Controls.Add(rotateSlider);
        lblRotVal = new Label {
            Text = cfg.AdvRotateDeg + "°", Bounds = new Rectangle(536, 364, 80, 26),
            Font = Ui.BodyBold, ForeColor = Ui.AccentDark, BackColor = Color.White
        };
        c6.Controls.Add(lblRotVal);
        rotateSlider.ValueChanged += (s, e) => {
            cfg.AdvRotateDeg = rotateSlider.Value;
            lblRotVal.Text = rotateSlider.Value + "°";
        };
        chkAdvDyn.CheckedChanged += (s, e) => cfg.AdvDyn = chkAdvDyn.Checked;
        chkAdvRotate.CheckedChanged += (s, e) => cfg.AdvRotate = chkAdvRotate.Checked;
        Muted(c6, "由 Synapse 软件处理；本程序不接管指针，仅保存预览。",
            24, 404, 660, 22);
        var btnMouseProps = Btn(c6, "打开 Windows 鼠标属性", 24, 428, 200, 34, ModernButton.Kind.Secondary, Color.White);
        btnMouseProps.Click += (s, e) => {
            try { System.Diagnostics.Process.Start("rundll32.exe", "shell32.dll,Control_RunDLL main.cpl"); } catch { }
        };
        UpdateCurveButtons();

        UpdatePollButtons();
        sec.Height = y + 2;
        return sec;
    }

    void UpdatePollButtons() {
        if (pollBtns != null) {
            for (int i = 0; i < PollRates.Length; i++) {
                bool on = PollRates[i] == pollRateSel;
                pollBtns[i].StyleKind = on ? ModernButton.Kind.Primary : ModernButton.Kind.Secondary;
                pollBtns[i].Invalidate();
            }
        }
        if (gamePollBtns != null) {
            for (int i = 0; i < PollRates.Length; i++) {
                bool on = PollRates[i] == gameRateSel;
                gamePollBtns[i].StyleKind = on ? ModernButton.Kind.Primary : ModernButton.Kind.Secondary;
                gamePollBtns[i].Invalidate();
            }
        }
    }

    void SetNormalPollRate(int hz) {
        pollRateSel = hz;
        cfg.PollRate = hz;
        // 智能切换 + 当前在游戏全屏：只改常规值，不覆盖游戏轮询率
        if (!cfg.SmartPoll || !Program.LastFullscreen) {
            if (NativeHid.SetPollRate(hz)) { Program.NotifyManualPoll(hz); Toast("应用成功", true); }
            else Toast("应用失败", false);
        } else {
            Toast("已保存常规轮询率", true);
        }
        UpdatePollButtons();
        UpdateMouseLive();
    }

    void SetGamePollRate(int hz) {
        gameRateSel = hz;
        cfg.GamePollRate = hz;
        if (cfg.SmartPoll && Program.LastFullscreen) {
            if (NativeHid.SetPollRate(hz)) { Program.NotifyManualPoll(hz); Toast("应用成功", true); }
            else Toast("应用失败", false);
        } else {
            Toast("已保存游戏轮询率", true);
        }
        UpdatePollButtons();
        UpdateMouseLive();
    }

    void LoadMousePage(bool showToast = true) {
        var st = NativeHid.ReadDpiStages();
        bool ok = st.Online && st.StageCount > 0;
        if (ok) {
            syncingDpiUi = true;
            for (int i = 0; i < 5; i++) {
                bool has = i < st.StageCount;
                dpiDots[i].Visible = has;
                dpiNums[i].Visible = has;
                dpiNumsY[i].Visible = has;
                dpiLinks[i].Visible = has;
                dpiBars[i].Visible = has;
                if (!has) continue;
                int x = Math.Max(100, Math.Min(35000, st.StageX[i]));
                int y = Math.Max(100, Math.Min(35000, st.StageY[i]));
                dpiNums[i].Value = x;
                dpiNumsY[i].Value = y;
                dpiBars[i].Value = x;
                bool linked = x == y;
                dpiLinks[i].Checked = linked;
                dpiNumsY[i].Enabled = !linked;
            }
            dpiActiveStage = Math.Max(1, Math.Min(st.StageCount, st.ActiveStage));
            UpdateStageDots();
            syncingDpiUi = false;
            lblDpiStatus.Text = "已读取 " + st.StageCount + " 个档位";
        } else {
            lblDpiStatus.Text = "读取失败（鼠标未连接）";
        }
        Program.RefreshMouseInfo(true);
        LoadPower(false);
        LoadCalibration(false);
        UpdateMouseHeader();
        UpdateMouseLive();
        if (showToast) Toast(ok ? "获取成功" : "获取失败", ok);
    }

    void ApplyDpi() {
        int[] xs = new int[5], ys = new int[5];
        for (int i = 0; i < 5; i++) {
            xs[i] = (int)dpiNums[i].Value;
            ys[i] = (int)dpiNumsY[i].Value;
        }
        if (NativeHid.SetDpiStages(dpiActiveStage, xs, ys)) {
            LoadMousePage(false);
            lblDpiStatus.Text = "已写入鼠标（当前档位 " + dpiActiveStage + "）";
            Toast("应用成功", true);
        } else {
            lblDpiStatus.Text = "写入失败（鼠标未连接）";
            Toast("应用失败", false);
        }
    }

    void LoadPower(bool showToast = true) {
        var p = NativeHid.ReadPower();
        if (p.Online) {
            if (p.IdleSeconds > 0) {
                int min = (int)Math.Round(p.IdleSeconds / 60.0);
                idleSlider.Value = Math.Max(1, Math.Min(15, min));
            }
            if (p.LowPercent > 0) lowSlider.Value = Math.Max(5, Math.Min(100, p.LowPercent));
            lblPowerStatus.Text = "已读取：睡眠 " + idleSlider.Value + " 分钟，低电量 " + lowSlider.Value + "%";
            if (showToast) Toast("获取成功", true);
        } else {
            lblPowerStatus.Text = "读取失败（鼠标未连接）";
            if (showToast) Toast("获取失败", false);
        }
    }

    void ApplyPower() {
        bool a = NativeHid.SetIdleSeconds(idleSlider.Value * 60);
        bool b = NativeHid.SetLowBatteryPercent(lowSlider.Value);
        if (a && b) {
            lblPowerStatus.Text = "已写入：睡眠 " + idleSlider.Value + " 分钟，低电量 " + lowSlider.Value + "%";
            Toast("应用成功", true);
        } else {
            lblPowerStatus.Text = "写入失败（鼠标未连接）";
            Toast("应用失败", false);
        }
    }

    void LoadCalibration(bool showToast = true) {
        var l = NativeHid.ReadLod();
        if (l.Online) {
            liftSlider.Value = Math.Max(2, Math.Min(26, l.Lift));
            landSlider.Value = Math.Max(1, Math.Min(25, l.Land));
            lblCalStatus.Text = l.Async ? "已读取：非对称中止开启，抬升 " + liftSlider.Value + "，着陆 " + landSlider.Value
                                        : "已读取：固定 LOD（抬升/着陆滑杆仅预览）";
            if (showToast) Toast("获取成功", true);
        } else {
            lblCalStatus.Text = "读取失败（鼠标未连接）";
            if (showToast) Toast("获取失败", false);
        }
    }

    void ApplyCalibration() {
        if (NativeHid.SetAsyncLod(liftSlider.Value, landSlider.Value)) {
            lblCalStatus.Text = "已写入：抬升 " + liftSlider.Value + "，着陆 " + landSlider.Value;
            Toast("应用成功", true);
        } else {
            lblCalStatus.Text = "写入失败（鼠标未连接）";
            Toast("应用失败", false);
        }
    }

    void UpdateCurveButtons() {
        if (curveBtns == null) return;
        for (int i = 0; i < curveBtns.Length; i++) {
            curveBtns[i].StyleKind = (i == cfg.AdvCurve) ? ModernButton.Kind.Primary : ModernButton.Kind.Secondary;
            curveBtns[i].Invalidate();
        }
    }

    // ---- 页面 4：提醒 ----
    Panel BuildNotifySection() {
        var sec = new Panel { Width = ContentW, Height = 10, BackColor = Ui.Bg };
        int y = 0;

        var c1 = AddCard(sec, ref y, "电量提醒", "只在状态发生变化时提醒，不会反复打扰", 180);
        chkLow = Tgl(c1, "低电量弹窗提醒", 24, R1 - 1, 176, cfg.LowBatteryNotify);
        Lbl(c1, "提醒阈值", 230, R1, 84, 28);
        numLow = Num(c1, 320, R1 - 2, 64, 1, 99, cfg.LowBatteryThreshold);
        Lbl(c1, "%", 388, R1, 18, 28);
        Muted(c1, "低于该电量时提醒一次", 418, R1, 260, 28);
        chkFull = Tgl(c1, "充满电弹窗提醒", 24, R2 - 1, 176, cfg.FullBatteryNotify);
        Muted(c1, "充满后提醒可以拔线", 230, R2, 280, 28);
        Muted(c1, "无线连接时才会弹出系统通知，有线充电期间不打扰", 24, R2 + 42, 672, 24);

        var c2 = AddCard(sec, ref y, "启动与更新", "开机后在后台静默运行；自动检查 GitHub 新版本", 232);
        chkAuto = Tgl(c2, "开机自动启动", 24, R1 - 1, 200, cfg.AutoStart);
        Muted(c2, "写入当前用户启动项（HKCU），无需管理员权限；重复启动会自动拦截。", 24, R1 + 44, 672, 24);
        Muted(c2, "静默启动：程序为纯 GUI 进程，开机不会闪现控制台窗口。", 24, R1 + 76, 672, 24);
        chkUpdate = Tgl(c2, "启动时自动检查更新", 24, R1 + 110, 240, cfg.AutoCheckUpdate);
        Muted(c2, "确认后自动下载、校验并安装；升级保留 config\\、log\\ 与全部设置。", 24, R1 + 148, 672, 24);

        sec.Height = y + 2;
        return sec;
    }

    // ---- 页面 4：预估与播报 ----
    Panel BuildEstimateSection() {
        var sec = new Panel { Width = ContentW, Height = 10, BackColor = Ui.Bg };
        int y = 0;

        var c1 = AddCard(sec, ref y, "可用时长预估", "根据最近几段掉电速度推算剩余可用时间", 188);
        chkEst = Tgl(c1, "显示预估可用时长", 24, R1 - 1, 220, cfg.EstimateEnabled);
        Lbl(c1, "按最近", 24, R2, 76, 28);
        numSeg = Num(c1, 104, R2 - 2, 56, 1, 10, cfg.EstimateSegments);
        Lbl(c1, "段掉电耗时平均估算", 168, R2, 240, 28);
        Muted(c1, "样本不足时会显示「采样中…」，不影响电量显示与提醒。", 24, R2 + 44, 672, 24);

        var c2 = AddCard(sec, ref y, "每日定时播报", "每天在指定时间播报当前电量", 200);
        chkDaily = Tgl(c2, "每日定时播报电量", 24, R1 - 1, 190, cfg.DailyReport);
        Lbl(c2, "每天最多", 250, R1, 84, 28);
        numDailyMax = Num(c2, 340, R1 - 2, 52, 1, 5, cfg.DailyReportMax);
        Lbl(c2, "次", 398, R1, 20, 28);
        dailyPickersPanel = new FlowLayoutPanel {
            Bounds = new Rectangle(24, R2 - 2, 672, 40), FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, AutoScroll = false, Margin = new Padding(0), Padding = new Padding(0),
            BackColor = Color.White
        };
        numDailyMax.ValueChanged += (s, e) => RebuildDailyPickers();
        chkDaily.CheckedChanged += (s, e) => RebuildDailyPickers();
        c2.Controls.Add(dailyPickersPanel);
        Muted(c2, "时间选择器会随次数自动增减（最多 5 个）。", 24, R2 + 46, 672, 24);

        sec.Height = y + 2;
        return sec;
    }

    // ---- 页面 5：记录与统计 ----
    Panel BuildRecordsSection() {
        var sec = new Panel { Width = ContentW, Height = 10, BackColor = Ui.Bg };
        int y = 0;

        var btnAll = Btn(sec, "使用记录", 10, 0, 124, 38, ModernButton.Kind.Primary, Ui.Bg);
        btnAll.Click += (s, e) => OpenHistory();
        var btnSleep = Btn(sec, "休眠 / 唤醒", 146, 0, 124, 38, ModernButton.Kind.Secondary, Ui.Bg);
        btnSleep.Click += (s, e) => ShowSleepRecords();
        var btnCharge = Btn(sec, "充电耗时", 282, 0, 112, 38, ModernButton.Kind.Secondary, Ui.Bg);
        btnCharge.Click += (s, e) => ShowChargeStats();
        var btnClear = Btn(sec, "清除记录与采样", 406, 0, 150, 38, ModernButton.Kind.Danger, Ui.Bg);
        btnClear.Click += (s, e) => ClearAllRecords();
        y = 54;

        lblFull = new Label();
        lblEmpty = new Label();
        lblEst = new Label();
        StatCard(sec, 10, y, "最近充满", lblFull);
        StatCard(sec, 254, y, "最近没电", lblEmpty);
        StatCard(sec, 498, y, "预估可用", lblEst);
        y += 92 + 16;

        var cRun = AddCard(sec, ref y, "续航统计", "满电 → 没电的完整使用区间", 210);
        lstRun = ListBoxAt(cRun, 24, 64, 672, 96);
        lblAvgRun = Muted(cRun, "", 24, 168, 672, 24);

        var cEvents = AddCard(sec, ref y, "近期事件", "唤醒 / 休眠 / 充电 / 充满 / 没电等事件（最新在前）", 236);
        lstEvents = ListBoxAt(cEvents, 24, 64, 672, 130);
        lblHist = Muted(cEvents, "", 24, 200, 672, 24);

        sec.Height = y + 2;
        return sec;
    }

    void StatCard(Panel sec, int x, int y, string title, Label value) {
        var c = new CardPanel {
            Title = title, Bounds = new Rectangle(x, y, 232, 92), BackColor = Ui.Bg
        };
        value.Bounds = new Rectangle(18, 52, 196, 28);
        value.AutoSize = false;
        value.TextAlign = ContentAlignment.MiddleLeft;
        value.Font = Ui.Stat;
        value.ForeColor = Ui.Text;
        value.BackColor = Color.White;
        value.AutoEllipsis = true;
        value.Text = "无记录";
        c.Controls.Add(value);
        sec.Controls.Add(c);
    }

    // ---- 页面 6：日志 ----
    Panel BuildLogSection() {
        var sec = new Panel { Width = ContentW, Height = 10, BackColor = Ui.Bg };
        int y = 0;

        var c1 = AddCard(sec, ref y, "运行日志", "当前运行日志大小与归档状态", 184);
        lblLogSize = Lbl(c1, "运行日志当前大小：" + Program.MainLogSizeText(), 24, R1, 672, 26);
        lblArchInfo = Muted(c1, "", 24, R1 + 38, 672, 26);
        Muted(c1, "超过 50MB 自动压缩归档；充满 / 没电事件另存 battery_events.log。", 24, R1 + 76, 672, 24);
        RefreshArchiveInfo();

        var c2 = AddCard(sec, ref y, "归档与清理", "清理运行日志或历史归档文件", 180);
        Lbl(c2, "归档保留", 24, R1, 84, 28);
        numLogKeep = Num(c2, 112, R1 - 2, 70, 0, 3650, cfg.LogKeepDays);
        Lbl(c2, "天", 186, R1, 18, 28);
        Muted(c2, "0 = 永久保留", 210, R1, 160, 28);

        var btnClearLog = Btn(c2, "清空运行日志", 24, R1 + 48, 150, 38, ModernButton.Kind.Secondary, Color.White);
        btnClearLog.Click += (s, e) => {
            try { if (File.Exists(Program.LogPathPublic)) File.WriteAllText(Program.LogPathPublic, ""); } catch { }
            lblLogSize.Text = "运行日志当前大小：0 B";
        };
        var btnClearArch = Btn(c2, "清理所有归档", 186, R1 + 48, 150, 38, ModernButton.Kind.Danger, Color.White);
        btnClearArch.Click += (s, e) => {
            try {
                int n = 0;
                if (Directory.Exists(Program.ArchiveDirPublic)) {
                    foreach (var f in Directory.GetFiles(Program.ArchiveDirPublic)) { File.Delete(f); n++; }
                    Directory.Delete(Program.ArchiveDirPublic);
                }
                RefreshArchiveInfo();
                MessageBox.Show(this, "已清理 " + n + " 个归档文件。", "日志管理");
            } catch (Exception ex) { MessageBox.Show(this, "清理失败：" + ex.Message, "日志管理"); }
        };

        sec.Height = y + 2;
        return sec;
    }

    // ---- 记录动作 ----
    void OpenHistory() {
        HistoryForm.Open(cfg, this, RefreshHistoryLists);
        RefreshHistoryLists();
    }

    void ClearAllRecords() {
        if (MessageBox.Show(this,
            "将清除：使用记录、续航统计、充电耗时、休眠/唤醒记录、电量采样与预估数据。\n\n确定继续吗？",
            "清除记录与采样", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        cfg.History.Clear();
        cfg.LastFullTime = "";
        cfg.LastEmptyTime = "";
        cfg.Samples.Clear();
        cfg.RunStats.Clear();
        cfg.CurrentFullStart = "";
        cfg.ChargeSamples.Clear();
        cfg.ChargeStats.Clear();
        cfg.SleepRecords.Clear();
        cfg.SleepOpen = "";
        Program.ClearSleepState();
        Program.SaveSettings();
        RefreshHistoryLists();
    }

    // 主界面列表 / 统计卡刷新（使用记录弹窗清理后也走这里）
    static string ShortStamp(string s) {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length > 16 ? s.Substring(0, 16) : s;
    }

    void RefreshHistoryLists() {
        if (lstEvents != null) {
            lstEvents.BeginUpdate();
            lstEvents.Items.Clear();
            for (int i = cfg.History.Count - 1; i >= 0; i--) lstEvents.Items.Add(cfg.History[i]);
            lstEvents.EndUpdate();
            if (lblHist != null) lblHist.Text = "共 " + cfg.History.Count + " 条记录（最新在前）";
        }
        RebuildRunStats();
        if (lblFull != null) {
            lblFull.Text = cfg.LastFullTime == "" ? "无记录" : ShortStamp(cfg.LastFullTime);
            lblFull.ForeColor = cfg.LastFullTime == "" ? Ui.Muted : Ui.Text;
        }
        if (lblEmpty != null) {
            lblEmpty.Text = cfg.LastEmptyTime == "" ? "无记录" : ShortStamp(cfg.LastEmptyTime);
            lblEmpty.ForeColor = cfg.LastEmptyTime == "" ? Ui.Muted : Ui.Text;
        }
        if (lblEst != null) {
            string est = cfg.EstimateEnabled ? Program.EstimateRemainingText() : "";
            lblEst.Text = est == "" ? (cfg.EstimateEnabled ? "采样中…" : "已关闭") : est;
            lblEst.ForeColor = est == "" ? Ui.Muted : Ui.AccentDark;
        }
        if (dgv != null) {
            foreach (DataGridViewRow row in dgv.Rows) {
                if (row.IsNewRow) continue;
                try {
                    var c = ColorTranslator.FromHtml(Convert.ToString(row.Cells[2].Value));
                    row.Cells[2].Style.BackColor = c;
                    row.Cells[2].Style.ForeColor = c.GetBrightness() > 0.62 ? Ui.Text : Color.White;
                } catch { }
            }
        }
    }

    // 顶部连接方式条刷新（Program.SyncLinkBar 调用，跨线程安全走 Invoke）
    public void SetLink(string text, Color color) {
        try {
            if (InvokeRequired) { BeginInvoke((Action)(() => SetLink(text, color))); return; }
            if (linkBar != null) {
                linkBar.SetText(text, color);
                LayoutHeader();
            }
        } catch (ObjectDisposedException) { }
    }

    // 续航统计列表回填（最新在前）+ 平均/最长/最短汇总
    void RebuildRunStats() {
        if (lstRun == null) return;
        lstRun.Items.Clear();
        for (int i = cfg.RunStats.Count - 1; i >= 0; i--) {
            var p = cfg.RunStats[i].Split('|');
            if (p.Length < 3) continue;
            long secs;
            if (!long.TryParse(p[2], out secs)) continue;
            lstRun.Items.Add(string.Format("{0}  →  {1}     用了 {2}", p[0], p[1], Program.RunStatText(secs)));
        }
        if (cfg.RunStats.Count == 0) {
            lblAvgRun.Text = cfg.CurrentFullStart == "" ? "暂无完整续航区间（充满后用到没电自动记录）"
                : "当前区间自 " + cfg.CurrentFullStart + " 起统计中…";
        } else {
            var secs = cfg.RunStats.Select(r => { var p = r.Split('|'); long v; long.TryParse(p.Length > 2 ? p[2] : "", out v); return v; }).Where(v => v > 0).ToList();
            if (secs.Count > 0)
                lblAvgRun.Text = string.Format("平均 {0} · 最长 {1} · 最短 {2}（共 {3} 次）",
                    Program.RunStatText((long)secs.Average()), Program.RunStatText(secs.Max()), Program.RunStatText(secs.Min()), secs.Count);
        }
    }

    void RefreshArchiveInfo() {
        try {
            int n = 0; long bytes = 0;
            if (Directory.Exists(Program.ArchiveDirPublic))
                foreach (var f in Directory.GetFiles(Program.ArchiveDirPublic, "log_*")) { n++; bytes += new FileInfo(f).Length; }
            lblArchInfo.Text = n == 0 ? "归档：无（battery_events.log 单独留存充满 / 没电，不参与清理）"
                : string.Format("归档：{0} 个，共 {1:F1} MB", n, bytes / 1048576.0);
        } catch { lblArchInfo.Text = "归档：读取失败"; }
    }

    // 自启：HKCU Run 键直接写 exe 绝对路径（任务管理器能正确显示名称/图标/路径）
    const string RunKeyName = "RazerV3BatteryTray";
    static string VbsPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "start_silent.vbs"); } }
    public static bool SilentStart { get { return false; } }
    static string RegValue() {
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            return key == null ? "" : Convert.ToString(key.GetValue(RunKeyName, ""));
    }

    void SetStart(bool enable, bool silent) {
        if (enable) {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                key.SetValue(RunKeyName, "\"" + Application.ExecutablePath + "\"");
        } else {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                if (key.GetValue(RunKeyName) != null) key.DeleteValue(RunKeyName);
        }
        try { if (File.Exists(VbsPath)) File.Delete(VbsPath); } catch { }
    }

    // 按次数动态生成时间选择器
    void RebuildDailyPickers() {
        if (dailyPickersPanel == null) return;
        dailyPickersPanel.SuspendLayout();
        dailyPickersPanel.Controls.Clear();
        int n = chkDaily.Checked ? (int)numDailyMax.Value : 0;
        for (int i = 0; i < n; i++) {
            var dtp = new DateTimePicker {
                Format = DateTimePickerFormat.Time, ShowUpDown = true, Width = 80,
                Font = Ui.Body, Margin = new Padding(1, 2, 6, 2),
                MinDate = new DateTime(2026, 1, 1, 0, 0, 0), MaxDate = new DateTime(2026, 1, 1, 23, 59, 0)
            };
            int hh = 12, mm = 0;
            if (i < cfg.DailyTimes.Count) {
                var parts = cfg.DailyTimes[i].Split(':');
                int.TryParse(parts[0], out hh); int.TryParse(parts.Length > 1 ? parts[1] : "0", out mm);
            }
            dtp.Value = new DateTime(2026, 1, 1, hh, mm, 0);
            dailyPickersPanel.Controls.Add(dtp);
        }
        dailyPickersPanel.ResumeLayout();
    }

    void DgvCellDoubleClick(object sender, DataGridViewCellEventArgs e) {
        if (e.RowIndex < 0) return;
        if (e.ColumnIndex == 2) {
            using (var dlg = new ColorDialog()) {
                try { dlg.Color = ColorTranslator.FromHtml((string)dgv.Rows[e.RowIndex].Cells[2].Value); } catch { }
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    dgv.Rows[e.RowIndex].Cells[2].Value = ColorTranslator.ToHtml(dlg.Color);
            }
        }
        RenderPreviews();
    }

    List<ColorRange> CollectRanges(out string err) {
        err = null;
        var list = new List<ColorRange>();
        foreach (DataGridViewRow row in dgv.Rows) {
            if (row.IsNewRow) continue;
            int lo, hi;
            if (!int.TryParse(Convert.ToString(row.Cells[0].Value), out lo) ||
                !int.TryParse(Convert.ToString(row.Cells[1].Value), out hi) || lo > hi || lo < 0 || hi > 100) {
                err = "阈值行有误：需为 0-100 且下限不大于上限。";
                return null;
            }
            string hex = Convert.ToString(row.Cells[2].Value);
            if (hex == null || hex.Length == 0) hex = "#FFFFFF";
            try { ColorTranslator.FromHtml(hex); } catch { err = "颜色值无效：" + hex; return null; }
            list.Add(new ColorRange { Min = lo, Max = hi, ColorHex = hex });
        }
        if (list.Count == 0) { err = "至少需要一条阈值规则。"; return null; }
        return list;
    }

    // 实时预览：左当前状态 / 右充电模拟
    void RenderPreviews() {
        if (picLive == null || cmbStyle == null || dgv == null) return;
        string err;
        var ranges = CollectRanges(out err) ?? cfg.Ranges;
        string style = IndexToStyle(cmbStyle.SelectedIndex);
        int fontScale = (int)numFont.Value, thick = Math.Max(2, Math.Min(4, (int)numThick.Value));
        var cur = Program.Current;
        var oLive = new RenderOpts {
            Style = style, ShowNumber = chkNumber.Checked, FontScale = fontScale,
            Thick = (int)numThick.Value, ShowPercent = chkPercent.Checked,
            NumberOutline = (int)numOutline.Value,
            NumberColorHex = chkAutoNum.Checked ? "auto" : ColorTranslator.ToHtml(pnlNumColor.BackColor),
            Pct = cur.Online ? cur.Percent : -1, Charging = cur.Charging,
            AnimPhase = Program.LiveAnimPhase
        };
        var oSim = new RenderOpts {
            Style = style, ShowNumber = chkNumber.Checked, FontScale = fontScale,
            Thick = (int)numThick.Value, ShowPercent = chkPercent.Checked,
            NumberOutline = (int)numOutline.Value,
            NumberColorHex = chkAutoNum.Checked ? "auto" : ColorTranslator.ToHtml(pnlNumColor.BackColor),
            Pct = cur.Online ? cur.Percent : 67, Charging = true,
            AnimPhase = previewPhase
        };
        if (picLive.Image != null) picLive.Image.Dispose();
        if (picSim.Image != null) picSim.Image.Dispose();
        int iw = IconRenderer.CanvasW(style) * 2, ih = IconRenderer.CanvasH(style) * 2;
        picLive.Image = ComposePreview(oLive, ranges, iw, ih);
        picSim.Image = ComposePreview(oSim, ranges, iw, ih);
        string estNow = cfg.EstimateEnabled && cur.Online ? Program.EstimateRemainingText() : "";
        if (lblEst != null) {
            lblEst.Text = estNow == "" ? (cfg.EstimateEnabled ? "采样中…" : "已关闭") : estNow;
            lblEst.ForeColor = estNow == "" ? Ui.Muted : Ui.AccentDark;
        }
        foreach (DataGridViewRow row in dgv.Rows) {
            if (row.IsNewRow) continue;
            try {
                var c = ColorTranslator.FromHtml(Convert.ToString(row.Cells[2].Value));
                row.Cells[2].Style.BackColor = c;
                row.Cells[2].Style.ForeColor = c.GetBrightness() > 0.62 ? Ui.Text : Color.White;
            } catch { }
        }
    }

    static Bitmap ComposePreview(RenderOpts o, List<ColorRange> ranges, int iw, int ih) {
        var bmp = new Bitmap(322, 84, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) {
            // 电池的描边/数字跟随任务栏深浅色：预览底色也跟随，否则黑描边在深色预览上会消失
            bool battery = IconRenderer.IsBatteryStyle(o.Style);
            Color bg = battery
                ? (Theme.LightTaskbar ? Color.FromArgb(244, 244, 248) : Color.FromArgb(32, 32, 36))
                : Color.FromArgb(35, 37, 43);
            g.Clear(bg);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            float scale = Math.Min(300f / iw, 72f / ih);
            float dw = iw * scale, dh = ih * scale;
            using (var icon = IconRenderer.Render(iw, ih, o, ranges))
                g.DrawImage(icon, (322 - dw) / 2f, (84 - dh) / 2f, dw, dh);
        }
        return bmp;
    }

    // 休眠/唤醒记录弹窗
    void ShowSleepRecords() {
        var rows = new List<string>();
        int n = 0;
        for (int i = cfg.SleepRecords.Count - 1; i >= 0; i--) {
            var p = cfg.SleepRecords[i].Split('|');
            if (p.Length < 5) continue;
            long secs; long.TryParse(p[4], out secs);
            int sPct, wPct; int.TryParse(p[1], out sPct); int.TryParse(p[3], out wPct);
            int drain = sPct - wPct;
            rows.Add(string.Format("{0}（{1}% 入睡）  →  {2}（{3}% 唤醒）     休眠 {4}     耗电 {5}%",
                p[0], sPct, p[2], wPct, Program.RunStatText(secs), drain >= 0 ? drain.ToString() : "0"));
            n++;
        }
        if (cfg.SleepOpen != "") {
            var sp = cfg.SleepOpen.Split('|');
            rows.Insert(0, string.Format("● 进行中：{0}（{1}% 入睡，未唤醒——含关机期间，下次上线闭环）",
                sp[0], sp.Length > 1 ? sp[1] : "?"));
        }
        if (rows.Count == 0) rows.Add("暂无记录——鼠标掉线/关机即记入睡，重新连接或下次开机时闭合。");
        string foot = n == 0 ? "" : string.Format("共 {0} 次休眠记录（上限 100，最新在前；关机跨重启自动闭环）", n);
        ShowRecordList("休眠 / 唤醒记录", "每次入睡与唤醒的时刻、电量与休眠耗电", rows, foot);
    }

    // 充电耗时记录弹窗
    void ShowChargeStats() {
        var rows = new List<string>();
        var secs = new List<long>();
        for (int i = cfg.ChargeStats.Count - 1; i >= 0; i--) {
            var p = cfg.ChargeStats[i].Split('|');
            if (p.Length < 4) continue;
            long v; long.TryParse(p[2], out v);
            rows.Add(string.Format("{0}（{1}% 开始充电）  →  {2} 充满     充电耗时 {3}",
                p[0], p[3], p[1], Program.RunStatText(v)));
            if (v > 0) secs.Add(v);
        }
        if (secs.Count == 0) rows.Add("暂无完整充电记录——从开始充电到充满自动计时（秒级）。");
        string foot = secs.Count == 0 ? "" : string.Format("共 {0} 次完整充电，平均耗时 {1}（精确到秒，最新在前）",
            secs.Count, Program.RunStatText((long)secs.Average()));
        ShowRecordList("充电耗时记录", "从开始充电到充满的完整计时", rows, foot);
    }

    void ShowRecordList(string title, string subtitle, List<string> rows, string footer) {
        using (var f = new RecordListForm(title, subtitle, rows, footer)) f.ShowDialog(this);
    }

    void Apply() {
        string err;
        var ranges = CollectRanges(out err);
        if (ranges == null) { MessageBox.Show(this, err, "设置有误"); return; }
        cfg.Ranges = ranges;
        cfg.IconStyle = IndexToStyle(cmbStyle.SelectedIndex);
        cfg.RefreshSeconds = (int)numRefresh.Value;
        cfg.FontScale = (int)numFont.Value;
        cfg.RingThickness = (int)numThick.Value;
        cfg.ShowNumber = chkNumber.Checked;
        cfg.ShowPercent = chkPercent.Checked;
        cfg.AutoStart = chkAuto.Checked;
        cfg.LowBatteryNotify = chkLow.Checked;
        cfg.LowBatteryThreshold = (int)numLow.Value;
        cfg.FullBatteryNotify = chkFull.Checked;
        cfg.NumberOutline = (int)numOutline.Value;
        cfg.NumberColorHex = chkAutoNum.Checked ? "auto" : ColorTranslator.ToHtml(pnlNumColor.BackColor);
        cfg.PollRate = pollRateSel;
        cfg.GamePollRate = gameRateSel;
        cfg.SmartPoll = chkSmartPoll.Checked;
        cfg.AdvDyn = chkAdvDyn.Checked;
        cfg.AdvCurve = curvePanel != null ? curvePanel.Kind : cfg.AdvCurve;
        if (curvePanel != null) cfg.CustomCurve = (float[])curvePanel.CustomY.Clone();
        cfg.AdvRotate = chkAdvRotate.Checked;
        cfg.AdvRotateDeg = rotateSlider != null ? rotateSlider.Value : cfg.AdvRotateDeg;
        cfg.EstimateEnabled = chkEst.Checked;
        cfg.EstimateSegments = (int)numSeg.Value;
        cfg.DailyReport = chkDaily.Checked;
        cfg.DailyReportMax = (int)numDailyMax.Value;
        cfg.AutoCheckUpdate = chkUpdate.Checked;
        cfg.LogKeepDays = (int)numLogKeep.Value;
        cfg.DailyTimes = new List<string>();
        foreach (Control c in dailyPickersPanel.Controls)
            if (c is DateTimePicker)
                cfg.DailyTimes.Add(((DateTimePicker)c).Value.ToString("HH:mm"));
        SetStart(chkAuto.Checked, false);
        Program.SaveSettings();
        if (onApply != null) onApply();
        MessageBox.Show(this, "已保存并应用。", "设置");
    }
}

// ======================= 使用记录弹窗 =======================
class HistoryForm : Form {
    class Entry {
        public DateTime Time = DateTime.MinValue;
        public string Message = "";
        public string Badge = "事件";
        public Color BadgeColor = Ui.Muted;
        public string Raw = "";
    }
    class DateEntry {
        public DateTime? Day;
        public string Title = "";
        public int Count;
        public override string ToString() { return Title; }
    }

    Settings cfg;
    Action onChanged;
    List<Entry> entries = new List<Entry>();
    ListBox dateList;
    FlowLayoutPanel flow;
    Panel scroll;
    Label lblFilter, lblCount;
    ModernButton btnClearAll, btnClearDate;
    DateTime? selectedDay;
    bool suppress;

    public static void Open(Settings cfg, IWin32Window owner, Action onChanged) {
        using (var f = new HistoryForm(cfg, onChanged)) {
            if (owner != null) f.ShowDialog(owner); else f.ShowDialog();
        }
    }

    public HistoryForm(Settings cfg, Action onChanged) {
        this.cfg = cfg;
        this.onChanged = onChanged;
        Text = "使用记录";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Ui.Bg;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Font;
        try { Icon = Util.AppIcon(); } catch { }
        ClientSize = new Size(920, 620);

        var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Ui.Bg };
        var lblTitle = new Label {
            Text = "使用记录", Bounds = new Rectangle(28, 20, 300, 32),
            Font = Ui.H1, ForeColor = Ui.Text, BackColor = Ui.Bg
        };
        var lblSub = new Label {
            Text = "按日期查看设备唤醒、休眠、充电、充满、没电等事件", Bounds = new Rectangle(30, 54, 560, 22),
            Font = Ui.Body, ForeColor = Ui.Muted, BackColor = Ui.Bg
        };
        header.Controls.Add(lblTitle);
        header.Controls.Add(lblSub);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding(24, 0, 24, 12) };
        var left = new CardPanel {
            Dock = DockStyle.Left, Width = 280, Title = "日期", BackColor = Ui.Bg,
            Padding = new Padding(10, 54, 10, 10)
        };
        dateList = new ListBox {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 40, Font = Ui.Body,
            BackColor = Color.White, ForeColor = Ui.Text
        };
        dateList.DrawItem += DateListDraw;
        dateList.SelectedIndexChanged += (s, e) => {
            if (suppress) return;
            var de = dateList.SelectedItem as DateEntry;
            selectedDay = de == null ? null : de.Day;
            RebuildRows();
        };
        left.Controls.Add(dateList);

        var right = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding(16, 0, 0, 0) };
        var bar = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Ui.Bg };
        lblFilter = new Label {
            Text = "全部记录", Bounds = new Rectangle(0, 12, 180, 24),
            Font = Ui.BodyBold, ForeColor = Ui.Text, BackColor = Ui.Bg,
            AutoEllipsis = true, AutoSize = false
        };
        lblCount = new Label {
            Text = "", Bounds = new Rectangle(190, 13, 110, 22),
            Font = Ui.Small, ForeColor = Ui.Muted, BackColor = Ui.Bg,
            AutoSize = false
        };
        btnClearDate = new ModernButton {
            Text = "清除此日期", Bounds = new Rectangle(0, 6, 116, 34),
            StyleKind = ModernButton.Kind.Secondary, BackColor = Ui.Bg,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        btnClearDate.Click += (s, e) => ClearSelectedDay();
        btnClearAll = new ModernButton {
            Text = "清空全部记录", Bounds = new Rectangle(0, 6, 128, 34),
            StyleKind = ModernButton.Kind.Danger, BackColor = Ui.Bg,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        btnClearAll.Click += (s, e) => ClearAll();
        bar.Controls.Add(lblFilter);
        bar.Controls.Add(lblCount);
        bar.Controls.Add(btnClearDate);
        bar.Controls.Add(btnClearAll);
        bar.Resize += (s, e) => {
            btnClearAll.Left = bar.ClientSize.Width - btnClearAll.Width;
            btnClearDate.Left = btnClearAll.Left - 10 - btnClearDate.Width;
        };

        scroll = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, AutoScroll = true };
        flow = new FlowLayoutPanel {
            Location = new Point(0, 0), AutoSize = false, WrapContents = false,
            FlowDirection = FlowDirection.TopDown, BackColor = Ui.Bg, Padding = new Padding(0)
        };
        scroll.Controls.Add(flow);
        scroll.Resize += (s, e) => LayoutRowWidths();
        right.Controls.Add(scroll);
        right.Controls.Add(bar);

        body.Controls.Add(right);
        body.Controls.Add(left);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Ui.Bg };
        var foot = new Label {
            Text = "提示：清空或按日期清除后，设置页的近期事件与统计会同步刷新。",
            Bounds = new Rectangle(28, 18, 640, 22), Font = Ui.Small, ForeColor = Ui.Muted, BackColor = Ui.Bg
        };
        var btnClose = new ModernButton {
            Text = "关闭", Bounds = new Rectangle(0, 11, 96, 36),
            StyleKind = ModernButton.Kind.Secondary, BackColor = Ui.Bg,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        btnClose.Click += (s, e) => Close();
        bottom.Controls.Add(foot);
        bottom.Controls.Add(btnClose);
        bottom.Resize += (s, e) => { btnClose.Left = bottom.ClientSize.Width - 28 - btnClose.Width; };

        Controls.Add(body);
        Controls.Add(bottom);
        Controls.Add(header);

        // 初始按钮位置（Resize 事件也会校正）
        btnClearAll.Left = Math.Max(0, bar.ClientSize.Width - btnClearAll.Width);
        btnClearDate.Left = Math.Max(0, btnClearAll.Left - 10 - btnClearDate.Width);
        btnClose.Left = Math.Max(0, bottom.ClientSize.Width - 28 - btnClose.Width);

        RefreshAll();
        LayoutRowWidths();
        Shown += (s, e) => {
            btnClearAll.Left = Math.Max(0, bar.ClientSize.Width - btnClearAll.Width);
            btnClearDate.Left = Math.Max(0, btnClearAll.Left - 10 - btnClearDate.Width);
            btnClose.Left = Math.Max(0, bottom.ClientSize.Width - 28 - btnClose.Width);
            LayoutRowWidths();
        };
    }

    static bool TryParseEntry(string raw, out DateTime time, out string msg) {
        time = DateTime.MinValue;
        msg = raw == null ? "" : raw;
        if (raw == null || raw.Length < 19) return false;
        if (!DateTime.TryParse(raw.Substring(0, 19), out time)) return false;
        msg = raw.Substring(19).Trim();
        return true;
    }

    static void Classify(string msg, out string badge, out Color color) {
        badge = "事件"; color = Ui.Muted;
        if (msg == null) return;
        if (msg.StartsWith("充满")) { badge = "充满"; color = Ui.AccentDark; }
        else if (msg.StartsWith("没电")) { badge = "没电"; color = Ui.Red; }
        else if (msg.StartsWith("休眠唤醒")) { badge = "唤醒"; color = Ui.Blue; }
        else if (msg.StartsWith("入睡") || msg.StartsWith("关机断开")) { badge = "休眠"; color = Ui.Purple; }
        else if (msg.Contains("充电")) { badge = "充电"; color = Ui.Amber; }
        else if (msg.Contains("耗电")) { badge = "耗电"; color = Ui.Orange; }
        else if (msg.Contains("连接")) { badge = "连接"; color = Ui.AccentDark; }
    }

    static string Weekday(DateTime d) {
        const string w = "日一二三四五六";
        return "周" + w[(int)d.DayOfWeek];
    }

    void LoadEntries() {
        entries.Clear();
        foreach (var raw in cfg.History) {
            DateTime t; string msg;
            bool ok = TryParseEntry(raw, out t, out msg);
            string badge; Color color;
            Classify(msg, out badge, out color);
            entries.Add(new Entry {
                Time = ok ? t : DateTime.MinValue, Message = ok ? msg : (raw == null ? "" : raw),
                Badge = badge, BadgeColor = color, Raw = raw
            });
        }
        entries.Sort((a, b) => b.Time.CompareTo(a.Time));
    }

    void RefreshAll() {
        LoadEntries();
        if (selectedDay != null && !entries.Any(e => e.Time.Date == selectedDay.Value))
            selectedDay = null;

        var dates = entries.Select(e => e.Time.Date).Distinct().OrderByDescending(d => d).ToList();
        suppress = true;
        dateList.BeginUpdate();
        dateList.Items.Clear();
        dateList.Items.Add(new DateEntry { Day = null, Title = "全部记录", Count = entries.Count });
        foreach (var d in dates) {
            dateList.Items.Add(new DateEntry {
                Day = d,
                Title = d == DateTime.MinValue.Date ? "更早记录" : d.ToString("yyyy-MM-dd") + " " + Weekday(d),
                Count = entries.Count(e => e.Time.Date == d)
            });
        }
        int sel = 0;
        if (selectedDay != null) {
            for (int i = 1; i < dateList.Items.Count; i++) {
                var de = (DateEntry)dateList.Items[i];
                if (de.Day == selectedDay) { sel = i; break; }
            }
        }
        dateList.SelectedIndex = sel;
        suppress = false;
        RebuildRows();
    }

    void RebuildRows() {
        if (flow == null) return;
        var de = dateList.SelectedItem as DateEntry;
        selectedDay = de == null ? null : de.Day;
        List<Entry> list = selectedDay == null
            ? entries
            : entries.Where(e => e.Time.Date == selectedDay.Value).ToList();

        flow.SuspendLayout();
        flow.Controls.Clear();
        foreach (var e in list) {
            var row = new HistoryRow {
                Time = e.Time, Message = e.Message, Badge = e.Badge, BadgeColor = e.BadgeColor,
                Height = 68, Margin = new Padding(0, 0, 0, 10), BackColor = Ui.Bg
            };
            flow.Controls.Add(row);
        }
        if (list.Count == 0) {
            var empty = new Label {
                Text = "暂无使用记录 —— 设备唤醒、休眠、充电、充满、没电等事件会自动记录在这里。",
                TextAlign = ContentAlignment.MiddleCenter, Font = Ui.Body, ForeColor = Ui.Muted,
                BackColor = Ui.Bg, Height = 80, Margin = new Padding(0, 20, 0, 0)
            };
            flow.Controls.Add(empty);
        }
        flow.ResumeLayout();
        LayoutRowWidths();

        if (lblFilter != null) {
            lblFilter.Text = selectedDay == null
                ? "全部记录"
                : (selectedDay.Value == DateTime.MinValue.Date ? "更早记录" : selectedDay.Value.ToString("yyyy年M月d日") + " " + Weekday(selectedDay.Value));
            int fw = TextRenderer.MeasureText(lblFilter.Text, lblFilter.Font).Width;
            // 让计数紧跟标题，同时为右侧两个按钮留出空间
            int left = Math.Min(204, fw + 12);
            lblFilter.Width = Math.Max(60, left - 10);
            if (lblCount != null) lblCount.Left = left;
        }
        if (lblCount != null) lblCount.Text = "共 " + list.Count + " 条";
        if (btnClearDate != null) btnClearDate.Enabled = selectedDay != null && list.Count > 0;
        if (btnClearAll != null) btnClearAll.Enabled = entries.Count > 0;
    }

    void LayoutRowWidths() {
        if (flow == null || scroll == null) return;
        int w = scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8;
        if (w < 240) w = 240;
        flow.Width = w;
        int total = 0;
        foreach (Control c in flow.Controls) {
            c.Width = w - 4;
            total += c.Height + c.Margin.Vertical;
        }
        flow.Height = Math.Max(total, 30);
    }

    void DateListDraw(object sender, DrawItemEventArgs e) {
        if (e.Index < 0) return;
        var de = dateList.Items[e.Index] as DateEntry;
        if (de == null) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var g = e.Graphics;
        Ui.Prepare(g);
        using (var b = new SolidBrush(sel ? Ui.AccentSoft : Color.White))
            g.FillRectangle(b, e.Bounds);
        if (sel)
            using (var b = new SolidBrush(Ui.Accent))
                g.FillRectangle(b, e.Bounds.X + 2, e.Bounds.Y + 9, 3, e.Bounds.Height - 18);
        using (var b = new SolidBrush(sel ? Ui.AccentDark : Ui.Text))
            g.DrawString(de.Title, dateList.Font, b, e.Bounds.X + 14, e.Bounds.Y + 10);
        string cs = de.Count + " 条";
        var sz = g.MeasureString(cs, Ui.Small);
        using (var b = new SolidBrush(sel ? Ui.AccentDark : Ui.Faint))
            g.DrawString(cs, Ui.Small, b, e.Bounds.Right - sz.Width - 10, e.Bounds.Y + 13);
    }

    void ClearSelectedDay() {
        if (selectedDay == null) return;
        int n = entries.Count(e => e.Time.Date == selectedDay.Value);
        string label = selectedDay.Value == DateTime.MinValue.Date ? "更早记录" : selectedDay.Value.ToString("yyyy-MM-dd");
        if (MessageBox.Show(this, "确定清除 " + label + " 的 " + n + " 条使用记录吗？\n\n此操作不可撤销。",
            "清除此日期", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        DateTime day = selectedDay.Value;
        cfg.History.RemoveAll(delegate(string raw) {
            DateTime t; string m;
            if (!TryParseEntry(raw, out t, out m)) return false;
            return t.Date == day;
        });
        Program.SaveSettings();
        if (onChanged != null) onChanged();
        RefreshAll();
    }

    void ClearAll() {
        if (entries.Count == 0) return;
        if (MessageBox.Show(this, "确定清空全部 " + entries.Count + " 条使用记录吗？\n\n此操作不可撤销。",
            "清空全部记录", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        cfg.History.Clear();
        Program.SaveSettings();
        if (onChanged != null) onChanged();
        selectedDay = null;
        RefreshAll();
    }
}

// ======================= 通用记录列表弹窗 =======================
class RecordListForm : Form {
    public RecordListForm(string title, string subtitle, List<string> rows, string footer) {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Ui.Bg;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Font;
        try { Icon = Util.AppIcon(); } catch { }
        ClientSize = new Size(840, 540);

        var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Ui.Bg };
        var lblTitle = new Label {
            Text = title, Bounds = new Rectangle(28, 20, 500, 32),
            Font = Ui.H1, ForeColor = Ui.Text, BackColor = Ui.Bg
        };
        var lblSub = new Label {
            Text = subtitle, Bounds = new Rectangle(30, 54, 680, 22),
            Font = Ui.Body, ForeColor = Ui.Muted, BackColor = Ui.Bg
        };
        header.Controls.Add(lblTitle);
        header.Controls.Add(lblSub);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding(24, 0, 24, 12) };
        var card = new CardPanel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding(18, 16, 18, 16) };
        var lst = new ListBox {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false,
            Font = Ui.Body, BackColor = Color.White, ForeColor = Ui.Text, HorizontalScrollbar = true
        };
        if (rows != null && rows.Count > 0) foreach (var r in rows) lst.Items.Add(r);
        else lst.Items.Add("暂无记录。");
        card.Controls.Add(lst);
        body.Controls.Add(card);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Ui.Bg };
        var lblFoot = new Label {
            Text = footer == null ? "" : footer, Bounds = new Rectangle(28, 18, 660, 22),
            Font = Ui.Small, ForeColor = Ui.Muted, BackColor = Ui.Bg, AutoEllipsis = true,
            AutoSize = false
        };
        var btnClose = new ModernButton {
            Text = "关闭", Bounds = new Rectangle(0, 11, 96, 36),
            StyleKind = ModernButton.Kind.Secondary, BackColor = Ui.Bg,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        btnClose.Click += (s, e) => Close();
        bottom.Controls.Add(lblFoot);
        bottom.Controls.Add(btnClose);
        bottom.Resize += (s, e) => { btnClose.Left = bottom.ClientSize.Width - 28 - btnClose.Width; };
        btnClose.Left = Math.Max(0, bottom.ClientSize.Width - 28 - btnClose.Width);

        Controls.Add(body);
        Controls.Add(bottom);
        Controls.Add(header);
    }
}

// ======================= 自动更新 =======================
class UpdateInfo {
    public string Version = "";
    public string Tag = "";
    public string Title = "";
    public string Notes = "";
    public string ReleaseUrl = "";
    public string AssetName = "";
    public string AssetUrl = "";
    public long AssetSize;
    public string Digest = "";
}

static class UpdateChecker {
    public const string Owner = "linglochen";
    public const string Repo = "RazerV3BatteryTray";
    public static readonly Version Current = Assembly.GetExecutingAssembly().GetName().Version;

    public static string CurrentText {
        get { return Current.Major + "." + Current.Minor + "." + Current.Build; }
    }
    public static string ApiUrl {
        get { return "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest"; }
    }

    public static UpdateInfo FetchLatest() {
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        var req = (HttpWebRequest)WebRequest.Create(ApiUrl);
        req.UserAgent = "RazerV3BatteryTray/" + CurrentText;
        req.Accept = "application/vnd.github+json";
        req.Timeout = 12000;
        req.ReadWriteTimeout = 12000;
        req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        string json;
        try {
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                json = sr.ReadToEnd();
        } catch (WebException ex) {
            var hr = ex.Response as HttpWebResponse;
            if (hr != null && (int)hr.StatusCode == 403) throw new Exception("GitHub 接口访问过于频繁，请稍后再试。");
            if (hr != null && (int)hr.StatusCode == 404) throw new Exception("仓库还没有发布版本。");
            throw new Exception("无法连接 GitHub：" + ex.Message);
        }
        return ParseLatest(json);
    }

    public static UpdateInfo ParseLatest(string json) {
        var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        if (root == null) throw new Exception("GitHub 返回的数据为空。");
        object o;
        string tag = root.TryGetValue("tag_name", out o) ? Convert.ToString(o) : "";
        if (string.IsNullOrEmpty(tag)) throw new Exception("GitHub 返回的数据格式不正确。");
        var info = new UpdateInfo {
            Tag = tag,
            Version = tag.TrimStart('v', 'V'),
            Title = root.TryGetValue("name", out o) ? Convert.ToString(o) : tag,
            Notes = root.TryGetValue("body", out o) ? Convert.ToString(o) : "",
            ReleaseUrl = root.TryGetValue("html_url", out o) ? Convert.ToString(o)
                : "https://github.com/" + Owner + "/" + Repo + "/releases/latest"
        };
        object assetsObj;
        var assets = root.TryGetValue("assets", out assetsObj)
            ? assetsObj as System.Collections.IEnumerable : null;
        if (assets != null) {
            foreach (var item in assets) {
                var a = item as Dictionary<string, object>;
                if (a == null) continue;
                string name = a.ContainsKey("name") ? Convert.ToString(a["name"]) : "";
                if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;
                info.AssetName = name;
                info.AssetUrl = a.ContainsKey("browser_download_url") ? Convert.ToString(a["browser_download_url"]) : "";
                if (a.ContainsKey("size")) {
                    long sz;
                    long.TryParse(Convert.ToString(a["size"]), out sz);
                    info.AssetSize = sz;
                }
                info.Digest = a.ContainsKey("digest") ? Convert.ToString(a["digest"]) : "";
                break;
            }
        }
        return info;
    }

    public static bool IsNewer(UpdateInfo info) {
        Version v;
        if (info == null || !Version.TryParse(info.Version, out v)) return false;
        if (v.Major != Current.Major) return v.Major > Current.Major;
        if (v.Minor != Current.Minor) return v.Minor > Current.Minor;
        return v.Build > Current.Build;
    }

    public static string Sha256(string path) {
        using (var sha = SHA256.Create())
        using (var fs = File.OpenRead(path)) {
            var hash = sha.ComputeHash(fs);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}

// 发现新版本：更新说明 + 下载进度 + 自动调用 MSI 安装
class UpdateForm : Form {
    UpdateInfo info;
    bool autoCheck;
    bool downloading;
    bool proxyTried;
    string msiPath;
    WebClient client;
    Panel barBg, barFill;
    Label lblStatus, lblPct;
    ModernButton btnNow, btnLater, btnPage;
    public bool Skipped;

    public UpdateForm(UpdateInfo info, bool autoCheck) {
        this.info = info;
        this.autoCheck = autoCheck;
        Text = "发现新版本";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = Ui.Bg;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(560, 474);
        try { Icon = Util.AppIcon(); } catch { }

        var lblTitle = new Label {
            Text = "发现新版本 v" + info.Version,
            Bounds = new Rectangle(28, 22, 504, 30),
            Font = Ui.Big, ForeColor = Ui.Text, BackColor = Ui.Bg
        };
        var lblSub = new Label {
            Text = "当前版本 v" + UpdateChecker.CurrentText + "   →   最新版本 v" + info.Version,
            Bounds = new Rectangle(30, 56, 504, 24),
            Font = Ui.Body, ForeColor = Ui.Muted, BackColor = Ui.Bg
        };
        var lblNotes = new Label {
            Text = "更新内容",
            Bounds = new Rectangle(30, 94, 200, 22),
            Font = Ui.BodyBold, ForeColor = Ui.Text, BackColor = Ui.Bg
        };
        var notes = new TextBox {
            Text = NormalizeNotes(info.Notes),
            Bounds = new Rectangle(28, 120, 504, 188),
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White,
            ForeColor = Ui.Text, Font = Ui.Small
        };

        barBg = new Panel {
            Bounds = new Rectangle(28, 322, 504, 16), BackColor = Color.FromArgb(232, 236, 240)
        };
        barFill = new Panel {
            Bounds = new Rectangle(0, 0, 0, 16), BackColor = Ui.Accent
        };
        barBg.Controls.Add(barFill);
        lblStatus = new Label {
            Text = string.IsNullOrEmpty(info.AssetUrl) ? "该版本没有 MSI 安装包，请打开发布页下载。" : "准备下载…",
            Bounds = new Rectangle(28, 346, 360, 24),
            Font = Ui.Body, ForeColor = Ui.Muted, BackColor = Ui.Bg
        };
        lblPct = new Label {
            Text = "", Bounds = new Rectangle(420, 346, 112, 24),
            Font = Ui.BodyBold, ForeColor = Ui.AccentDark, BackColor = Ui.Bg,
            TextAlign = ContentAlignment.MiddleRight
        };

        btnPage = new ModernButton {
            Text = "打开发布页", Bounds = new Rectangle(28, 414, 132, 38),
            StyleKind = ModernButton.Kind.Secondary, BackColor = Ui.Bg
        };
        btnPage.Click += (s, e) => {
            try { System.Diagnostics.Process.Start(info.ReleaseUrl); } catch { }
        };
        btnNow = new ModernButton {
            Text = "立即更新", Bounds = new Rectangle(400, 414, 132, 38),
            StyleKind = ModernButton.Kind.Primary, BackColor = Ui.Bg
        };
        btnNow.Click += (s, e) => StartDownload(false);
        btnLater = new ModernButton {
            Text = "稍后", Bounds = new Rectangle(292, 414, 96, 38),
            StyleKind = ModernButton.Kind.Secondary, BackColor = Ui.Bg
        };
        btnLater.Click += (s, e) => { Skipped = true; Close(); };
        if (string.IsNullOrEmpty(info.AssetUrl)) btnNow.Enabled = false;
        AcceptButton = btnNow;
        CancelButton = btnLater;

        var line = new Panel { Bounds = new Rectangle(0, 400, 560, 1), BackColor = Ui.Border };
        Controls.Add(lblTitle);
        Controls.Add(lblSub);
        Controls.Add(lblNotes);
        Controls.Add(notes);
        Controls.Add(barBg);
        Controls.Add(lblStatus);
        Controls.Add(lblPct);
        Controls.Add(line);
        Controls.Add(btnPage);
        Controls.Add(btnNow);
        Controls.Add(btnLater);
        notes.SelectionStart = 0;
        notes.SelectionLength = 0;
    }

    static string NormalizeNotes(string text) {
        if (string.IsNullOrEmpty(text)) return "";
        string s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        s = s.Replace("`", "");
        s = Regex.Replace(s, @"(?m)^\s*#{1,6}\s*", "");
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)]+)\)", "$1: $2");
        s = s.Replace("\n", "\r\n");
        if (s.Length > 6000) s = s.Substring(0, 6000) + "\r\n…";
        return s;
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        try { btnNow.Focus(); } catch { }
    }

    void StartDownload(bool viaProxy) {
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        if (string.IsNullOrEmpty(info.AssetUrl)) {
            MessageBox.Show(this, "该版本没有提供 MSI 安装包，请打开发布页手动下载。", "检查更新",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (viaProxy && string.IsNullOrEmpty(info.Digest)) {
            Fail("直连下载失败，且该版本没有 SHA256 校验值，已停止自动更新。\n请打开发布页手动下载。");
            return;
        }
        downloading = true;
        proxyTried = viaProxy;
        btnNow.Enabled = false;
        btnLater.Enabled = false;
        btnPage.Enabled = false;
        lblStatus.ForeColor = Ui.Muted;
        lblStatus.Text = viaProxy ? "直连较慢，正在尝试加速下载…" : "正在下载…";
        lblPct.Text = "0%";
        barFill.Width = 0;
        try {
            string dir = Path.Combine(Path.GetTempPath(), "RazerV3BatteryTray_Update");
            Directory.CreateDirectory(dir);
            msiPath = Path.Combine(dir, string.IsNullOrEmpty(info.AssetName) ? "update.msi" : info.AssetName);
            if (File.Exists(msiPath)) File.Delete(msiPath);
        } catch (Exception ex) {
            Fail("无法创建下载目录：" + ex.Message);
            return;
        }
        try {
            client = new WebClient();
            client.Headers.Add("User-Agent", "RazerV3BatteryTray/" + UpdateChecker.CurrentText);
            client.DownloadProgressChanged += (s, e) => {
                int p = Math.Max(0, Math.Min(100, e.ProgressPercentage));
                barFill.Width = barBg.Width * p / 100;
                lblPct.Text = p + "%";
                lblStatus.Text = info.AssetSize > 0
                    ? "正在下载… " + FormatSize(e.BytesReceived) + " / " + FormatSize(info.AssetSize)
                    : "正在下载… " + FormatSize(e.BytesReceived);
            };
            client.DownloadFileCompleted += (s, e) => {
                downloading = false;
                if (e.Error != null) {
                    try { if (client != null) client.Dispose(); } catch { }
                    if (!proxyTried && !string.IsNullOrEmpty(info.Digest)) {
                        try { if (File.Exists(msiPath)) File.Delete(msiPath); } catch { }
                        StartDownload(true);
                    } else {
                        Fail("下载失败：" + e.Error.Message + "\n可打开发布页手动下载。");
                    }
                    return;
                }
                lblStatus.Text = "正在校验安装包…";
                string err = VerifyDigest();
                if (err != null) { Fail(err); return; }
                lblStatus.Text = "下载完成，正在启动安装程序…";
                lblPct.Text = "100%";
                try {
                    Program.LaunchUpdater(msiPath, info);
                    Close();
                } catch (Exception ex) {
                    Fail("启动安装程序失败：" + ex.Message);
                }
            };
            string url = viaProxy ? "https://gh-proxy.com/" + info.AssetUrl : info.AssetUrl;
            client.DownloadFileAsync(new Uri(url), msiPath);
        } catch (Exception ex) {
            Fail("下载失败：" + ex.Message);
        }
    }

    string VerifyDigest() {
        try {
            if (string.IsNullOrEmpty(info.Digest) || !info.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                return null;   // 没有校验值时仍走 HTTPS 官方地址
            string want = info.Digest.Substring(7).Trim().ToLowerInvariant();
            string got = UpdateChecker.Sha256(msiPath);
            if (!string.Equals(want, got, StringComparison.OrdinalIgnoreCase)) {
                try { File.Delete(msiPath); } catch { }
                return "安装包 SHA256 校验失败，已取消安装。";
            }
            return null;
        } catch (Exception ex) {
            return "校验安装包失败：" + ex.Message;
        }
    }

    void Fail(string message) {
        downloading = false;
        btnNow.Enabled = !string.IsNullOrEmpty(info.AssetUrl);
        btnLater.Enabled = true;
        btnPage.Enabled = true;
        lblStatus.ForeColor = Ui.Red;
        lblStatus.Text = "更新未完成";
        lblPct.Text = "";
        try { if (client != null) client.Dispose(); } catch { }
        MessageBox.Show(this, message, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    static string FormatSize(long bytes) {
        if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " KB";
        return bytes + " B";
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (downloading) {
            e.Cancel = true;
            return;
        }
        if (e.CloseReason == CloseReason.UserClosing) Skipped = true;
        base.OnFormClosing(e);
    }
}

static class Program {
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    static Settings cfg;
    static string baseDir, logPath, cfgDir, logDir;

    // 旧版文件迁移到标准目录（只在目标不存在时搬，幂等）
    static void MigrateLegacyFiles() {
        try {
            Directory.CreateDirectory(cfgDir);
            Directory.CreateDirectory(logDir);
            var moves = new[] {
                // 旧路径（根目录），新路径
                new[] { Path.Combine(baseDir, "settings.xml"), Path.Combine(cfgDir, "settings.xml") },
                new[] { Path.Combine(baseDir, "start_silent.vbs"), Path.Combine(cfgDir, "start_silent.vbs") },
                new[] { Path.Combine(baseDir, "razer_battery_tray.log"), Path.Combine(logDir, "razer_battery_tray.log") },
                new[] { Path.Combine(baseDir, "battery_events.log"), Path.Combine(logDir, "battery_events.log") },
                new[] { Path.Combine(baseDir, "log_archive"), Path.Combine(logDir, "log_archive") },
            };
            foreach (var m in moves) {
                if (File.Exists(m[0]) && !File.Exists(m[1])) File.Move(m[0], m[1]);
                else if (Directory.Exists(m[0]) && !Directory.Exists(m[1])) Directory.Move(m[0], m[1]);
            }
        } catch { }
    }
    static NotifyIcon tray;
    static Form marshalForm;   // 隐藏载体窗体：后台线程经它 BeginInvoke 回 UI 线程
    static System.Windows.Forms.Timer timer;        // 电量轮询
    static System.Windows.Forms.Timer animTimer;    // 充电动画帧（仅在充电时运行）
    static Mutex mutex;
    static System.Windows.Forms.Timer updateTimer;   // 每 6 小时检查一次 GitHub Release
    static bool updateChecking, updateDialogOpen;

    // 最近一次查询结果 + 动画相位（充电视觉状态由动画定时器驱动，查询结果只作数据源）
    static NativeHid.BatteryResult last = new NativeHid.BatteryResult();
    static int animPhase;
    // 鼠标 DPI/轮询率缓存（悬浮提示、设置页顶栏、鼠标设置页共用；3 秒刷新一次）
    public static NativeHid.MouseInfo mouseInfo = new NativeHid.MouseInfo();
    static DateTime lastMouseInfo = DateTime.MinValue;
    public static bool LastFullscreen { get; private set; }
    static int smartAppliedHz = -1;
    static DateTime smartRetryAt = DateTime.MinValue;
    static System.Windows.Forms.Timer smartTimer;

    // 设置窗实时预览用：当前真实电量/充电状态与托盘动画相位
    public static NativeHid.BatteryResult Current { get { return last; } }
    public static int LiveAnimPhase { get { return animPhase; } }
    // 设置窗预览调用的托盘即时刷新：数字大小/描边/颜色等改动不必保存即看到托盘效果
    public static void RefreshTrayIcon() { RenderTray(); }

    static void SetTrayText(string s) {
        if (s.Length > 63) s = s.Substring(0, 63);
        tray.Text = s;
    }

    public static void RefreshMouseInfo(bool force = false) {
        if (!force && (DateTime.Now - lastMouseInfo).TotalSeconds < 3) return;
        lastMouseInfo = DateTime.Now;
        var m = NativeHid.ReadDpiPoll();
        mouseInfo.Online = m.Online;
        if (m.Online) {
            if (m.DpiX > 0) { mouseInfo.DpiX = m.DpiX; mouseInfo.DpiY = m.DpiY; }
            if (m.PollHz > 0) mouseInfo.PollHz = m.PollHz;
        }
    }

    // 全屏游戏检测：前台窗口覆盖整个屏幕（任务栏也被盖住）且不是桌面/任务栏
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    static bool IsFullscreenGame() {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        var sb = new StringBuilder(64);
        GetClassName(h, sb, 64);
        string cls = sb.ToString();
        if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" ||
            cls == "NotifyIconOverflowWindow" || cls == "Windows.UI.Core.CoreWindow") return false;
        RECT r;
        if (!GetWindowRect(h, out r)) return false;
        var s = Screen.FromHandle(h).Bounds;
        int w = r.Right - r.Left, hh = r.Bottom - r.Top;
        return w >= s.Width - 2 && hh >= s.Height - 2 &&
               r.Left <= s.Left && r.Top <= s.Top && r.Right >= s.Right && r.Bottom >= s.Bottom;
    }

    // 智能轮询率（本程序实现）：全屏游戏时切到游戏轮询率，退出全屏恢复常规轮询率。
    // Synapse 自己的智能切换也能被 ReadDpiPoll 读成“当前轮询率”，两者建议只留一个。
    public static void CheckSmartPolling(bool force = false) {
        if (cfg == null || !cfg.SmartPoll) {
            LastFullscreen = false;
            smartAppliedHz = -1;
            return;
        }
        bool fs = IsFullscreenGame();
        LastFullscreen = fs;
        int want = fs ? cfg.GamePollRate : cfg.PollRate;
        if (want <= 0) return;
        if (!force && want == smartAppliedHz) return;
        if (DateTime.Now < smartRetryAt) return;
        if (NativeHid.SetPollRate(want)) {
            smartAppliedHz = want;
            mouseInfo.PollHz = want;
            mouseInfo.Online = true;
            Log("智能轮询率: " + (fs ? "游戏" : "桌面") + " -> " + want + "Hz");
        } else {
            smartRetryAt = DateTime.Now.AddSeconds(10);
            Log("智能轮询率写入失败: " + want + "Hz（10 秒后重试）");
        }
    }

    public static void NotifyManualPoll(int hz) {
        smartAppliedHz = hz;
        mouseInfo.PollHz = hz;
        mouseInfo.Online = true;
    }

    public static void RestoreNormalPoll() {
        if (cfg != null && cfg.SmartPoll && cfg.PollRate > 0 && mouseInfo.PollHz != cfg.PollRate) {
            NativeHid.SetPollRate(cfg.PollRate);
            mouseInfo.PollHz = cfg.PollRate;
        }
    }

    [STAThread]
    static void Main() {
        bool created;
        mutex = new Mutex(true, "RazerV3BatteryTray_SingleInstance", out created);
        if (!created) { MessageBox.Show("毒蝰 V3 Pro 电量程序已在运行。"); return; }

        baseDir = AppDomain.CurrentDomain.BaseDirectory;
        // 目录标准化：配置进 config\，日志进 log\（旧版散在根目录的文件启动时自动迁移）
        cfgDir = Path.Combine(baseDir, "config");
        logDir = Path.Combine(baseDir, "log");
        MigrateLegacyFiles();
        logPath = Path.Combine(logDir, "razer_battery_tray.log");        cfg = LoadSettings();
        // 升级后修复自启动：旧版 MSI 卸载会删 Run 键（新版升级无法改旧包），
        // 按当前运行 exe 的绝对路径写回，保证"开机自启"设置不因升级丢失
        if (cfg.AutoStart) {
            try {
                using (var runKey = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run"))
                    runKey.SetValue("RazerV3BatteryTray", "\"" + Application.ExecutablePath + "\"");
            } catch { }
        }
        // HID 枚举/查询细节挂钩到主日志：插线/无线切换问题看日志即可判断
        NativeHid.DebugLog = msg => Log("[hid] " + msg);

        Application.EnableVisualStyles();
        // DPI 感知：否则高分屏下托盘按位图强缩，32px 图标被压成 16px 糊掉
        try { SetProcessDPIAware(); } catch { }
        IconRenderer.InitDpi();   // 按实际缩放选托盘画布预案（125-175%→48px 等）
        // 全局异常兜底：任何未处理异常记日志，不让程序带崩
        Application.ThreadException += (s, e) => Log("UI异常: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("未处理异常: " + e.ExceptionObject);

        // 关机/注销断开精准记录：系统会话结束时若鼠标仍在线，记"关机断开"+持久化入睡——
        // 下次开机程序随自启运行、鼠标重新上线时闭合整个关机期间的掉电（跨重启闭环）
        SystemEvents.SessionEnding += (s, e) => {
            try {
                if (last.Online) RecordShutdownOffline();
                SaveSettings();
            } catch { }
        };

        // 隐藏载体窗体：Application.Run 需要一个消息载体，后台线程也经它回 UI 线程
        marshalForm = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized, Visible = false };

        tray = new NotifyIcon {
            Text = "毒蝰 V3 Pro：查询中…",
            Visible = true,
            Icon = Util.AppIcon()
        };
 tray.ContextMenu = new ContextMenu(new MenuItem[] {
 new MenuItem("设置", (s, e) => ShowSettings()),
 new MenuItem("使用记录", (s, e) => HistoryForm.Open(cfg, null, null)),
 new MenuItem("立即刷新", (s, e) => Update()),
 new MenuItem("检查更新", (s, e) => CheckForUpdates(true)),
 new MenuItem("-"),
 new MenuItem("连接方式：--", (s, e) => {}) { Enabled = false, Name = "link" },
 new MenuItem("预估值待采样更新", (s, e) => {}) { Enabled = false, Name = "est" },
 new MenuItem("-"),
 new MenuItem("退出", (s, e) => Quit())
        });
        tray.DoubleClick += (s, e) => ShowSettings();

        timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, cfg.RefreshSeconds) * 1000 };
        timer.Tick += (s, e) => Update();

        // 充电动画：~6.7 帧周期（150ms/帧），涨+闪都靠相位驱动；只在充电态运行
        animTimer = new System.Windows.Forms.Timer { Interval = 150 };
        animTimer.Tick += (s, e) => {
            animPhase = (animPhase + 1) % 8;
            RenderTray();
        };

        // 智能轮询率：1 秒检查一次全屏状态，只在状态变化时写鼠标
        smartTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        smartTimer.Tick += (s, e) => CheckSmartPolling();
        smartTimer.Start();

        // R3 开机自启兼容：系统冷启动时 USB/HID 栈未必就绪，首次查不到设备不放弃，
        // 每 5 秒重试，最多 12 分钟；一旦连上转到正常轮询节奏
        RetryBootstrap();
        StartUpdateTimer();

        Log("启动 v" + Assembly.GetExecutingAssembly().GetName().Version);
        CleanOldArchives();   // 清理超期日志归档（LogKeepDays=0 永久保留）
        // 测试/便捷入口：--settings 直接打开设置窗
        if (Environment.GetCommandLineArgs().Contains("--settings", StringComparer.OrdinalIgnoreCase)) {
            var t = new System.Windows.Forms.Timer { Interval = 800 };
            t.Tick += (s, e) => { t.Stop(); ShowSettings(); };
            t.Start();
        }
        Application.Run(marshalForm);
        Log("退出");
        animTimer.Stop(); timer.Stop();
        mutex.ReleaseMutex();
    }

    static void RetryBootstrap() {
        int attempts = 0;
        var bootTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        bootTimer.Tick += (s, e) => {
            attempts++;
            SafeRead();
            if (last.Online || attempts >= 144) {
                bootTimer.Stop();
                ApplyResult(last);          // 应用最终状态（含事件/采样）
                timer.Start();              // 转入正常轮询
            } else {
                RenderTray();               // 未连上：更新"未连接"图标继续等
            }
        };
        Update();                            // 立即先试一次
        bootTimer.Start();
    }

    static void Quit() {
        timer.Stop(); animTimer.Stop();
        try { if (smartTimer != null) smartTimer.Stop(); } catch { }
        try { if (updateTimer != null) updateTimer.Stop(); } catch { }
        RestoreNormalPoll();   // 退出时把游戏轮询率恢复成常规轮询率，避免鼠标留在 8000Hz
        tray.Visible = false;
        Application.Exit();
    }

    // ---- 自动更新 ----
    static void StartUpdateTimer() {
        var first = new System.Windows.Forms.Timer { Interval = 10000 };
        first.Tick += (s, e) => {
            first.Stop(); first.Dispose();
            CheckForUpdates(false);
        };
        first.Start();
        updateTimer = new System.Windows.Forms.Timer { Interval = 6 * 60 * 60 * 1000 };
        updateTimer.Tick += (s, e) => CheckForUpdates(false);
        updateTimer.Start();
    }

    public static void CheckForUpdates(bool manual) {
        if (updateChecking) return;
        if (!manual) {
            if (cfg == null || !cfg.AutoCheckUpdate) return;
            DateTime last;
            if (DateTime.TryParse(cfg.LastUpdateCheck, out last) &&
                (DateTime.Now - last).TotalHours < 6) return;
            try { if (IsFullscreenGame()) return; } catch { }
        }
        updateChecking = true;
        if (settingsForm != null && !settingsForm.IsDisposed) {
            if (manual) settingsForm.ShowToast("正在检查更新…", true);
            settingsForm.SetUpdateBusy(true);
        } else if (manual && tray != null) {
            tray.ShowBalloonTip(2000, "检查更新", "正在连接 GitHub…", ToolTipIcon.Info);
        }
        ThreadPool.QueueUserWorkItem(_ => {
            UpdateInfo info = null;
            string err = null;
            try { info = UpdateChecker.FetchLatest(); } catch (Exception ex) { err = ex.Message; }
            try {
                marshalForm.BeginInvoke((Action)(() => {
                    updateChecking = false;
                    if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.SetUpdateBusy(false);
                    cfg.LastUpdateCheck = DateTime.Now.ToString("o");
                    SaveSettings();
                    if (err != null) {
                        if (manual) ShowUpdateMessage("检查更新失败：\n\n" + err, "检查更新", MessageBoxIcon.Warning);
                        return;
                    }
                    if (!UpdateChecker.IsNewer(info)) {
                        if (manual) ShowUpdateMessage("当前已是最新版本 v" + UpdateChecker.CurrentText + "。",
                            "检查更新", MessageBoxIcon.Information);
                        return;
                    }
                    if (!manual && cfg.SkippedUpdate == info.Version) return;
                    ShowUpdateDialog(info, !manual);
                }));
            } catch (ObjectDisposedException) { }
        });
    }

    static void ShowUpdateMessage(string text, string title, MessageBoxIcon icon) {
        try {
            if (settingsForm != null && !settingsForm.IsDisposed && settingsForm.Visible)
                MessageBox.Show(settingsForm, text, title, MessageBoxButtons.OK, icon);
            else
                MessageBox.Show(text, title, MessageBoxButtons.OK, icon);
        } catch { }
    }

    static void ShowUpdateDialog(UpdateInfo info, bool autoCheck) {
        if (updateDialogOpen) return;
        updateDialogOpen = true;
        try {
            using (var f = new UpdateForm(info, autoCheck)) {
                if (settingsForm != null && !settingsForm.IsDisposed && settingsForm.Visible)
                    f.ShowDialog(settingsForm);
                else
                    f.ShowDialog();
                if (autoCheck && f.Skipped) {
                    cfg.SkippedUpdate = info.Version;
                    SaveSettings();
                }
            }
        } catch (ObjectDisposedException) { } finally {
            updateDialogOpen = false;
        }
    }

    static string GetInstalledDir() {
        try {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\LanQiao\RazerV3BatteryTray")) {
                string s = k == null ? null : k.GetValue("InstallDir") as string;
                if (!string.IsNullOrEmpty(s) && Directory.Exists(s)) return s.TrimEnd('\\');
            }
        } catch { }
        return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    }

    // 先退出旧进程，再静默升级（MSI 自带 config/log 备份恢复），完成后自动启动新版本
    public static void LaunchUpdater(string msiPath, UpdateInfo info) {
        string target = GetInstalledDir();
        string dir = Path.GetDirectoryName(msiPath);
        string bat = Path.Combine(dir, "apply_update.cmd");
        string log = Path.Combine(dir, "update.log");
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazerV3BatteryTray");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine("set \"MSI=" + msiPath + "\"");
        sb.AppendLine("set \"DST=" + target + "\"");
        sb.AppendLine("set \"FALLBACK=" + fallback + "\"");
        sb.AppendLine("echo [%date% %time%] wait app exit >> \"" + log + "\"");
        sb.AppendLine(":wait");
        sb.AppendLine("tasklist /FI \"IMAGENAME eq RazerV3BatteryTray.exe\" 2>nul | find /I \"RazerV3BatteryTray.exe\" >nul");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  ping -n 2 127.0.0.1 >nul");
        sb.AppendLine("  goto wait");
        sb.AppendLine(")");
        sb.AppendLine("echo [%date% %time%] install >> \"" + log + "\"");
        sb.AppendLine("msiexec /i \"%MSI%\" /passive /norestart INSTALLFOLDER=\"%DST%\"");
        sb.AppendLine("echo [%date% %time%] msiexec rc=%errorlevel% >> \"" + log + "\"");
        sb.AppendLine("if not exist \"%DST%\\RazerV3BatteryTray.exe\" set \"DST=%FALLBACK%\"");
        sb.AppendLine("start \"\" \"%DST%\\RazerV3BatteryTray.exe\"");
        sb.AppendLine("exit /b");
        File.WriteAllText(bat, sb.ToString(), Encoding.Default);
        SaveSettings();
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"") {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            WorkingDirectory = dir
        };
        System.Diagnostics.Process.Start(psi);
        try {
            marshalForm.BeginInvoke((Action)(() => {
                try { tray.Visible = false; } catch { }
                Application.Exit();
            }));
        } catch { Application.Exit(); }
    }

    static SettingsForm settingsForm;

    static void ShowSettings() {
        if (settingsForm == null || settingsForm.IsDisposed) {
            settingsForm = new SettingsForm(cfg, () => {
                timer.Interval = Math.Max(1, cfg.RefreshSeconds) * 1000;
                animPhase = 0;
                RenderTray();
            });
        }
        settingsForm.Show();
        settingsForm.BringToFront();
    }

    // 设置窗打开时同步顶部连接方式条（每轮查询后调用，窗体未开则跳过）
    static void SyncLinkBar(NativeHid.BatteryResult res) {
        if (settingsForm == null || settingsForm.IsDisposed) return;
        try {
            if (!res.Online) settingsForm.SetLink("连接方式：未连接", Color.FromArgb(198, 40, 40));
            else {
                string link = res.Link == NativeHid.Link.Wired ? "有线连接" : "无线连接";
                settingsForm.SetLink(string.Format("连接方式：{0}   当前 {1}{2}", link,
                    res.Percent < 0 ? "电量读取中…" : res.Percent + "%",
                    res.Charging ? "（充电中）" : ""),
                    res.Link == NativeHid.Link.Wired ? Color.FromArgb(21, 101, 192) : Color.FromArgb(0, 150, 90));
            }
        } catch { }
    }

    // 关机断开：系统会话结束（关机/注销）时鼠标随 USB 断电离线——记事件+持久化入睡，
    // 下次开机鼠标上线时 RecordSleepWake 闭合完整关机区间（含掉电）
    static void RecordShutdownOffline() {
        // 鼠标先睡、随后关机：保留更早的入睡开档，别把起点改到关机时刻
        if (cfg.SleepOpen != "" || sleepStart != default(DateTime)) return;
        int pct = last.Online && last.Percent >= 0 ? last.Percent : lastPercent;
        sleepStart = DateTime.Now;
        sleepStartPct = pct;
        cfg.SleepOpen = sleepStart.ToString("yyyy-MM-dd HH:mm:ss") + "|" + Math.Max(0, pct);
        SaveSettings();
        AddHistory(string.Format("关机断开（电量 {0}%，关机期间掉电计入下次唤醒）", pct));
    }

    // ---- 状态跟踪（充满/没电/充电事件记录 + 耗电采样）----
    static bool wasOnline, wasFull, wasCharging, wasInit, wasLowNotified, wasFullPending;
    static bool firstLinkDone;                       // 本次程序进程是否已记过"开机首次连接"
    static int lastPercent;
    static DateTime chargeStart;      // 本次充电开始时刻（充满时闭合进 ChargeStats）
    static int chargeStartPct;        // 本次充电开始电量

    static void Update() {
        // 电量查询要往鼠标收 HID 指令（含 80-250ms 等待），放到线程池执行，
        // 避免阻塞 UI 消息循环；完成后回到 UI 线程更新托盘
        ThreadPool.QueueUserWorkItem(_ => {
            var res = SafeRead();
            if (res == null) return;   // 上一轮还没结束（防止重入叠查询）
            try {
                marshalForm.BeginInvoke((Action)(() => ApplyResult(res)));
            } catch (ObjectDisposedException) { }  // 程序正在退出
        });
    }

    static int reading;   // 0=空闲 1=查询中（Interlocked 防重入）

    static NativeHid.BatteryResult SafeRead() {
        if (Interlocked.Exchange(ref reading, 1) == 1) return null;
        try {
            var res = NativeHid.Read();
            Log(string.Format("查询 online={0} pct={1} charging={2}", res.Online, res.Online ? res.Percent.ToString() : "-", res.Charging));
            last = res;
            return res;
        } catch (Exception ex) {
            Log("查询异常: " + ex);
            return new NativeHid.BatteryResult();
        } finally { Interlocked.Exchange(ref reading, 0); }
    }

    static void ApplyResult(NativeHid.BatteryResult res) {
        // 休眠/唤醒开档：无线接收器常驻，鼠标睡下时 online 仍为 True、只是电量读不到，
        // 旧逻辑只认 online False→True 导致整夜鼠标休眠和早上唤醒都不记录
        TrackSleepWake(res);

        // 电量低提醒：可配置阈值。只在"电量跌破阈值"那一刻弹一次（防每轮轮询重复弹），
        // 重新充电后复位允许下次再弹
        if (res.Online && !res.Charging && res.Percent >= 0 && cfg.LowBatteryNotify &&
            res.Percent <= cfg.LowBatteryThreshold && !wasLowNotified) {
            wasLowNotified = true;
            tray.ShowBalloonTip(5000, "毒蝰 V3 Pro 电量低",
                string.Format("电量仅剩 {0}%（低于提醒阈值 {1}%），请及时充电。", res.Percent, cfg.LowBatteryThreshold),
                ToolTipIcon.Warning);
        }
        if (res.Charging || res.Percent > cfg.LowBatteryThreshold) wasLowNotified = false;

        // 事件记录
        bool fullNow = false;
        if (res.Online) {
            if (!wasInit) { /* 首次查询不产生事件 */ }
            else {
        // 满电判定：连续两轮读到 ≥99 才算满（真充满会持续多轮；单次电量尖峰/异常 255 只出现一轮——
        // 23:31 误报由此防掉）。≥99 连续两轮 OR 充电中路径，覆盖固件电压钳制报不满 100。
        // 满电只在充电态判定（2026-10-13 修复）：拔线后电量缓冲期仍报 100，若允许非充电态"
        // 充满"会连续刷满电日志；"充满"语义 = 充电过程的终点
                fullNow = res.Charging && wasFullPending && res.Percent >= 99;
                wasFullPending = res.Percent >= 99;
                if (fullNow && !wasFull) {
                    cfg.LastFullTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    // 续航统计开档：本次满电时刻（待没电时闭合区间，精确到秒）
                    cfg.CurrentFullStart = cfg.LastFullTime;
                    AddHistory("充满电（" + res.Percent + "%）");
                    // 充电耗时闭合：开始充电 → 充满的完整过程
                    if (chargeStart != default(DateTime)) {
                        long cost = (long)(DateTime.Now - chargeStart).TotalSeconds;
                        cfg.ChargeStats.Add(chargeStart.ToString("yyyy-MM-dd HH:mm:ss") + "|" +
                            cfg.LastFullTime + "|" + cost + "|" + chargeStartPct);
                        while (cfg.ChargeStats.Count > 100) cfg.ChargeStats.RemoveAt(0);
                        AddHistory("本次充电耗时 " + RunStatText(cost) + "（" + chargeStartPct + "% → 100%）");
                        chargeStart = default(DateTime);
                    }
                    bool wired = res.Link == NativeHid.Link.Wired;
                    if (cfg.FullBatteryNotify && !wired)
                        tray.ShowBalloonTip(5000, "毒蝰 V3 Pro 已充满",
                            string.Format("电池已充满（{0}%），可以拔掉充电线了。", res.Percent), ToolTipIcon.Info);
                }
                // 拔线瞬间（充电→不充电）即开始放电：立即结束满电态；续航统计起点 =
                // 拔线时刻（用户口径：从充到100拔掉供电开始算可用时长），即便电量还
                // 显示 100（电池缓冲），物理上已在耗电——满电状态不得延续到放电阶段
                if (!res.Charging && wasCharging && fullNow) {
                    cfg.CurrentFullStart = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");   // 起点改记拔线时刻
                    AddHistory(string.Format("拔线开始耗电（{0}%）", res.Percent));
                    fullNow = false;   // 满电态即刻终结
                }
                if (res.Percent >= 0 && res.Percent < 100 && wasFull && !fullNow) AddHistory(string.Format("开始耗电（{0}%）", res.Percent));
                if (res.Charging && !wasCharging) {
                    AddHistory(string.Format("开始充电（{0}%）", res.Percent));
                    chargeStart = DateTime.Now;      // 充电耗时开档
                    chargeStartPct = res.Percent;
                }
                if (!res.Charging && wasCharging) AddHistory(string.Format("停止充电（{0}%）", res.Percent));
            }
            wasFull = fullNow;
            wasCharging = res.Charging;
            if (res.Percent >= 0) lastPercent = res.Percent;
            RecordSample(res);   // 采样供预估时长（内含充电采样分流）
            // 唤醒闭环：会话内入睡（内存态）或跨程序重启的遗留入睡（持久化）都闭合。
            // 电量未知（-1）不算醒来——等第一次读到有效电量再闭环，避免记录"醒来 -1%"
            if (res.Percent >= 0 && (sleepStart != default(DateTime) || cfg.SleepOpen != "")) {
                RecordSleepWake(res.Percent);
                firstLinkDone = true;   // 已经记过唤醒，同一次上线不再重复"开机首次连接"
            } else if (!firstLinkDone && res.Percent >= 0) {
                firstLinkDone = true;
                // 本次程序启动后首次连上鼠标 = 开机/重连首次连接（用户要求精准记录）
                AddHistory(string.Format("开机首次连接（电量 {0}%，{1}）", res.Percent,
                    res.Link == NativeHid.Link.Wired ? "有线" : "2.4G"));
            }
        } else {
            if (wasInit && wasOnline) {
                // 掉线：若之前电量已经处于最低阈值以下，记为“没电”
                int lowest = cfg.Ranges.Min(r => r.Min);
                if (lastPercent <= lowest) {
                    cfg.LastEmptyTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    AddHistory(string.Format("没电断开（最后电量 {0}%）", lastPercent));
                    // 续航统计闭合：满电 → 没电的完整区间（秒级精度）
                    CloseRunStat(cfg.LastEmptyTime, lastPercent);
                } else {
                    // 非没电掉线 = 休眠开始：入睡时刻+电量持久化，待唤醒闭合（跨重启）
                    RecordSleepOffline(lastPercent, "设备掉线");
                }
            }
        }
        wasOnline = res.Online;
        if (!wasInit) wasInit = true;

        // 充电动画：连上且充电中才跑帧；其他状态停帧
        if (res.Online && res.Charging) {
            if (!animTimer.Enabled) { animPhase = 0; animTimer.Start(); }
        } else animTimer.Stop();

        RenderTray();
        RefreshMouseInfo();

        // 悬浮提示：设备 / DPI+轮询率 / 连接+电量 / 预估；NotifyIcon.Text 上限 63 字符
        // 注意：res.Percent==-1 表示"在线但电量未知"（有线插线切换中）
        if (!res.Online) {
            SetTrayText("毒蝰 V3 Pro\n未连接");
        } else {
            string link = res.Link == NativeHid.Link.Wired ? "有线" : "无线";
            // 充电中 → 充满预估；非充电 → 可用时长预估
            string est = res.Charging
                ? (res.Percent >= 100 || res.Percent < 0 ? "已充满"
                    : (cfg.EstimateEnabled ? EstimateChargeText() : "充电中"))
                : (cfg.EstimateEnabled ? EstimateRemainingText() : "");
            string pctLine = res.Charging
                ? link + " · 电量 " + (res.Percent < 0 ? "读取中…" : res.Percent + "%") + " 充电中"
                : link + " · 电量 " + (res.Percent < 0 ? "读取中…" : res.Percent + "%");
            string estLine = est == "" ? (res.Charging ? "估时 采样中…" : "预估 采样中…")
                : (res.Charging ? "估时 " + est : "预估 " + est);
            string mouseLine = mouseInfo.Online && mouseInfo.PollHz > 0
                ? "DPI " + mouseInfo.DpiX + " · 轮询率 " + mouseInfo.PollHz + "Hz"
                : "DPI — · 轮询率 —";
            SetTrayText("毒蝰 V3 Pro\n" + mouseLine + "\n" + pctLine + "\n" + estLine);
            var estItem = tray.ContextMenu.MenuItems.Find("est", false);
            foreach (var mi in estItem)
                mi.Text = res.Charging
                    ? (res.Percent >= 100 ? "已充满（" + ChargeCostText() + "）"
                      : (est == "" || !cfg.EstimateEnabled ? "充电中，估时采样中…" : est))
                    : (!cfg.EstimateEnabled ? "预估可用时长：已关闭" :
                       (est == "" ? "预估可用时长：采样中…" : "预估可用时长：" + est));
            var linkItem = tray.ContextMenu.MenuItems.Find("link", false);
            foreach (var mi in linkItem)
                mi.Text = "连接方式：" + (res.Link == NativeHid.Link.Wired ? "有线连接" : "无线连接");
        }
        // 每日定时播报：到达设定时间（HH:mm）弹当前电量。当日实际弹出次数不超过上限；
        // 跨天自动重置；错过的时间点（程序没在跑）跳过不补弹
        if (cfg.DailyReport && cfg.DailyTimes.Count > 0)
            CheckDailyReport(res);
        SyncLinkBar(res);   // 设置窗打开时刷新顶部连接方式条
    }

    static DateTime lastReportDay = DateTime.MinValue;   // 播报计数所属日
    static int reportCountToday;                          // 当日已弹出次数
    static List<string> firedTimes = new List<string>();  // 当日已触发过的时间点

    static void CheckDailyReport(NativeHid.BatteryResult res) {
        DateTime now = DateTime.Now;
        if (now.Date != lastReportDay.Date) {
            lastReportDay = now.Date;
            reportCountToday = 0;
            firedTimes.Clear();
        }
        if (reportCountToday >= cfg.DailyReportMax) return;
        string hm = now.ToString("HH:mm");
        foreach (var t in cfg.DailyTimes) {
            string ts = t.Trim();
            if (ts.Length != 5) continue;
            if (!firedTimes.Contains(ts) && ts == hm) {
                firedTimes.Add(ts);
                reportCountToday++;
                if (res.Online)
                    tray.ShowBalloonTip(5000, "毒蝰 V3 Pro 电量播报",
                        string.Format("当前电量 {0}%{1}", res.Percent, res.Charging ? "（充电中）" : ""),
                        ToolTipIcon.Info);
                return;
            }
        }
    }

    // 托盘图标重绘（查询结果与动画相位共用入口）
    // HICON 泄漏修复（原卡死根源）：每次 GetHicon 的新句柄赋给 tray 后，
    // 立刻销毁上一帧句柄。净持有一个句柄，充电动画连跑数小时也只占 1 个 GDI 槽。
    static IntPtr lastHicon = IntPtr.Zero;
    static int lastLoggedW = -1;

    // 双托盘已移除：数字始终和电池画在同一个图标里（任务栏两图标之间是系统固定槽距，
    // 再优化也贴不近，反而又远又小）

    static void RenderTray() {
        try {
            var o = cfg.ToOpts(animPhase);
            o.Pct = last.Online ? last.Percent : -1;
            o.Charging = last.Charging;

            int w, h;
            if (IconRenderer.IsBatteryStyle(o.Style)) {
                // 电池按托盘图标实际像素 1:1 渲染（150% 屏=24px）：系统不再缩放，
                // 描边、正极帽、电池内数字都保持清晰
                w = h = IconRenderer.TrayIconPx;
            } else {
                // 其他样式保持原 DPI 预案画布（圆环等外观不动）
                int baseW = (int)Math.Round(IconRenderer.CanvasW(o.Style) * IconRenderer.DpiScale);
                int baseH = (int)Math.Round(IconRenderer.Canvas * IconRenderer.DpiScale);
                w = Math.Min(baseW * 2, 44);
                h = Math.Min(baseH * 2, 44);
            }
            IntPtr hNew;
            using (var bmp = IconRenderer.Render(w, h, o, cfg.Ranges))
                hNew = bmp.GetHicon();
            IntPtr hOld = lastHicon;
            tray.Icon = Icon.FromHandle(hNew);
            lastHicon = hNew;
            if (hOld != IntPtr.Zero) IconRenderer.DestroyIcon(hOld);
            if (w != lastLoggedW) { Log(string.Format("托盘图标画布 {0}x{1}", w, h)); lastLoggedW = w; }
        } catch (Exception ex) { Log("图标渲染异常: " + ex.Message); }
    }

    // ---- 预估可用时长：非充电状态下记录 (时间, 电量) 采样；把最近 3 天样本切成
    // "连续掉电段"（电量下降 ≥1% 即成段），取最近 N 段（cfg.EstimateSegments，默认 3）
    // 平均出 小时/% 耗电速率，再乘当前电量。样本上限 200 条。
    static void RecordSample(NativeHid.BatteryResult res) {
        if (res.Charging) {
            // 充电采样（估充满时间）：同样只在电量变化时记一条（3 秒轮询全存会 10 分钟滑掉 200 条窗口）
            string lastC = cfg.ChargeSamples.Count > 0 ? cfg.ChargeSamples[cfg.ChargeSamples.Count - 1] : null;
            int lastCPct = -1;
            if (lastC != null) { var lp = lastC.Split('|'); int.TryParse(lp.Length > 1 ? lp[1] : "", out lastCPct); }
            if (lastCPct == res.Percent) return;
            cfg.ChargeSamples.Add(DateTime.Now.ToString("s") + "|" + res.Percent);
            while (cfg.ChargeSamples.Count > 200) cfg.ChargeSamples.RemoveAt(0);
            SaveSettings();
            return;
        }
        if (res.Percent <= 0) return;
        // 掉电采样：电量与上一条不同才记录——1 分钟 20 轮查询若全存，200 条窗口只覆盖
        // 10 分钟平台期，掉电历史全被冲掉（2026-10-17 实测 100→85 仍无预估的根因）。
        // 变化触发后 200 条 ≈ 200 次电量变化，覆盖数周
        string lastS = cfg.Samples.Count > 0 ? cfg.Samples[cfg.Samples.Count - 1] : null;
        int lastPct = -1;
        if (lastS != null) { var lp = lastS.Split('|'); int.TryParse(lp.Length > 1 ? lp[1] : "", out lastPct); }
        if (lastPct == res.Percent) return;
        cfg.Samples.Add(DateTime.Now.ToString("s") + "|" + res.Percent + "|0");
        while (cfg.Samples.Count > 200) cfg.Samples.RemoveAt(0);
        SaveSettings();
    }

    // ---- 充电时长预估：最近 3 天充电采样切"充电段"（回升 ≥1%），按速率估剩余充满时间 ----
    public static string EstimateChargeText() {
        if (!last.Online || !last.Charging || last.Percent >= 100) return "";
        if (cfg.ChargeSamples.Count < 2) return "";
        var pts = new List<KeyValuePair<DateTime, int>>();
        DateTime cutoff = DateTime.Now.AddDays(-3);
        foreach (var s in cfg.ChargeSamples) {
            var parts = s.Split('|');
            if (parts.Length < 2) continue;
            DateTime t; int p;
            if (DateTime.TryParse(parts[0], out t) && int.TryParse(parts[1], out p) && t >= cutoff)
                pts.Add(new KeyValuePair<DateTime, int>(t, p));
        }
        // 取最新一段连续上升区间（最新点在前不方便：采样 append 序即时间序）
        // 从尾往前找单调升段（允许 ≤1% 回落噪声）
        int i2 = pts.Count - 1;
        int j2 = i2;
        while (j2 > 0 && pts[j2 - 1].Value <= pts[j2].Value + 1) j2--;
        double hours = (pts[i2].Key - pts[j2].Key).TotalHours;
        int gain = pts[i2].Value - pts[j2].Value;
        if (hours < 5.0 / 60 || gain < 1) return "";   // 5 分钟内升 ≥1% 才可估
        double pctPerHour = gain / hours;
        double remain = (100 - last.Percent) / pctPerHour;
        if (remain > 24 * 14) return "";
        if (remain >= 1) return string.Format("{0} 小时 {1} 分后充满", (int)remain, (int)((remain % 1) * 60));
        int mins = Math.Max(1, (int)(remain * 60));
        return string.Format("{0} 分钟后充满", mins);
    }

    // 本次充电已进行时长（"3 小时 25 分充满"文案用）
    public static string ChargeCostText() {
        if (chargeStart == default(DateTime)) return "本次充电";
        return "本次充电 " + RunStatText((long)(DateTime.Now - chargeStart).TotalSeconds) + " 充满";
    }

    // ---- 休眠/唤醒使用记录 ----
    // 三条开档路径：①鼠标省电/关机（无线接收器常驻，online=True 但电量读不到）；
    // ②系统睡眠（进程被挂起，轮询出现长空档）；③关机/注销（SessionEnding）。
    // 没电断开不记休眠（那是续航终点，另有事件）。
    static DateTime sleepStart;      // 进程内镜像（与 cfg.SleepOpen 同步，便于快速判空）
    static int sleepStartPct;

    static DateTime lastApplyAt;     // 上一轮结果时刻（检测系统睡眠造成的轮询空档）
    static DateTime unknownSince;    // 本轮"在线但电量读不到"的起点
    static bool sleepTrackStarted;   // 首轮查询只建基线，不判睡眠
    static int lastGoodPct = -1;     // 最近一次有效电量（入睡起点电量用）
    const int SleepGapSeconds = 120;      // 轮询空档 ≥2 分钟 = 系统睡眠/休眠
    const int UnknownSleepSeconds = 90;   // 电量持续读不到 ≥90 秒 = 鼠标休眠

    // 每轮查询先判睡眠/唤醒：这里只负责"开档"，闭环仍走 ApplyResult 里的 RecordSleepWake
    // （必须等读到有效电量才算醒来，否则会记成"醒来 -1%"）
    static void TrackSleepWake(NativeHid.BatteryResult res) {
        DateTime now = DateTime.Now;
        if (!sleepTrackStarted) {
            sleepTrackStarted = true;
            lastApplyAt = now;
            if (res.Online && res.Percent >= 0) lastGoodPct = res.Percent;
            return;
        }

        // ① 系统睡眠/休眠：进程被挂起，轮询出现长空档；起点取实际最后一次轮询时刻
        double gap = (now - lastApplyAt).TotalSeconds;
        if (gap >= SleepGapSeconds) {
            DateTime start = lastApplyAt;
            string reason = "系统睡眠";
            if (unknownSince != default(DateTime) && unknownSince < start) { start = unknownSince; reason = "鼠标休眠"; }
            if (lastGoodPct >= 0 || cfg.SleepOpen != "" || sleepStart != default(DateTime))
                OpenSleep(start, lastGoodPct >= 0 ? lastGoodPct : lastPercent, reason);
        }
        lastApplyAt = now;

        if (!res.Online) { unknownSince = default(DateTime); return; }

        if (res.Percent < 0) {
            // 有线插线瞬间固件报 0（Link=Wired）不是休眠，跳过
            if (res.Link == NativeHid.Link.Wired) { unknownSince = default(DateTime); return; }
            // ② 鼠标休眠：接收器还在（online=True），鼠标本体不应答 → 电量 -1
            if (unknownSince == default(DateTime)) unknownSince = now;
            if ((now - unknownSince).TotalSeconds >= UnknownSleepSeconds &&
                (lastGoodPct >= 0 || cfg.SleepOpen != "" || sleepStart != default(DateTime)))
                OpenSleep(unknownSince, lastGoodPct >= 0 ? lastGoodPct : lastPercent, "鼠标休眠");
            return;
        }

        lastGoodPct = res.Percent;
        unknownSince = default(DateTime);
    }

    // 入睡开档：持久化到 settings（跨程序重启/电脑关机不丢——1.5.x 之前只在内存，
    // 调试重启进程会把入睡状态清掉，休眠记录永远闭不了环）。
    // 已有更早的开档时保留原起点——先鼠标休眠、后关机，不能把入睡时刻改晚。
    static void OpenSleep(DateTime when, int pct, string reason) {
        if (cfg.SleepOpen != "" || sleepStart != default(DateTime)) return;
        if (when > DateTime.Now) when = DateTime.Now;
        sleepStart = when;
        sleepStartPct = Math.Max(0, pct);
        cfg.SleepOpen = when.ToString("yyyy-MM-dd HH:mm:ss") + "|" + sleepStartPct;
        SaveSettings();
        AddHistoryAt(when, string.Format("入睡（{0}，电量 {1}%）", reason, sleepStartPct));
    }

    // 掉线路径（有线拔掉/接收器拔出）：online 直接变 False
    static void RecordSleepOffline(int lastPct, string reason) {
        if (cfg.SleepOpen != "" || sleepStart != default(DateTime)) return;
        OpenSleep(DateTime.Now, lastPct, reason);
    }

    // 唤醒闭环：入睡时刻从持久化读取（哪怕程序中途重启过也能闭合）
    static void RecordSleepWake(int wakePct) {
        if (wakePct < 0) return;   // 电量未知不算醒来
        if (cfg.SleepOpen == "" && sleepStart == default(DateTime)) return;
        // 进程内存态优先（更精确的本会话入睡），否则用持久化遗留（跨重启场景）
        DateTime from = sleepStart != default(DateTime) ? sleepStart : DateTime.MinValue;
        int fromPct = sleepStartPct;
        if (from == DateTime.MinValue) {
            var p = cfg.SleepOpen.Split('|');
            if (!DateTime.TryParse(p[0], out from)) { cfg.SleepOpen = ""; return; }
            int.TryParse(p.Length > 1 ? p[1] : "0", out fromPct);
        }
        long secs = (long)(DateTime.Now - from).TotalSeconds;
        if (secs < 0) secs = 0;   // 时钟回拨保护
        string rec = from.ToString("yyyy-MM-dd HH:mm:ss") + "|" + fromPct + "|" +
                     DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "|" + wakePct + "|" + secs;
        cfg.SleepRecords.Add(rec);
        while (cfg.SleepRecords.Count > 100) cfg.SleepRecords.RemoveAt(0);
        cfg.SleepOpen = "";
        sleepStart = default(DateTime);
        SaveSettings();
        int drain = fromPct - wakePct;
        AddHistory(string.Format("休眠唤醒（入睡 {0}% → 醒来 {1}%，休眠 {2}，耗电 {3}%）",
            fromPct, wakePct, RunStatText(secs), drain >= 0 ? drain.ToString() : "0"));
    }

    // 设置页"清除记录"调用：复位进程内入睡镜像（持久化字段已由调用方清）；
    // 设备当前仍在休眠时，从清除这一刻重新开档，不把清除前的时段补回来
    public static void ClearSleepState() {
        sleepStart = default(DateTime);
        sleepStartPct = -1;
        unknownSince = default(DateTime);
    }

    // 把时间序列切成"掉电段"列表（每段 = 电量从 a 掉到 b 的一段时间）。
    // 抖动容差 1%：电量小幅回升（100↔99 边界抖动、换算噪声）不断段——实际掉电速率估算
    // 由此大幅降低断段率（1.3.0 前严格要求单调不升，100→89 掉 11% 都没成段就是它害的）
    static List<double[]> DrainSegments(List<KeyValuePair<DateTime, int>> pts) {
        var segs = new List<double[]>();
        int i = pts.Count - 1;
        while (i > 0) {
            // 从 i 往前找同段起点：允许 ≤1% 的回升噪声，出现 >1% 回升视为充电/换挡断段
            int j = i;
            while (j > 0 && pts[j - 1].Value >= pts[j].Value - 1) j--;
            // 段内真实落差按 (段首最高点 - 段尾) 计，排除抖峰
            int head = int.MinValue;
            for (int k = j; k <= i; k++) head = Math.Max(head, pts[k].Value);
            double hours = (pts[i].Key - pts[j].Key).TotalHours;
            int drop = head - pts[i].Value;
            if (hours >= 5.0 / 60 && drop >= 1)   // ≥5 分钟且净掉电 ≥1% 即成段
                segs.Add(new[] { hours, drop });
            i = j - 1;
            // 跳过回升部分：回溯到下一个下降点（仍是全量回升=充电后的新段）
            while (i > 0 && pts[i - 1].Value < pts[i].Value) i--;
        }
        return segs;
    }

    public static string EstimateRemainingText() {
        if (cfg.Samples.Count < 2 || !last.Online) return "";
        var pts = new List<KeyValuePair<DateTime, int>>();
        DateTime cutoff = DateTime.Now.AddDays(-3);
        foreach (var s in cfg.Samples) {
            var parts = s.Split('|');
            if (parts.Length < 2) continue;
            DateTime t; int p;
            if (DateTime.TryParse(parts[0], out t) && int.TryParse(parts[1], out p) && t >= cutoff)
                pts.Add(new KeyValuePair<DateTime, int>(t, p));
        }
        if (pts.Count < 2) return "";
        pts.Sort((a, b) => a.Key.CompareTo(b.Key));   // append 序不保证严格时间序（重启混入），切段前必须排序
        var segs = DrainSegments(pts);
        int use = Math.Max(1, Math.Min(cfg.EstimateSegments, segs.Count));
        if (segs.Count < 1) return "";
        double hours = 0, drop = 0;
        for (int k = 0; k < use; k++) { hours += segs[k][0]; drop += segs[k][1]; }
        int cur = last.Percent;
        if (hours < 10.0 / 60 || drop < 1) return "";   // 10 分钟跨度即可估算（5 秒刷新下 1% 掉电几分钟就成段）
        double pctPerHour = drop / hours;
        double remainHours = cur / pctPerHour;
        if (remainHours > 720) return "";
        if (remainHours >= 48) return string.Format("{0} 天", (int)(remainHours / 24));
        if (remainHours >= 1) return string.Format("{0} 小时 {1} 分", (int)remainHours, (int)((remainHours % 1) * 60));
        int mins = Math.Max(1, (int)(remainHours * 60));
        return string.Format("{0} 分钟", mins);
    }

    static void AddHistory(string msg) {
        AddHistoryAt(DateTime.Now, msg);
    }

    static void AddHistoryAt(DateTime when, string msg) {
        string entry = when.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
        cfg.History.Add(entry);
        while (cfg.History.Count > 500) cfg.History.RemoveAt(0);   // 使用记录支持按日期回看更长历史
        SaveSettings();
        Log("事件: " + msg);
        // 充满/没电两类事件单独留存（用户要求独立一份，永久不清理）
        if (msg.StartsWith("充满") || msg.StartsWith("没电"))
            AppendFile(BatteryEventsPath, entry);
    }

    static string BatteryEventsPath { get { return Path.Combine(logDir, "battery_events.log"); } }

    // ---- 续航统计：满电时刻 → 没电时刻 的完整区间，秒级精度 ----
    static void CloseRunStat(string emptyTime, int lastPct) {
        if (cfg.CurrentFullStart == "") return;
        DateTime from, to;
        if (!DateTime.TryParse(cfg.CurrentFullStart, out from)) { cfg.CurrentFullStart = ""; return; }
        if (!DateTime.TryParse(emptyTime, out to)) return;
        long secs = (long)(to - from).TotalSeconds;
        if (secs < 0) { cfg.CurrentFullStart = ""; return; }   // 时钟回拨等异常，丢弃区间
        cfg.RunStats.Add(cfg.CurrentFullStart + "|" + emptyTime + "|" + secs);
        while (cfg.RunStats.Count > 100) cfg.RunStats.RemoveAt(0);
        cfg.CurrentFullStart = "";
        SaveSettings();
        Log("续航区间闭合: " + RunStatText(secs) + "（至没电前电量 " + lastPct + "%）");
    }

    // 秒数 → "X天X小时X分X秒"（精确到秒）
    public static string RunStatText(long secs) {
        if (secs < 0) return "-";
        int d = (int)(secs / 86400), h = (int)(secs % 86400 / 3600), m = (int)(secs % 3600 / 60), s = (int)(secs % 60);
        if (d > 0) return string.Format("{0}天{1}小时{2}分{3}秒", d, h, m, s);
        if (h > 0) return string.Format("{0}小时{1}分{2}秒", h, m, s);
        if (m > 0) return string.Format("{0}分{1}秒", m, s);
        return s + "秒";
    }

    // ---- 运行日志归档：主日志 >50MB → cab → tar.gz，旧归档按保留天数清理 ----
    const long ArchiveThreshold = 50L * 1024 * 1024;
    static string ArchiveDir { get { return Path.Combine(logDir, "log_archive"); } }
    public static string MainLogSizeText() {
        try {
            if (File.Exists(logPath)) {
                var len = new FileInfo(logPath).Length;
                return len >= 1024 * 1024 ? string.Format("{0:F1} MB", len / 1048576.0)
                      : len >= 1024 ? string.Format("{0:F0} KB", len / 1024.0) : len + " B";
            }
        } catch { }
        return "0 B";
    }

    static void AppendFile(string path, string line) {
        try { File.AppendAllText(path, line + "\r\n"); } catch { }
    }

    // 日志写入时检查归档（低频触发：每 64 次写检查一次，避免每行 stat）
    static int logWriteCount;
    // 在 Log() 内调用
    static void MaybeArchive() {
        try {
            if (!File.Exists(logPath) || new FileInfo(logPath).Length < ArchiveThreshold) return;
            Directory.CreateDirectory(ArchiveDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string cab = Path.Combine(ArchiveDir, "log_" + stamp + ".cab");
            string tgz = Path.Combine(ArchiveDir, "log_" + stamp + ".tar.gz");
            string tmp = Path.Combine(ArchiveDir, "log_" + stamp + ".log");
            // 先拷出归档副本，再清空主日志（短窗内并发写入会进新日志开头，不丢行）
            File.Copy(logPath, tmp, true);
            File.WriteAllText(logPath, "");
            // 1) makecab 单文件压缩
            RunCmd("makecab", "\"" + tmp + "\" \"" + cab + "\"");
            // 2) tar 打成 tar.gz（Win10+ 自带 bsdtar）
            RunCmd("tar", "-czf \"" + tgz + "\" -C \"" + ArchiveDir + "\" \"" + Path.GetFileName(cab) + "\"");
            if (File.Exists(tgz)) { File.Delete(cab); File.Delete(tmp); }
            else if (File.Exists(cab)) { File.Delete(tmp); }   // tar 失败至少保留 cab
        } catch (Exception ex) { try { Log("归档失败: " + ex.Message); } catch { } }
    }

    static void RunCmd(string exe, string args) {
        try {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args) {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = System.Diagnostics.Process.Start(psi)) {
                p.WaitForExit(30000);
            }
        } catch (Exception ex) { try { Log("执行 " + exe + " 失败: " + ex.Message); } catch { } }
    }

    // 启动时：清理超期归档（文件名内嵌时间戳；LogKeepDays=0 永久保留）
    static void CleanOldArchives() {
        try {
            if (cfg.LogKeepDays <= 0 || !Directory.Exists(ArchiveDir)) return;
            var cutoff = DateTime.Now.AddDays(-cfg.LogKeepDays);
            foreach (var f in Directory.GetFiles(ArchiveDir, "log_*.tar.gz")) {
                string name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(f)); // log_20260101_120000
                var parts = name.Split('_');
                DateTime t;
                if (parts.Length >= 3 && DateTime.TryParse(parts[1] + parts[2].PadLeft(6, '0').Insert(4, ":"), out t)) {
                    if (t < cutoff) { File.Delete(f); Log("清理过期归档: " + Path.GetFileName(f)); }
                }
            }
        } catch { }
    }

    // ---- 设置持久化 ----
    static string SettingsPath { get { return Path.Combine(cfgDir, "settings.xml"); } }
    public static string LogPathPublic { get { return logPath; } }
    public static string ArchiveDirPublic { get { return ArchiveDir; } }

    public static Settings LoadSettings() {
        try {
            if (File.Exists(SettingsPath)) {
                var ser = new XmlSerializer(typeof(Settings));
                using (var fs = File.OpenRead(SettingsPath)) {
                    var s = (Settings)ser.Deserialize(fs);
                    if (s != null) {
                        // 老版本 bug：dgv 每次保存都写入重复行（实测堆到 36 条阈值），
                        // 按 Min+Max+颜色 去重，防止 xml 无限膨胀
                        var seen = new HashSet<string>();
                        var clean = new List<ColorRange>();
                        foreach (var r in s.Ranges)
                            if (seen.Add(r.Min + "-" + r.Max + "-" + r.ColorHex))
                                clean.Add(r);
                        s.Ranges = clean;
                        // v1.7.3：旧的宽幅电池样式并入"电池-横向"（双格方案已移除）
                        if (s.IconStyle == "batPct" || s.IconStyle == "batWin") s.IconStyle = "batH";
                        return s;
                    }
                }
            }
        } catch (Exception ex) { Log("读取设置失败: " + ex.Message); }
        return new Settings();
    }

    public static void SaveSettings() {
        try {
            var ser = new XmlSerializer(typeof(Settings));
            using (var fs = File.Create(SettingsPath)) ser.Serialize(fs, cfg);
        } catch (Exception ex) { Log("保存设置失败: " + ex.Message); }
    }

    static void Log(string msg) {
        try {
            File.AppendAllText(logPath, string.Format("{0:u} {1}\r\n", DateTime.Now, msg));
            // 每 64 次写入检查一次大小（50MB 阈值在设置页即时检查）
            if ((++logWriteCount & 63) == 0) MaybeArchive();
        } catch { }
    }
}


