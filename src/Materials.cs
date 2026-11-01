using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LumaBar
{
    public enum MaterialScene { Desktop, Window, Maximized }

    public sealed class TaskbarAppearance
    {
        public string Material = "blur";
        public bool AutomaticColor = true;
        public string Color = "#202630";
        public int TintOpacity = 22;
        public int BlurRadius = 24;
        public bool ShowLine;

        public void Validate()
        {
            if (Material != "normal" && Material != "clear" && Material != "blur" && Material != "acrylic" && Material != "opaque")
                throw new ArgumentException("未知的任务栏材质。");
            if (TintOpacity < 0 || TintOpacity > 100 || BlurRadius < 0 || BlurRadius > 100)
                throw new ArgumentException("染色不透明度和模糊强度必须在 0–100 之间。");
            if (Color == null || Color.Length != 7 || Color[0] != '#') throw new ArgumentException("颜色应为 #RRGGBB。");
            int rgb;
            if (!Int32.TryParse(Color.Substring(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out rgb)) throw new ArgumentException("颜色应为 #RRGGBB。");
        }

        internal uint Abgr(Tone tone)
        {
            System.Drawing.Color color = AutomaticColor ? (tone == Tone.Light ? System.Drawing.Color.FromArgb(248, 250, 255) : System.Drawing.Color.FromArgb(20, 24, 32)) : ColorTranslator.FromHtml(Color);
            uint alpha = Material == "opaque" ? 255 : (uint)Math.Round(TintOpacity * 255.0 / 100);
            return alpha << 24 | (uint)color.B << 16 | (uint)color.G << 8 | color.R;
        }
        internal TaskbarAppearance Copy() { return (TaskbarAppearance)MemberwiseClone(); }
    }

    public sealed class GlassSettings
    {
        public bool Enabled = true;
        public bool TaskbarRegion = true;
        public bool AutoContrast = true;
        public bool PerMonitor = true;
        public Dictionary<string, MonitorAppearanceSet> MonitorOverrides = new Dictionary<string, MonitorAppearanceSet>(StringComparer.OrdinalIgnoreCase);
        public TaskbarAppearance Desktop = new TaskbarAppearance();
        public TaskbarAppearance Window = new TaskbarAppearance();
        public TaskbarAppearance Maximized = new TaskbarAppearance { Material = "normal", ShowLine = true };
        public void Validate()
        {
            if (Desktop == null || Window == null || Maximized == null) throw new ArgumentException("任务栏场景设置不能为空。");
            Desktop.Validate(); Window.Validate(); Maximized.Validate();
            Dictionary<string, MonitorAppearanceSet> overrides = new Dictionary<string, MonitorAppearanceSet>(StringComparer.OrdinalIgnoreCase);
            if (MonitorOverrides != null)
                foreach (KeyValuePair<string, MonitorAppearanceSet> item in MonitorOverrides)
                {
                    if (String.IsNullOrWhiteSpace(item.Key) || item.Value == null) throw new ArgumentException("显示器外观设置无效。");
                    item.Value.Validate(); overrides.Add(item.Key, item.Value);
                }
            MonitorOverrides = overrides;
        }
        internal TaskbarAppearance For(MaterialScene scene)
        { return scene == MaterialScene.Desktop ? Desktop : scene == MaterialScene.Maximized ? Maximized : Window; }
        internal TaskbarAppearance For(MaterialScene scene, string device)
        {
            MonitorAppearanceSet custom;
            return device != null && MonitorOverrides.TryGetValue(device, out custom) && custom.Enabled ? custom.For(scene) : For(scene);
        }
    }

    internal static class TaskbarGeometry
    {
        internal static List<IntPtr> Windows()
        {
            List<IntPtr> result = new List<IntPtr>();
            Native.EnumWindows(delegate(IntPtr window, IntPtr unused) {
                string cls = Native.ClassOf(window);
                if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") result.Add(window);
                return true;
            }, IntPtr.Zero);
            return result;
        }
        internal static Rectangle SampleArea(Rectangle monitor)
        {
            foreach (IntPtr window in Windows())
            {
                if (Screen.FromHandle(window).Bounds != monitor) continue;
                Native.Rect rect;
                if (!Native.GetWindowRect(window, out rect)) continue;
                Rectangle area = Rectangle.Intersect(monitor, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
                // Auto-hide may leave just a 1px edge. Use a visible-sized strip instead.
                if (area.Width >= 8 && area.Height >= 8) return area;
            }
            Screen screen = null;
            foreach (Screen candidate in Screen.AllScreens) if (candidate.Bounds == monitor) screen = candidate;
            int height = screen == null ? 48 : Math.Max(48, monitor.Bottom - screen.WorkingArea.Bottom);
            height = Math.Min(height, monitor.Height);
            return new Rectangle(monitor.Left, monitor.Bottom - height, monitor.Width, height);
        }
    }

    // COM vtable follows TranslucentTB 2026.2 ExplorerTAP/ITaskbarAppearanceService.idl.
    [ComImport, Guid("5bcf9150-c28a-4ef2-913c-4c3ea2f5ead0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITaskbarAppearanceService
    {
        void SetTaskbarAppearance(IntPtr taskbar, int brush, uint color);
        void SetTaskbarBlur(IntPtr taskbar, uint color, float amount);
        void ReturnTaskbarToDefaultAppearance(IntPtr taskbar);
        void SetTaskbarBorderVisibility(IntPtr taskbar, [MarshalAs(UnmanagedType.Bool)] bool visible);
        void RestoreAllTaskbarsToDefault();
        void RestoreAllTaskbarsToDefaultWhenProcessDies(uint pid);
        void KillExplorerWhenPackageUninstalls([MarshalAs(UnmanagedType.LPWStr)] string package);
    }

    internal interface IMaterialBackend : IDisposable
    {
        uint ExplorerPid { get; }
        void Connect();
        void Apply(MaterialTarget target);
        void Restore();
    }

    internal sealed class NativeMaterialBackend : IMaterialBackend
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int Inject(IntPtr taskbar, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out ITaskbarAppearanceService service);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        private ITaskbarAppearanceService service;
        private IntPtr module;
        private uint connectedPid;
        public uint ExplorerPid
        {
            get { uint pid; Native.GetWindowThreadProcessId(Native.FindWindow("Shell_TrayWnd", null), out pid); return pid; }
        }
        public void Connect()
        {
            bool conflict = false;
            foreach (Process process in Process.GetProcessesByName("TranslucentTB")) { process.Dispose(); conflict = true; }
            if (conflict) throw new InvalidOperationException("请先退出独立 TranslucentTB，再重试。");
            uint pid = ExplorerPid;
            if (service != null && pid == connectedPid) return;
            Dispose();
            if (pid == 0) throw new InvalidOperationException("正在等待 Windows 任务栏。");
            if (IntPtr.Size != 8 || String.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "ARM64", StringComparison.OrdinalIgnoreCase))
                throw new PlatformNotSupportedException("当前玻璃组件支持 Windows 11 x64。");
            string path = Path.Combine(Files.Root, "native", "ExplorerTAP.dll");
            if (!File.Exists(path)) throw new FileNotFoundException("缺少 native\\ExplorerTAP.dll，请完整解压程序。");
            if (module == IntPtr.Zero) module = LoadLibraryEx(path, IntPtr.Zero, 0x00000100 | 0x00000800);
            if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            IntPtr address = GetProcAddress(module, "InjectExplorerTAP");
            if (address == IntPtr.Zero) throw new InvalidOperationException("材质组件接口不匹配。");
            Inject inject = (Inject)Marshal.GetDelegateForFunctionPointer(address, typeof(Inject));
            Guid iid = typeof(ITaskbarAppearanceService).GUID;
            try
            {
                Marshal.ThrowExceptionForHR(inject(Native.FindWindow("Shell_TrayWnd", null), ref iid, out service));
                connectedPid = pid;
                service.RestoreAllTaskbarsToDefaultWhenProcessDies((uint)Process.GetCurrentProcess().Id);
            }
            catch { Dispose(); throw; }
        }
        public void Apply(MaterialTarget target)
        {
            TaskbarAppearance appearance = target.Appearance;
            uint color = appearance.Abgr(target.Tone);
            if (appearance.Material == "normal") service.ReturnTaskbarToDefaultAppearance(target.Taskbar);
            else if (appearance.Material == "blur") service.SetTaskbarBlur(target.Taskbar, color, appearance.BlurRadius / 3f);
            else service.SetTaskbarAppearance(target.Taskbar, appearance.Material == "acrylic" ? 0 : 1, color);
            service.SetTaskbarBorderVisibility(target.Taskbar, appearance.Material == "normal" || appearance.ShowLine);
        }
        public void Restore()
        {
            if (service == null) return;
            // Explorer's old process no longer exists: its old brushes no longer exist either.
            if (ExplorerPid != connectedPid) { Dispose(); return; }
            service.RestoreAllTaskbarsToDefault();
        }
        public void Dispose()
        {
            if (service != null) { Marshal.ReleaseComObject(service); service = null; }
            // Keep the DLL loaded: COM proxy/stub registrations point into it until process exit.
        }
    }

    internal enum MaterialState { Waiting, Connecting, Applying, Applied, Native, Fallback, RestoreFailed }
    internal sealed class MaterialSnapshot
    {
        internal readonly MaterialState State;
        internal readonly string Message;
        internal readonly bool CanRetry;
        internal MaterialSnapshot(MaterialState state, string message, bool canRetry = false)
        { State = state; Message = message; CanRetry = canRetry; }
    }

    internal sealed class MaterialController : IDisposable
    {
        private sealed class Request
        {
            internal MaterialPlan Plan;
            internal bool Stop;
            internal ManualResetEvent Completed;
            internal Exception Error;
            internal bool Abandoned;
            internal int Generation;
            internal long Sequence;
        }
        private readonly object gate = new object();
        private readonly Queue<Request> requests = new Queue<Request>();
        private readonly AutoResetEvent ready = new AutoResetEvent(false);
        private readonly Thread worker;
        private readonly IMaterialBackend backend;
        private bool owned, disposed, faulted;
        private uint ownerPid, failurePid;
        private string failureReason;
        private string failureTopology;
        private int retryEpoch, seenRetryEpoch;
        private int generation;
        private string lastKey;
        private long nextApply;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private volatile MaterialSnapshot snapshot = new MaterialSnapshot(MaterialState.Waiting, "等待有效场景，尚未应用材质");
        internal MaterialSnapshot Snapshot { get { return snapshot; } }
        internal string Status { get { return snapshot.Message; } }
        private long completedUpdates;
        private long nextSequence, lastQueuedSequence, appliedSequence;
        internal long AppliedSequence { get { return Interlocked.Read(ref appliedSequence); } }
        internal long CompletedUpdates { get { return Interlocked.Read(ref completedUpdates); } }

        internal MaterialController() : this(new NativeMaterialBackend()) { }
        internal MaterialController(IMaterialBackend backend)
        {
            this.backend = backend;
            worker = new Thread(Run) { IsBackground = true, Name = "LumaBar taskbar materials" };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
        }
        internal void Retry()
        { Interlocked.Increment(ref retryEpoch); lastKey = null; }
        internal void InvalidatePending()
        {
            lock (gate)
            {
                generation++;
                Request[] pending = requests.ToArray(); requests.Clear();
                foreach (Request request in pending) if (request.Completed != null) requests.Enqueue(request);
            }
            lastKey = null;
        }
        internal long Apply(GlassSettings settings, MaterialScene scene, Tone tone, bool force = false)
        { return Apply(ReadabilityPolicy.Capture(settings, scene, tone), force); }
        internal long Apply(MaterialPlan plan, bool force = false)
        {
            if (disposed) return 0;
            string key = plan.Key;
            if (!force && key == lastKey && clock.ElapsedMilliseconds < nextApply) return lastQueuedSequence;
            lastKey = key; nextApply = clock.ElapsedMilliseconds + 2000;
            lock (gate)
            {
                if (requests.Count > 0 && requests.Peek().Completed == null) requests.Dequeue();
                lastQueuedSequence = ++nextSequence;
                requests.Enqueue(new Request { Plan = plan.Enabled ? plan : null, Generation = generation, Sequence = lastQueuedSequence });
            }
            ready.Set();
            return lastQueuedSequence;
        }
        internal void Restore()
        {
            if (disposed) return;
            Request request = new Request { Completed = new ManualResetEvent(false) };
            lock (gate)
            {
                generation++;
                Request[] pending = requests.ToArray(); requests.Clear();
                foreach (Request previous in pending) if (previous.Completed != null) requests.Enqueue(previous);
                requests.Enqueue(request);
            }
            ready.Set();
            if (!request.Completed.WaitOne(5000))
            {
                lock (request)
                {
                    if (request.Completed.WaitOne(0)) request.Completed.Dispose(); else request.Abandoned = true;
                }
                throw new TimeoutException("任务栏材质仍在连接或恢复，请稍后重试。");
            }
            lock (request) { request.Completed.Dispose(); }
            lastKey = null;
            if (request.Error != null) throw new InvalidOperationException("材质恢复未确认，请重试或正常退出。", request.Error);
        }
        private void RestoreOwned()
        {
            if (owned) { backend.Restore(); owned = false; }
        }
        private void Run()
        {
            try
            {
                while (true)
                {
                    ready.WaitOne();
                    while (true)
                    {
                        Request request;
                        lock (gate) { if (requests.Count == 0) break; request = requests.Dequeue(); }
                        if (request.Stop) { backend.Dispose(); return; }
                        try
                        {
                            if (request.Plan == null)
                            {
                                RestoreOwned();
                                snapshot = new MaterialSnapshot(MaterialState.Native, "系统原生材质", faulted);
                                Interlocked.Exchange(ref appliedSequence, request.Sequence);
                            }
                            else if (ApplyCore(request.Plan, request.Generation)) Interlocked.Exchange(ref appliedSequence, request.Sequence);
                        }
                        catch (Exception error)
                        {
                            request.Error = error; faulted = true; failurePid = backend.ExplorerPid; failureReason = error.Message;
                            failureTopology = request.Plan == null ? null : request.Plan.TopologyKey;
                            Files.Log("Material fallback: " + error);
                            try
                            {
                                RestoreOwned();
                                snapshot = new MaterialSnapshot(MaterialState.Fallback, "已降级为原生材质 · " + failureReason + "（自动明暗继续）", true);
                            }
                            catch (Exception restoreError)
                            {
                                Files.Log("Material restore unconfirmed: " + restoreError);
                                snapshot = new MaterialSnapshot(MaterialState.RestoreFailed, "材质恢复未确认 · 请重试，自动明暗继续", true);
                            }
                        }
                        finally
                        {
                            Interlocked.Increment(ref completedUpdates);
                            if (request.Completed != null)
                                lock (request) { if (request.Abandoned) request.Completed.Dispose(); else request.Completed.Set(); }
                        }
                    }
                }
            }
            finally { ready.Dispose(); }
        }
        private bool ApplyCore(MaterialPlan plan, int requestedGeneration)
        {
            // Explorer/desktop transitions can make one enumeration temporarily empty.
            // Retain the last brush and retry on the next valid topology; do not flash a fallback.
            if (plan.Targets.Count == 0)
            {
                snapshot = new MaterialSnapshot(MaterialState.Waiting, "正在等待 Windows 任务栏就绪");
                return false;
            }
            uint pid = backend.ExplorerPid;
            int requestedRetry = Interlocked.CompareExchange(ref retryEpoch, 0, 0);
            if (requestedRetry != seenRetryEpoch || (faulted && (pid != failurePid || plan.TopologyKey != failureTopology)))
            { faulted = false; seenRetryEpoch = requestedRetry; }
            if (owned && pid != ownerPid) { backend.Dispose(); owned = false; }
            if (faulted)
            {
                if (snapshot.State != MaterialState.RestoreFailed)
                    snapshot = new MaterialSnapshot(MaterialState.Fallback, "已降级为原生材质 · " + failureReason + "（可手动重试）", true);
                return false;
            }
            if (!plan.Custom && !owned)
            { snapshot = new MaterialSnapshot(MaterialState.Native, "系统原生材质"); return true; }
            snapshot = new MaterialSnapshot(MaterialState.Connecting, "正在连接材质组件，尚未确认应用");
            backend.Connect(); ownerPid = pid;
            // A newer scene or pause may arrive while upstream initialization waits for Explorer.
            lock (gate) { if (requests.Count > 0 || requestedGeneration != generation) return false; }
            snapshot = new MaterialSnapshot(MaterialState.Applying, "正在应用外观，等待组件完成");
            owned = true;
            foreach (MaterialTarget target in plan.Targets)
            {
                lock (gate) { if (requestedGeneration != generation) return false; }
                backend.Apply(target);
            }
            snapshot = new MaterialSnapshot(plan.Custom ? MaterialState.Applied : MaterialState.Native,
                plan.Custom ? "玻璃效果已应用 · " + plan.Description : "系统原生材质");
            return true;
        }
        public void Dispose()
        {
            if (disposed) return;
            Restore(); disposed = true;
            lock (gate) { requests.Enqueue(new Request { Stop = true }); }
            ready.Set(); worker.Join(1000);
        }
    }
}
