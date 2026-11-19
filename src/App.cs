using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyVersion(LumaBar.Program.BuildVersion)]
[assembly: System.Reflection.AssemblyFileVersion(LumaBar.Program.BuildVersion)]

namespace LumaBar
{
    internal static class Program
    {
        internal const string BuildVersion = "0.6.14.0";
        internal static string ActivationName { get { return @"Local\LumaBar-Activate-" + WindowsIdentity.GetCurrent().User.Value; } }
        internal static bool RequestActivation(string eventName = null)
        {
            // The mutex may be acquired before the tray context has created its event.
            for (int attempt = 0; attempt < 40; attempt++)
            {
                try
                {
                    using (EventWaitHandle activation = EventWaitHandle.OpenExisting(eventName ?? ActivationName))
                    {
                        IntPtr window = eventName == null ? Native.FindWindow(null, "LumaBar") : IntPtr.Zero;
                        uint pid; Native.GetWindowThreadProcessId(window, out pid);
                        if (pid != 0) Native.AllowSetForegroundWindow(pid);
                        activation.Set(); return true;
                    }
                }
                catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(50); }
            }
            return false;
        }
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Native.EnableDpi();
            string healthToken = UpdateLauncher.Argument(args,"--update-health");
            try
            {
                if (Array.IndexOf(args,"--update") >= 0)
                {
                    string package = UpdateLauncher.Argument(args,"--update");
                    if (String.IsNullOrEmpty(package)) throw new ArgumentException("请指定更新包路径。");
                    UpdateLauncher.Start(Files.Root,package); return 0;
                }
                if (UpdateLauncher.RecoverBeforeStartup(Files.Root,healthToken)) return 0;
            }
            catch (Exception error) { Files.Log(error.ToString()); Ui.Message(null,error.Message,"LumaBar 更新未完成"); return 1; }
            string sid = WindowsIdentity.GetCurrent().User.Value;
            using (Mutex mutex = new Mutex(false, @"Local\LumaBar-" + sid))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    if (Array.IndexOf(args, "--startup") >= 0) return 0;
                    return RequestActivation() ? 0 : 2;
                }
                try
                {
                    ThemeController controller = new ThemeController(new RegistryThemeStore(), new FileJournal());
                    if (Array.IndexOf(args, "--restore") >= 0)
                    {
                        bool restored = controller.Restore();
                        Ui.Message(null, restored ? "恢复完成（或没有待恢复的更改）。" : "主题已被其他程序更改，保留当前设置。", "LumaBar");
                        return 0;
                    }
                    Settings settings = Files.ReadSettings();
                    controller.Restore(); // Recover interrupted session before capturing a new baseline.
                    using (TrayApp context = new TrayApp(settings, controller, Array.IndexOf(args, "--monitor") >= 0, Array.IndexOf(args, "--startup") >= 0, null, healthToken)) Application.Run(context);
                    return 0;
                }
                catch (Exception error)
                {
                    Files.Log(error.ToString());
                    Ui.Message(null, error.Message + "\n\n请在可写文件夹中运行；详细信息见 LumaBar.log。", "LumaBar 无法启动");
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private Settings settings;
        private MaterialController materials;
        private MaterialScene materialScene = MaterialScene.Desktop;
        private readonly SceneCoordinator sceneCoordinator = new SceneCoordinator();
        private readonly VisualTransitionGate visualTransition = new VisualTransitionGate();
        private readonly SamplingCadence cadence = new SamplingCadence();
        private readonly ForegroundActivity activity;
        private readonly TaskbarBlend blend = new TaskbarBlend();
        private readonly TaskbarGlow glow;
        private string visualKey;
        private GlassSettings previewGlass;
        private bool? previewGlowEnabled;
        private int previewGlowRadius, previewGlowStrength;
        private readonly ThemeController controller;
        private DecisionEngine engine;
        private readonly NotifyIcon tray;
        private readonly System.Windows.Forms.Timer timer;
        private readonly StatusWindow window;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly ToolStripMenuItem pauseItem;
        private readonly EventWaitHandle activation;
        private readonly EventWaitHandle shutdown;
        private readonly RegisteredWaitHandle activationWait, shutdownWait;
        internal static string ShutdownName(int pid) { return @"Local\LumaBar-Exit-" + WindowsIdentity.GetCurrent().User.Value + "-" + pid; }
        private long recoveryAt = -1;
        private bool refreshAfterRecovery;
        internal bool RecoveryPending { get { return recoveryAt >= 0; } }
        private EventWaitHandle startupHealth;
        private int successfulStartupTicks;
        private long nextUpdateNotice;
        private bool idleReleased;
        private bool paused;
        private bool sessionLocked;
        private bool suspended;
        private bool restoring;
        private bool exitRequested;
        private bool exiting;
        private Exception restoreFailure;
        private string pendingMode;
        private string pauseStatus;
        private string mode = "自动适应";
        private string lastApplication;
        private long lastApplicationAt = -100000;
        internal ContextMenuStrip TrayMenu { get { return tray.ContextMenuStrip; } }
        internal bool StatusWindowVisible { get { return window.Visible; } }
        internal bool StatusHandleCreated { get { return window.IsHandleCreated; } }

        internal TrayApp(Settings settings, ThemeController controller, bool monitorOnly, bool startHidden = false, string activationName = null, string healthToken = null)
        {
            this.settings = settings;
            if (healthToken != null) startupHealth = EventWaitHandle.OpenExisting(healthToken);
            activation = new EventWaitHandle(false, EventResetMode.AutoReset, activationName ?? Program.ActivationName);
            this.controller = controller;
            engine = new DecisionEngine(settings);
            window = new StatusWindow();
            if (monitorOnly) { mode = "仅检测（不修改）"; window.SelectMode(mode); }
            window.HideRequested += delegate { window.Hide(); };
            window.ExitRequested += delegate { ExitThread(); };
            window.ModeChanged += ChangeMode;
            window.PauseRequested += TogglePause;
            window.RulesRequested += delegate { ShowPreferenceMenu(true); };
            window.PaceRequested += delegate { ShowPreferenceMenu(false); };
            window.SettingsRequested += ShowSettings;
            window.RetryRequested += RetryMaterials;
            ContextMenuStrip menu = Ui.Menu();
            menu.Items.Add("打开 LumaBar", null, delegate { ShowWindow(); });
            menu.Items.Add("设置…", null, delegate { ShowSettings(); });
            pauseItem = new ToolStripMenuItem("暂停并恢复原主题", null, delegate { TogglePause(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(pauseItem);
            menu.Items.Add("重新同步任务栏", null, delegate { Resync(); });
            menu.Items.Add("重试玻璃效果", null, delegate { RetryMaterials(); });
            ToolStripMenuItem rules = new ToolStripMenuItem("应用纠正");
            rules.DropDownItems.Add("读取目标应用…");
            rules.DropDownOpening += delegate { PopulateRules(rules.DropDownItems); };
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(rules);
            ToolStripMenuItem pace = new ToolStripMenuItem("切换节奏") { Name = "TrayPace" };
            pace.DropDownItems.Add("读取切换节奏…");
            pace.DropDownOpening += delegate { PopulatePace(pace.DropDownItems); };
            menu.Items.Add(pace);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开设置文件", null, delegate
            {
                try { Process.Start("notepad.exe", "\"" + Path.Combine(Files.Root, "settings.json") + "\""); }
                catch (Exception error) { Report(error); }
            });
            menu.Items.Add("从更新包更新…", null, delegate {
                ShowWindow();
                using (OpenFileDialog picker = new OpenFileDialog { Filter = "LumaBar 更新包 (*.zip)|*.zip", Title = "选择 LumaBar 更新包" })
                    if (picker.ShowDialog(window) == DialogResult.OK)
                        try { UpdateLauncher.Start(Files.Root,picker.FileName); window.UpdateStatus("更新包已验证，正在准备更新…",null,controller.Current.Tone); }
                        catch (Exception error) { Report(error); }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出并恢复", null, delegate { ExitThread(); });
            tray = new NotifyIcon { Icon = Ui.AppIcon, Text = "LumaBar · 自动适应", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += delegate { ShowWindow(); };
            timer = new System.Windows.Forms.Timer { Interval = settings.PollMilliseconds };
            timer.Tick += delegate {
                Tick();
                if (startupHealth != null && !paused && !restoring && !exiting && ++successfulStartupTicks >= 2)
                { startupHealth.Set(); startupHealth.Dispose(); startupHealth = null; }
                if (clock.ElapsedMilliseconds >= nextUpdateNotice)
                {
                    nextUpdateNotice = clock.ElapsedMilliseconds + 5000;
                    string notice = UpdateLauncher.TakeNotice(Files.Root);
                    if (notice != null) tray.ShowBalloonTip(5000,"LumaBar 更新",notice,ToolTipIcon.Info);
                }
            };
            activity = new ForegroundActivity(WakeSampling);
            glow = new TaskbarGlow(delegate { return (previewGlowEnabled ?? this.settings.MouseGlow) && mode != "仅检测（不修改）" && !paused && !sessionLocked && !suspended && !restoring && !exiting && recoveryAt < 0 && !blend.Active; }, delegate { return controller.Current.Tone == Tone.Light; }, radius: delegate { return previewGlowEnabled.HasValue ? previewGlowRadius : this.settings.MouseGlowRadius; }, strength: delegate { return previewGlowEnabled.HasValue ? previewGlowStrength : this.settings.MouseGlowStrength; });
            activationWait = ThreadPool.RegisterWaitForSingleObject(activation, delegate { Dispatch(ShowWindow); }, null, Timeout.Infinite, false);
            shutdown = new EventWaitHandle(false,EventResetMode.AutoReset,ShutdownName(Process.GetCurrentProcess().Id));
            shutdownWait = ThreadPool.RegisterWaitForSingleObject(shutdown, delegate { Dispatch(delegate { ExitThread(); }); }, null, Timeout.Infinite, false);
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.SessionEnding += OnSessionEnding;
            SystemEvents.PowerModeChanged += OnPowerMode;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettings;
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            window.ShellRestarted += delegate { ScheduleRecovery("Explorer taskbar recreated"); };
            // Create a handle for session callbacks even when logging in to tray.
            IntPtr handle = window.Handle;
            if (!startHidden) window.Show();
            controller.Refresh(); // Also repair stale visuals when the registry already matches.
            timer.Start();
            Files.Log("Started. No images or window titles are recorded.");
        }

        private void WakeSampling()
        {
            if (restoring || exiting) return;
            cadence.Wake(clock.ElapsedMilliseconds);
            timer.Interval = Math.Min(40,settings.PollMilliseconds);
        }
        private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
        {
            if (args.Category == UserPreferenceCategory.Desktop)
                Dispatch(delegate { WallpaperSampler.Invalidate(); WakeSampling(); });
        }
        private void ShowWindow() { WakeSampling(); window.Show(); window.WindowState = FormWindowState.Normal; window.Activate(); }
        private void Resync()
        {
            if (restoring || exiting || restoreFailure != null) return;
            try { controller.Refresh(); ApplyMaterials(true); window.UpdateStatus("已重新发送主题与任务栏设置通知", null, controller.Current.Tone); }
            catch (Exception error) { Report(error); }
        }
        private bool SavePreferences(Action<Settings> change)
        {
            try
            {
                // Write a new configuration first; a failure must not change
                // live rules or timing without also persisting that choice.
                Settings next = Files.Json.Deserialize<Settings>(Files.Json.Serialize(settings));
                next.Validate(); change(next); next.Validate(); Files.SaveSettings(next);
                settings = next; if (!settings.MouseGlow || settings.MouseGlowStrength == 0) glow.Clear(); engine = new DecisionEngine(settings); visualTransition.Reset();
                cadence.Wake(clock.ElapsedMilliseconds);
                timer.Interval = settings.PollMilliseconds;
                window.UpdateStatus("设置已保存，立即生效", null, controller.Current.Tone);
                return true;
            }
            catch (Exception error) { Report(error); return false; }
        }
        private bool CanApplyAppearance
        { get { return mode != "仅检测（不修改）" && !paused && !sessionLocked && !suspended && !restoring && !exiting && !SystemInformation.HighContrast && sceneCoordinator.Ready && !visualTransition.Waiting; } }
        private MaterialPlan PrepareAppearance(Tone tone)
        {
            GlassSettings glass = previewGlass ?? settings.Glass;
            MaterialPlan plan = ReadabilityPolicy.Capture(glass, materialScene, tone);
            string key = plan.Key;
            if (visualKey != null && visualKey != key) { glow.Clear(); blend.Begin(settings.TransitionMilliseconds); }
            visualKey = key;
            return plan;
        }
        private void ApplyMaterials(bool force = false)
        {
            if (!CanApplyAppearance) return;
            GlassSettings glass = previewGlass ?? settings.Glass;
            MaterialPlan plan = PrepareAppearance(controller.Current.Tone);
            if (materials == null && glass.Enabled) materials = new MaterialController();
            if (materials != null)
            {
                long submitted = materials.Apply(plan, force);
                blend.WhenReady(delegate { return materials.AppliedSequence >= submitted; });
            }
            else blend.WhenReady(delegate { return true; });
        }
        internal static void RestoreBoth(Action material, Action theme)
        {
            Exception failure = null;
            try { material(); } catch (Exception error) { failure = error; }
            RestoreBoth(failure, theme);
        }
        private static void RestoreBoth(Exception materialFailure, Action theme)
        {
            System.Collections.Generic.List<Exception> errors = new System.Collections.Generic.List<Exception>();
            if (materialFailure != null) errors.Add(materialFailure);
            try { theme(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count > 0) throw new AggregateException("部分恢复未完成，请重试。", errors);
        }
        private void ClearManagedAppearance() { glow.Clear(); blend.Cancel(); visualKey = null; }
        private void ResetDecisions() { engine.Reset(); sceneCoordinator.ResetPending(); visualTransition.Reset(); }
        private void SetPaused(bool value)
        {
            paused = value;
            bool retry = restoreFailure != null;
            pauseItem.Text = retry ? "重试恢复" : value ? "恢复自动适应" : "暂停并恢复原主题";
            window.SetPaused(value, retry);
        }
        private void ShowRestoreProgress()
        {
            window.UpdateStatusText(exitRequested ? "正在恢复任务栏与主题，完成后退出…" : "正在恢复任务栏与主题…");
            UpdateMaterialStatus();
        }
        private void BeginRestore()
        {
            if (restoring || exiting) return;
            restoring = true;
            restoreFailure = null;
            window.SetRestoring(true);
            pauseItem.Enabled = false;
            timer.Interval = 1000;
            ClearManagedAppearance();
            ResetDecisions();
            ShowRestoreProgress();

            if (materials == null) { CompleteRestore(null); return; }
            materials.RestoreAsync().ContinueWith(task => Dispatch(delegate {
                CompleteRestore(task.IsFaulted ? task.Exception.InnerException : null);
            }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        private void CompleteRestore(Exception materialFailure)
        {
            try
            {
                RestoreBoth(materialFailure, delegate { controller.Restore(); });
            }
            catch (Exception error) { restoreFailure = error; }

            if (restoreFailure == null && exitRequested)
            {
                FinishExit();
                return;
            }

            if (restoreFailure == null && pendingMode != null) mode = pendingMode;
            pendingMode = null;
            window.SelectMode(mode);
            restoring = false;
            window.SetRestoring(false);
            pauseItem.Enabled = true;
            SetPaused(paused || restoreFailure != null);
            bool failedExit = exitRequested;
            exitRequested = false;
            if (restoreFailure != null)
            {
                Report(restoreFailure);
                if (failedExit)
                {
                    ShowWindow();
                    window.UpdateStatusText("恢复未完成 · 可重试恢复或重新选择退出");
                }
            }
            else if (paused) window.UpdateStatus(pauseStatus ?? "已暂停 · 已恢复或保留原主题", null, controller.Current.Tone);
            UpdateMaterialStatus();
            WakeSampling();
        }
        private void RetryMaterials()
        {
            if (restoring || exiting) return;
            if (paused || sessionLocked || suspended || SystemInformation.HighContrast)
            {
                BeginRestore(); return;
            }
            if (materials != null) materials.Retry();
            ApplyMaterials(true);
            UpdateMaterialStatus();
        }
        private void CommitTheme(Tone tone)
        {
            if (tone != controller.Current.Tone)
            {
                if (CanApplyAppearance) PrepareAppearance(tone);
                if (materials != null) materials.InvalidatePending();
            }
            controller.Apply(tone, delegate { ApplyMaterials(true); });
        }
        private void UpdateMaterialStatus()
        {
            if (restoring) { window.UpdateMaterialStatus("正在恢复系统原生外观…", false, false); return; }
            if (restoreFailure != null) { window.UpdateMaterialStatus("恢复未确认 · 请重试", true, true, "重试恢复"); return; }
            if (mode == "仅检测（不修改）") { window.UpdateMaterialStatus("仅检测 · 不修改材质", false, false); return; }
            if (materials == null) { window.UpdateMaterialStatus("尚未应用 / 系统原生", false); return; }
            MaterialSnapshot state = materials.Snapshot;
            window.UpdateMaterialStatus(state.Message, state.CanRetry, state.CanRetry || (!paused && !sessionLocked && !suspended && !SystemInformation.HighContrast));
        }
        private Tone? DesiredTone(WindowSample sample, Tone current)
        {
            if (!sample.Valid) return null;
            double value = sample.DecisionBrightness;
            if (value <= settings.DarkThreshold) return Tone.Dark;
            if (value >= settings.LightThreshold) return Tone.Light;
            return current;
        }
        private void ShowSettings()
        {
            if (restoring || exiting) return;
            try
            {
                using (PreferencesWindow preferences = new PreferencesWindow(settings, StartupRegistration.Read(), SavePreferences))
                {
                    preferences.GlowPreviewChanged += delegate(bool enabled, int radius, int strength) {
                        previewGlowEnabled = enabled; previewGlowRadius = radius; previewGlowStrength = strength;
                        if (!enabled || strength == 0) glow.Clear();
                    };
                    preferences.AppearancePreview += delegate(GlassSettings glass) { previewGlass = glass; ApplyMaterials(true); };
                    try { preferences.ShowDialog(window); }
                    finally { previewGlowEnabled = null; glow.Clear(); previewGlass = null; engine = new DecisionEngine(settings); visualTransition.Reset(); cadence.Wake(clock.ElapsedMilliseconds); ApplyMaterials(true); }
                }
            }
            catch (Exception error) { Report(error); }
        }
        private void PopulateRules(ToolStripItemCollection items)
        {
            items.Clear();
            string process = lastApplication;
            if (String.IsNullOrEmpty(process) || clock.ElapsedMilliseconds - lastApplicationAt > 30000)
            {
                items.Add(new ToolStripMenuItem("先切到目标应用，再打开此菜单") { Enabled = false });
                return;
            }
            items.Add(new ToolStripMenuItem("目标应用：" + process) { Enabled = false });
            items.Add(new ToolStripSeparator());
            string[] values = { "auto", "light", "dark", "hold" };
            string[] labels = { "自动识别", "此应用使用浅色", "此应用使用深色", "保持进入前的明暗" };
            for (int i = 0; i < values.Length; i++)
            {
                string choice = values[i];
                ToolStripMenuItem item = new ToolStripMenuItem(labels[i]) { Checked = settings.RuleFor(process) == choice };
                item.Click += delegate
                {
                    SavePreferences(delegate(Settings next)
                    {
                        string key = Settings.ProcessKey(process);
                        if (choice == "auto") next.ApplicationRules.Remove(key);
                        else next.ApplicationRules[key] = choice;
                    });
                };
                items.Add(item);
            }
        }
        private void PopulatePace(ToolStripItemCollection items)
        {
            items.Clear();
            foreach (string name in new string[] { "灵敏", "均衡", "舒缓" })
            {
                string choice = name;
                Settings preset = new Settings(); preset.SetPace(choice);
                ToolStripMenuItem item = new ToolStripMenuItem(choice == "均衡" ? "均衡（推荐）" : choice)
                {
                    Checked = settings.PollMilliseconds == preset.PollMilliseconds && settings.StableMilliseconds == preset.StableMilliseconds &&
                        settings.ContentStableMilliseconds == preset.ContentStableMilliseconds && settings.MinimumSwitchMilliseconds == preset.MinimumSwitchMilliseconds &&
                        settings.SourceStableMilliseconds == preset.SourceStableMilliseconds && settings.SourceMinimumSwitchMilliseconds == preset.SourceMinimumSwitchMilliseconds
                };
                item.Click += delegate { SavePreferences(delegate(Settings next) { next.SetPace(choice); }); };
                items.Add(item);
            }
        }
        private void ShowPreferenceMenu(bool rules)
        {
            ContextMenuStrip menu = Ui.Menu();
            if (rules) PopulateRules(menu.Items); else PopulatePace(menu.Items);
            menu.Closed += delegate { menu.Dispose(); };
            menu.Show(Cursor.Position);
        }
        private void Dispatch(Action action, bool duringExit = false)
        {
            if ((!exiting || duringExit) && !window.IsDisposed)
            {
                try { window.BeginInvoke(action); }
                catch (InvalidOperationException) { }
            }
        }
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
        {
            if (args.Reason == SessionSwitchReason.SessionLock)
                Dispatch(delegate { sessionLocked = true; BeginRestore(); });
            else if (args.Reason == SessionSwitchReason.SessionUnlock)
                Dispatch(delegate { sessionLocked = false; ScheduleRecovery("Session unlocked"); });
        }
        private void OnPowerMode(object sender, PowerModeChangedEventArgs args)
        {
            if (args.Mode == PowerModes.Suspend)
                Dispatch(delegate { suspended = true; BeginRestore(); });
            else if (args.Mode == PowerModes.Resume)
                Dispatch(delegate { suspended = false; ScheduleRecovery("System resumed"); });
        }
        private void OnDisplaySettings(object sender, EventArgs args)
        { Dispatch(delegate { ScheduleRecovery("Display settings changed"); }); }
        internal void ScheduleRecovery(string reason)
        {
            if (exiting) return;
            ClearManagedAppearance();
            ResetDecisions();
            cadence.Wake(clock.ElapsedMilliseconds);
            WallpaperSampler.Invalidate();
            if (materials != null) { materials.InvalidatePending(); materials.Retry(); }
            recoveryAt = clock.ElapsedMilliseconds + 300;
            refreshAfterRecovery = true;
            timer.Interval = 75;
            Files.Log("Appearance recovery scheduled: " + reason);
        }
        private void OnSessionEnding(object sender, SessionEndingEventArgs args)
        {
            // SessionEnding may run on another thread. Marshal synchronously to
            // serialize restoration with timer/theme writes.
            if (!exiting && window.IsHandleCreated)
            {
                try { window.Invoke(new Action(delegate {
                    if (exiting) return;
                    timer.Stop();
                    ClearManagedAppearance();
                    ResetDecisions();
                    // The OS can terminate the message loop once this callback returns.
                    try { RestoreBoth(delegate { if (materials != null) materials.Restore(); }, delegate { controller.Restore(); }); }
                    catch (Exception error) { restoreFailure = error; SetPaused(true); Report(error); }
                })); }
                catch (InvalidOperationException) { }
            }
        }
        private void TogglePause()
        {
            if (restoring || exiting) return;
            if (!paused || restoreFailure != null)
            {
                if (!paused) pauseStatus = null;
                SetPaused(true);
                BeginRestore();
                return;
            }
            pauseStatus = null;
            SetPaused(false);
            ResetDecisions();
            WakeSampling(); Tick();
        }
        private void ChangeMode(string next)
        {
            if (restoring || exiting || next == mode) return;
            pendingMode = next;
            BeginRestore();
        }
        private void Tick()
        {
            if (restoring || exiting) return;
            try
            {
                if (paused || sessionLocked || suspended || SystemInformation.HighContrast)
                {
                    if (SystemInformation.HighContrast && !idleReleased)
                    { idleReleased = true; BeginRestore(); return; }
                    timer.Interval = 1000;
                    engine.Reset();
                    if (restoreFailure == null)
                        window.UpdateStatus(paused ? pauseStatus ?? "已暂停 · 已恢复或保留原主题" : "系统暂停 · 高对比度 / 锁屏 / 休眠", null, controller.Current.Tone);
                    UpdateMaterialStatus();
                    return;
                }
                idleReleased = false;
                if (!controller.OwnsCurrent())
                {
                    SetPaused(true);
                    pauseStatus = "检测到外部主题更改，已暂停";
                    Files.Log("Paused after an external theme change.");
                    BeginRestore(); // Leaves external theme changes in place.
                    return;
                }
                if (recoveryAt >= 0)
                {
                    if (clock.ElapsedMilliseconds < recoveryAt) return;
                    recoveryAt = -1;
                }
                if (refreshAfterRecovery)
                {
                    refreshAfterRecovery = false;
                    if (mode != "仅检测（不修改）") controller.Refresh();
                }
                // Leave the UI timer free to fade at display cadence; thumbnail reads can take 30ms.
                if (blend.Active) { timer.Interval = 40; return; }
                Tone current = controller.Current.Tone;
                Settings samplingSettings = settings;
                if (previewGlass != null)
                {
                    samplingSettings = Files.Json.Deserialize<Settings>(Files.Json.Serialize(settings));
                    samplingSettings.Glass = previewGlass;
                }
                WindowSample sample = WindowSampler.Read(samplingSettings);
                long now = clock.ElapsedMilliseconds;
                Rectangle sampledFrame = Native.FrameOf(sample.Window);
                if (sample.IsApplication && !String.IsNullOrEmpty(sample.ProcessName))
                { lastApplication = sample.ProcessName; lastApplicationAt = now; }
                bool visualReady = !visualTransition.Waiting;
                if (sample.Scene.HasValue || sample.Valid)
                    visualReady = visualTransition.Observe(SceneFor(sample), sample.Window.ToInt64(), sampledFrame, now);
                Tone? wanted = DesiredTone(sample, current);
                Tone? commit = null;
                bool fixedMode = mode == "固定浅色" || mode == "固定深色";
                if (fixedMode)
                {
                    wanted = mode == "固定浅色" ? Tone.Light : Tone.Dark;
                    if (visualReady) commit = wanted;
                }
                if (sample.Valid)
                {
                    if (!fixedMode)
                    {
                        Tone? decision = engine.Observe(sample.DecisionContext, sample.DecisionBrightness, current, now, sample.ExplicitRule || sample.IsWallpaper, sample.IsWallpaper, visualReady);
                        if (decision.HasValue && mode != "仅检测（不修改）") commit = decision;
                    }
                }
                else { engine.Reset(); timer.Interval = settings.PollMilliseconds; }
                if (sample.Scene.HasValue || sample.Valid)
                {
                    sceneCoordinator.Observe(SceneFor(sample), sample.DecisionContext, mode == "仅检测（不修改）" ? null : wanted, commit ?? current, now, settings.SourceStableMilliseconds);
                    if (sceneCoordinator.Ready) materialScene = sceneCoordinator.Current;
                    string basis = sample.Valid && !sample.ExplicitRule
                        ? (sample.IsWallpaper ? "壁纸" : "窗口内容") + (sample.Brightness >= .5 ? "偏亮 " : "偏暗 ") + sample.Brightness.ToString("P0")
                        : sample.Reason;
                    window.UpdateSceneStatus(visualTransition.Waiting ? "等待窗口动画稳定" : sceneCoordinator.Ready ? MaterialNames.Scene(materialScene) : "正在确认场景", basis);
                }
                timer.Interval = cadence.Observe(sample,sampledFrame,current,settings,now,
                    (sample.Valid || sample.Scene.HasValue) && (engine.HasPendingDecision || visualTransition.Waiting || !sceneCoordinator.Ready || (mode != "仅检测（不修改）" && wanted.HasValue && wanted.Value != current)), activity.Available);
                if (visualTransition.Waiting) timer.Interval = Math.Min(40, settings.PollMilliseconds);
                // Confirm the target scene before the taskbar notification callback uses it.
                if (commit.HasValue) CommitTheme(commit.Value);
                window.UpdateStatus(sample.Reason, sample.Valid && !sample.ExplicitRule ? (double?)sample.Brightness : null, controller.Current.Tone, sample.IsWallpaper ? "壁纸" : "窗口");
                ApplyMaterials();
                UpdateMaterialStatus();
                tray.Text = "LumaBar · " + (mode == "仅检测（不修改）" ? "仅检测" : controller.Current.Tone == Tone.Light ? "浅色" : "深色");
            }
            catch (Exception error)
            {
                SetPaused(true);
                pauseStatus = "已暂停 · " + error.Message;
                Report(error);
                BeginRestore();
            }
        }
        internal static MaterialScene SceneFor(WindowSample sample)
        { return sample.Scene ?? (sample.IsWallpaper ? (sample.IsDesktop ? MaterialScene.Desktop : MaterialScene.Window) : MaterialScene.Maximized); }
        private void Report(Exception error)
        {
            Files.Log(error.ToString());
            window.UpdateStatus("错误：" + error.Message, null, Tone.Dark);
            tray.ShowBalloonTip(5000, "LumaBar", error.Message.Length > 200 ? error.Message.Substring(0, 200) : error.Message, ToolTipIcon.Warning);
            UpdateMaterialStatus();
        }
        protected override void ExitThreadCore()
        {
            if (exitRequested || exiting) return;
            exitRequested = true;
            if (restoring) ShowRestoreProgress();
            else BeginRestore();
        }
        private void FinishExit()
        {
            exiting = true;
            timer.Stop();
            Task stopped = materials == null ? null : materials.StopAfterRestore();
            if ((stopped == null || stopped.IsCompleted) && Native.WaitForThemeRefresh(0)) { CloseTray(); return; }
            WaitForExitCleanup(stopped).ContinueWith(task => Dispatch(CloseTray, true), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        private static async Task WaitForExitCleanup(Task stopped)
        {
            if (stopped != null && !stopped.IsCompleted) await Task.WhenAny(stopped, Task.Delay(1000)).ConfigureAwait(false);
            if (!Native.WaitForThemeRefresh(0)) await Task.Run(delegate { Native.WaitForThemeRefresh(1500); }).ConfigureAwait(false);
        }
        private void CloseTray()
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.SessionEnding -= OnSessionEnding;
            SystemEvents.PowerModeChanged -= OnPowerMode;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettings;
            SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            activity.Dispose();
            activationWait.Unregister(null); shutdownWait.Unregister(null);
            activation.Dispose(); shutdown.Dispose();
            if (startupHealth != null) { startupHealth.Dispose(); startupHealth = null; }
            glow.Dispose(); blend.Dispose();
            tray.Visible = false;
            tray.Dispose();
            timer.Dispose();
            window.AllowClose = true;
            window.Close();
            window.Dispose();
            Files.Log("Stopped; restored owned theme changes.");
            base.ExitThreadCore();
        }
    }

    internal sealed class PreferencesWindow : Form
    {
        internal event Action<GlassSettings> AppearancePreview;
        internal event Action<bool, int, int> GlowPreviewChanged;
        private readonly AppearanceEditor appearance;
        private readonly ComboBox pace;
        private readonly ComboBox transition;
        private readonly CheckBox mouseGlow;
        private readonly ModernSlider glowRadius, glowStrength;
        private readonly CheckBox startup;
        private readonly DataGridView rules;
        private readonly bool originalStartup;
        private readonly Func<Action<Settings>, bool> save;

        internal PreferencesWindow(Settings settings, string startupCommand, Func<Action<Settings>, bool> save)
        {
            this.save = save;
            originalStartup = !String.IsNullOrEmpty(startupCommand);
            SuspendLayout();
            Text = "LumaBar · 设置";
            Ui.Window(this);
            ClientSize = new Size(806, 654);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            TabControl tabs = new SettingsPages { Name = "SettingsPages", Location = new Point(153, 14), Size = new Size(637, 576),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, TabStop = false };
            string[] pageNames = { "常规", "应用规则", "任务栏外观" };
            foreach (string name in pageNames)
            {
                TabPage page = new TabPage(name) { AutoScroll = true };
                tabs.TabPages.Add(page);
                AddLabel(page, name, 10, 8, 350, 32, heading: true);
            }
            Controls.Add(tabs);
            TabPage general = tabs.TabPages[0], applications = tabs.TabPages[1], visuals = tabs.TabPages[2];

            UiCard launch = AddCard(general, 47, 88);
            startup = new ModernCheckBox { Text = "登录 Windows 后自动启动", AccessibleName = "登录 Windows 后自动启动并收起到托盘",
                Checked = originalStartup, Location = new Point(16, 12), Size = new Size(565, 30) };
            launch.Controls.Add(startup);
            string expected = StartupRegistration.Command(Application.ExecutablePath);
            AddLabel(launch, originalStartup && !String.Equals(expected, startupCommand, StringComparison.OrdinalIgnoreCase)
                ? "检测到其他位置的自启项。保存时将改用当前程序，请先放到固定文件夹。"
                : "启动后收起到托盘。若自启被禁用，请在 Windows 的“启动应用”中启用。",
                16, 47, 570, 32, muted: true);

            UiCard timing = AddCard(general, 147, 129);
            AddLabel(timing, "切换速度", 16, 17, 238, 25);
            pace = new ModernComboBox { Name = "Pace", AccessibleName = "明暗判断速度", DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(320, 12), Size = new Size(274, 32) };
            pace.Items.AddRange(new object[] { "灵敏", "均衡", "舒缓", "自定义（保留当前参数）" });
            pace.SelectedIndex = 3;
            for (int i = 0; i < 3; i++)
            {
                Settings preset = new Settings(); preset.SetPace(pace.Items[i].ToString());
                if (settings.PollMilliseconds == preset.PollMilliseconds && settings.StableMilliseconds == preset.StableMilliseconds &&
                    settings.ContentStableMilliseconds == preset.ContentStableMilliseconds && settings.MinimumSwitchMilliseconds == preset.MinimumSwitchMilliseconds &&
                    settings.SourceStableMilliseconds == preset.SourceStableMilliseconds && settings.SourceMinimumSwitchMilliseconds == preset.SourceMinimumSwitchMilliseconds) pace.SelectedIndex = i;
            }
            timing.Controls.Add(pace);
            AddLabel(timing, "控制窗口切换与内容变化后的确认速度。", 16, 48, 562, 24, muted: true);
            AddLabel(timing, "过渡动画", 16, 75, 238, 25);
            transition = new ModernComboBox { Name = "Transition", AccessibleName = "过渡动画", DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(320, 70), Size = new Size(274, 32) };
            transition.Items.AddRange(new object[] { "标准（当前效果）", "柔和", "关闭" });
            transition.SelectedIndex = settings.Transition == "soft" ? 1 : settings.Transition == "off" ? 2 : 0;
            timing.Controls.Add(transition);
            AddLabel(timing, "只改变外观过渡，明暗判断速度和玻璃参数独立设置。", 16, 101, 562, 24, muted: true);

            UiCard glowCard = AddCard(general, 288, 228);
            mouseGlow = new ModernCheckBox { Name = "MouseGlow", AccessibleName = "任务栏鼠标光效", Text = "任务栏鼠标光效",
                Checked = settings.MouseGlow, Location = new Point(16, 8), Size = new Size(565, 30) };
            glowCard.Controls.Add(mouseGlow);
            AddLabel(glowCard, "光晕跟随任务栏上的指针，移开后淡出。", 16, 39, 570, 24, muted: true);
            AddLabel(glowCard, "光晕半径", 16, 70, 116, 27);
            glowRadius = new ModernSlider { Name = "MouseGlowRadius", AccessibleName = "光晕半径（百分比）", Minimum = 5, Maximum = 200,
                SmallChange = 1, LargeChange = 10, Value = settings.MouseGlowRadius, Location = new Point(126, 65), Size = new Size(400, 34) };
            Label radiusValue = new Label { Name = "MouseGlowRadiusValue", Location = new Point(530, 70), Size = new Size(64, 26), TextAlign = ContentAlignment.MiddleRight };
            glowCard.Controls.Add(glowRadius); glowCard.Controls.Add(radiusValue);
            AddLabel(glowCard, "光效强度", 16, 109, 116, 27);
            glowStrength = new ModernSlider { Name = "MouseGlowStrength", AccessibleName = "光效强度（百分比）", Minimum = 0, Maximum = 100,
                SmallChange = 1, LargeChange = 10, Value = settings.MouseGlowStrength, Location = new Point(126, 104), Size = new Size(400, 34) };
            Label strengthValue = new Label { Name = "MouseGlowStrengthValue", Location = new Point(530, 109), Size = new Size(64, 26), TextAlign = ContentAlignment.MiddleRight };
            glowCard.Controls.Add(glowStrength); glowCard.Controls.Add(strengthValue);
            AddLabel(glowCard, "拖动滑块实时预览。强度设为 0% 时关闭光效。", 16, 142, 570, 24, muted: true);
            GlowPreview glowPreview = new GlowPreview { Name = "MouseGlowPreview", Location = new Point(16, 168), Size = new Size(578, 48) };
            glowCard.Controls.Add(glowPreview);
            ModernButton resetGlow = new ModernButton { Name = "ResetMouseGlow", Text = "恢复光效默认值", Location = new Point(10, 528), Size = new Size(155, 32) };
            resetGlow.Click += delegate { glowRadius.Value = 100; glowStrength.Value = 25; };
            general.Controls.Add(resetGlow);

            Action updateGlow = delegate {
                glowRadius.Enabled = glowStrength.Enabled = mouseGlow.Checked;
                radiusValue.Text = glowRadius.Value + "%"; strengthValue.Text = glowStrength.Value + "%";
                glowRadius.AccessibleDescription = "当前半径 " + radiusValue.Text;
                glowStrength.AccessibleDescription = "当前强度 " + strengthValue.Text;
                glowPreview.UpdateEffect(glowRadius.Value, glowStrength.Value, mouseGlow.Checked && glowStrength.Value > 0);
                if (GlowPreviewChanged != null) GlowPreviewChanged(mouseGlow.Checked, glowRadius.Value, glowStrength.Value);
            };
            glowRadius.ValueChanged += delegate { updateGlow(); };
            glowStrength.ValueChanged += delegate { updateGlow(); };
            mouseGlow.CheckedChanged += delegate { updateGlow(); };
            updateGlow();
            LinkLabel license = new LinkLabel { Text = "关于与开源许可（含 TranslucentTB）", Location = new Point(184, 535), Size = new Size(410, 24) };
            license.LinkClicked += delegate { try { Process.Start("notepad.exe", "\"" + Path.Combine(Files.Root, "THIRD_PARTY_NOTICES.md") + "\""); } catch (Exception error) { Ui.Message(this, error.Message, "LumaBar"); } };
            general.Controls.Add(license);

            AddLabel(applications, "在空白行填写进程名（如 Code.exe、WeChat.exe）。规则仅在最大化或全屏时生效。\n删除该规则后，应用恢复自动识别。",
                10, 43, 607, 52, muted: true);
            UiCard ruleCard = AddCard(applications, 102, 394);
            rules = new DataGridView { Name = "ApplicationRules", AccessibleName = "应用规则列表", Location = new Point(12, 12), Size = new Size(588, 320),
                AutoGenerateColumns = false, AllowUserToAddRows = true, AllowUserToDeleteRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
            rules.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            rules.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            rules.Columns.Add(new DataGridViewTextBoxColumn { Name = "Process", HeaderText = "进程名", FillWeight = 55 });
            DataGridViewComboBoxColumn choice = new DataGridViewComboBoxColumn { Name = "Rule", HeaderText = "使用规则", FillWeight = 45,
                DisplayMember = "Label", ValueMember = "Value", DataSource = new RuleChoice[] {
                    new RuleChoice("auto", "自动识别"), new RuleChoice("light", "固定浅色"),
                    new RuleChoice("dark", "固定深色"), new RuleChoice("hold", "保持进入前的明暗") } };
            rules.Columns.Add(choice);
            rules.CellPainting += Ui.PaintRuleChoice;
            rules.DefaultValuesNeeded += delegate(object sender, DataGridViewRowEventArgs args) { args.Row.Cells[1].Value = "auto"; };
            foreach (System.Collections.Generic.KeyValuePair<string, string> entry in settings.ApplicationRules) rules.Rows.Add(entry.Key, entry.Value);
            ruleCard.Controls.Add(rules);
            Button remove = new ModernButton { Text = "删除选中规则", Location = new Point(12, 347), Size = new Size(155, 32) };
            remove.Click += delegate { if (rules.CurrentRow != null && !rules.CurrentRow.IsNewRow) rules.Rows.Remove(rules.CurrentRow); };
            ruleCard.Controls.Add(remove);

            UiCard appearanceCard = AddCard(visuals, 47, 520);
            appearanceCard.Anchor |= AnchorStyles.Bottom;
            appearanceCard.Padding = new Padding(6);
            appearance = new AppearanceEditor(settings.Glass) { Dock = DockStyle.Fill };
            appearance.PreviewRequested += delegate(GlassSettings glass) { if (AppearancePreview != null) AppearancePreview(glass); };
            appearanceCard.Controls.Add(appearance);

            Controls.Add(new PictureBox { Image = Ui.BrandImage, Location = new Point(19, 24), Size = new Size(24, 24), SizeMode = PictureBoxSizeMode.Zoom });
            AddLabel(this, "设置", 50, 22, 75, 28, heading: true);
            Controls.Add(new Panel { BackColor = Ui.Border, Location = new Point(141, 16), Size = new Size(1, 572),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left });
            ModernButton[] navigation = new ModernButton[pageNames.Length];
            for (int index = 0; index < navigation.Length; index++)
            {
                int target = index;
                navigation[index] = new ModernButton { Name = "SettingsNavigation" + index, Navigation = true, Text = pageNames[index], AccessibleName = pageNames[index],
                    Location = new Point(15, 68 + index*42), Size = new Size(112, 36), TabIndex = index };
                navigation[index].Click += delegate { tabs.SelectedIndex = target; };
                Controls.Add(navigation[index]);
            }

            Controls.Add(new Panel { BackColor = Ui.Border, Location = new Point(16, 598), Size = new Size(774, 1),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right });
            Button cancel = new ModernButton { Text = "取消", Location = new Point(528, 609), Size = new Size(112, 34),
                DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, TabIndex = 4 };
            Button apply = new ModernButton { Text = "保存并应用", Primary = true, Location = new Point(650, 609), Size = new Size(140, 34),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right, TabIndex = 5 };
            apply.Click += delegate { ApplySettings(startupCommand, expected); };
            Controls.Add(cancel); Controls.Add(apply); AcceptButton = apply; CancelButton = cancel;
            tabs.TabIndex = 3;
            Ui.Apply(this);
            foreach (ModernButton item in navigation) item.BackColor = Ui.Canvas;
            foreach (Control section in new Control[] { general, launch, timing, glowCard, applications, ruleCard }) Ui.OrderTabStops(section);
            Action updateNavigation = delegate {
                for (int index = 0; index < navigation.Length; index++)
                {
                    ModernButton item = navigation[index];
                    item.Selected = index == tabs.SelectedIndex;
                    item.AccessibleDescription = item.Selected ? "当前页面" : "";
                    item.Invalidate();
                }
            };
            tabs.SelectedIndexChanged += delegate { updateNavigation(); };
            tabs.SelectedIndex = 0;
            updateNavigation();
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(true);
            Shown += delegate {
                updateNavigation();
                Rectangle work = Screen.FromControl(this).WorkingArea;
                if (Height > work.Height - 24) Height = Math.Max(300, work.Height - 24);
                if (Width > work.Width - 24) Width = Math.Max(320, work.Width - 24);
                Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
                Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
            };
        }

        private static UiCard AddCard(TabPage page, int top, int height)
        {
            UiCard card = new UiCard { Location = new Point(10, top), Size = new Size(606, height), MinimumSize = new Size(606, 0) };
            page.Controls.Add(card);
            return card;
        }

        private static void AddLabel(Control parent, string text, int x, int y, int width, int height, bool muted = false, bool heading = false)
        {
            Label label = new Label { Text = text, Location = new Point(x, y), Size = new Size(width, height) };
            if (muted) { label.ForeColor = Color.DimGray; label.Font = new Font(Ui.BodyFont.FontFamily, 9); }
            if (heading) label.Font = new Font(Ui.BodyFont.FontFamily, 15, FontStyle.Bold);
            parent.Controls.Add(label);
        }
        private void ApplySettings(string previousCommand, string expected)
        {
            try
            {
                if (!rules.EndEdit()) throw new ArgumentException("请先完成正在编辑的规则。");
                Settings edited = new Settings();
                foreach (DataGridViewRow row in rules.Rows)
                {
                    if (row.IsNewRow) continue;
                    string process = Settings.ProcessKey(Convert.ToString(row.Cells[0].Value));
                    if (String.IsNullOrWhiteSpace(process)) throw new ArgumentException("规则中的进程名不能为空。请填写或删除该行。");
                    if (edited.ApplicationRules.ContainsKey(process)) throw new ArgumentException("进程名重复：" + process);
                    edited.ApplicationRules.Add(process, Convert.ToString(row.Cells[1].Value));
                }
                edited.Validate();
                string selectedPace = pace.SelectedIndex < 3 ? pace.SelectedItem.ToString() : null;
                bool startupChanged = startup.Checked != originalStartup || (startup.Checked && previousCommand != expected);
                if (startupChanged) StartupRegistration.Set(startup.Checked);
                bool saved = save(delegate(Settings next) {
                    if (selectedPace != null) next.SetPace(selectedPace);
                    next.MouseGlow = mouseGlow.Checked;
                    next.MouseGlowRadius = glowRadius.Value; next.MouseGlowStrength = glowStrength.Value;
                    next.Transition = transition.SelectedIndex == 1 ? "soft" : transition.SelectedIndex == 2 ? "off" : "standard";
                    next.ApplicationRules = edited.ApplicationRules;
                    next.Glass = appearance.Value;
                });
                if (!saved)
                {
                    if (startupChanged)
                    {
                        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                            StartupRegistration.Write(key, previousCommand);
                    }
                    Ui.Message(this, "保存未完成，请检查主界面错误信息后重试。", "LumaBar");
                    return;
                }
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception error) { Ui.Message(this, error.Message, "无法保存设置"); }
        }
        private sealed class RuleChoice
        {
            public string Value { get; private set; }
            public string Label { get; private set; }
            internal RuleChoice(string value, string label) { Value = value; Label = label; }
        }
    }

    internal sealed class StatusWindow : Form
    {
        private static readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        internal event Action ShellRestarted;
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (taskbarCreated != 0 && message.Msg == taskbarCreated && ShellRestarted != null) ShellRestarted();
        }
        internal event Action HideRequested;
        internal event Action ExitRequested;
        internal event Action PauseRequested;
        internal event Action<string> ModeChanged;
        internal event Action RulesRequested;
        internal event Action PaceRequested;
        internal event Action SettingsRequested;
        internal event Action RetryRequested;
        internal bool AllowClose;
        private readonly Label status, brightness, theme, sceneValue, basisValue, materialValue;
        private readonly Button retry, pause;
        private readonly ToolTip details = new ToolTip();
        private readonly ProgressBar meter;
        private readonly ComboBox modes;

        internal StatusWindow()
        {
            SuspendLayout();
            Text = "LumaBar";
            Ui.Window(this);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(568, 588);

            Controls.Add(new PictureBox { Image = Ui.BrandImage, Location = new Point(20,18), Size = new Size(36,36), SizeMode = PictureBoxSizeMode.Zoom });
            AddLabel(this, "LumaBar", 68, 12, 260, 34, 17, FontStyle.Bold);
            Version version = typeof(Program).Assembly.GetName().Version;
            AddLabel(this, version.ToString(version.Revision > 0 ? 4 : 3), 70, 47, 170, 20, 9, muted: true);
            AddButton(this, "HomeSettings", "设置…", 448, 22, 100, delegate { if (SettingsRequested != null) SettingsRequested(); });

            UiCard detection = AddCard(80, 139);
            AddLabel(detection, "明暗检测", 16, 12, 180, 28, 12, FontStyle.Bold);
            UiCard badge = new UiCard { BackColor = Ui.AccentSoft, Location = new Point(355, 12), Size = new Size(157, 30) };
            detection.Controls.Add(badge);
            theme = AddLabel(badge, "等待取样", 7, 3, 143, 24, 9);
            theme.TextAlign = ContentAlignment.MiddleCenter;
            status = AddLabel(detection, "切换到其他应用后开始检测", 16, 48, 496, 43, 10);
            status.Name = "DetectionStatus";
            brightness = AddLabel(detection, "亮度 —", 16, 94, 496, 23, 9, muted: true);
            brightness.Name = "DetectionBrightness";
            meter = new ModernMeter { Name = "BrightnessMeter", AccessibleName = "当前取样亮度", Location = new Point(16, 121), Size = new Size(496, 5), Maximum = 100 };
            detection.Controls.Add(meter);

            UiCard operation = AddCard(231, 126);
            AddLabel(operation, "运行模式", 16, 9, 180, 25, 12, FontStyle.Bold);
            modes = new ModernComboBox { Name = "HomeMode", AccessibleName = "运行模式", Location = new Point(16, 44), Size = new Size(248, 32), DropDownStyle = ComboBoxStyle.DropDownList };
            modes.Items.AddRange(new object[] { "自动适应", "固定浅色", "固定深色", "仅检测（不修改）" });
            modes.SelectedIndex = 0;
            modes.SelectedIndexChanged += delegate { if (ModeChanged != null && modes.SelectedItem != null) ModeChanged(modes.SelectedItem.ToString()); };
            operation.Controls.Add(modes);
            pause = AddButton(operation, "HomePause", "暂停并恢复", 276, 39, 236, delegate { if (PauseRequested != null) PauseRequested(); });
            AddButton(operation, "HomeRules", "应用纠正…", 16, 79, 248, delegate { if (RulesRequested != null) RulesRequested(); });
            AddButton(operation, "HomePace", "切换节奏…", 276, 79, 236, delegate { if (PaceRequested != null) PaceRequested(); });

            UiCard appearance = AddCard(369, 151);
            AddLabel(appearance, "任务栏外观", 16, 13, 180, 28, 12, FontStyle.Bold);
            retry = AddButton(appearance, "HomeRetry", "重新应用任务栏外观", 328, 10, 184, delegate { if (RetryRequested != null) RetryRequested(); });
            sceneValue = AddDetail(appearance, "场景", "等待场景", 50, 24);
            basisValue = AddDetail(appearance, "依据", "等待取样", 77, 24);
            materialValue = AddDetail(appearance, "材质", "尚未应用", 104, 36);

            Controls.Add(new Panel { BackColor = Ui.Border, Location = new Point(20, 532), Size = new Size(528, 1) });
            AddButton(this, "HomeHide", "收起到托盘", 276, 544, 130, delegate { if (HideRequested != null) HideRequested(); });
            AddButton(this, "HomeExit", "退出并恢复", 418, 544, 130, delegate { if (ExitRequested != null) ExitRequested(); });
            Ui.Apply(this);
            theme.ForeColor = Ui.SelectionText(true);
            Ui.OrderTabStops(this);
            Disposed += delegate { details.Dispose(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (!AllowClose && args.CloseReason == CloseReason.UserClosing)
                { args.Cancel = true; if (HideRequested != null) HideRequested(); }
            };
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(true);
            Shown += delegate {
                Rectangle work = Screen.FromControl(this).WorkingArea;
                if (Height > work.Height - 24 || Width > work.Width - 24)
                {
                    AutoScroll = true;
                    Height = Math.Min(Height, Math.Max(300, work.Height - 24));
                    Width = Math.Min(Width, Math.Max(320, work.Width - 24));
                    Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
                    Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
                }
            };
        }
        private UiCard AddCard(int y, int height)
        {
            UiCard card = new UiCard { Location = new Point(20, y), Size = new Size(528, height) };
            Controls.Add(card);
            return card;
        }
        private static Label AddLabel(Control parent, string text, int x, int y, int width, int height, float size, FontStyle style = FontStyle.Regular, bool muted = false)
        {
            Label label = new Label { Text = text, Location = new Point(x, y), Size = new Size(width, height),
                Font = new Font(Ui.BodyFont.FontFamily, size, style), ForeColor = muted ? Color.DimGray : Ui.Text,
                AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            parent.Controls.Add(label);
            return label;
        }
        private static Label AddDetail(Control parent, string caption, string value, int y, int height)
        {
            AddLabel(parent, caption, 16, y, 47, 24, 9, muted: true);
            Label detail = AddLabel(parent, value, 68, y, 444, height, 9);
            detail.TextAlign = ContentAlignment.TopLeft;
            detail.Padding = new Padding(0,3,0,0);
            return detail;
        }
        private static Button AddButton(Control parent, string name, string text, int x, int y, int width, Action action)
        {
            Button button = new ModernButton { Name = name, Text = text, Location = new Point(x, y), Size = new Size(width, 32) };
            button.Click += delegate { action(); };
            parent.Controls.Add(button);
            return button;
        }
        private void SetDetail(Label label, string value, string suffix = "")
        {
            if (label.Text == value) return;
            label.Text = value;
            details.SetToolTip(label, value + suffix);
        }
        internal void UpdateSceneStatus(string scene, string basis)
        {
            SetDetail(sceneValue, scene);
            SetDetail(basisValue, basis);
        }
        internal void UpdateMaterialStatus(string text, bool canRetry = false, bool allowAction = true, string actionText = null)
        {
            SetDetail(materialValue, text, "\n图标与文字明暗由 Windows 统一控制。");
            retry.Visible = allowAction;
            retry.Text = actionText ?? (canRetry ? "重试玻璃效果" : "重新应用任务栏外观");
        }
        internal void SetPaused(bool value, bool retryRestore)
        { pause.Text = value ? "继续适应" : "暂停并恢复"; pause.Enabled = !retryRestore; }
        internal void SetRestoring(bool value) { pause.Enabled = modes.Enabled = !value; }
        internal void SelectMode(string value) { modes.SelectedItem = value; }
        internal void UpdateStatusText(string text) { SetDetail(status, text); }
        internal void UpdateStatus(string text, double? value, Tone current, string source = "窗口")
        {
            UpdateStatusText(text);
            theme.Text = "系统模式 · " + (current == Tone.Light ? "浅色" : "深色");
            brightness.Text = value.HasValue ? source + "亮度 " + value.Value.ToString("P0") + "  · 稳定后切换" : "亮度 —";
            meter.Value = value.HasValue ? Math.Max(0, Math.Min(100, (int)(value.Value * 100))) : 0;
        }
    }
}
