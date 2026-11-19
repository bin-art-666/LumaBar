using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LumaBar
{
    // A small per-pixel-alpha window; no screenshots, hooks or input interception.
    internal sealed class TaskbarGlowWindow : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Pair { internal int X, Y; internal Pair(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct Blend { internal byte Operation, Flags, Alpha, Format; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screen, ref Pair position, ref Pair size,
            IntPtr source, ref Pair origin, uint key, ref Blend blend, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x20 | 0x80 | 0x80000; return p; }
        }
        internal TaskbarGlowWindow()
        {
            AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; Enabled = false; TabStop = false;
            StartPosition = FormStartPosition.Manual;
        }
        internal void Present(Bitmap sprite, Point pointer, Rectangle bounds, double opacity)
        {
            Rectangle area = Clip(pointer, bounds, sprite.Size);
            if (area.IsEmpty) { Hide(); return; }
            using (Bitmap frame = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(frame))
                    g.DrawImageUnscaled(sprite, pointer.X - sprite.Width / 2 - area.X, pointer.Y - sprite.Height / 2 - area.Y);
                IntPtr screen = Native.GetDC(IntPtr.Zero), dc = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    dc = Native.CreateCompatibleDC(screen);
                    bitmap = frame.GetHbitmap(Color.FromArgb(0));
                    old = Native.SelectObject(dc, bitmap);
                    Pair position = new Pair(area.X, area.Y), size = new Pair(area.Width, area.Height), origin = new Pair(0, 0);
                    Blend blend = new Blend { Alpha = (byte)Math.Round(Math.Max(0, Math.Min(1, opacity)) * 255), Format = 1 };
                    if (!UpdateLayeredWindow(Handle, screen, ref position, ref size, dc, ref origin, 0, ref blend, 2))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (!SetWindowPos(Handle, new IntPtr(-1), area.X, area.Y, area.Width, area.Height, 0x10 | 0x40))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally
                {
                    if (old != IntPtr.Zero) Native.SelectObject(dc, old);
                    if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
                    if (dc != IntPtr.Zero) Native.DeleteDC(dc);
                    if (screen != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }
        internal static Rectangle Clip(Point pointer, Rectangle taskbar, Size sprite)
        { return Rectangle.Intersect(taskbar, new Rectangle(pointer.X - sprite.Width / 2, pointer.Y - sprite.Height / 2, sprite.Width, sprite.Height)); }
        internal static Bitmap CreateSprite(int diameter, bool light, int strength = 25)
        {
            Bitmap result = new Bitmap(diameter, diameter, PixelFormat.Format32bppPArgb);
            double boost = Math.Max(0, Math.Min(1, (strength - 25) / 75.0));
            double peak = strength <= 25 ? (light ? 48 : 64) * Math.Pow(strength / 25.0, 1.6)
                : (light ? 48 : 64) + (235 - (light ? 48 : 64)) * boost;
            int red = (int)Math.Round((light ? 55 : 170) + (255 - (light ? 55 : 170)) * boost);
            int green = (int)Math.Round((light ? 190 : 235) + (255 - (light ? 190 : 235)) * boost);
            int blue = (int)Math.Round(245 + 10 * boost);
            try
            {
                BitmapData data = result.LockBits(new Rectangle(0, 0, diameter, diameter), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    int[] colors = new int[256];
                    for (int alpha = 1; alpha < colors.Length; alpha++)
                        colors[alpha] = (alpha << 24) | (((red * alpha + 127) / 255) << 16)
                            | (((green * alpha + 127) / 255) << 8) | ((blue * alpha + 127) / 255);

                    int[] row = new int[diameter];
                    double half = diameter / 2.0;
                    for (int y = 0; y < (diameter + 1) / 2; y++)
                    {
                        double dy = (y + .5 - half) / half;
                        for (int x = 0; x < (diameter + 1) / 2; x++)
                        {
                            double dx = (x + .5 - half) / half;
                            double distance = Math.Sqrt(dx * dx + dy * dy);
                            double falloff = Math.Max(0, 1 - distance);
                            // Strong light has a broad white core, rather than one bright pixel.
                            double t = Math.Max(0, Math.Min(1, (distance - .15) / .85));
                            double broad = 1 - t * t * (3 - 2 * t);
                            double profile = falloff * falloff * (1 - boost) + broad * boost;
                            int alpha = (int)Math.Round(peak * profile);
                            row[x] = row[diameter - 1 - x] = colors[alpha];
                        }
                        Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), diameter);
                        int opposite = diameter - 1 - y;
                        if (opposite != y) Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, opposite * data.Stride), diameter);
                    }
                }
                finally { result.UnlockBits(data); }
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x84) { message.Result = new IntPtr(-1); return; }
            if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; }
            base.WndProc(ref message);
        }
    }

    internal sealed class TaskbarGlow : IDisposable
    {
        private readonly Timer timer;
        private readonly Func<bool> enabled;
        private readonly Func<bool> light;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Func<Point> readPointer;
        private readonly Func<Point, Rectangle> readTarget;
        private readonly Func<long> readTime;
        private readonly Func<int> radius, strength;
        private TaskbarGlowWindow window;
        private Bitmap sprite;
        private int diameter;
        private bool spriteLight;
        private int spriteStrength;
        private Point pointer;
        private Rectangle bounds;
        private long lastTick;
        private double alpha;
        private bool failed;
        internal bool Active { get { return window != null; } }
        internal int PresentCount { get; private set; }
        internal TaskbarGlow(Func<bool> enabled, Func<bool> light, Func<Point> readPointer = null,
            Func<Point, Rectangle> readTarget = null, Func<long> readTime = null, Func<int> radius = null, Func<int> strength = null)
        {
            this.enabled = enabled; this.light = light;
            this.readPointer = readPointer ?? delegate { return Cursor.Position; };
            this.readTarget = readTarget; this.readTime = readTime ?? delegate { return clock.ElapsedMilliseconds; };
            this.radius = radius ?? delegate { return 100; }; this.strength = strength ?? delegate { return 25; };
            timer = new Timer { Interval = 120 };
            timer.Tick += delegate { Advance(); }; timer.Start();
        }
        private bool FindTarget(Point point, out Rectangle target)
        {
            target = Rectangle.Empty;
            IntPtr root = Native.GetAncestor(Native.WindowFromPoint(new Native.Point(point.X, point.Y)), 2);
            foreach (IntPtr taskbar in TaskbarGeometry.Windows())
            {
                Native.Rect rect;
                if (taskbar != root || !Native.IsWindowVisible(taskbar) || !Native.GetWindowRect(taskbar, out rect)) continue;
                Rectangle visible = Rectangle.Intersect(SystemInformation.VirtualScreen, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
                // An auto-hidden strip must not be illuminated or revealed.
                if (visible.Width < 8 || visible.Height < 8 || !visible.Contains(point)) continue;
                target = visible; return true;
            }
            return false;
        }
        internal void Advance()
        {
            long now = readTime();
            double elapsed = Math.Min(50, Math.Max(1, now - lastTick)); lastTick = now;
            if (failed || !enabled() || strength() == 0 || SystemInformation.HighContrast) { Clear(); timer.Interval = 500; return; }
            try
            {
                Point next = readPointer(); Rectangle target;
                bool hover;
                if (readTarget == null) hover = FindTarget(next, out target);
                else { target = readTarget(next); hover = !target.IsEmpty; }
                // Close immediately when menus or other windows cover the same point.
                if (!hover && bounds.Contains(next)) { Clear(); timer.Interval = 120; return; }
                double previous = alpha;
                alpha = Math.Max(0, Math.Min(1, alpha + (hover ? elapsed / 140 : -elapsed / 220)));
                bool changed = previous != alpha;
                if (hover)
                {
                    changed |= pointer != next || bounds != target;
                    pointer = next; bounds = target;
                    int size = Diameter(target, radius());
                    bool isLight = light();
                    int intensity = strength();
                    if (sprite == null || diameter != size || spriteLight != isLight || spriteStrength != intensity)
                    {
                        Bitmap nextSprite = TaskbarGlowWindow.CreateSprite(size, isLight, intensity);
                        if (sprite != null) sprite.Dispose();
                        sprite = nextSprite; diameter = size; spriteLight = isLight; spriteStrength = intensity; changed = true;
                    }
                    if (window == null) { window = new TaskbarGlowWindow(); changed = true; }
                }
                if (alpha <= 0) Clear();
                else if (changed && window != null) { window.Present(sprite, pointer, bounds, alpha); PresentCount++; }
                timer.Interval = hover ? (changed ? 16 : 40) : Active ? 16 : 120;
            }
            catch (Exception error)
            {
                if (!(error is Win32Exception) && !(error is ExternalException) && !(error is ArgumentException)) throw;
                Clear(); failed = true; Files.Log("Mouse glow disabled: " + error.Message);
            }
        }
        internal void Clear()
        {
            if (window != null) { window.Dispose(); window = null; }
            alpha = 0; bounds = Rectangle.Empty;
        }
        internal static int Diameter(Rectangle taskbar, int radius)
        { return (int)Math.Round(Math.Max(96, Math.Min(360, Math.Min(taskbar.Width, taskbar.Height) * 4)) * radius / 100.0); }
        public void Dispose()
        { timer.Stop(); timer.Dispose(); Clear(); if (sprite != null) { sprite.Dispose(); sprite = null; } }
    }

    internal sealed class GlowPreview : Control
    {
        private Bitmap sprite;
        private int diameter, strength;
        internal GlowPreview() { DoubleBuffered = true; TabStop = false; }
        internal void UpdateEffect(int radius, int strength, bool enabled)
        {
            int nextDiameter = 192 * radius / 100;
            if ((!enabled || diameter != nextDiameter || this.strength != strength) && sprite != null)
            {
                sprite.Dispose();
                sprite = null;
            }
            diameter = nextDiameter;
            this.strength = strength;
            Enabled = enabled; Invalidate();
        }
        protected override void OnPaint(PaintEventArgs args)
        {
            args.Graphics.Clear(Ui.Canvas);
            if (Enabled && diameter > 0)
            {
                // Paint the latest slider values once per redraw.
                if (sprite == null) sprite = TaskbarGlowWindow.CreateSprite(diameter, !Ui.Dark, strength);
                int size = sprite.Width * DeviceDpi / 96;
                args.Graphics.DrawImage(sprite, new Rectangle((Width - size) / 2, (Height - size) / 2, size, size));
            }
            using (Pen outline = new Pen(Ui.Border)) args.Graphics.DrawRectangle(outline, 0, 0, Width - 1, Height - 1);
            int dot = Math.Max(6, 6 * DeviceDpi / 96);
            using (Brush marker = new SolidBrush(Ui.Text)) args.Graphics.FillEllipse(marker, (Width - dot) / 2, (Height - dot) / 2, dot, dot);
            int inset = Math.Max(10, Font.Height / 2);
            TextRenderer.DrawText(args.Graphics, Enabled ? "光效预览" : "光效已关闭", Font,
                new Rectangle(inset, inset / 2, Width - 2 * inset, Font.Height + 4), Ui.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        protected override void Dispose(bool disposing)
        { if (disposing && sprite != null) { sprite.Dispose(); sprite = null; } base.Dispose(disposing); }
    }
}
