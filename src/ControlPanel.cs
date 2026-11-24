// T-Dagger Lighting Control Panel
// Lighting control for the T-Dagger Bora Lite, model T-TGK313-2 (Sino Wealth USB 258A:013B), with a live preview.
//
// Safety rules, enforced in Keyboard.Write:
//  - talks only to 258A:013B, vendor collection 0xFF00 with a 42-byte feature report
//  - only ever sends feature report 0x0A (lighting: mode / speed / brightness)
//  - always reads the current 0x0A report first, checks its fixed bytes, and changes only bytes 1, 3, 4
//  - for Game mode only, first sends report 0x0B page 1 exactly as the official app does (the list of
//    keys Game mode lights: W A S D and the arrows); this is a byte-for-byte copy of recorded official traffic
//  - never sends key-mapping (0x09), other 0x0B pages, or any firmware/bootloader command
//  - no network access, no update checks
//
// Build: run build.cmd in the repository root (uses the C# compiler built into Windows, no extra dependencies).
// Unofficial project: not made, endorsed or supported by T-Dagger. Made by Tar. MIT License.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: System.Reflection.AssemblyTitle("T-Dagger Lighting Control Panel")]
[assembly: System.Reflection.AssemblyProduct("T-Dagger Lighting Control Panel")]
[assembly: System.Reflection.AssemblyVersion("3.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("3.2.0.0")]

namespace TDaggerLighting
{
    // ------------------------------------------------------------------ model

    enum Sync { Static, Timed, Keys, Random }

    class Mode
    {
        public string Name; public byte Code; public bool Speed; public bool Brightness; public Sync Sync;
        public double BasePeriod;     // seconds per cycle at speed 3 (estimate until calibrated)
        public string Marker;         // what the user taps on during calibration
        public Mode(string n, byte c, bool s, bool b, Sync sy, double period, string marker)
        { Name = n; Code = c; Speed = s; Brightness = b; Sync = sy; BasePeriod = period; Marker = marker; }
    }

    class LightState
    {
        public byte Mode = 1; public int Brightness = 5; public int Speed = 4;
        public LightState Clone() { var s = new LightState(); s.Mode = Mode; s.Brightness = Brightness; s.Speed = Speed; return s; }
    }

    static class Modes
    {
        public static readonly Mode[] All = new Mode[] {
            new Mode("Steady", 1, false, true, Sync.Static, 0, null),
            new Mode("Breathing", 2, true, true, Sync.Timed, 4.0, "each time the keys are at their brightest"),
            new Mode("Reactive", 3, true, true, Sync.Keys, 0, null),
            new Mode("Neon Stream", 4, true, true, Sync.Timed, 3.0, "each time the bright band passes the Esc key"),
            new Mode("Sweep", 5, true, false, Sync.Timed, 3.0, "each time the light starts again at the left edge"),
            new Mode("Sine Wave", 6, true, false, Sync.Timed, 2.5, "each time the bright band passes the Esc key"),
            new Mode("Snake", 7, true, false, Sync.Timed, 6.0, "each time the snake starts again at Esc"),
            new Mode("Waterfall", 8, true, false, Sync.Timed, 2.5, "each time the light starts again at the top row"),
            new Mode("Ripples", 9, true, true, Sync.Timed, 5.0, null),
            new Mode("Rain", 10, true, true, Sync.Timed, 5.0, null),
            new Mode("Windmill", 11, true, true, Sync.Timed, 2.0, "each time a beam sweeps over the Esc key"),
            new Mode("Game Mode", 12, false, true, Sync.Static, 0, null),
            new Mode("Off", 0, false, false, Sync.Static, 0, null),
        };
        public static Mode ByCode(byte c) { foreach (var m in All) if (m.Code == c) return m; return null; }
        // Relative cycle length per speed step (1 slow .. 5 fast) until calibrated.
        public static readonly double[] SpeedFactor = { 0, 2.0, 1.5, 1.0, 0.72, 0.5 };

        // Seconds per cycle measured on a real T-TGK313-2 (guided calibration, 2026-11-24) at speeds 1, 3 and 5;
        // speeds 2 and 4 are the midpoints (the measured steps are evenly spaced).
        static double[] Cycle(double s1, double s3, double s5) { return new[] { 0, s1, (s1 + s3) / 2, s3, (s3 + s5) / 2, s5 }; }
        static readonly Dictionary<byte, double[]> Measured = new Dictionary<byte, double[]> {
            // Breathing: measured on video at every speed (glow of the whole board over 4-12 cycles per speed).
            { 2, new double[] { 0, 5.84, 4.79, 3.83, 2.795, 1.92 } },
            { 4, Cycle(5.690, 3.839, 1.905) },   // Neon Stream
            // Sweep: one row took 1.069 / 0.814 / 0.455 s (calibration); the full six-row cycle measured 7.06 s on video at speed 1.
            { 5, Cycle(7.06, 7.06 * 0.814 / 1.069, 7.06 * 0.455 / 1.069) },
            { 6, Cycle(5.743, 3.831, 1.918) },   // Sine Wave
            { 7, Cycle(2.945, 2.300, 1.615) },   // Snake (video: 2.956 s)
            { 8, Cycle(5.752, 3.839, 1.922) },   // Waterfall (video: 5.785 s)
            // Ripples: 5.752 s on video at speed 1; other speeds assumed to scale like Waterfall/Windmill.
            { 9, Cycle(5.752, 3.80, 1.87) },
            // Rain: 7.068 s on video at speed 1, same as Sweep; other speeds assumed to scale like Sweep.
            { 10, Cycle(7.068, 7.068 * 0.814 / 1.069, 7.068 * 0.455 / 1.069) },
            { 11, Cycle(5.743, 3.719, 1.924) },  // Windmill (video: 5.764 s)
        };
        public static double MeasuredCycle(byte mode, int speed)
        {
            double[] c; if (speed < 1 || speed > 5 || !Measured.TryGetValue(mode, out c)) return 0; return c[speed];
        }
    }

    // Bora Lite versions that use this USB ID (258A:013B). The keyboard does not report which one it is,
    // so the user picks it; the choice only changes what the app shows, never what it sends.
    enum Body { Black, Gray, White }
    class KbModel
    {
        public string Code, Edition; public Body Body;
        public KbModel(string c, string e, Body b) { Code = c; Edition = e; Body = b; }
        // Header chip: edition name next to a dot in the body color.
        public string Chip { get { return Body == Body.White ? "Rainbow Lite  \u00B7  White" : Body == Body.Gray ? "Rainbow Lite  \u00B7  Gray / Black" : "Rainbow  \u00B7  Black"; } }
        public Color Swatch { get { return Body == Body.White ? Color.FromArgb(236, 238, 241) : Body == Body.Gray ? Color.FromArgb(120, 125, 132) : Color.FromArgb(28, 30, 34); } }
        // Shared by every Bora Lite version this app supports.
        public static readonly string[] Common = { "87-key TKL", "Mechanical", "Rainbow LED", "Wired USB" };
    }
    static class KbModels
    {
        public static readonly KbModel[] All = {
            new KbModel("T-TGK313-2", "Rainbow, black", Body.Black),
            new KbModel("T-TGK313GB-LIT", "Rainbow Lite, gray / black", Body.Gray),
            new KbModel("T-TGK313W-LIT", "Rainbow Lite, white", Body.White),
        };
        public static KbModel ByCode(string c) { foreach (var m in All) if (m.Code == c) return m; return All[0]; }
    }

    // One shared clock for the keyboard commands and the preview animation.
    static class Clock
    {
        static readonly Stopwatch sw = Stopwatch.StartNew();
        public static double Seconds { get { return sw.Elapsed.TotalSeconds; } }
    }

    // --------------------------------------------------------------- hardware

    static class Keyboard
    {
        const ushort VID = 0x258A, PID = 0x013B;
        const byte REPORT_ID = 0x0A;
        const int REPORT_LEN = 42;
        public static double LastSendSeconds;   // Clock time of the last lighting command the keyboard accepted

        // Game-mode key list, recorded from the official T-Dagger app (sent right before it selects Game mode).
        // Bytes: report 0x0B, page 1, value 7 at the LED slots of A, W, S, D, Left, Up, Down, Right.
        static byte[] GameKeysReport()
        {
            var r = new byte[127];
            r[0] = 0x0B; r[1] = 0x01;
            foreach (int i in new[] { 11, 16, 17, 23, 91, 96, 97, 103 }) r[i] = 0x07;
            return r;
        }

        static bool SendGameKeys(out string error)
        {
            error = null;
            string path = FindPath(127);
            if (path == null) { error = "Game mode key list: keyboard collection not found."; return false; }
            using (var h = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) { error = "Could not open the keyboard (error " + Marshal.GetLastWin32Error() + ")."; return false; }
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    if (attempt > 0) Thread.Sleep(120 * attempt);
                    var r = GameKeysReport();
                    if (Native.HidD_SetFeature(h, r, r.Length)) { Thread.Sleep(30); return true; }
                    error = "Keyboard is busy and refused the Game mode key list (error " + Marshal.GetLastWin32Error() + ").";
                }
                return false;
            }
        }

        static string FindPath() { return FindPath(REPORT_LEN); }

        public static bool IsPresent() { try { return FindPath() != null; } catch { return false; } }

        static string FindPath(int featureLen)
        {
            Guid g; Native.HidD_GetHidGuid(out g);
            IntPtr set = Native.SetupDiGetClassDevs(ref g, null, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) return null;
            try
            {
                for (uint i = 0; ; i++)
                {
                    var did = new Native.SP_DEVICE_INTERFACE_DATA(); did.cbSize = Marshal.SizeOf(did);
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did)) break;
                    uint need; Native.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    IntPtr buf = Marshal.AllocHGlobal((int)need);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref did, buf, need, out need, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(buf + 4);
                        string lp = path.ToLowerInvariant();
                        if (lp.IndexOf("vid_258a&pid_013b") < 0 || lp.IndexOf("&mi_01") < 0) continue;
                        if (IsLightingCollection(path, featureLen)) return path;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return null;
        }

        static bool IsLightingCollection(string path, int featureLen)
        {
            using (var h = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return false;
                var a = new Native.HIDD_ATTRIBUTES(); a.Size = Marshal.SizeOf(a);
                if (!Native.HidD_GetAttributes(h, ref a) || a.VendorID != VID || a.ProductID != PID) return false;
                IntPtr pp;
                if (!Native.HidD_GetPreparsedData(h, out pp)) return false;
                Native.HIDP_CAPS caps; Native.HidP_GetCaps(pp, out caps); Native.HidD_FreePreparsedData(pp);
                return caps.UsagePage == 0xFF00 && caps.FeatureReportByteLength == featureLen;
            }
        }

        static SafeFileHandle Open(out string error)
        {
            error = null;
            string path = FindPath();
            if (path == null) { error = "Keyboard not detected. Check the USB cable."; return null; }
            var h = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) { error = "Could not open the keyboard (error " + Marshal.GetLastWin32Error() + ")."; return null; }
            return h;
        }

        static byte[] ReadRaw(SafeFileHandle h)
        {
            var buf = new byte[REPORT_LEN]; buf[0] = REPORT_ID;
            if (!Native.HidD_GetFeature(h, buf, buf.Length)) return null;
            return buf;
        }

        // Every 0x0A report seen from this keyboard has these fixed bytes; refuse to write back anything else.
        static bool LooksValid(byte[] r)
        {
            if (r[0] != REPORT_ID || r[1] > 12 || r[9] != 0x93 || r[14] != 0x10) return false;
            for (int i = 15; i < r.Length; i++) if (r[i] != 0) return false;
            return true;
        }

        public static LightState Read(out string error)
        {
            using (var h = Open(out error))
            {
                if (h == null) return null;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (attempt > 0) Thread.Sleep(120 * attempt);
                    var r = ReadRaw(h);
                    if (r == null || !LooksValid(r)) continue;
                    var s = new LightState();
                    s.Mode = r[1]; s.Speed = Clamp(6 - r[3], 1, 5); s.Brightness = Clamp(r[4], 0, 5);
                    if (Modes.ByCode(s.Mode) == null) s.Mode = 1;
                    return s;
                }
                error = "Keyboard did not answer the lighting read."; return null;
            }
        }

        // force: send even if the keyboard already reports these values (restarts the effect).
        public static bool Write(LightState s, bool force, out string error)
        {
            if (Modes.ByCode(s.Mode) == null) { error = "Invalid mode."; return false; }
            int speedByte = 6 - Clamp(s.Speed, 1, 5);
            int bright = Clamp(s.Brightness, 0, 5);
            // Game mode needs its key list first; the keyboard does not keep it, so send it every time (as the official app does).
            if (s.Mode == 12 && !SendGameKeys(out error)) return false;
            using (var h = Open(out error))
            {
                if (h == null) return false;
                bool sent = false;
                // The keyboard occasionally refuses a request while busy (error 31): retry with backoff,
                // and confirm by reading the report back.
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    if (attempt > 0) Thread.Sleep(120 * attempt);
                    var r = ReadRaw(h);
                    if (r == null || !LooksValid(r)) { error = "Keyboard did not answer the lighting read; nothing was sent."; continue; }
                    bool matches = r[1] == s.Mode && r[3] == speedByte && r[4] == bright;
                    if (matches && (!force || sent)) { error = null; return true; }
                    r[1] = s.Mode; r[3] = (byte)speedByte; r[4] = (byte)bright;
                    if (!Native.HidD_SetFeature(h, r, r.Length)) { error = "Keyboard is busy and refused the command (error " + Marshal.GetLastWin32Error() + ")."; continue; }
                    LastSendSeconds = Clock.Seconds;
                    sent = true; Thread.Sleep(30);
                }
                return false;
            }
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    // ---------------------------------------------------------------- storage

    static class Store
    {
        public const string AppName = "T-Dagger Lighting Control Panel";
        static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static readonly string Dir = Path.Combine(AppData, "T-Dagger Lighting Control Panel");
        static readonly string[] OldDirs = { Path.Combine(AppData, "T-Dagger Bora Lite Lighting"), Path.Combine(AppData, "BoraLight") };
        static readonly string FilePath = Path.Combine(Dir, "settings.ini");
        static readonly string LogPath = Path.Combine(Dir, "log.txt");
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "TDaggerLightingControlPanel";

        static Dictionary<string, string> ReadIni()
        {
            var d = new Dictionary<string, string>();
            string f = FilePath;
            if (!File.Exists(f)) foreach (var o in OldDirs) if (File.Exists(Path.Combine(o, "settings.ini"))) { f = Path.Combine(o, "settings.ini"); break; }
            if (!File.Exists(f)) return d;
            try
            {
                foreach (var line in File.ReadAllLines(f))
                {
                    int i = line.IndexOf('='); if (i <= 0) continue;
                    d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch (Exception ex) { Log("settings unreadable, using defaults: " + ex.Message); d.Clear(); }
            return d;
        }

        static void WriteIni(Dictionary<string, string> d)
        {
            Directory.CreateDirectory(Dir);
            var sb = new System.Text.StringBuilder();
            foreach (var kv in d) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
        }

        static int Int(Dictionary<string, string> d, string k, int lo, int hi, ref int ok)
        {
            string s; int v;
            if (d.TryGetValue(k, out s) && int.TryParse(s, out v) && v >= lo && v <= hi) { ok++; return v; }
            return -1;
        }

        public static LightState Load()
        {
            var d = ReadIni(); int ok = 0;
            int m = Int(d, "mode", 0, 12, ref ok), b = Int(d, "brightness", 0, 5, ref ok), sp = Int(d, "speed", 1, 5, ref ok);
            if (ok != 3) return null;
            var s = new LightState(); s.Mode = (byte)m; s.Brightness = b; s.Speed = sp; return s;
        }

        public static void Save(LightState s)
        {
            var d = ReadIni();
            d["mode"] = s.Mode.ToString(); d["brightness"] = s.Brightness.ToString(); d["speed"] = s.Speed.ToString();
            WriteIni(d);
        }

        public static string GetModel() { string s; return ReadIni().TryGetValue("model", out s) ? KbModels.ByCode(s).Code : KbModels.All[0].Code; }
        public static void SetModel(string code) { var d = ReadIni(); d["model"] = code; WriteIni(d); }

        public static double GetPeriod(byte mode, int speed)
        {
            var d = ReadIni(); string s; double v;
            if (d.TryGetValue("period." + mode + "." + speed, out s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0.2 && v < 60) return v;
            return 0;
        }

        public static void SetPeriod(byte mode, int speed, double seconds)
        {
            var d = ReadIni(); d["period." + mode + "." + speed] = seconds.ToString("0.000", CultureInfo.InvariantCulture); WriteIni(d);
        }

        public static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 100000) File.Delete(LogPath);
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n");
            }
            catch { }
        }

        public static bool StartupEnabled
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --apply");
                    else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
                }
            }
        }
    }

    // ---------------------------------------------------------------- program

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (o, e) => Store.Log("crash: " + e.ExceptionObject);
            if (args.Length > 0 && args[0] == "--apply")
            {
                try { return ApplySaved(); } catch (Exception ex) { Store.Log("sign-in: error: " + ex); return 1; }
            }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (o, e) =>
            {
                Store.Log("error: " + e.Exception);
                MessageBox.Show("Something went wrong: " + e.Exception.Message + "\n\nThe app will keep running. Details are in log.txt in " + Store.Dir,
                    Store.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            bool created;
            using (var mtx = new Mutex(true, "TDaggerLightingControlPanel.SingleInstance", out created))
            {
                if (!created) { BringExistingToFront(); return 0; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            return 0;
        }

        static void BringExistingToFront()
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                Native.ShowWindow(p.MainWindowHandle, 9); Native.SetForegroundWindow(p.MainWindowHandle); break;
            }
        }

        static int ApplySaved()
        {
            var s = Store.Load();
            if (s == null) { Store.Log("sign-in: no saved setting, nothing to do"); return 0; }
            string err = null;
            for (int i = 0; i < 30; i++)
            {
                if (Keyboard.Write(s, false, out err)) { Store.Log("sign-in: restored mode=" + s.Mode + " brightness=" + s.Brightness + " speed=" + s.Speed); return 0; }
                Thread.Sleep(2000);
            }
            Store.Log("sign-in: failed: " + err);
            return 1;
        }
    }

    // ------------------------------------------------------------------ theme

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(10, 11, 13);
        public static readonly Color Card = Color.FromArgb(18, 20, 23);
        public static readonly Color Tile = Color.FromArgb(24, 27, 31);
        public static readonly Color TileHover = Color.FromArgb(32, 36, 41);
        public static readonly Color Hair = Color.FromArgb(34, 38, 43);
        public static readonly Color Text = Color.FromArgb(236, 238, 241);
        public static readonly Color Sub = Color.FromArgb(139, 146, 156);
        public static readonly Color Faint = Color.FromArgb(72, 78, 86);
        public static readonly Color Accent = Color.FromArgb(46, 230, 96);
        public static readonly Color AccentSoft = Color.FromArgb(18, 46, 28);
        public static readonly Color Bad = Color.FromArgb(240, 92, 82);

        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        public static Font F(float size, bool semi)
        {
            string key = size + (semi ? "s" : "r"); Font f;
            if (!fonts.TryGetValue(key, out f)) { f = new Font(semi ? "Segoe UI Semibold" : "Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point); fonts[key] = f; }
            return f;
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            if (d > r.Width) d = r.Width; if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        public static void Hq(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }

        public static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static void Text_(Graphics g, string s, Font f, Color c, float x, float y)
        { using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y); }

        public static void TextRight(Graphics g, string s, Font f, Color c, float right, float y)
        { var w = g.MeasureString(s, f).Width; using (var b = new SolidBrush(c)) g.DrawString(s, f, b, right - w, y); }

        public static void FillRound(Graphics g, RectangleF r, float rad, Color c)
        { if (r.Width < 1 || r.Height < 1) return; using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p); }
    }

    class Smooth : Control
    {
        public Smooth()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false); TabStop = false;
        }
    }

    // ------------------------------------------------------------ effect tile

    class Tile : Smooth
    {
        public Mode Mode; public bool Selected; bool hover;
        public Tile(Mode m) { Mode = m; Cursor = Cursors.Hand; }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var p = Theme.Round(r, 10))
            {
                using (var b = new SolidBrush(Selected ? Theme.AccentSoft : (hover ? Theme.TileHover : Theme.Tile))) g.FillPath(b, p);
                if (Selected) using (var pen = new Pen(Color.FromArgb(160, Theme.Accent), 1.2f)) g.DrawPath(pen, p);
            }
            Color ic = Selected ? Theme.Accent : (hover ? Theme.Text : Theme.Sub);
            Icons.Draw(g, Mode.Code, new RectangleF(Width / 2f - 11, 13, 22, 22), ic);
            using (var b = new SolidBrush(Selected ? Theme.Text : (hover ? Theme.Text : Color.FromArgb(196, 201, 208))))
            using (var f = new StringFormat { Alignment = StringAlignment.Center })
                g.DrawString(Mode.Name, Theme.F(8.75f, Selected), b, new RectangleF(0, 43, Width, 20), f);
        }
    }

    static class Icons
    {
        public static void Draw(Graphics g, byte code, RectangleF r, Color c)
        {
            using (var pen = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var br = new SolidBrush(c))
            {
                float x = r.X, y = r.Y, w = r.Width, h = r.Height, cx = x + w / 2, cy = y + h / 2;
                switch (code)
                {
                    case 1: using (var p = Theme.Round(new RectangleF(x + 3, y + 3, w - 6, h - 6), 4)) g.FillPath(br, p); break;
                    case 2: g.FillEllipse(br, cx - 3, cy - 3, 6, 6); g.DrawEllipse(pen, cx - 7, cy - 7, 14, 14);
                        using (var p2 = new Pen(Color.FromArgb(110, c), 1.4f)) g.DrawEllipse(p2, cx - 10.5f, cy - 10.5f, 21, 21); break;
                    case 3: g.DrawRectangle(pen, x + 4, y + 8, 14, 10); g.DrawLine(pen, cx, y + 1, cx, y + 4);
                        g.DrawLine(pen, x + 4, y + 3, x + 6, y + 5); g.DrawLine(pen, x + 18, y + 3, x + 16, y + 5); break;
                    case 4: for (int i = 0; i < 3; i++) { float yy = y + 5 + i * 6; g.DrawBezier(pen, x + 1, yy, x + 7, yy - 4, x + 15, yy + 4, x + 21, yy); } break;
                    case 5: g.DrawLine(pen, x + 2, cy, x + 18, cy); g.DrawLine(pen, x + 13, cy - 5, x + 18, cy); g.DrawLine(pen, x + 13, cy + 5, x + 18, cy);
                        g.DrawLine(pen, x + 2, y + 3, x + 2, y + h - 3); break;
                    case 6: { var pts = new PointF[23]; for (int i = 0; i < 23; i++) pts[i] = new PointF(x + i, cy - (float)Math.Sin(i / 22.0 * Math.PI * 2) * 7); g.DrawLines(pen, pts); } break;
                    case 7: g.DrawLines(pen, new[] { new PointF(x + 2, y + 5), new PointF(x + 18, y + 5), new PointF(x + 18, cy), new PointF(x + 4, cy), new PointF(x + 4, y + 17), new PointF(x + 20, y + 17) });
                        g.FillEllipse(br, x + 17, y + 14.5f, 5, 5); break;
                    case 8: for (int i = 0; i < 3; i++) { float xx = x + 5 + i * 6; g.DrawLine(pen, xx, y + 2, xx, y + 14); g.DrawLine(pen, xx - 3, y + 11, xx, y + 14); g.DrawLine(pen, xx + 3, y + 11, xx, y + 14); }
                        g.DrawLine(pen, x + 2, y + 19, x + 20, y + 19); break;
                    case 9: g.FillEllipse(br, cx - 2, cy - 2, 4, 4); g.DrawEllipse(pen, cx - 6, cy - 6, 12, 12);
                        using (var p2 = new Pen(Color.FromArgb(110, c), 1.4f)) g.DrawEllipse(p2, cx - 10.5f, cy - 10.5f, 21, 21); break;
                    case 10: { float[] xs = { 3, 8, 13, 18 }; float[] ys = { 2, 8, 4, 10 }; for (int i = 0; i < 4; i++) g.DrawLine(pen, x + xs[i], y + ys[i], x + xs[i], y + ys[i] + 7); } break;
                    case 11: for (int i = 0; i < 4; i++) { double a = i * Math.PI / 2; var tip = new PointF(cx + (float)Math.Cos(a) * 10, cy + (float)Math.Sin(a) * 10);
                            var side = new PointF(cx + (float)Math.Cos(a + 0.6) * 7, cy + (float)Math.Sin(a + 0.6) * 7);
                            g.FillPolygon(br, new[] { new PointF(cx, cy), tip, side }); } break;
                    case 12: { float k = 6.5f; RectangleF[] ks = { new RectangleF(cx - k / 2, y + 2, k, k), new RectangleF(cx - k * 1.5f - 1, y + 3 + k, k, k), new RectangleF(cx - k / 2, y + 3 + k, k, k), new RectangleF(cx + k / 2 + 1, y + 3 + k, k, k) };
                            foreach (var kr in ks) using (var p = Theme.Round(kr, 1.5f)) g.DrawPath(pen, p); } break;
                    case 0: g.DrawArc(pen, x + 3, y + 3, w - 6, h - 6, -60, 300); g.DrawLine(pen, cx, y + 1, cx, cy); break;
                }
            }
        }
    }

    // ------------------------------------------------------------- step slider

    class StepSlider : Smooth
    {
        int min, max, val; public event EventHandler ValueChanged;
        public StepSlider(int mn, int mx) { min = mn; max = mx; val = mn; Cursor = Cursors.Hand; Height = 26; }
        public int Value { get { return val; } set { int v = Math.Max(min, Math.Min(max, value)); if (v != val) { val = v; Invalidate(); } } }
        float X0 { get { return 9; } } float X1 { get { return Width - 9; } }
        float Px(int v) { return X0 + (X1 - X0) * (v - min) / (float)(max - min); }
        void Set(int x)
        {
            if (!Enabled) return;
            int v = Math.Max(min, Math.Min(max, (int)Math.Round(min + (x - X0) / (X1 - X0) * (max - min))));
            if (v != val) { val = v; Invalidate(); if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); }
        }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Capture = true; Set(e.X); } base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (Capture && e.Button == MouseButtons.Left) Set(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Capture = false; base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            float cy = Height / 2f, px = Px(val); Color on = Enabled ? Theme.Accent : Theme.Faint;
            Theme.FillRound(g, new RectangleF(X0, cy - 2, X1 - X0, 4), 2, Theme.Tile);
            if (px - X0 > 1) Theme.FillRound(g, new RectangleF(X0, cy - 2, px - X0, 4), 2, on);
            for (int v = min; v <= max; v++) if (v > val) using (var b = new SolidBrush(Theme.Hair)) g.FillEllipse(b, Px(v) - 2, cy - 2, 4, 4);
            if (Enabled) using (var glow = new SolidBrush(Color.FromArgb(36, Theme.Accent))) g.FillEllipse(glow, px - 12, cy - 12, 24, 24);
            using (var b = new SolidBrush(Enabled ? Color.White : Theme.Sub)) g.FillEllipse(b, px - 7, cy - 7, 14, 14);
        }
    }

    class Toggle : Smooth
    {
        bool on; public event EventHandler CheckedChanged;
        public Toggle() { Size = new Size(40, 22); Cursor = Cursors.Hand; }
        public bool Checked { get { return on; } set { if (on != value) { on = value; Invalidate(); } } }
        protected override void OnClick(EventArgs e) { on = !on; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); base.OnClick(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            Theme.FillRound(g, new RectangleF(0, 0, Width - 1, Height - 1), (Height - 1) / 2f, on ? Theme.Accent : Theme.Tile);
            float d = Height - 6; using (var b = new SolidBrush(on ? Color.White : Theme.Sub)) g.FillEllipse(b, on ? Width - d - 4 : 3, 3, d, d);
        }
    }

    class PillButton : Smooth
    {
        bool hover; public string Label; public bool Primary;
        public PillButton(string label, bool primary) { Label = label; Primary = primary; Cursor = Cursors.Hand; Height = 28; }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            if (Primary) Theme.FillRound(g, r, 14, hover ? Color.FromArgb(28, 70, 40) : Theme.AccentSoft);
            else Theme.FillRound(g, r, 14, hover ? Theme.TileHover : Theme.Tile);
            using (var b = new SolidBrush(Primary ? Theme.Accent : Theme.Text))
            using (var f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(Label, Theme.F(8.75f, true), b, r, f);
        }
    }

    class TitleButton : Smooth
    {
        bool hover; readonly bool close;
        public TitleButton(bool isClose) { close = isClose; Size = new Size(46, 40); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        // Act on the release itself. WinForms' Click also requires WindowFromPoint to return this button,
        // which fails when a click-through overlay (game/GPU/recording overlays) covers the title bar.
        public event EventHandler Pressed;
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location) && Pressed != null) Pressed(this, EventArgs.Empty);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            g.Clear(hover ? (close ? Color.FromArgb(196, 43, 28) : Theme.Tile) : BackColor);
            using (var pen = new Pen(hover ? Color.White : Theme.Sub, 1.1f))
            {
                float cx = Width / 2f, cy = Height / 2f;
                if (close) { g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5); g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5); }
                else g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
            }
        }
    }

    // Model code next to "Bora Lite"; click it to choose which Bora Lite version is connected.
    class ModelPicker : Smooth
    {
        bool hover; KbModel model = KbModels.All[0];
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        public event EventHandler ModelChanged;
        public KbModel Model { get { return model; } set { model = value; Width = Measure(); Invalidate(); } }

        public ModelPicker()
        {
            Cursor = Cursors.Hand; Height = 24;
            menu.Renderer = new DarkMenu(); menu.ShowImageMargin = false; menu.BackColor = Theme.Card; menu.Padding = new Padding(4);
        }

        // Filled right before showing: WinForms cancels opening a menu that is empty at that moment.
        void BuildMenu()
        {
            {
                while (menu.Items.Count > 0) menu.Items[0].Dispose();   // dispose removes it from the menu
                foreach (var m in KbModels.All)
                {
                    var km = m;
                    var it = new ToolStripMenuItem(m.Code + "     " + m.Edition) { ForeColor = m == model ? Theme.Accent : Theme.Text, Font = Theme.F(9f, m == model), Padding = new Padding(4, 6, 4, 6) };
                    it.Click += (s, a) => { if (km == model) return; Model = km; if (ModelChanged != null) ModelChanged(this, EventArgs.Empty); };
                    menu.Items.Add(it);
                }
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("The keyboard does not report its version, so pick yours here.") { Enabled = false, Font = Theme.F(8f, false), Padding = new Padding(4, 4, 4, 4) });
            }
        }

        int Measure() { return TextRenderer.MeasureText(model.Code, Theme.F(9f, false)).Width + 26; }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) { BuildMenu(); menu.Show(this, new Point(0, Height + 4)); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            if (hover) Theme.FillRound(g, new RectangleF(0, 0, Width - 1, Height - 1), 6, Theme.Tile);
            Color c = hover ? Theme.Text : Theme.Sub;
            TextRenderer.DrawText(g, model.Code, Theme.F(9f, false), new Point(4, 4), c, TextFormatFlags.NoPadding);
            float cx = Width - 11, cy = Height / 2f + 1;
            using (var pen = new Pen(c, 1.3f)) { g.DrawLine(pen, cx - 3.5f, cy - 2, cx, cy + 1.5f); g.DrawLine(pen, cx, cy + 1.5f, cx + 3.5f, cy - 2); }
        }
        protected override void Dispose(bool disposing) { if (disposing) menu.Dispose(); base.Dispose(disposing); }
    }

    class DarkMenu : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Theme.Card); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        { using (var pen = new Pen(Theme.Hair)) e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1); }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected && e.Item.Enabled) { var g = e.Graphics; Theme.Hq(g); Theme.FillRound(g, new RectangleF(2, 0, e.Item.Width - 4, e.Item.Height - 1), 6, Theme.TileHover); }
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        { e.TextColor = e.Item.Enabled ? e.Item.ForeColor : Theme.Faint; base.OnRenderItemText(e); }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        { using (var pen = new Pen(Theme.Hair)) e.Graphics.DrawLine(pen, 6, e.Item.Height / 2, e.Item.Width - 6, e.Item.Height / 2); }
    }

    // ---------------------------------------------------------------- preview

    class K
    {
        public string Label; public RectangleF R; public int Vk; public int Row; public float U, V, Order;
        public K(string l, int x1, int y1, int x2, int y2, int vk) { Label = l; R = new RectangleF(x1, y1, x2 - x1, y2 - y1); Vk = vk; }
    }

    class Preview : Smooth
    {
        static readonly K[] Keys = new K[] {
            new K("Esc",15,70,38,95,27), new K("F1",84,70,107,95,112), new K("F2",119,70,142,95,113), new K("F3",154,70,177,95,114),
            new K("F4",189,70,212,95,115), new K("F5",241,70,264,95,116), new K("F6",276,70,299,95,117), new K("F7",311,70,334,95,118),
            new K("F8",346,70,369,95,119), new K("F9",399,70,422,95,120), new K("F10",433,70,456,95,121), new K("F11",469,70,492,95,122),
            new K("F12",504,70,527,95,123), new K("PrtSc",548,70,571,95,44), new K("ScrLk",583,70,606,95,145), new K("Pause",617,70,640,95,19),
            new K("`",14,110,37,135,192), new K("1",49,110,72,135,49), new K("2",84,110,107,135,50), new K("3",118,110,141,135,51),
            new K("4",154,110,177,135,52), new K("5",189,110,212,135,53), new K("6",224,110,247,135,54), new K("7",259,110,282,135,55),
            new K("8",294,110,317,135,56), new K("9",329,110,352,135,57), new K("0",363,110,386,135,48), new K("-",399,110,422,135,189),
            new K("=",434,110,457,135,187), new K("Backspace",469,110,526,135,8), new K("Ins",547,110,570,135,45), new K("Home",583,110,606,135,36),
            new K("PgUp",618,110,641,135,33), new K("Tab",14,145,54,170,9), new K("Q",67,145,90,170,81), new K("W",102,145,125,170,87),
            new K("E",136,145,159,170,69), new K("R",171,145,194,170,82), new K("T",206,145,229,170,84), new K("Y",241,145,264,170,89),
            new K("U",276,145,299,170,85), new K("I",311,145,334,170,73), new K("O",346,145,369,170,79), new K("P",381,145,404,170,80),
            new K("[",415,145,438,170,219), new K("]",451,145,474,170,221), new K("\\",486,145,526,170,220), new K("Del",548,145,571,170,46),
            new K("End",583,145,606,170,35), new K("PgDn",617,145,640,170,34), new K("Caps",15,180,63,205,20), new K("A",75,180,98,205,65),
            new K("S",110,180,133,205,83), new K("D",145,180,168,205,68), new K("F",180,180,203,205,70), new K("G",215,180,238,205,71),
            new K("H",250,180,273,205,72), new K("J",285,180,308,205,74), new K("K",320,180,343,205,75), new K("L",355,180,378,205,76),
            new K(";",390,180,413,205,186), new K("'",425,180,448,205,222), new K("Enter",460,180,526,205,13), new K("Shift",15,215,80,240,160),
            new K("Z",93,215,116,240,90), new K("X",128,215,151,240,88), new K("C",163,215,186,240,67), new K("V",198,215,221,240,86),
            new K("B",233,215,256,240,66), new K("N",267,215,290,240,78), new K("M",302,215,325,240,77), new K(",",337,215,360,240,188),
            new K(".",372,215,395,240,190), new K("/",407,215,430,240,191), new K("Shift",442,214,526,239,161), new K("\u2191",583,215,606,240,38),
            new K("Ctrl",14,250,44,275,162), new K("Win",58,250,88,275,91), new K("Alt",102,250,132,275,164), new K("",146,250,351,275,32),
            new K("Alt",364,250,394,275,165), new K("Fn",407,250,437,275,250), new K("Menu",451,250,481,275,93), new K("Ctrl",495,250,525,275,163),
            new K("\u2190",548,250,571,275,37), new K("\u2193",583,250,606,275,40), new K("\u2192",618,251,641,276,39),
        };

        // Fixed LED colour of each row on this keyboard, top row first (confirmed by the owner).
        public static readonly Color[] RowColors = new Color[] {
            Color.FromArgb(40, 120, 255),   // blue   (Esc / F-row)
            Color.FromArgb(150, 80, 255),   // purple (number row)
            Color.FromArgb(40, 225, 90),    // green  (Tab row)
            Color.FromArgb(255, 140, 30),   // orange (Caps row)
            Color.FromArgb(255, 80, 175),   // pink   (Shift row)
            Color.FromArgb(255, 45, 45),    // red    (bottom row)
        };

        static readonly HashSet<int> GameKeys = new HashSet<int> { 0x57, 0x41, 0x53, 0x44, 0x26, 0x28, 0x25, 0x27 };
        const float MinX = 14, MaxX = 641, MinY = 70, MaxY = 276;
        static readonly float EscAngle;

        public byte Mode = 1; public int Brightness = 5; public int Speed = 4;
        public double Period = 3;     // seconds per cycle for timed effects
        double anchor;                // time at which the cycle phase is 0
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Random rnd = new Random();
        readonly Dictionary<int, double> pressed = new Dictionary<int, double>();
        readonly List<PointF> rippleAt = new List<PointF>(); readonly List<double> rippleT = new List<double>();
        readonly List<float> dropX = new List<float>(); readonly List<double> dropT = new List<double>();
        double nextEvent, lastRealKey = -100;
        // (legend fonts live with the drawing code below)

        static Preview()
        {
            int[] rowsY = { 70, 110, 145, 180, 214, 250 };
            foreach (var k in Keys)
            {
                for (int i = 0; i < rowsY.Length; i++) if (Math.Abs(k.R.Y - rowsY[i]) <= 2) k.Row = i;
                k.U = (k.R.X + k.R.Width / 2 - MinX) / (MaxX - MinX); k.V = k.Row / 5f;
            }
            var order = new List<K>(Keys);
            order.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : (a.Row % 2 == 0 ? a.R.X.CompareTo(b.R.X) : b.R.X.CompareTo(a.R.X)));
            for (int i = 0; i < order.Count; i++) order[i].Order = i;
            var esc = Keys[0]; EscAngle = (float)Math.Atan2((esc.V - 0.5) * 0.36, esc.U - 0.5);
            AddLegends();
        }

        public Preview() { timer.Interval = 30; timer.Tick += (o, e) => Invalidate(); timer.Start(); }

        // Redraw ~33 times a second only while needed (visible and the effect moves).
        public bool Animate { get { return timer.Enabled; } set { if (timer.Enabled != value) timer.Enabled = value; } }

        public double Now { get { return Clock.Seconds; } }
        public void Restart() { anchor = Now; pressed.Clear(); rippleAt.Clear(); rippleT.Clear(); dropX.Clear(); dropT.Clear(); }
        public void SetAnchor(double t) { anchor = t; }
        // Change the cycle length but keep the current position in the cycle (no jump).
        public void SetPeriod(double p) { if (Math.Abs(p - Period) < 1e-6) return; double t = Now, ph = Phase(t); Period = p; anchor = t - ph * p; }

        // A real key press (from the global key watcher) for Reactive / Ripples.
        public void Press(int vk)
        {
            double t = Now; pressed[vk] = t; lastRealKey = t;
        }

        double Phase(double t) { double p = (t - anchor) / Period; return p - Math.Floor(p); }

        double Intensity(K k, double t)
        {
            double p = Phase(t), sp = 0.45 + 0.35 * (Speed - 1);
            switch (Mode)
            {
                case 0: return 0;
                case 1: return 1;
                case 2:
                    {
                        // Breathing: the keyboard's measured brightness over one cycle at this speed, 40 steps,
                        // starting when the effect is (re)started: short dark moment, breath, then off for the rest.
                        var curve = BreathCurves[Math.Max(1, Math.Min(5, Speed))];
                        double x = p * curve.Length; int i0 = (int)Math.Floor(x) % curve.Length, i1 = (i0 + 1) % curve.Length;
                        double v = curve[i0] + (curve[i1] - curve[i0]) * (x - Math.Floor(x));
                        return Math.Max(0, Math.Min(1, (v - 1.5) / 6.5));
                    }
                case 3: { double ts; if (!pressed.TryGetValue(k.Vk, out ts)) return 0; double age = t - ts, dur = 1.2 / sp; return age < 0 || age > dur ? 0 : 1 - age / dur; }
                case 4: { double w = Math.Cos(2 * Math.PI * (k.U * 1.0 + k.V * 0.35 - p)); return Math.Pow(0.5 + 0.5 * w, 1.6); }
                // Effects 5-11 are modelled on a video of the real keyboard (speed 1, 2026-11-24).
                case 5:
                    {
                        // Sweep: a band with a fading tail runs left to right along one row at a time, top row first.
                        // Each row takes 1.069 s of the 7.06 s cycle at speed 1; the tail spills into the next row's time.
                        const double rowFrac = 1.069 / 7.06, tail = 0.6;
                        double best = 0;
                        for (int wrap = 0; wrap <= 1; wrap++)
                        {
                            double head = (p + wrap - k.Row * rowFrac) / rowFrac;   // 0..1 while the head crosses this row
                            double d = head - k.U;
                            if (d >= 0 && d <= tail) best = Math.Max(best, Math.Pow(1 - d / tail, 0.7));
                        }
                        return best;
                    }
                case 6:
                    {
                        // Sine Wave: every key fades in and out on its own timing; about half are lit at once.
                        // (The keyboard's per-key timing could not be recovered from video, so offsets are fixed pseudo-random.)
                        double off = KeyOffset(k);
                        double w = 0.5 + 0.5 * Math.Cos(2 * Math.PI * (p - off));
                        return Smooth(0.35, 0.85, w);
                    }
                case 7:
                    {
                        // Snake: every row carries a long band at the same time; even rows run left to right,
                        // odd rows right to left, so the board zig-zags. 2.95 s per cycle at speed 1.
                        const double tail = 0.55;
                        double u = k.Row % 2 == 0 ? k.U : 1 - k.U;
                        double head = p * (1 + tail + 0.15) - 0.05;
                        double d = head - u;
                        return d < 0 || d > tail ? 0 : 1 - Math.Pow(d / tail, 1.5);
                    }
                case 8:
                    {
                        // Waterfall: rows switch on one after another from the top, the whole board stays lit,
                        // then rows switch off from the top; dark for the last ~45% of the cycle.
                        double s = p - k.Row * 0.035;
                        if (s < 0) s += 1;
                        if (s < 0.06) return s / 0.06;
                        if (s < 0.40) return 1;
                        if (s < 0.48) return 1 - (s - 0.40) / 0.08;
                        return 0;
                    }
                case 9:
                    {
                        // Ripples: a disc of light grows from the centre keys until the whole board is lit,
                        // then a dark hole grows from the centre outwards; dark until the next ripple.
                        double dist = Dist(k, 0.45, 0.5);
                        double fill = p / 0.31, hole = (p - 0.43) / 0.32;
                        double on = Smooth(-0.06, 0.06, fill - dist);
                        double off = p < 0.43 ? 0 : Smooth(-0.06, 0.06, hole - dist);
                        return Math.Max(0, on - off);
                    }
                case 10:
                    {
                        // Rain: a vertical strip about three keys wide sweeps from left to right, then a short pause.
                        double c = p * 1.25 - 0.12, d = Math.Abs(k.U - c);
                        return d > 0.11 ? 0 : (d < 0.05 ? 1 : 1 - (d - 0.05) / 0.06);
                    }
                case 11:
                    {
                        // Windmill: a broad beam through the centre turns clockwise; it looks the same after half a turn,
                        // which is one cycle (5.74 s at speed 1). Phase 0 = beam from top-right to bottom-left.
                        double dx = (k.U - 0.5) * 627, dy = (k.V - 0.5) * 180;
                        double th = Math.Atan2(dy, dx);                    // screen coordinates: increasing = clockwise
                        double beam = -Math.PI / 4 + Math.PI * p;
                        double c = Math.Cos(2 * (th - beam));
                        return Smooth(0.25, 0.75, c);
                    }
                case 12: return GameKeys.Contains(k.Vk) ? 1 : 0;
            }
            return 1;
        }

        void Simulate(double t) { }

        // Breathing brightness (0-9) over one cycle per speed, folded from a video of the real keyboard.
        static readonly double[][] BreathCurves = {
            null,
            new double[] { 1,1,1,1,1,1,2,4,4,6,5,6,6,7,6,7,8,7,7,6,5,4,3,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1 },
            new double[] { 1,1,1,1,1,3,4,5,7,8,8,9,9,9,9,8,8,7,7,5,4,2,1,0,0,0,0,1,1,1,1,1,1,1,1,1,1,1,1,1 },
            new double[] { 2,2,2,3,4,5,6,7,8,8,8,8,8,8,8,7,6,6,4,3,2,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,2 },
            new double[] { 1,1,1,1,2,2,3,5,5,6,7,7,7,8,7,7,6,7,5,4,4,2,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1 },
            new double[] { 1,1,1,1,1,2,3,4,5,6,7,8,8,8,8,8,8,8,7,6,4,3,2,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1 },
        };

        static double Smooth(double a, double b, double x) { double v = (x - a) / (b - a); if (v < 0) return 0; if (v > 1) return 1; return v * v * (3 - 2 * v); }

        // Distance from a point on the board (u, v in 0..1), in real key-map units, scaled so a corner is about 1.
        static double Dist(K k, double cu, double cv)
        {
            double dx = (k.U - cu) * 627, dy = (k.V - cv) * 180;
            return Math.Sqrt(dx * dx + dy * dy) / 360.0;
        }

        // Fixed per-key offset in 0..1 (stable between runs).
        static double KeyOffset(K k)
        {
            unchecked
            {
                uint h = (uint)(k.R.X * 7919 + k.R.Y * 104729) ^ 0x9E3779B9u;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                return (h % 10000) / 10000.0;
            }
        }

        // ------------------------------------------------------------ drawing
        // Outline of the real case, in the same coordinates as the key map (both traced from the official product image).
        const float CaseX0 = 2, CaseX1 = 652, CaseY0 = 57, CaseY1 = 290;

        // Keycap legends traced from the official image: shifted symbol, small Fn label, icons (Segoe MDL2 Assets).
        class Legend { public string Main, Alt, Sub; public char Icon, SubIcon; public bool Word; }
        static readonly Dictionary<K, Legend> Legends = new Dictionary<K, Legend>();
        static void AddLegends()
        {
            var alt = new Dictionary<int, string> { { 192, "~" }, { 49, "!" }, { 50, "@" }, { 51, "#" }, { 52, "$" }, { 53, "%" }, { 54, "^" }, { 55, "&" },
                { 56, "*" }, { 57, "(" }, { 48, ")" }, { 189, "_" }, { 187, "+" }, { 219, "{" }, { 221, "}" }, { 220, "|" }, { 186, ":" }, { 222, "\"" },
                { 188, "<" }, { 190, ">" }, { 191, "?" } };
            var sub = new Dictionary<int, string> { { 49, "LR1" }, { 50, "LR2" }, { 51, "LR3" },
                { 38, "W" }, { 37, "A" }, { 40, "S" }, { 39, "D" } };
            // F1..F12 media/shortcut icons, in order as printed on the keycaps
            char[] fIcons = { '\uE7F4', '\uE721', '\uE8EF', '\uE8D6', '\uE892', '\uE768', '\uE893', '\uE71A', '\uE774', '\uE80F', '\uE767', '\uE72E' };
            var words = new Dictionary<string, string> { { "Caps", "CapsLk" } };
            foreach (var k in Keys)
            {
                var l = new Legend(); string s;
                l.Main = words.TryGetValue(k.Label, out s) ? s : k.Label;
                if (alt.TryGetValue(k.Vk, out s)) l.Alt = s;
                if (sub.TryGetValue(k.Vk, out s)) l.Sub = s;
                if (k.Vk >= 112 && k.Vk <= 123) l.SubIcon = fIcons[k.Vk - 112];
                if (k.Vk == 87) l.SubIcon = '\uE74A'; if (k.Vk == 65) l.SubIcon = '\uE72B'; if (k.Vk == 83) l.SubIcon = '\uE74B'; if (k.Vk == 68) l.SubIcon = '\uE72A';   // WASD double as arrows
                if (k.Vk == 91) { l.Main = null; l.SubIcon = '\uE72E'; }                         // Windows logo (drawn as four panes) + Win-lock padlock
                if (k.Vk == 93) { l.Main = null; l.Icon = '\uE8A5'; }                               // menu key
                if (k.Vk == 38) { l.Main = null; l.Icon = '\uE74A'; }
                if (k.Vk == 40) { l.Main = null; l.Icon = '\uE74B'; }
                if (k.Vk == 37) { l.Main = null; l.Icon = '\uE72B'; }
                if (k.Vk == 39) { l.Main = null; l.Icon = '\uE72A'; }
                if (k.Vk == 8 || k.Vk == 13 || k.Vk == 32) l.Main = null;                           // drawn as shapes
                l.Word = l.Main != null && l.Main.Length > 1 && char.IsLetter(l.Main[0]) && !(l.Main[0] == 'F' && char.IsDigit(l.Main[1]));
                Legends[k] = l;
            }
        }

        // Gray/black edition: alphanumerics, spacebar and F5-F8 are light gray; everything else is black.
        static bool GrayCap(K k)
        {
            int v = k.Vk;
            return (v >= 65 && v <= 90) || (v >= 48 && v <= 57) || v == 32 || (v >= 116 && v <= 119) ||
                   v == 192 || v == 189 || v == 187 || v == 219 || v == 221 || v == 186 || v == 222 || v == 188 || v == 190 || v == 191;
        }

        class Look { public Color Frame, FrameEdge, PlateTop, PlateBot, Skirt, FaceTop, FaceBot, LegendOff, Cable; }
        static Look LookFor(Body b, bool grayCap)
        {
            var l = new Look();
            if (b == Body.White)
            {
                l.Frame = Color.FromArgb(214, 217, 221); l.FrameEdge = Color.FromArgb(246, 247, 249); l.PlateTop = Color.FromArgb(240, 241, 243); l.PlateBot = Color.FromArgb(226, 228, 231);
                l.Skirt = Color.FromArgb(196, 199, 204); l.FaceTop = Color.FromArgb(255, 255, 255); l.FaceBot = Color.FromArgb(232, 234, 237); l.LegendOff = Color.FromArgb(150, 155, 162);
                l.Cable = Color.FromArgb(225, 227, 230);
                return l;
            }
            l.Frame = Color.FromArgb(13, 14, 15); l.FrameEdge = Color.FromArgb(64, 67, 72); l.Cable = Color.FromArgb(20, 21, 23);
            if (b == Body.Gray) { l.PlateTop = Color.FromArgb(24, 25, 27); l.PlateBot = Color.FromArgb(16, 17, 19); }
            else { l.PlateTop = Color.FromArgb(44, 46, 50); l.PlateBot = Color.FromArgb(31, 33, 36); }
            if (grayCap) { l.Skirt = Color.FromArgb(92, 96, 102); l.FaceTop = Color.FromArgb(150, 154, 160); l.FaceBot = Color.FromArgb(116, 120, 126); l.LegendOff = Color.FromArgb(62, 65, 70); }
            else { l.Skirt = Color.FromArgb(7, 7, 8); l.FaceTop = Color.FromArgb(48, 50, 54); l.FaceBot = Color.FromArgb(24, 25, 27); l.LegendOff = Color.FromArgb(132, 136, 142); }
            return l;
        }

        Font fMain, fWord, fWordSmall, fSub, fIcon, fIconMain; float lastScale = -1;
        Bitmap plateLayer, capsLayer; Size layerSize; Body layerBody = (Body)(-1); Bitmap layerLogo;
        public Bitmap Logo;   // T-Dagger shield, drawn on the spacebar like the real keyboard

        void MakeFonts(float s)
        {
            foreach (var f in new[] { fMain, fWord, fWordSmall, fSub, fIcon, fIconMain }) if (f != null) f.Dispose();
            const string legendFace = "Bahnschrift SemiBold SemiConden";
            fMain = new Font(legendFace, Math.Max(6f, 8.4f * s), FontStyle.Regular, GraphicsUnit.Pixel);
            fWord = new Font(legendFace, Math.Max(5.5f, 7.1f * s), FontStyle.Regular, GraphicsUnit.Pixel);
            fWordSmall = new Font(legendFace, Math.Max(4.6f, 5.7f * s), FontStyle.Regular, GraphicsUnit.Pixel);
            fSub = new Font(legendFace, Math.Max(4f, 4.6f * s), FontStyle.Regular, GraphicsUnit.Pixel);
            fIcon = new Font("Segoe MDL2 Assets", Math.Max(4f, 4.8f * s), FontStyle.Regular, GraphicsUnit.Pixel);
            fIconMain = new Font("Segoe MDL2 Assets", Math.Max(5.5f, 7.2f * s), FontStyle.Regular, GraphicsUnit.Pixel);
            lastScale = s;
        }

        void Geometry(out float s, out float ox, out float oy)
        {
            s = Math.Min((Width - 12) / (CaseX1 - CaseX0), (Height - 14) / (CaseY1 - CaseY0));
            ox = (Width - (CaseX1 - CaseX0) * s) / 2 - CaseX0 * s;
            oy = (Height - (CaseY1 - CaseY0) * s) / 2 - CaseY0 * s + 4;
        }

        static RectangleF Face(RectangleF r, float s) { return new RectangleF(r.X + 2.3f * s, r.Y + 1.1f * s, r.Width - 4.6f * s, r.Height - 4.4f * s); }

        // Legends of one key in one color: main (small caps for words), shifted symbol, Fn label, icons, and the drawn Backspace/Enter/spacebar marks.
        void DrawLegend(Graphics g, K k, RectangleF r, float s, Color c)
        {
            Legend l; if (!Legends.TryGetValue(k, out l)) return;
            var f = Face(r, s); float cx = f.X + f.Width / 2, top = f.Y + 0.9f * s;
            using (var b = new SolidBrush(c))
            using (var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center })
            {
                if (l.Icon != '\0') g.DrawString(l.Icon.ToString(), fIconMain, b, cx, top + 0.8f * s, fmt);
                else if (l.Main != null && l.Alt != null)
                {
                    g.DrawString(l.Main, fMain, b, cx - 3.3f * s, top, fmt);
                    g.DrawString(l.Alt, fMain, b, cx + 3.3f * s, top, fmt);
                }
                else if (l.Word)
                {
                    // small caps: first letter full size, the rest smaller and baseline-aligned
                    string first = l.Main.Substring(0, 1).ToUpperInvariant(), rest = l.Main.Substring(1).ToUpperInvariant();
                    var lfmt = StringFormat.GenericTypographic;
                    float w1 = g.MeasureString(first, fWord, PointF.Empty, lfmt).Width, w2 = g.MeasureString(rest, fWordSmall, PointF.Empty, lfmt).Width;
                    float x = cx - (w1 + w2) / 2, asc1 = fWord.Size * 0.80f, asc2 = fWordSmall.Size * 0.80f;
                    g.DrawString(first, fWord, b, x, top + 0.6f * s, lfmt);
                    g.DrawString(rest, fWordSmall, b, x + w1, top + 0.6f * s + (asc1 - asc2), lfmt);
                }
                else if (l.Main != null) g.DrawString(l.Main, fMain, b, cx, top, fmt);

                float subY = f.Bottom - 5.6f * s;
                if (l.Sub != null) g.DrawString(l.Sub, fSub, b, cx, subY, fmt);
                if (l.SubIcon != '\0') g.DrawString(l.SubIcon.ToString(), fIcon, b, cx + (k.Vk == 91 ? 3f * s : 0), subY, fmt);
            }
            using (var pen = new Pen(c, Math.Max(1f, 1.3f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float my = f.Y + f.Height * 0.36f;
                if (k.Vk == 8)            // Backspace: long left arrow
                {
                    float x0 = cx - 7f * s, x1 = cx + 7f * s;
                    g.DrawLine(pen, x0, my, x1, my); g.DrawLine(pen, x0, my, x0 + 2.6f * s, my - 2f * s); g.DrawLine(pen, x0, my, x0 + 2.6f * s, my + 2f * s);
                }
                else if (k.Vk == 13)      // Enter: return arrow
                {
                    float x0 = cx - 6f * s, x1 = cx + 6f * s, y0 = my - 2.6f * s, y1 = my + 1.2f * s;
                    g.DrawLine(pen, x1, y0, x1, y1); g.DrawLine(pen, x1, y1, x0, y1);
                    g.DrawLine(pen, x0, y1, x0 + 2.4f * s, y1 - 1.9f * s); g.DrawLine(pen, x0, y1, x0 + 2.4f * s, y1 + 1.9f * s);
                }
                else if (k.Vk == 91)      // Windows logo: four panes
                {
                    float q = 2.1f * s, gp = 0.5f * s, lx = cx - q - gp / 2, ly = f.Y + 2.2f * s;
                    using (var b = new SolidBrush(c))
                    { g.FillRectangle(b, lx, ly, q, q); g.FillRectangle(b, lx + q + gp, ly, q, q); g.FillRectangle(b, lx, ly + q + gp, q, q); g.FillRectangle(b, lx + q + gp, ly + q + gp, q, q); }
                }
                else if (k.Vk == 32)      // spacebar dash
                    g.DrawLine(pen, cx - 6f * s, my + 1f * s, cx + 6f * s, my + 1f * s);
            }
        }

        // Frame, plate and keycaps never change while an effect runs, so they are drawn once per size/body and reused every frame.
        void BuildLayers(float s, float ox, float oy)
        {
            if (plateLayer != null) { plateLayer.Dispose(); capsLayer.Dispose(); }
            plateLayer = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            capsLayer = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            layerSize = Size; layerBody = BodyColor; layerLogo = Logo;
            var lk = LookFor(BodyColor, false);
            var body = new RectangleF(ox + CaseX0 * s, oy + CaseY0 * s, (CaseX1 - CaseX0) * s, (CaseY1 - CaseY0) * s);

            using (var g = Graphics.FromImage(plateLayer))
            {
                Theme.Hq(g); g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                // cable leaving the top edge
                using (var b = new SolidBrush(lk.Cable)) g.FillRectangle(b, ox + 381 * s, 0, 5 * s, body.Y + 1);
                using (var b = new SolidBrush(lk.Frame)) g.FillRectangle(b, ox + 379 * s, body.Y - 7 * s, 9 * s, 8 * s);
                // frame
                using (var p = Theme.Round(body, 9 * s)) using (var b = new SolidBrush(lk.Frame)) g.FillPath(b, p);
                using (var p = Theme.Round(body, 9 * s)) using (var pen = new Pen(lk.FrameEdge, 1f)) g.DrawPath(pen, p);
                // exposed plate inside the frame
                var plate = RectangleF.Inflate(body, -3.2f * s, -3.2f * s);
                using (var p = Theme.Round(plate, 6.5f * s)) using (var b = new LinearGradientBrush(plate, lk.PlateTop, lk.PlateBot, 90f)) g.FillPath(b, p);
                // small hole beside Esc and the two indicator windows right of Enter
                using (var b = new SolidBrush(Color.FromArgb(BodyColor == Body.White ? 150 : 255, 6, 6, 7))) g.FillEllipse(b, ox + 41.5f * s, oy + 75.5f * s, 3.4f * s, 3.4f * s);
                var ic = BodyColor == Body.White ? Color.FromArgb(150, 155, 162) : Color.FromArgb(120, 124, 130);
                using (var pen = new Pen(ic, Math.Max(1f, 0.7f * s)))
                using (var fb = new SolidBrush(ic))
                using (var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center })
                {
                    foreach (var y in new[] { 184.5f, 195.5f })
                    {
                        g.DrawRectangle(pen, ox + 635.5f * s, oy + y * s, 5f * s, 5f * s);
                        using (var b = new SolidBrush(Color.FromArgb(70, 20, 20))) g.FillRectangle(b, ox + 626f * s, oy + (y + 1.6f) * s, 5.5f * s, 2.2f * s);
                    }
                    g.DrawString("A", fSub, fb, ox + 638f * s, oy + 184.6f * s, fmt);
                    g.DrawString("\uE72E", fIcon, fb, ox + 638f * s, oy + 196f * s, fmt);
                }
            }

            using (var g = Graphics.FromImage(capsLayer))
            {
                Theme.Hq(g); g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                foreach (var k in Keys)
                {
                    var r = new RectangleF(ox + k.R.X * s, oy + k.R.Y * s, k.R.Width * s, k.R.Height * s);
                    var look = LookFor(BodyColor, BodyColor == Body.Gray && GrayCap(k));
                    using (var p = Theme.Round(r, 2.6f * s)) using (var b = new SolidBrush(look.Skirt)) g.FillPath(b, p);
                    var f = Face(r, s);
                    using (var p = Theme.Round(f, 2.2f * s)) using (var b = new LinearGradientBrush(f, look.FaceTop, look.FaceBot, 90f)) g.FillPath(b, p);
                    using (var p = Theme.Round(f, 2.2f * s)) using (var pen = new Pen(Color.FromArgb(BodyColor == Body.White ? 40 : 18, 255, 255, 255), 1f)) g.DrawPath(pen, p);
                    DrawLegend(g, k, r, s, look.LegendOff);
                    if (k.Vk == 32 && Logo != null)
                    {
                        // T-Dagger shield on the right of the spacebar, shown in monochrome like the printed logo
                        var cm = new System.Drawing.Imaging.ColorMatrix(new[] {
                            new float[] { .3f, .3f, .3f, 0, 0 }, new float[] { .59f, .59f, .59f, 0, 0 }, new float[] { .11f, .11f, .11f, 0, 0 },
                            new float[] { 0, 0, 0, BodyColor == Body.Black ? .9f : .75f, 0 }, new float[] { BodyColor == Body.Black ? .25f : -.25f, BodyColor == Body.Black ? .25f : -.25f, BodyColor == Body.Black ? .25f : -.25f, 0, 1 } });
                        using (var ia = new System.Drawing.Imaging.ImageAttributes())
                        {
                            ia.SetColorMatrix(cm);
                            float ls = 12.5f * s, lx = ox + 312f * s - ls / 2, ly = f.Y + (f.Height - ls) / 2;
                            g.DrawImage(Logo, new Rectangle((int)lx, (int)ly, (int)ls, (int)ls), 0, 0, Logo.Width, Logo.Height, GraphicsUnit.Pixel, ia);
                        }
                    }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.Clear(BackColor);
            if (Width < 50 || Height < 50) return;
            double t = Now; Simulate(t);
            float s, ox, oy; Geometry(out s, out ox, out oy);
            if (s != lastScale) MakeFonts(s);
            if (plateLayer == null || layerSize != Size || layerBody != BodyColor || layerLogo != Logo) BuildLayers(s, ox, oy);

            double br = Brightness / 5.0; var m = Modes.ByCode(Mode); if (m != null && !m.Brightness) br = 1;
            var ints = new double[Keys.Length]; double avgI = 0; double rr = 0, gg = 0, bb = 0;
            for (int i = 0; i < Keys.Length; i++)
            {
                ints[i] = Math.Max(0, Math.Min(1, Intensity(Keys[i], t))) * br; avgI += ints[i];
                var c = RowColors[Keys[i].Row]; rr += c.R * ints[i]; gg += c.G * ints[i]; bb += c.B * ints[i];
            }
            var body = new RectangleF(ox + CaseX0 * s, oy + CaseY0 * s, (CaseX1 - CaseX0) * s, (CaseY1 - CaseY0) * s);

            // ambient light spill around the keyboard
            if (avgI > 0.01)
            {
                Color amb = Color.FromArgb((int)Math.Min(255, rr / avgI), (int)Math.Min(255, gg / avgI), (int)Math.Min(255, bb / avgI));
                var glowR = body; glowR.Inflate(body.Width * 0.08f, body.Height * 0.35f);
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(glowR);
                    using (var pgb = new PathGradientBrush(path))
                    {
                        pgb.CenterColor = Color.FromArgb((int)(70 * Math.Min(1, avgI / Keys.Length * 1.6)), amb);
                        pgb.SurroundColors = new[] { Color.FromArgb(0, amb) };
                        g.FillPath(pgb, path);
                    }
                }
            }

            g.DrawImageUnscaled(plateLayer, 0, 0);
            // LED light spilling onto the plate around each floating key
            bool lightPlate = BodyColor == Body.White;
            for (int i = 0; i < Keys.Length; i++)
            {
                double I = ints[i]; if (I <= 0.02) continue;
                var k = Keys[i]; Color led = RowColors[k.Row];
                var r = new RectangleF(ox + k.R.X * s, oy + k.R.Y * s, k.R.Width * s, k.R.Height * s);
                var g1 = r; g1.Inflate(4.2f * s, 4.2f * s);
                using (var p = Theme.Round(g1, 6 * s)) using (var b = new SolidBrush(Color.FromArgb((int)((lightPlate ? 60 : 46) * I), led))) g.FillPath(b, p);
                var g2 = r; g2.Inflate(1.3f * s, 1.3f * s);
                using (var p = Theme.Round(g2, 3.5f * s)) using (var b = new SolidBrush(Color.FromArgb((int)((lightPlate ? 120 : 165) * I), led))) g.FillPath(b, p);
            }
            g.DrawImageUnscaled(capsLayer, 0, 0);
            // light shining through the legends
            for (int i = 0; i < Keys.Length; i++)
            {
                double I = ints[i]; if (I <= 0.02) continue;
                var k = Keys[i]; Color led = RowColors[k.Row];
                bool darkCap = BodyColor == Body.Black || (BodyColor == Body.Gray && !GrayCap(k));
                Color lit = darkCap ? Theme.Mix(led, Color.White, 0.35) : (BodyColor == Body.Gray ? Theme.Mix(led, Color.White, 0.3) : led);
                var r = new RectangleF(ox + k.R.X * s, oy + k.R.Y * s, k.R.Width * s, k.R.Height * s);
                DrawLegend(g, k, r, s, Color.FromArgb((int)(255 * Math.Min(1, I * 1.1)), lit));
            }
            // Caps Lock indicator follows the real Caps Lock state
            if (Control.IsKeyLocked(System.Windows.Forms.Keys.CapsLock))
                using (var b = new SolidBrush(Color.FromArgb(255, 40, 40))) g.FillRectangle(b, ox + 626f * s, oy + 186.1f * s, 5.5f * s, 2.2f * s);

            if (Overlay != null) Overlay(g);
        }

        public Action<Graphics> Overlay;
        public Body BodyColor = Body.Black;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                foreach (var f in new[] { fMain, fWord, fWordSmall, fSub, fIcon, fIconMain }) if (f != null) f.Dispose();
                if (plateLayer != null) { plateLayer.Dispose(); capsLayer.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------- global key watcher

    // Lights the preview when you type anywhere (Reactive and Ripples respond to your real key presses).
    // Key presses are only passed to the preview; nothing is stored or sent anywhere.
    class KeyWatcher : IDisposable
    {
        IntPtr hook; readonly Native.HookProc proc; readonly Action<int> onKey;
        public KeyWatcher(Action<int> onKeyDown) { onKey = onKeyDown; proc = Callback; }
        public bool Active { get { return hook != IntPtr.Zero; } }
        public void Start() { if (hook == IntPtr.Zero) hook = Native.SetWindowsHookEx(13, proc, Native.GetModuleHandle(null), 0); }
        public void Stop() { if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }
        IntPtr Callback(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0 && (w == (IntPtr)0x100 || w == (IntPtr)0x104)) { try { onKey(Marshal.ReadInt32(l)); } catch { } }
            return Native.CallNextHookEx(hook, code, w, l);
        }
        public void Dispose() { Stop(); }
    }

    // -------------------------------------------------------------- main form

    class MainForm : Form
    {
        const int W = 1000, H = 740, TitleH = 58, M = 24;
        static readonly Rectangle Hero = new Rectangle(M, TitleH + 4, W - 2 * M, 352);
        static readonly Rectangle FxCard = new Rectangle(M, TitleH + 4 + 352 + 16, 600, 270);
        static readonly Rectangle AdjCard = new Rectangle(M + 600 + 16, TitleH + 4 + 352 + 16, W - 2 * M - 600 - 16, 270);

        readonly Preview preview = new Preview();
        readonly ModelPicker picker = new ModelPicker();
        readonly List<Tile> tiles = new List<Tile>();
        readonly StepSlider bright = new StepSlider(0, 5), speed = new StepSlider(1, 5);
        readonly Toggle startup = new Toggle();
        readonly PillButton syncBtn = new PillButton("Sync now", true), calBtn = new PillButton("Calibrate", false), guideBtn = new PillButton("Guided calibration", false);
        readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer fgTimer = new System.Windows.Forms.Timer();
        readonly KeyWatcher watcher;
        Bitmap logo;
        LightState state = new LightState();
        string status = "Connecting..."; bool statusBad, connected, modeChangedSinceSend = true;
        // calibration
        bool calibrating; readonly List<double> taps = new List<double>();

        public MainForm()
        {
            Text = Store.AppName; FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(W, H); BackColor = Theme.Bg; DoubleBuffered = true; KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); logo = new Icon(Icon, 32, 32).ToBitmap(); } catch { }
            watcher = new KeyWatcher(vk => preview.Press(vk));
            preview.Overlay = g => { if (calibrating) PaintCalibration(g, Modes.ByCode(state.Mode)); };

            var close = new TitleButton(true) { Location = new Point(W - 46, 0), BackColor = Theme.Bg }; close.Pressed += (o, e) => Close();
            var min = new TitleButton(false) { Location = new Point(W - 92, 0), BackColor = Theme.Bg }; // Minimize after the click has finished (minimizing while the button still holds mouse capture is ignored).
            min.Pressed += (o, e) => BeginInvoke(new Action(() => { Capture = false; Native.ShowWindow(Handle, 6); }));   // SW_MINIMIZE
            Controls.Add(close); Controls.Add(min);

            picker.BackColor = Theme.Card; picker.Location = new Point(Hero.X + 104, Hero.Y + 19);
            try { picker.Model = KbModels.ByCode(Store.GetModel()); } catch { picker.Model = KbModels.All[0]; }
            preview.BodyColor = picker.Model.Body; preview.Logo = logo;
            picker.ModelChanged += (o, e) =>
            {
                preview.BodyColor = picker.Model.Body; preview.Invalidate(); Invalidate(new Rectangle(Hero.X, Hero.Y, Hero.Width, 64));
                try { Store.SetModel(picker.Model.Code); SetStatus("Model set to " + picker.Model.Code + ".", false); }
                catch (Exception ex) { SetStatus("Could not save the model: " + ex.Message, true); }
            };
            Controls.Add(picker);

            preview.BackColor = Theme.Card;
            preview.Bounds = new Rectangle(Hero.X + 8, Hero.Y + 64, Hero.Width - 16, Hero.Height - 72);
            Controls.Add(preview);

            syncBtn.BackColor = Theme.Card; calBtn.BackColor = Theme.Card;
            calBtn.Bounds = new Rectangle(Hero.Right - 22 - 96, Hero.Y + 22, 96, 28);
            syncBtn.Bounds = new Rectangle(Hero.Right - 22 - 96, Hero.Y + 22, 96, 28);
            syncBtn.Click += (o, e) => SyncNow();
            calBtn.Click += (o, e) => { if (!wizard) StartCalibration(); };
            // Timing comes from measurements of the real keyboard and syncing is automatic,
            // so neither the calibration tools nor the Sync button are shown.

            for (int i = 0; i < Modes.All.Length; i++)
            {
                var tl = new Tile(Modes.All[i]) { BackColor = Theme.Card };
                int col = i % 5, row = i / 5;
                tl.Bounds = new Rectangle(FxCard.X + 18 + col * 114, FxCard.Y + 52 + row * 72, 106, 64);
                tl.Click += (o, e) => { if (calibrating) return; var c =((Tile)o).Mode.Code; if (c != state.Mode) modeChangedSinceSend = true; state.Mode = c; RefreshUi(); Changed(); };
                tiles.Add(tl); Controls.Add(tl);
            }

            bright.BackColor = Theme.Card; speed.BackColor = Theme.Card;
            bright.Bounds = new Rectangle(AdjCard.X + 14, AdjCard.Y + 74, AdjCard.Width - 28, 26);
            speed.Bounds = new Rectangle(AdjCard.X + 14, AdjCard.Y + 140, AdjCard.Width - 28, 26);
            bright.ValueChanged += (o, e) => { if (!calibrating) state.Brightness = bright.Value; RefreshUi(); Changed(); };
            speed.ValueChanged += (o, e) => { if (!calibrating) state.Speed = speed.Value; RefreshUi(); Changed(); };
            Controls.Add(bright); Controls.Add(speed);

            startup.BackColor = Theme.Card; startup.Location = new Point(AdjCard.Right - 22 - 40, AdjCard.Y + 192);
            startup.Checked = Store.StartupEnabled;
            startup.CheckedChanged += (o, e) =>
            {
                try { Store.StartupEnabled = startup.Checked; SetStatus(startup.Checked ? "Your setting will be restored at sign-in." : "Sign-in restore is off.", false); }
                catch (Exception ex) { SetStatus("Could not change sign-in setting: " + ex.Message, true); }
            };
            Controls.Add(startup);

            debounce.Interval = 110; debounce.Tick += (o, e) => { debounce.Stop(); Push(); };
            deviceTimer.Tick += (o, e) => CheckDevice();
            fgTimer.Interval = 1000; fgTimer.Tick += (o, e) => CheckForeground(); fgTimer.Start();
            var workerThread = new Thread(WorkerLoop) { IsBackground = true, Name = "keyboard" };
            workerThread.Start();
            Load += (o, e) => { ReadFromKeyboard(); };
            Resize += (o, e) => UpdateActivity();
            VisibleChanged += (o, e) => UpdateActivity();
            FormClosing += (o, e) => { closing = true; jobSignal.Set(); };
            FormClosed += (o, e) => { watcher.Dispose(); workerThread.Join(2500); };
            HandleCreated += (o, e) => { int on = 1; Native.DwmSetWindowAttribute(Handle, 20, ref on, 4); };
        }

        // Small spec chips in the top-right of the keyboard card, right-aligned: edition (with a body-color dot), then shared specs.
        void PaintSpecChips(Graphics g, KbModel km)
        {
            var f = Theme.F(8f, false); const float h = 24, gap = 6, pad = 11, dot = 9;
            var labels = new List<string> { km.Chip }; labels.AddRange(KbModel.Common);
            float x = Hero.Right - 22, y = Hero.Y + 22;
            for (int i = labels.Count - 1; i >= 0; i--)
            {
                bool first = i == 0;
                float tw = TextRenderer.MeasureText(g, labels[i], f, Size.Empty, TextFormatFlags.NoPadding).Width;
                float w = tw + pad * 2 + (first ? dot + 7 : 0);
                var r = new RectangleF(x - w, y, w, h);
                Theme.FillRound(g, r, h / 2, first ? Theme.TileHover : Theme.Tile);
                float tx = r.X + pad;
                if (first)
                {
                    var dr = new RectangleF(tx, y + (h - dot) / 2, dot, dot);
                    using (var b = new SolidBrush(km.Swatch)) g.FillEllipse(b, dr);
                    using (var pen = new Pen(Color.FromArgb(110, 255, 255, 255), 1f)) g.DrawEllipse(pen, dr);
                    tx += dot + 7;
                }
                TextRenderer.DrawText(g, labels[i], f, new Point((int)tx, (int)(y + (h - f.Height) / 2)), first ? Theme.Text : Theme.Sub, TextFormatFlags.NoPadding);
                x -= w + gap;
            }
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.Style |= 0x20000 | 0x80000; cp.ClassStyle |= 0x20000; return cp; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Y < TitleH) { Native.ReleaseCapture(); Native.SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }
            base.OnMouseDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (calibrating)
            {
                var kc = e.KeyCode;
                if (kc == System.Windows.Forms.Keys.Escape)
                {
                    if (wizard) FinishWizard(true);
                    else { calibrating = false; SetStatus("Calibration cancelled.", false); InvalidateHero(); }
                }
                else if (kc == System.Windows.Forms.Keys.Space) Tap();
                else if (kc == System.Windows.Forms.Keys.Back) { taps.Clear(); wizMsg = "Step restarted. Tap again from the next cycle."; InvalidateHero(); }
                else if (kc == System.Windows.Forms.Keys.S && wizard) { wizSkipped++; GoStep(wizIndex + 1); }
                e.Handled = true; e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        // ------------------------------------------------------- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            var m = Modes.ByCode(state.Mode);

            // title bar
            if (logo != null) g.DrawImage(logo, M, 15, 28, 28);
            Theme.Text_(g, "T-DAGGER", Theme.F(7.5f, true), Theme.Sub, M + 38, 13);
            Theme.Text_(g, "Lighting Control Panel", Theme.F(11.5f, true), Theme.Text, M + 36, 27);

            // hero
            Theme.FillRound(g, Hero, 16, Theme.Card);
            Theme.Text_(g, "Bora Lite", Theme.F(15f, true), Theme.Text, Hero.X + 22, Hero.Y + 16);
            int px = Hero.X + 22 + (int)g.MeasureString("Bora Lite", Theme.F(15f, true)).Width - 2;
            if (picker.Left != px) picker.Location = new Point(px, Hero.Y + 19);
            PaintSpecChips(g, picker.Model);
            // connection + effect line
            using (var b = new SolidBrush(statusBad ? Theme.Bad : (connected ? Theme.Accent : Theme.Faint))) g.FillEllipse(b, Hero.X + 24, Hero.Y + 50, 7, 7);
            string line = (connected ? "Connected" : "Not connected") + "   \u00B7   " + (m != null ? m.Name : "") + SyncNote(m);
            Theme.Text_(g, line, Theme.F(8.5f, false), Theme.Sub, Hero.X + 36, Hero.Y + 45);


            // effects
            Theme.FillRound(g, FxCard, 16, Theme.Card);
            Theme.Text_(g, "Effects", Theme.F(10.5f, true), Theme.Text, FxCard.X + 20, FxCard.Y + 16);

            // adjust
            Theme.FillRound(g, AdjCard, 16, Theme.Card);
            Theme.Text_(g, "Adjust", Theme.F(10.5f, true), Theme.Text, AdjCard.X + 22, AdjCard.Y + 16);
            bool bOn = m != null && m.Brightness, sOn = m != null && m.Speed;
            Row(g, "Brightness", bOn ? (state.Brightness * 20) + "%" : "Fixed", AdjCard.Y + 52, bOn);
            Row(g, "Speed", sOn ? state.Speed + " / 5" : "Fixed", AdjCard.Y + 118, sOn);
            using (var pen = new Pen(Theme.Hair)) g.DrawLine(pen, AdjCard.X + 22, AdjCard.Y + 178, AdjCard.Right - 22, AdjCard.Y + 178);
            Theme.Text_(g, "Restore at sign-in", Theme.F(9.5f, false), Theme.Text, AdjCard.X + 22, AdjCard.Y + 193);
            using (var b = new SolidBrush(statusBad ? Theme.Bad : Theme.Sub))
                g.DrawString(status, Theme.F(8.25f, false), b, new RectangleF(AdjCard.X + 22, AdjCard.Bottom - 44, AdjCard.Width - 44, 36));

            // footer: credit
            using (var f = new StringFormat { Alignment = StringAlignment.Center })
            using (var b = new SolidBrush(Theme.Faint))
                g.DrawString("Made by Tar   \u00B7   v3.2", Theme.F(8f, false), b, new RectangleF(0, FxCard.Bottom + 10, W, 18), f);

            using (var pen = new Pen(Theme.Hair)) g.DrawRectangle(pen, 0, 0, W - 1, H - 1);
        }

        string SyncNote(Mode m)
        {
            if (m == null) return "";
            if (m.Code == 6) return "   \u00B7   same style as the keyboard; individual keys may differ";
            if (m.Sync == Sync.Keys) return "   \u00B7   follows your typing";
            return "";
        }

        void PaintCalibration(Graphics g, Mode m)
        {
            if (m == null) return;
            float h = wizard ? 212 : 150;
            var r = new RectangleF(preview.Width / 2f - 320, preview.Height / 2f - h / 2, 640, h);
            using (var p = Theme.Round(r, 14))
            {
                using (var b = new SolidBrush(Color.FromArgb(244, 14, 16, 19))) g.FillPath(b, p);
                using (var pen = new Pen(Color.FromArgb(120, Theme.Accent))) g.DrawPath(pen, p);
            }
            float y = r.Y + 20;
            if (wizard)
            {
                Theme.Text_(g, "GUIDED CALIBRATION  \u00B7  STEP " + (wizIndex + 1) + " OF " + wizSteps.Count, Theme.F(7.5f, true), Theme.Accent, r.X + 24, y);
                // progress bar
                var bar = new RectangleF(r.X + 24, y + 18, r.Width - 48, 3);
                Theme.FillRound(g, bar, 1.5f, Theme.Tile);
                Theme.FillRound(g, new RectangleF(bar.X, bar.Y, bar.Width * wizIndex / wizSteps.Count, 3), 1.5f, Theme.Accent);
                y += 32;
            }
            Theme.Text_(g, m.Name + "  \u00B7  speed " + state.Speed, Theme.F(12f, true), Theme.Text, r.X + 24, y);
            using (var b = new SolidBrush(Theme.Sub))
                g.DrawString("Watch your keyboard and press Space " + m.Marker + ". Start from any cycle; six taps finish this step.",
                    Theme.F(9f, false), b, new RectangleF(r.X + 24, y + 30, r.Width - 48, 40));
            float dy = r.Bottom - (wizard ? 62 : 34);
            for (int i = 0; i < 6; i++)
                using (var b = new SolidBrush(i < taps.Count ? Theme.Accent : Theme.Tile)) g.FillEllipse(b, r.X + 24 + i * 20, dy, 10, 10);
            if (!string.IsNullOrEmpty(wizMsg))
                Theme.Text_(g, wizMsg, Theme.F(8.5f, false), wizMsgBad ? Theme.Bad : Theme.Accent, r.X + 150, dy - 3);
            string keys = wizard ? "Space  tap      Backspace  redo step      S  skip step      Esc  stop and keep what's measured"
                                 : "Space  tap      Backspace  redo      Esc  cancel";
            Theme.Text_(g, keys, Theme.F(8f, false), Theme.Faint, r.X + 24, r.Bottom - (wizard ? 32 : 18) + (wizard ? 0 : -2));
        }

        void Row(Graphics g, string name, string val, int y, bool on)
        {
            Theme.Text_(g, name, Theme.F(9.5f, false), on ? Theme.Text : Theme.Faint, AdjCard.X + 22, y);
            Theme.TextRight(g, val, Theme.F(9.5f, true), on ? Theme.Accent : Theme.Faint, AdjCard.Right - 22, y);
        }

        void InvalidateHero() { Invalidate(Hero); }

        // ------------------------------------------------------- behaviour

        void ReadFromKeyboard()
        {
            string err; var s = Keyboard.Read(out err);
            if (s != null) { state = s; connected = true; SetStatus("Changes apply to the keyboard instantly.", false); }
            else
            {
                var saved = Store.Load(); if (saved != null) state = saved; connected = false;
                SetStatus("Keyboard not detected. Plug it in and your setting will be applied automatically.", true);
            }
            wasPresent = connected;
            RefreshUi();
            // The keyboard's own animation clock can't be read, so restart its effect once to line the preview up.
            if (connected) SyncNow();
        }

        void RefreshUi()
        {
            var m = Modes.ByCode(state.Mode);
            foreach (var tl in tiles) { bool sel = tl.Mode == m; if (tl.Selected != sel) { tl.Selected = sel; tl.Invalidate(); } }
            bright.Value = state.Brightness; speed.Value = state.Speed;
            bright.Enabled = m != null && m.Brightness; speed.Enabled = m != null && m.Speed;
            preview.Mode = state.Mode; preview.Brightness = state.Brightness; preview.Speed = state.Speed;
            preview.SetPeriod(PeriodFor(m, state.Speed));
            bool timed = m != null && m.Sync == Sync.Timed;
            syncBtn.Visible = timed; calBtn.Visible = timed;
            UpdateActivity();
            preview.Invalidate(); InvalidateHero(); Invalidate(AdjCard);
        }

        static double PeriodFor(Mode m, int speed)
        {
            if (m == null || m.Sync != Sync.Timed) return 3;
            double measured = Modes.MeasuredCycle(m.Code, speed);
            if (measured > 0) return measured;
            double cal = Store.GetPeriod(m.Code, speed);
            if (cal > 0) return cal;
            // scale from a calibration at another speed if there is one
            for (int s = 1; s <= 5; s++)
            {
                double c = Store.GetPeriod(m.Code, s);
                if (c > 0) return c * Modes.SpeedFactor[speed] / Modes.SpeedFactor[s];
            }
            return m.BasePeriod * Modes.SpeedFactor[speed];
        }

        void Changed() { if (calibrating) return; debounce.Stop(); debounce.Start(); }

        // Looping effects: restart the effect on the keyboard with every change (mode, speed or brightness),
        // so the keyboard and the preview always start the cycle together. No button needed.
        void Push()
        {
            var m = Modes.ByCode(state.Mode);
            Enqueue(state, m != null && m.Sync == Sync.Timed, true);
        }

        void SyncNow()
        {
            var m = Modes.ByCode(state.Mode);
            if (m == null || m.Sync != Sync.Timed) { preview.Restart(); return; }
            Enqueue(state, true, false);
        }

        // ------------------------------------------------- keyboard worker thread
        // All USB traffic runs here, never on the window thread. Each command takes 0.1-1.5 s; on the window
        // thread that would freeze the app and, in Reactive mode, delay every keystroke on the PC through the hook.
        // Only the newest request is kept, so rapid changes collapse into one.

        readonly object jobLock = new object();
        readonly AutoResetEvent jobSignal = new AutoResetEvent(false);
        LightState pendingJob; bool pendingRestart, pendingSave;
        volatile bool closing;

        void Enqueue(LightState s, bool restart, bool save)
        {
            lock (jobLock) { pendingJob = s.Clone(); pendingRestart = restart; pendingSave = pendingSave || save; }
            jobSignal.Set();
        }

        void WorkerLoop()
        {
            while (!closing)
            {
                jobSignal.WaitOne(500);
                LightState job; bool restart, save;
                lock (jobLock) { job = pendingJob; restart = pendingRestart; save = pendingSave; pendingJob = null; pendingSave = false; }
                if (job == null) continue;
                string err; bool ok;
                try
                {
                    if (restart)
                    {
                        // Briefly switch to Steady and back: this restarts the effect from the beginning.
                        var tmp = job.Clone(); tmp.Mode = 1;
                        ok = Keyboard.Write(tmp, true, out err) && Keyboard.Write(job.Clone(), true, out err);
                    }
                    else ok = Keyboard.Write(job, false, out err);
                }
                catch (Exception ex) { ok = false; err = "Keyboard error: " + ex.Message; Store.Log("worker: " + ex); }
                double sent = Keyboard.LastSendSeconds;
                bool newer; lock (jobLock) newer = pendingJob != null;
                if (closing) break;
                var j = job; var r = restart; var sv = save; var o = ok; var e2 = err;
                try { BeginInvoke(new Action(() => OnApplied(j, r, sv, o, e2, sent, newer))); } catch { }
            }
        }

        void OnApplied(LightState job, bool restart, bool save, bool ok, string err, double sent, bool newer)
        {
            if (ok)
            {
                connected = true; wasPresent = true;
                if (!newer)
                {
                    if (restart) { preview.Restart(); preview.SetAnchor(sent); } else preview.Restart();
                }
                if (save)
                {
                    try { Store.Save(job); } catch (Exception ex) { SetStatus("Applied, but could not save: " + ex.Message, true); InvalidateHero(); return; }
                }
                SetStatus(save ? "Applied and saved." : "Keyboard connected. Changes apply instantly.", false);
            }
            else { connected = false; SetStatus(err, true); }
            InvalidateHero();
        }

        // ------------------------------------------- plug / unplug, sleep / resume

        readonly System.Windows.Forms.Timer deviceTimer = new System.Windows.Forms.Timer();
        bool wasPresent, resumePending;

        protected override void WndProc(ref Message msg)
        {
            const int WM_DEVICECHANGE = 0x219, WM_POWERBROADCAST = 0x218;
            if (msg.Msg == WM_DEVICECHANGE) { deviceTimer.Stop(); deviceTimer.Interval = 1500; deviceTimer.Start(); }
            else if (msg.Msg == WM_POWERBROADCAST && ((int)msg.WParam == 0x12 || (int)msg.WParam == 0x7))
            { resumePending = true; deviceTimer.Stop(); deviceTimer.Interval = 3000; deviceTimer.Start(); }
            base.WndProc(ref msg);
        }

        // A USB device was added or removed, or the PC woke up: re-apply the setting if the keyboard (re)appeared.
        void CheckDevice()
        {
            deviceTimer.Stop();
            bool present = Keyboard.IsPresent();
            if (present && (!wasPresent || resumePending))
            {
                var m = Modes.ByCode(state.Mode);
                SetStatus(resumePending && wasPresent ? "Re-syncing after sleep..." : "Keyboard connected, applying your setting...", false);
                Enqueue(state, m != null && m.Sync == Sync.Timed, false);
            }
            else if (!present && wasPresent)
            {
                connected = false;
                SetStatus("Keyboard disconnected. Your setting will be re-applied when it is plugged back in.", true);
            }
            wasPresent = present; resumePending = false;
            InvalidateHero();
        }

        // Animate the preview only while it can be seen and something moves; watch keys only for Reactive.
        // True while another app fills a whole monitor (a fullscreen or borderless game, a fullscreen video).
        // The preview then stops drawing and the Reactive key watcher stops, so nothing runs during play.
        bool otherAppFullscreen;
        static bool IsOtherAppFullscreen(IntPtr self)
        {
            try
            {
                IntPtr h = Native.GetForegroundWindow();
                if (h == IntPtr.Zero || h == self) return false;
                var cls = new System.Text.StringBuilder(64); Native.GetClassName(h, cls, 64);
                string c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;   // desktop / taskbar
                Native.RECT r; if (!Native.GetWindowRect(h, out r)) return false;
                var b = Screen.FromHandle(h).Bounds;
                return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
            }
            catch { return false; }
        }

        void CheckForeground()
        {
            bool fs = IsOtherAppFullscreen(Handle);
            if (fs != otherAppFullscreen) { otherAppFullscreen = fs; UpdateActivity(); }
        }

        void UpdateActivity()
        {
            var m = Modes.ByCode(state.Mode);
            bool visible = WindowState != FormWindowState.Minimized && Visible && !otherAppFullscreen;
            bool moving = m != null && (m.Sync == Sync.Timed || m.Sync == Sync.Keys);
            preview.Animate = visible && moving;
            if (visible && m != null && m.Sync == Sync.Keys) watcher.Start(); else watcher.Stop();
        }

        void StartCalibration()
        {
            var m = Modes.ByCode(state.Mode); if (m == null || m.Sync != Sync.Timed) return;
            calibrating = true; taps.Clear(); Activate(); Focus(); InvalidateHero();
        }

        void Tap()
        {
            taps.Add(preview.Now); wizMsg = null; InvalidateHero();
            if (taps.Count < 6) return;
            var gaps = new List<double>();
            for (int i = 1; i < taps.Count; i++) gaps.Add(taps[i] - taps[i - 1]);
            var sorted = new List<double>(gaps); sorted.Sort(); double period = sorted[sorted.Count / 2];
            // average the gaps that agree with the median, to reject one missed or doubled tap
            double sum = 0; int n = 0; foreach (var gp in gaps) if (Math.Abs(gp - period) < period * 0.2) { sum += gp; n++; }
            var gapText = string.Join(" ", gaps.ConvertAll(x => x.ToString("0.000", CultureInfo.InvariantCulture)).ToArray());
            if (n < 4 || period < 0.25 || period > 60)
            {
                Store.Log("calibration rejected mode=" + state.Mode + " speed=" + state.Speed + " gaps=" + gapText);
                taps.Clear(); wizMsg = "Taps were uneven, let's do this one again."; wizMsgBad = true;
                if (!wizard) { calibrating = false; SetStatus("Taps were too uneven. Try Calibrate again.", true); }
                InvalidateHero(); return;
            }
            period = sum / n;
            Store.SetPeriod(state.Mode, state.Speed, period);
            Store.Log("calibrated mode=" + state.Mode + " speed=" + state.Speed + " period=" + period.ToString("0.000", CultureInfo.InvariantCulture) + " gaps=" + gapText);
            if (wizard) { wizDone++; GoStep(wizIndex + 1); return; }
            calibrating = false;
            preview.Period = period;
            preview.SetAnchor(taps[taps.Count - 1]);     // your last tap is the start of a cycle
            SetStatus("Calibrated: one cycle every " + period.ToString("0.00") + " s at speed " + state.Speed + ".", false);
            InvalidateHero();
        }

        // ---------------------------------------------------- guided calibration

        bool wizard, wizMsgBad; int wizIndex, wizDone, wizSkipped; string wizMsg;
        readonly List<KeyValuePair<byte, int>> wizSteps = new List<KeyValuePair<byte, int>>();
        LightState wizRestore;

        void StartWizard()
        {
            if (!connected) { SetStatus("Connect the keyboard first.", true); return; }
            wizSteps.Clear();
            foreach (var m in Modes.All)
                if (m.Sync == Sync.Timed)
                    foreach (int sp in new[] { 1, 3, 5 }) wizSteps.Add(new KeyValuePair<byte, int>(m.Code, sp));
            wizRestore = state.Clone(); wizard = true; calibrating = true; wizDone = 0; wizSkipped = 0;
            watcher.Stop(); Activate(); Focus();
            GoStep(0);
        }

        void GoStep(int i)
        {
            wizIndex = i; taps.Clear(); wizMsg = null; wizMsgBad = false;
            if (i >= wizSteps.Count) { FinishWizard(false); return; }
            state.Mode = wizSteps[i].Key; state.Speed = wizSteps[i].Value; state.Brightness = 5;
            RefreshUi();
            string err;
            if (!Keyboard.Write(state.Clone(), true, out err)) { wizMsg = err; wizMsgBad = true; }
            preview.Restart();
            InvalidateHero();
        }

        void FinishWizard(bool stopped)
        {
            wizard = false; calibrating = false; taps.Clear();
            state = wizRestore; modeChangedSinceSend = true;
            RefreshUi(); Push();
            Store.Log("guided calibration " + (stopped ? "stopped" : "finished") + ": measured=" + wizDone + " skipped=" + wizSkipped);
            SetStatus((stopped ? "Calibration stopped. " : "Calibration finished. ") + wizDone + " of " + wizSteps.Count + " steps measured and saved.", false);
            InvalidateHero();
        }

        void SetStatus(string s, bool bad) { status = s; statusBad = bad; Invalidate(AdjCard); }
    }

    // ----------------------------------------------------------------- native

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
        [StructLayout(LayoutKind.Sequential)] public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }
        [StructLayout(LayoutKind.Sequential)] public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
        public delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] b, int len);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SetupDiGetClassDevs(ref Guid g, string e, IntPtr p, int f);
        [DllImport("setupapi.dll")] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA did);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA did, IntPtr detail, uint size, out uint need, IntPtr dev);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    }
}
