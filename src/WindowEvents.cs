using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LumaBar
{
    // Keep idle polling economical; briefly sample faster after actual window transitions.
    internal sealed class ProbeSchedule
    {
        private bool? wallpaper;
        private long context;
        private long fastUntil;
        private long lastKick = -100000;
        internal void Observe(long now, bool isWallpaper, long currentContext)
        {
            if (wallpaper != isWallpaper || context != currentContext) fastUntil = now + 1000;
            wallpaper = isWallpaper; context = currentContext;
        }
        internal bool Signal(long now)
        {
            fastUntil = now + 1000;
            if (now - lastKick < 75) return false;
            lastKick = now; return true;
        }
        internal int Interval(long now, int normal) { return now < fastUntil ? Math.Min(50, normal) : normal; }
    }

    internal sealed class WindowEvents : IDisposable
    {
        private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
        private readonly WinEventProc callback;
        private readonly List<IntPtr> hooks = new List<IntPtr>();
        private bool disposed;
        internal bool Available { get { return hooks.Count == 3; } }

        internal WindowEvents(Action changed, bool includeOwnProcess = false)
        {
            // OUTOFCONTEXT callbacks run on the registering UI thread's message loop.
            // They only request an earlier timer tick, never sample or write a theme.
            callback = delegate(IntPtr hook, uint type, IntPtr window, int objectId, int childId, uint thread, uint time) {
                if (disposed) return;
                if (Relevant(type, window, objectId, childId, Native.GetForegroundWindow())) changed();
            };
            uint flags = includeOwnProcess ? 0U : 2U; // WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
            Add(0x0003, 0x0003, flags); // EVENT_SYSTEM_FOREGROUND
            Add(0x0016, 0x0017, flags); // EVENT_SYSTEM_MINIMIZESTART / END
            Add(0x800B, 0x800B, flags); // EVENT_OBJECT_LOCATIONCHANGE
        }
        private void Add(uint first, uint last, uint flags)
        {
            IntPtr hook = SetWinEventHook(first, last, IntPtr.Zero, callback, 0, 0, flags);
            if (hook != IntPtr.Zero) hooks.Add(hook);
        }
        internal static bool Relevant(uint type, IntPtr window, int objectId, int childId, IntPtr foreground)
        {
            if (window == IntPtr.Zero) return false;
            if (type == 0x0003 || type == 0x0016 || type == 0x0017) return true;
            return type == 0x800B && objectId == 0 && childId == 0 && window == foreground;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (IntPtr hook in hooks) UnhookWinEvent(hook);
            hooks.Clear();
            GC.KeepAlive(callback);
        }
    }
}
