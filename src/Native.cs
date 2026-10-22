using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LumaBar
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr item);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] internal static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wparam,
            string lparam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] internal static extern uint GetGuiResources(IntPtr process, uint flags);
        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int GetFrameBounds(IntPtr hwnd, int attr, out Rect value, int size);

        internal static void EnableDpi()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; }
            catch (EntryPointNotFoundException) { }
            SetProcessDPIAware();
        }

        internal static bool NotifyThemeWindow(IntPtr hwnd, bool isShell, uint timeout)
        {
            IntPtr result;
            bool immersive = SendMessageTimeout(hwnd, 0x001A, IntPtr.Zero, "ImmersiveColorSet", 0x0022, timeout, out result) != IntPtr.Zero;
            // Explorer also caches tray settings. ImmersiveColorSet alone can
            // leave the taskbar dark while Start and the registry are light.
            bool tray = !isShell || SendMessageTimeout(hwnd, 0x001A, IntPtr.Zero, "TraySettings", 0x0022, timeout, out result) != IntPtr.Zero;
            return immersive && tray;
        }

        // Enumerate instead of HWND_BROADCAST: bound the TOTAL wait, not merely
        // each recipient's timeout, and notify Explorer's hidden hosts as well.
        internal static void RefreshTaskbars()
        {
            List<IntPtr> taskbars = new List<IntPtr>();
            List<IntPtr> shell = new List<IntPtr>();
            List<IntPtr> others = new List<IntPtr>();
            uint shellPid;
            GetWindowThreadProcessId(GetShellWindow(), out shellPid);
            EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            {
                string cls = ClassOf(hwnd);
                if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                    taskbars.Add(hwnd);
                else
                {
                    uint pid; GetWindowThreadProcessId(hwnd, out pid);
                    if (shellPid != 0 && pid == shellPid) shell.Add(hwnd);
                    else others.Add(hwnd);
                }
                return true;
            }, IntPtr.Zero);
            Stopwatch budget = Stopwatch.StartNew();
            int delivered = 0, failed = 0;
            foreach (IntPtr hwnd in taskbars)
            {
                if (budget.ElapsedMilliseconds >= 1200) break;
                if (NotifyThemeWindow(hwnd, true, 100)) delivered++; else failed++;
            }
            foreach (IntPtr hwnd in shell)
            {
                if (budget.ElapsedMilliseconds >= 1200) break;
                if (NotifyThemeWindow(hwnd, true, 30)) delivered++; else failed++;
            }
            foreach (IntPtr hwnd in others)
            {
                if (budget.ElapsedMilliseconds >= 1200) break;
                if (NotifyThemeWindow(hwnd, false, 20)) delivered++; else failed++;
            }
            Files.Log(String.Format("Theme refresh: taskbars={0}, delivered={1}, failed={2}, ms={3}. Delivery is not visual confirmation.", taskbars.Count, delivered, failed, budget.ElapsedMilliseconds));
        }

        // Diagnostic pixels only, never an image. Do not equate a registry value
        // or a successful SendMessage with the taskbar's rendered background.
        internal static double? TaskbarBrightness()
        {
            IntPtr hwnd = FindWindow("Shell_TrayWnd", null);
            Rect rect;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out rect) || rect.Bottom - rect.Top < 10) return null;
            List<double> samples = new List<double>();
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            try
            {
                for (int column = 1; column <= 23; column++)
                {
                    Point p = new Point(rect.Left + (rect.Right - rect.Left) * column / 24,
                        rect.Top + Math.Max(3, (rect.Bottom - rect.Top) / 6));
                    if (GetAncestor(WindowFromPoint(p), 2) != hwnd) continue;
                    uint pixel = GetPixel(dc, p.X, p.Y);
                    if (pixel != 0xFFFFFFFF)
                        samples.Add(Brightness.Of((byte)(pixel & 255), (byte)((pixel >> 8) & 255), (byte)((pixel >> 16) & 255)));
                }
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            return samples.Count >= 8 ? (double?)Brightness.Median(samples) : null;
        }

        internal static string ClassOf(IntPtr hwnd)
        {
            StringBuilder name = new StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            return name.ToString();
        }
    }

    // One bounded GPU/desktop read per frame instead of 77 GetPixel screen
    // round-trips. The 96x64 thumbnail lives only in memory and is disposed.
    internal sealed class WindowPixels : IDisposable
    {
        private IntPtr dc, bitmap, previous;
        private readonly Native.Point origin;
        private readonly int width, height;
        private WindowPixels(Native.Point origin, int width, int height) { this.origin = origin; this.width = width; this.height = height; }
        internal static WindowPixels Capture(Native.Point origin, int width, int height)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            WindowPixels pixels = new WindowPixels(origin, width, height);
            bool success = false;
            try
            {
                pixels.dc = Native.CreateCompatibleDC(screen);
                pixels.bitmap = Native.CreateCompatibleBitmap(screen, 96, 64);
                if (pixels.dc == IntPtr.Zero || pixels.bitmap == IntPtr.Zero) return null;
                pixels.previous = Native.SelectObject(pixels.dc, pixels.bitmap);
                if (pixels.previous == IntPtr.Zero || pixels.previous == new IntPtr(-1)) { pixels.previous = IntPtr.Zero; return null; }
                Native.SetStretchBltMode(pixels.dc, 3);
                success = Native.StretchBlt(pixels.dc, 0, 0, 96, 64, screen, origin.X, origin.Y, width, height, 0x40CC0020);
                return success ? pixels : null;
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screen);
                if (!success) pixels.Dispose();
            }
        }
        internal uint At(Native.Point point)
        {
            int x = (point.X - origin.X) * 96 / width, y = (point.Y - origin.Y) * 64 / height;
            if (x < 0 || x >= 96 || y < 0 || y >= 64) return 0xFFFFFFFF;
            return Native.GetPixel(dc, x, y);
        }
        public void Dispose()
        {
            if (previous != IntPtr.Zero) { Native.SelectObject(dc, previous); previous = IntPtr.Zero; }
            if (bitmap != IntPtr.Zero) { Native.DeleteObject(bitmap); bitmap = IntPtr.Zero; }
            if (dc != IntPtr.Zero) { Native.DeleteDC(dc); dc = IntPtr.Zero; }
        }
    }

    internal sealed class WindowSample
    {
        public IntPtr Window;
        public string ProcessName;
        public double Brightness;
        public int Count;
        public string Reason;
        public bool Valid;
        public bool ExplicitRule;
        public bool IsApplication;
        public bool IsWallpaper;
        public long WallpaperContext;
        public long DecisionContext { get { return IsWallpaper ? WallpaperContext : Window.ToInt64(); } }
        public double DecisionBrightness { get { return IsWallpaper ? (double)WallpaperAnalyzer.ToneOf(Brightness) : Brightness; } }
    }

    internal static class WindowSampler
    {
        private static readonly int OwnProcessId = Process.GetCurrentProcess().Id;
        internal static WindowSample Read(Settings settings) { return Read(settings, false); }
        internal static WindowSample Read(Settings settings, bool includeOwnWindow)
        { return Read(settings, includeOwnWindow, IntPtr.Zero); }
        // Explicit target is for visible synthetic-window regression tests;
        // production always resolves the actual foreground window above.
        internal static WindowSample Read(Settings settings, bool includeOwnWindow, IntPtr targetWindow)
        { return Read(settings, includeOwnWindow, targetWindow, true); }
        internal static WindowSample Read(Settings settings, bool includeOwnWindow, IntPtr targetWindow, bool applyCoveragePolicy,
            Func<System.Drawing.Rectangle, WindowSample> wallpaperReader = null)
        {
            WindowSample sample = new WindowSample();
            IntPtr hwnd = targetWindow == IntPtr.Zero ? Native.GetForegroundWindow() : targetWindow;
            sample.Window = hwnd;
            sample.Reason = "等待可取样的活动窗口";
            if (!includeOwnWindow && (Native.GetAsyncKeyState(0x12) & 0x8000) != 0)
            { sample.Reason = "正在切换窗口：暂缓适应"; return sample; }
            if (hwnd == IntPtr.Zero || Native.IsIconic(hwnd) || !Native.IsWindowVisible(hwnd))
                return applyCoveragePolicy ? ReadWallpaper(sample, hwnd, wallpaperReader) : sample;
            string cls = Native.ClassOf(hwnd);
            if ((cls == "Progman" || cls == "WorkerW") && applyCoveragePolicy) return ReadWallpaper(sample, hwnd, wallpaperReader);
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "Progman" || cls == "WorkerW" || cls == "#32768" || cls == "MultitaskingViewFrame" || cls == "TaskSwitcherWnd")
            { sample.Reason = "桌面或系统菜单：保持当前明暗"; return sample; }
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (!includeOwnWindow && pid == OwnProcessId) { sample.Reason = "设置窗口：保持当前明暗"; return sample; }
            try
            {
                using (Process process = Process.GetProcessById((int)pid)) sample.ProcessName = process.ProcessName;
            }
            catch (ArgumentException) { return sample; }
            catch (InvalidOperationException) { return sample; }
            catch (System.ComponentModel.Win32Exception)
            { sample.Reason = "暂时无法读取应用信息：保持当前明暗"; return sample; }
            string[] systemProcesses = { "StartMenuExperienceHost", "ShellExperienceHost", "ShellHost", "SearchHost", "SearchApp", "LockApp", "LogonUI", "Taskmgr" };
            foreach (string name in systemProcesses)
                if (String.Equals(name, sample.ProcessName, StringComparison.OrdinalIgnoreCase))
                { sample.Reason = "系统界面：保持当前明暗"; return sample; }
            sample.IsApplication = true;
            int cloaked;
            if (Native.DwmGetWindowAttribute(hwnd, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) return sample;
            IntPtr owner = Native.GetWindow(hwnd, 4);
            if (settings.HoldOwnedDialogs && owner != IntPtr.Zero && Native.IsWindowVisible(owner))
            {
                uint ownerPid; Native.GetWindowThreadProcessId(owner, out ownerPid);
                if (ownerPid == pid)
                { sample.Reason = "临时对话框：保持主窗口明暗"; return sample; }
            }
            if (applyCoveragePolicy)
            {
                System.Windows.Forms.Screen screen = Screen.FromHandle(hwnd);
                Native.Rect frame;
                bool expanded = Native.IsZoomed(hwnd);
                if (!expanded && (Native.GetFrameBounds(hwnd, 9, out frame, Marshal.SizeOf(typeof(Native.Rect))) == 0 || Native.GetWindowRect(hwnd, out frame)))
                    expanded = WindowCoverage.Expanded(false, System.Drawing.Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom), screen.Bounds, screen.WorkingArea);
                if (!expanded) return ReadWallpaper(sample, hwnd, wallpaperReader);
            }
            foreach (string name in settings.IgnoredProcesses)
                if (String.Equals(Settings.ProcessKey(name), sample.ProcessName, StringComparison.OrdinalIgnoreCase))
                { sample.Reason = "已忽略应用：" + sample.ProcessName; return sample; }
            string rule = settings.RuleFor(sample.ProcessName);
            if (rule == "hold") { sample.Reason = "应用规则：保持当前明暗 · " + sample.ProcessName; return sample; }
            if (rule == "auto" && settings.HoldMediaPlayers)
            {
                string[] players = { "vlc", "mpv", "PotPlayerMini", "PotPlayerMini64", "wmplayer", "Microsoft.Media.Player" };
                foreach (string player in players)
                    if (String.Equals(player, sample.ProcessName, StringComparison.OrdinalIgnoreCase))
                    { sample.Reason = "播放器：保持当前明暗（可通过应用规则更改）"; return sample; }
            }
            if (rule == "light" || rule == "dark")
            {
                sample.ExplicitRule = true; sample.Valid = true;
                sample.Brightness = rule == "light" ? 1 : 0;
                sample.Reason = "应用规则：" + (rule == "light" ? "浅色" : "深色") + " · " + sample.ProcessName;
                return sample;
            }
            Native.Rect client;
            Native.Point origin = new Native.Point(0, 0);
            if (!Native.GetClientRect(hwnd, out client) || !Native.ClientToScreen(hwnd, ref origin)) return sample;
            int width = client.Right - client.Left, height = client.Bottom - client.Top;
            if (width < 160 || height < 100) return sample;
            List<double> bottom = new List<double>(), content = new List<double>(), top = new List<double>();
            using (WindowPixels pixels = WindowPixels.Capture(origin, width, height))
            {
                if (pixels == null) { sample.Reason = "暂时无法取样窗口：保持当前明暗"; return sample; }
                SampleRows(bottom, pixels, hwnd, origin, width, height, new double[] { 0.78, 0.86, 0.93 });
                SampleRows(content, pixels, hwnd, origin, width, height, new double[] { 0.38, 0.58 });
                SampleRows(top, pixels, hwnd, origin, width, height, new double[] { 0.06, 0.14 });
            }
            if ((targetWindow == IntPtr.Zero && Native.GetForegroundWindow() != hwnd) || bottom.Count < 12 || content.Count < 8 || top.Count < 8)
            { sample.Reason = "窗口切换或被遮挡：等待稳定取样"; return sample; }
            sample.Count = bottom.Count + content.Count + top.Count;
            sample.Brightness = Brightness.Regional(bottom, content, top);
            sample.Valid = true;
            sample.Reason = "底部优先识别 · " + sample.ProcessName;
            return sample;
        }

        private static WindowSample ReadWallpaper(WindowSample application, IntPtr hwnd, Func<System.Drawing.Rectangle, WindowSample> reader)
        {
            WindowSample sample = (reader ?? WallpaperSampler.Read)(Screen.FromHandle(hwnd).Bounds);
            sample.Window = hwnd;
            sample.ProcessName = application.ProcessName;
            sample.IsApplication = application.IsApplication;
            return sample;
        }

        private static void SampleRows(List<double> values, WindowPixels pixels, IntPtr hwnd, Native.Point origin, int width, int height, double[] rows)
        {
            foreach (double row in rows)
                for (int col = 1; col <= 11; col++)
                    Add(values, pixels, hwnd, new Native.Point(origin.X + width * col / 12, origin.Y + (int)(height * row)));
        }

        private static void Add(List<double> values, WindowPixels pixels, IntPtr expected, Native.Point point)
        {
            IntPtr at = Native.WindowFromPoint(point);
            if (Native.GetAncestor(at, 2) != expected) return; // Avoid sampling another window/monitor/taskbar.
            uint pixel = pixels.At(point);
            if (pixel == 0xFFFFFFFF) return;
            byte r = (byte)(pixel & 255), g = (byte)((pixel >> 8) & 255), b = (byte)((pixel >> 16) & 255);
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 100) return; // Ignore saturated toolbars/images.
            values.Add(Brightness.Of(r, g, b));
        }
    }
}
