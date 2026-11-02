using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LumaBar
{
    // Stable content can be sampled less often; native activity wakes the UI timer.
    internal sealed class SamplingCadence
    {
        private long stableSince, fastUntil;
        private string identity;
        private double brightness;
        internal void Wake(long now) { identity = null; stableSince = now; fastUntil = now + 1000; }
        internal int Observe(WindowSample sample, Rectangle frame, Tone current, Settings settings, long now, bool pending, bool eventsAvailable)
        {
            string next = sample.Window.ToInt64() + ":" + sample.DecisionContext + ":" + sample.Scene + ":" + sample.Valid + ":" + frame;
            bool changed = next != identity || Math.Abs(sample.DecisionBrightness - brightness) > .035;
            if (changed) { identity = next; brightness = sample.DecisionBrightness; stableSince = now; fastUntil = now + 1000; }
            if (pending) { stableSince = now; return Math.Min(75, settings.PollMilliseconds); }
            if (now < fastUntil) return Math.Min(75, settings.PollMilliseconds);
            if (!eventsAvailable || now - stableSince < 3000) return settings.PollMilliseconds;
            // Keep the observation gap below the decision engine's stale-source limit.
            // Content-only changes have no reliable OS event: still inspect at least every 600ms.
            return Math.Max(settings.PollMilliseconds, sample.IsWallpaper || !sample.Valid ? 1000 : 600);
        }
    }

    internal sealed class ForegroundActivity : IDisposable
    {
        private delegate void Callback(IntPtr hook, uint kind, IntPtr window, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, Callback callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        private readonly Callback callback;
        private IntPtr foregroundHook, geometryHook, minimizeHook;
        private readonly Action changed;
        private IntPtr lastWindow;
        private Rectangle lastFrame;
        internal bool Available { get { return foregroundHook != IntPtr.Zero && geometryHook != IntPtr.Zero && minimizeHook != IntPtr.Zero; } }
        internal ForegroundActivity(Action changed)
        {
            this.changed = changed; callback = OnEvent;
            foregroundHook = SetWinEventHook(3,3,IntPtr.Zero,callback,0,0,2);
            WatchGeometry(Native.GetForegroundWindow());
            minimizeHook = SetWinEventHook(0x16,0x17,IntPtr.Zero,callback,0,0,2);
        }
        private void WatchGeometry(IntPtr window)
        {
            if (geometryHook != IntPtr.Zero) UnhookWinEvent(geometryHook);
            uint pid; Native.GetWindowThreadProcessId(window,out pid);
            geometryHook = pid == 0 ? IntPtr.Zero : SetWinEventHook(0x800B,0x800B,IntPtr.Zero,callback,pid,0,2);
        }
        private void OnEvent(IntPtr hook, uint kind, IntPtr window, int obj, int child, uint thread, uint time)
        {
            if (kind == 3) WatchGeometry(window);
            if (kind == 0x800B && (obj != 0 || child != 0 || window != Native.GetForegroundWindow())) return;
            Rectangle frame = Native.FrameOf(window);
            if (kind == 0x800B && window == lastWindow && frame == lastFrame) return;
            lastWindow = window; lastFrame = frame;
            changed(); // OUTOFCONTEXT callbacks run on the registering UI thread.
        }
        public void Dispose()
        {
            if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
            if (geometryHook != IntPtr.Zero) UnhookWinEvent(geometryHook);
            if (minimizeHook != IntPtr.Zero) UnhookWinEvent(minimizeHook);
            foregroundHook = geometryHook = minimizeHook = IntPtr.Zero;
            GC.KeepAlive(callback);
        }
    }
}
