using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LumaBar
{
    public static class WindowCoverage
    {
        public static bool Expanded(bool maximized, Rectangle frame, Rectangle monitor, Rectangle workArea)
        {
            if (maximized) return true;
            return Covers(frame, monitor) || Covers(frame, workArea);
        }
        private static bool Covers(Rectangle frame, Rectangle target)
        {
            return target.Width > 0 && target.Height > 0 && frame.Left <= target.Left + 2 && frame.Top <= target.Top + 2 &&
                frame.Right >= target.Right - 2 && frame.Bottom >= target.Bottom - 2;
        }
    }

    internal sealed class WallpaperDescription
    {
        public string Path;
        public int Position = 4; // center, tile, stretch, fit, fill, span (0..5)
        public Color Background;
        public Rectangle Monitor;
        public Rectangle VirtualDesktop;
    }

    internal static class WallpaperAnalyzer
    {
        internal static Tone ToneOf(double brightness) { return brightness >= 0.5 ? Tone.Light : Tone.Dark; }
        internal static double Analyze(Bitmap image, WallpaperDescription description)
        {
            List<double> values = new List<double>();
            Rectangle area = description.Position == 5 ? description.VirtualDesktop : description.Monitor;
            double sx = 1, sy = 1;
            if (image != null)
            {
                if (description.Position == 2) { sx = (double)area.Width / image.Width; sy = (double)area.Height / image.Height; }
                else if (description.Position >= 3)
                {
                    double x = (double)area.Width / image.Width, y = (double)area.Height / image.Height;
                    sx = sy = description.Position == 3 ? Math.Min(x, y) : Math.Max(x, y);
                }
            }
            for (int row = 0; row < 40; row++)
                for (int col = 0; col < 64; col++)
                {
                    Color color = description.Background;
                    if (image != null)
                    {
                        double x = description.Monitor.Left + (col + 0.5) * description.Monitor.Width / 64;
                        double y = description.Monitor.Top + (row + 0.5) * description.Monitor.Height / 40;
                        double px, py;
                        if (description.Position == 1)
                        {
                            px = Mod(x - description.VirtualDesktop.Left, image.Width);
                            py = Mod(y - description.VirtualDesktop.Top, image.Height);
                        }
                        else
                        {
                            px = (x - area.Left - (area.Width - image.Width * sx) / 2) / sx;
                            py = (y - area.Top - (area.Height - image.Height * sy) / 2) / sy;
                        }
                        if (px >= 0 && py >= 0 && px < image.Width && py < image.Height)
                        {
                            Color pixel = image.GetPixel((int)px, (int)py);
                            double alpha = pixel.A / 255.0;
                            color = Color.FromArgb((int)(pixel.R * alpha + color.R * (1 - alpha)),
                                (int)(pixel.G * alpha + color.G * (1 - alpha)), (int)(pixel.B * alpha + color.B * (1 - alpha)));
                        }
                    }
                    values.Add(Brightness.Of(color.R, color.G, color.B));
                }
            return Brightness.Median(values);
        }
        private static double Mod(double value, double modulus) { return (value % modulus + modulus) % modulus; }
    }

    internal static class WallpaperSampler
    {
        private sealed class Cache
        {
            internal long NextCheck;
            internal string Fingerprint;
            internal double? Brightness;
            internal string Reason;
        }
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static readonly Dictionary<string, Cache> cache = new Dictionary<string, Cache>();

        internal static WindowSample Read(Rectangle monitor)
        {
            string key = monitor.ToString() + SystemInformation.VirtualScreen.ToString();
            Cache entry;
            if (!cache.TryGetValue(key, out entry))
            {
                if (cache.Count > 16) cache.Clear();
                entry = new Cache(); cache[key] = entry;
            }
            if (entry.NextCheck <= clock.ElapsedMilliseconds)
            {
                entry.NextCheck = clock.ElapsedMilliseconds + 2000;
                try
                {
                    WallpaperDescription description = Describe(monitor);
                    string fingerprint = description.Path + "|" + description.Position + "|" + description.Background.ToArgb() + "|" + key;
                    if (!String.IsNullOrEmpty(description.Path))
                    {
                        FileInfo info = new FileInfo(description.Path);
                        if (!info.Exists) throw new IOException("Wallpaper unavailable");
                        fingerprint += "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length;
                    }
                    if (entry.Brightness == null || entry.Fingerprint != fingerprint)
                    {
                        if (String.IsNullOrEmpty(description.Path)) entry.Brightness = WallpaperAnalyzer.Analyze(null, description);
                        else
                        {
                            using (FileStream stream = new FileStream(description.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            using (Bitmap image = new Bitmap(stream))
                                entry.Brightness = WallpaperAnalyzer.Analyze(image, description);
                        }
                        entry.Fingerprint = fingerprint;
                    }
                    entry.Reason = "普通窗口 / 桌面：跟随壁纸";
                }
                catch (Exception error)
                {
                    if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is ArgumentException) &&
                        !(error is ExternalException) && !(error is NotSupportedException) && !(error is OutOfMemoryException)) throw;
                    entry.Brightness = null;
                    entry.Reason = "无法读取壁纸：保持当前明暗";
                }
            }
            return new WindowSample
            {
                Valid = entry.Brightness.HasValue, IsWallpaper = true,
                Brightness = entry.Brightness ?? 0, Reason = entry.Reason,
                WallpaperContext = unchecked(long.MinValue ^ ((long)(uint)monitor.Left << 32) ^ (uint)monitor.Top)
            };
        }

        internal static WallpaperDescription Describe(Rectangle monitor)
        {
            WallpaperDescription result = new WallpaperDescription { Monitor = monitor, VirtualDesktop = SystemInformation.VirtualScreen };
            object instance = null;
            try
            {
                instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")));
                IDesktopWallpaper wallpaper = (IDesktopWallpaper)instance;
                uint count; wallpaper.GetMonitorDevicePathCount(out count);
                string selected = null;
                long best = 0;
                for (uint index = 0; index < count; index++)
                {
                    string device; wallpaper.GetMonitorDevicePathAt(index, out device);
                    Native.Rect rect;
                    if (wallpaper.GetMonitorRECT(device, out rect) != 0) continue;
                    Rectangle overlap = Rectangle.Intersect(monitor, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
                    long area = (long)Math.Max(0, overlap.Width) * Math.Max(0, overlap.Height);
                    if (area > best) { best = area; selected = device; }
                }
                if (selected == null) throw new COMException("No wallpaper monitor");
                wallpaper.GetWallpaper(selected, out result.Path);
                uint background; wallpaper.GetBackgroundColor(out background);
                result.Background = FromColorRef(background);
                wallpaper.GetPosition(out result.Position);
                return result;
            }
            catch (COMException) { }
            catch (InvalidCastException) { }
            catch (UnauthorizedAccessException) { }
            finally { if (instance != null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance); }
            // Fallback for machines where the Shell COM service is unavailable.
            // This API supplies the common/primary wallpaper, not per-monitor images.
            StringBuilder path = new StringBuilder(32768);
            if (!SystemParametersInfo(0x0073, (uint)path.Capacity, path, 0)) throw new IOException("Wallpaper API unavailable");
            result.Path = path.ToString();
            result.Background = FromColorRef(GetSysColor(1));
            using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
            {
                string tile = key == null ? "0" : Convert.ToString(key.GetValue("TileWallpaper", "0"));
                string style = key == null ? "10" : Convert.ToString(key.GetValue("WallpaperStyle", "10"));
                result.Position = tile == "1" ? 1 : style == "0" ? 0 : style == "2" ? 2 : style == "6" ? 3 : style == "22" ? 5 : 4;
            }
            return result;
        }
        private static Color FromColorRef(uint value) { return Color.FromArgb((int)(value & 255), (int)((value >> 8) & 255), (int)((value >> 16) & 255)); }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfo(uint action, uint size, StringBuilder value, uint flags);
        [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    }

    // IUnknown vtable prefix through GetPosition, in Windows SDK declaration order.
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] out string path);
        void GetMonitorDevicePathAt(uint index, [MarshalAs(UnmanagedType.LPWStr)] out string monitor);
        void GetMonitorDevicePathCount(out uint count);
        [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitor, out Native.Rect rect);
        void SetBackgroundColor(uint color);
        void GetBackgroundColor(out uint color);
        void SetPosition(int position);
        void GetPosition(out int position);
    }
}
