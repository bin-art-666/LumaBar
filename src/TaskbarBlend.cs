using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LumaBar
{
    // Only the short transition is overlaid. Bitmaps stay in memory and are disposed.
    internal sealed class TaskbarBlendWindow : Form
    {
        private Bitmap picture;
        internal TaskbarBlendWindow(Rectangle bounds, Bitmap bitmap)
        {
            picture = bitmap;
            AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual; Bounds = bounds;
            ShowInTaskbar = false; DoubleBuffered = true;
            Enabled = false; TabStop = false;
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
        internal void SetBlendOpacity(double opacity)
        {
            byte alpha = (byte)Math.Round(Math.Max(0, Math.Min(1, opacity)) * 255);
            if (!SetLayeredWindowAttributes(Handle, 0, alpha, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        internal void ShowPassive()
        {
            SetBlendOpacity(.999);
            // Form.Show can activate a topmost layered form on this Windows build.
            if (!SetWindowPos(Handle, new IntPtr(-1), Left, Top, Width, Height, 0x0010 | 0x0040))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Update();
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x00000020 | 0x00000080 | 0x00080000; return value; }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084) { message.Result = new IntPtr(-1); return; }
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; }
            base.WndProc(ref message);
        }
        protected override void OnPaint(PaintEventArgs args)
        { if (picture != null) args.Graphics.DrawImageUnscaled(picture, 0, 0); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && picture != null) { picture.Dispose(); picture = null; }
            base.Dispose(disposing);
        }
    }

    internal sealed class TaskbarBlend : IDisposable
    {
        internal const int Duration = 180;
        private readonly List<TaskbarBlendWindow> windows = new List<TaskbarBlendWindow>();
        private readonly Timer timer;
        private readonly Stopwatch clock = new Stopwatch();
        private Func<bool> ready;
        private long fadeAt = -1;
        private IntPtr foreground;
        private Rectangle foregroundFrame;
        internal bool Active { get { return windows.Count != 0; } }
        private readonly Func<IntPtr> readForeground;
        private readonly Func<IntPtr,Rectangle> readFrame;
        internal TaskbarBlend(Func<IntPtr> readForeground = null, Func<IntPtr,Rectangle> readFrame = null)
        {
            this.readForeground = readForeground ?? Native.GetForegroundWindow;
            this.readFrame = readFrame ?? Native.FrameOf;
            timer = new Timer { Interval = 15 }; timer.Tick += delegate { Advance(); };
        }
        internal static double OpacityAt(double progress)
        { double t = Math.Max(0, Math.Min(1, progress)); return 1 - t * t * (3 - 2 * t); }
        private int duration = Duration;
        internal void Begin() { Begin(Duration); }
        internal void Begin(int milliseconds)
        {
            // Finish an interrupted blend rather than capture our own layered windows.
            Cancel();
            if (milliseconds <= 0) return;
            duration = milliseconds;
            foreground = readForeground();
            foregroundFrame = readFrame(foreground);
            try
            {
                foreach (IntPtr taskbar in TaskbarGeometry.Windows())
                {
                    Native.Rect native;
                    if (!Native.IsWindowVisible(taskbar) || !Native.GetWindowRect(taskbar, out native)) continue;
                    Rectangle bounds = Rectangle.Intersect(SystemInformation.VirtualScreen,
                        Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom));
                    if (bounds.Width < 8 || bounds.Height < 8) continue; // Hidden taskbar edge.
                    Bitmap image = new Bitmap(bounds.Width, bounds.Height);
                    try
                    {
                        using (Graphics graphics = Graphics.FromImage(image))
                            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                        windows.Add(new TaskbarBlendWindow(bounds, image)); image = null;
                    }
                    finally { if (image != null) image.Dispose(); }
                }
                foreach (TaskbarBlendWindow window in windows) { window.ShowPassive(); }
                if (Active) { ready = null; fadeAt = -1; clock.Restart(); timer.Start(); }
            }
            catch (Exception error)
            {
                Cancel();
                if (!(error is System.ComponentModel.Win32Exception) && !(error is ExternalException) && !(error is ArgumentException)) throw;
                Files.Log("Taskbar blend skipped: " + error.GetType().Name);
            }
        }
        internal void WhenReady(Func<bool> predicate) { ready = predicate; }
        private void Advance()
        {
            if (!Active) { timer.Stop(); return; }
            // A new app, menu or desktop interaction must never retain a stale image.
            if (readForeground() != foreground || readFrame(foreground) != foregroundFrame) { Cancel(); return; }
            long now = clock.ElapsedMilliseconds;
            if (fadeAt < 0)
            {
                if ((ready == null || !ready()) && now < 500) return;
                fadeAt = now;
            }
            double t = (now - fadeAt) / (double)duration;
            if (t >= 1) { Cancel(); return; }
            double opacity = OpacityAt(t);
            try { foreach (TaskbarBlendWindow window in windows) window.SetBlendOpacity(opacity); }
            catch (System.ComponentModel.Win32Exception) { Cancel(); }
        }
        internal void Cancel()
        {
            timer.Stop();
            foreach (TaskbarBlendWindow window in windows) window.Dispose();
            windows.Clear(); ready = null; fadeAt = -1; clock.Reset();
        }
        public void Dispose() { Cancel(); timer.Dispose(); }
    }
}
