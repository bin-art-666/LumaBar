using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LumaBar
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Native.EnableDpi();
            string sid = WindowsIdentity.GetCurrent().User.Value;
            using (Mutex mutex = new Mutex(false, @"Local\LumaBar-" + sid))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    if (Array.IndexOf(args, "--startup") >= 0) return 0;
                    MessageBox.Show("LumaBar 已经运行。请在系统托盘中打开它，或先退出另一个副本。", "LumaBar");
                    return 2;
                }
                try
                {
                    ThemeController controller = new ThemeController(new RegistryThemeStore(), new FileJournal());
                    if (Array.IndexOf(args, "--restore") >= 0)
                    {
                        bool restored = controller.Restore();
                        MessageBox.Show(restored ? "恢复完成（或没有待恢复的更改）。" : "主题已被其他程序更改，保留当前设置。", "LumaBar");
                        return 0;
                    }
                    Settings settings = Files.ReadSettings();
                    controller.Restore(); // Recover interrupted session before capturing a new baseline.
                    using (TrayApp context = new TrayApp(settings, controller, Array.IndexOf(args, "--monitor") >= 0, Array.IndexOf(args, "--startup") >= 0)) Application.Run(context);
                    return 0;
                }
                catch (Exception error)
                {
                    Files.Log(error.ToString());
                    MessageBox.Show(error.Message + "\n\n请在可写文件夹中运行；详细信息见 LumaBar.log。", "LumaBar 无法启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        private GlassSettings previewGlass;
        private readonly ThemeController controller;
        private DecisionEngine engine;
        private readonly NotifyIcon tray;
        private readonly System.Windows.Forms.Timer timer;
        private readonly StatusWindow window;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly ToolStripMenuItem pauseItem;
        private bool paused;
        private bool sessionLocked;
        private bool suspended;
        private bool exiting;
        private string mode = "自动适应";
        private string lastApplication;
        private long lastApplicationAt = -100000;
        private ProbeSchedule probeSchedule = new ProbeSchedule();
        private readonly WindowEvents windowEvents;
        internal bool StatusWindowVisible { get { return window.Visible; } }
        internal bool StatusHandleCreated { get { return window.IsHandleCreated; } }

        internal TrayApp(Settings settings, ThemeController controller, bool monitorOnly, bool startHidden = false)
        {
            this.settings = settings;
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
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开 LumaBar", null, delegate { ShowWindow(); });
            menu.Items.Add("设置…", null, delegate { ShowSettings(); });
            pauseItem = new ToolStripMenuItem("暂停并恢复原主题", null, delegate { TogglePause(); });
            menu.Items.Add(pauseItem);
            menu.Items.Add("重新同步任务栏", null, delegate { Resync(); });
            menu.Items.Add("重试玻璃效果", null, delegate { RetryMaterials(); });
            ToolStripMenuItem rules = new ToolStripMenuItem("应用纠正");
            rules.DropDownItems.Add("读取目标应用…");
            rules.DropDownOpening += delegate { PopulateRules(rules.DropDownItems); };
            menu.Items.Add(rules);
            ToolStripMenuItem pace = new ToolStripMenuItem("切换节奏");
            pace.DropDownItems.Add("读取切换节奏…");
            pace.DropDownOpening += delegate { PopulatePace(pace.DropDownItems); };
            menu.Items.Add(pace);
            menu.Items.Add("打开设置文件", null, delegate
            {
                try { Process.Start("notepad.exe", "\"" + Path.Combine(Files.Root, "settings.json") + "\""); }
                catch (Exception error) { Report(error); }
            });
            menu.Items.Add("退出并恢复", null, delegate { ExitThread(); });
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "LumaBar · 自动适应", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += delegate { ShowWindow(); };
            timer = new System.Windows.Forms.Timer { Interval = settings.PollMilliseconds };
            timer.Tick += delegate { Tick(); };
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.SessionEnding += OnSessionEnding;
            SystemEvents.PowerModeChanged += OnPowerMode;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            windowEvents = new WindowEvents(RequestFastProbe);
            if (!windowEvents.Available) Files.Log("Some window event hooks are unavailable; timer polling remains active.");
            // Create a handle for session callbacks even when logging in to tray.
            IntPtr handle = window.Handle;
            if (!startHidden) window.Show();
            controller.Refresh(); // Also repair stale visuals when the registry already matches.
            RequestFastProbe();
            timer.Start();
            Files.Log("Started. No images or window titles are recorded.");
        }

        private void ShowWindow() { window.Show(); window.WindowState = FormWindowState.Normal; window.Activate(); }
        private void Resync()
        {
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
                settings = next; engine = new DecisionEngine(settings);
                probeSchedule = new ProbeSchedule();
                timer.Interval = settings.PollMilliseconds;
                window.UpdateStatus("设置已保存，立即生效", null, controller.Current.Tone);
                return true;
            }
            catch (Exception error) { Report(error); return false; }
        }
        private void ApplyMaterials(bool force = false)
        {
            if (mode == "仅检测（不修改）" || paused || sessionLocked || suspended || SystemInformation.HighContrast || !sceneCoordinator.Ready) return;
            GlassSettings glass = previewGlass ?? settings.Glass;
            if (materials == null && glass.Enabled) materials = new MaterialController();
            if (materials != null) materials.Apply(glass, materialScene, controller.Current.Tone, force);
        }
        private void RestoreMaterials() { if (materials != null) materials.Restore(); }
        internal static void RestoreBoth(Action material, Action theme)
        {
            System.Collections.Generic.List<Exception> errors = new System.Collections.Generic.List<Exception>();
            try { material(); } catch (Exception error) { errors.Add(error); }
            try { theme(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count > 0) throw new AggregateException("部分恢复未完成，请重试。", errors);
        }
        private void RestoreManagedState() { RestoreBoth(RestoreMaterials, delegate { controller.Restore(); }); }
        private void RetryMaterials()
        {
            if (paused || sessionLocked || suspended || SystemInformation.HighContrast)
            {
                try { RestoreManagedState(); } catch (Exception error) { Report(error); }
                UpdateMaterialStatus(); return;
            }
            if (materials != null) materials.Retry();
            ApplyMaterials(true);
            UpdateMaterialStatus();
        }
        private void CommitTheme(Tone tone)
        {
            if (tone != controller.Current.Tone && materials != null) materials.InvalidatePending();
            controller.Apply(tone);
        }
        private void UpdateMaterialStatus()
        {
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
            try
            {
                using (PreferencesWindow preferences = new PreferencesWindow(settings, StartupRegistration.Read(), SavePreferences))
                {
                    preferences.AppearancePreview += delegate(GlassSettings glass) { previewGlass = glass; ApplyMaterials(true); };
                    try { preferences.ShowDialog(window); }
                    finally { previewGlass = null; engine = new DecisionEngine(settings); probeSchedule = new ProbeSchedule(); RequestFastProbe(); ApplyMaterials(true); }
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
            ContextMenuStrip menu = new ContextMenuStrip();
            if (rules) PopulateRules(menu.Items); else PopulatePace(menu.Items);
            menu.Closed += delegate { menu.Dispose(); };
            menu.Show(Cursor.Position);
        }
        private void Dispatch(Action action)
        {
            if (!exiting && !window.IsDisposed)
            {
                try { window.BeginInvoke(action); }
                catch (InvalidOperationException) { }
            }
        }
        private void RequestFastProbe()
        {
            if (exiting || paused || sessionLocked || suspended || timer == null) return;
            if (probeSchedule.Signal(clock.ElapsedMilliseconds)) timer.Interval = 1;
        }
        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
        {
            if (args.Category == UserPreferenceCategory.Desktop)
                Dispatch(delegate { WallpaperSampler.Invalidate(); RequestFastProbe(); });
        }
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
        {
            if (args.Reason == SessionSwitchReason.SessionLock)
                Dispatch(delegate { sessionLocked = true; ReleaseForIdle(); });
            else if (args.Reason == SessionSwitchReason.SessionUnlock)
                Dispatch(delegate { sessionLocked = false; engine.Reset(); RequestFastProbe(); });
        }
        private void OnPowerMode(object sender, PowerModeChangedEventArgs args)
        {
            if (args.Mode == PowerModes.Suspend)
                Dispatch(delegate { suspended = true; ReleaseForIdle(); });
            else if (args.Mode == PowerModes.Resume)
                Dispatch(delegate { suspended = false; engine.Reset(); WallpaperSampler.Invalidate(); RequestFastProbe(); });
        }
        private void OnSessionEnding(object sender, SessionEndingEventArgs args)
        {
            // SessionEnding may run on another thread. Marshal synchronously to
            // serialize restoration with timer/theme writes.
            if (!exiting && window.IsHandleCreated)
            {
                try { window.Invoke(new Action(delegate { timer.Stop(); ReleaseForIdle(); })); }
                catch (InvalidOperationException) { }
            }
        }
        private void ReleaseForIdle()
        {
            engine.Reset(); sceneCoordinator.ResetPending();
            try { RestoreManagedState(); }
            catch (Exception error) { paused = true; Report(error); }
        }

        private void TogglePause()
        {
            try
            {
                bool resume = paused;
                paused = !resume;
                pauseItem.Text = paused ? "恢复自动适应" : "暂停并恢复原主题";
                window.SetPaused(paused);
                if (paused) RestoreManagedState();
                engine.Reset(); sceneCoordinator.ResetPending();
                Tick();
            }
            catch (Exception error) { Report(error); }
        }
        private void ChangeMode(string next)
        {
            try
            {
                RestoreManagedState();
                engine.Reset(); sceneCoordinator.ResetPending();
                mode = next;
                Tick();
            }
            catch (Exception error) { paused = true; Report(error); }
        }
        private void Tick()
        {
            if (exiting) return;
            try
            {
                if (paused || sessionLocked || suspended || SystemInformation.HighContrast)
                {
                    if (SystemInformation.HighContrast) ReleaseForIdle();
                    engine.Reset();
                    window.UpdateStatus(paused ? "已暂停 · 已恢复或保留原主题" : "系统暂停 · 高对比度 / 锁屏 / 休眠", null, controller.Current.Tone);
                    UpdateMaterialStatus();
                    return;
                }
                if (!controller.OwnsCurrent())
                {
                    paused = true;
                    RestoreManagedState(); // Leaves external theme changes in place.
                    pauseItem.Text = "恢复自动适应";
                    window.SetPaused(true);
                    window.UpdateStatus("检测到外部主题更改，已暂停", null, controller.Current.Tone);
                    return;
                }
                Tone current = controller.Current.Tone;
                Settings samplingSettings = settings;
                if (previewGlass != null)
                {
                    samplingSettings = Files.Json.Deserialize<Settings>(Files.Json.Serialize(settings));
                    samplingSettings.Glass = previewGlass;
                }
                WindowSample sample = WindowSampler.Read(samplingSettings);
                long now = clock.ElapsedMilliseconds;
                if (sample.IsApplication && !String.IsNullOrEmpty(sample.ProcessName))
                { lastApplication = sample.ProcessName; lastApplicationAt = now; }
                Tone? wanted = DesiredTone(sample, current);
                bool fixedMode = mode == "固定浅色" || mode == "固定深色";
                if (fixedMode)
                {
                    wanted = mode == "固定浅色" ? Tone.Light : Tone.Dark;
                    CommitTheme(wanted.Value);
                }
                if (sample.Valid)
                {
                    probeSchedule.Observe(now, sample.IsWallpaper, sample.DecisionContext);
                    timer.Interval = probeSchedule.Interval(now, settings.PollMilliseconds);
                    if (!fixedMode)
                    {
                        Tone? decision = engine.Observe(sample.DecisionContext, sample.DecisionBrightness, current, now, sample.ExplicitRule || sample.IsWallpaper, sample.IsWallpaper);
                        if (decision.HasValue && mode != "仅检测（不修改）") CommitTheme(decision.Value);
                    }
                }
                else { engine.Reset(); timer.Interval = probeSchedule.Interval(now, settings.PollMilliseconds); }
                if (sample.Scene.HasValue || sample.Valid)
                {
                    sceneCoordinator.Observe(SceneFor(sample), sample.DecisionContext, mode == "仅检测（不修改）" ? null : wanted, controller.Current.Tone, now, settings.SourceStableMilliseconds);
                    if (sceneCoordinator.Ready) materialScene = sceneCoordinator.Current;
                    string basis = sample.Valid && !sample.ExplicitRule
                        ? (sample.IsWallpaper ? "壁纸" : "窗口内容") + (sample.Brightness >= .5 ? "偏亮 " : "偏暗 ") + sample.Brightness.ToString("P0")
                        : sample.Reason;
                    window.UpdateSceneStatus(sceneCoordinator.Ready ? MaterialNames.Scene(materialScene) : "正在确认场景", basis);
                }
                window.UpdateStatus(sample.Reason, sample.Valid && !sample.ExplicitRule ? (double?)sample.Brightness : null, controller.Current.Tone, sample.IsWallpaper ? "壁纸" : "窗口");
                ApplyMaterials();
                UpdateMaterialStatus();
                tray.Text = "LumaBar · " + (mode == "仅检测（不修改）" ? "仅检测" : controller.Current.Tone == Tone.Light ? "浅色" : "深色");
            }
            catch (Exception error)
            {
                paused = true;
                pauseItem.Text = "恢复自动适应";
                window.SetPaused(true);
                try { RestoreManagedState(); } catch (Exception restoreError) { Files.Log(restoreError.ToString()); }
                Report(error);
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
            if (exiting) return;
            timer.Stop();
            try { RestoreManagedState(); }
            catch (Exception error)
            {
                paused = true;
                window.SetPaused(true);
                pauseItem.Text = "恢复自动适应";
                Report(error);
                ShowWindow();
                MessageBox.Show(window, "恢复失败，程序保持暂停。请检查日志后重试退出；恢复记录会保留。", "LumaBar");
                return;
            }
            // Give the final restore notifications a bounded chance to complete before process exit.
            Native.WaitForThemeRefresh(1500);
            exiting = true;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.SessionEnding -= OnSessionEnding;
            SystemEvents.PowerModeChanged -= OnPowerMode;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            windowEvents.Dispose();
            if (materials != null) materials.Dispose();
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
        private readonly AppearanceEditor appearance;
        private readonly ComboBox pace;
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
            Font = new Font("Microsoft YaHei UI", 10);
            ClientSize = new Size(660, 680);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(247, 249, 252);
            TabControl tabs = new TabControl { Location = new Point(20, 20), Size = new Size(620, 588), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            TabPage general = new TabPage("常规") { BackColor = Color.White, AutoScroll = true };
            TabPage applications = new TabPage("应用规则") { BackColor = Color.White, AutoScroll = true };
            tabs.TabPages.Add(general); tabs.TabPages.Add(applications); Controls.Add(tabs);
            TabPage visuals = new TabPage("任务栏外观") { BackColor = Color.White };
            appearance = new AppearanceEditor(settings.Glass) { Dock = DockStyle.Fill };
            appearance.PreviewRequested += delegate(GlassSettings glass) { if (AppearancePreview != null) AppearancePreview(glass); };
            visuals.Controls.Add(appearance); tabs.TabPages.Add(visuals);
            Label title = new Label { Text = "让任务栏与开始菜单自然适应明暗", Location = new Point(22, 20), Size = new Size(555, 32), Font = new Font(Font.FontFamily, 14, FontStyle.Bold) };
            general.Controls.Add(title);
            general.Controls.Add(new Label { Text = "最大化 / 全屏时跟随软件；普通窗口与桌面跟随壁纸。\n自动 / 固定浅色 / 固定深色可在主界面切换。", Location = new Point(24, 65), Size = new Size(550, 55) });
            startup = new CheckBox { Text = "登录 Windows 后自动启动（收起到托盘）", Checked = originalStartup, Location = new Point(24, 142), Size = new Size(550, 30) };
            general.Controls.Add(startup);
            string expected = StartupRegistration.Command(Application.ExecutablePath);
            general.Controls.Add(new Label { Text = originalStartup && !String.Equals(expected, startupCommand, StringComparison.OrdinalIgnoreCase)
                ? "已存在其他位置的 LumaBar 自启项。\n保存时将更新为当前程序；请先放到固定文件夹。"
                : "建议先解压到固定文件夹。移动程序后需重新开启自启。\n若 Windows 禁用了此启动项，请在系统的“启动应用”中启用。",
                Location = new Point(44, 179), Size = new Size(530, 65), ForeColor = Color.DimGray });
            general.Controls.Add(new Label { Text = "切换速度", Location = new Point(24, 260), Size = new Size(160, 27) });
            pace = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(190, 257), Size = new Size(260, 30) };
            pace.Items.AddRange(new object[] { "灵敏", "均衡", "舒缓", "自定义（保留当前参数）" });
            pace.SelectedIndex = 3;
            for (int i = 0; i < 3; i++)
            {
                Settings preset = new Settings(); preset.SetPace(pace.Items[i].ToString());
                if (settings.PollMilliseconds == preset.PollMilliseconds && settings.StableMilliseconds == preset.StableMilliseconds &&
                    settings.ContentStableMilliseconds == preset.ContentStableMilliseconds && settings.MinimumSwitchMilliseconds == preset.MinimumSwitchMilliseconds &&
                    settings.SourceStableMilliseconds == preset.SourceStableMilliseconds && settings.SourceMinimumSwitchMilliseconds == preset.SourceMinimumSwitchMilliseconds) pace.SelectedIndex = i;
            }
            general.Controls.Add(pace);
            general.Controls.Add(new Label { Text = "灵敏：响应更快　·　均衡：推荐　·　舒缓：减少切换\n速度调整同时作用于最大化、还原和窗口内容变化。", Location = new Point(24, 305), Size = new Size(550, 62), ForeColor = Color.DimGray });
            LinkLabel license = new LinkLabel { Text = "关于与开源许可（含 TranslucentTB）", Location = new Point(24, 382), Size = new Size(550, 28) };
            license.LinkClicked += delegate { try { Process.Start("notepad.exe", "\"" + Path.Combine(Files.Root, "THIRD_PARTY_NOTICES.md") + "\""); } catch (Exception error) { MessageBox.Show(this, error.Message, "LumaBar"); } };
            general.Controls.Add(license);
            applications.Controls.Add(new Label { Text = "填写进程名（如 Code.exe、WeChat.exe）。规则仅在最大化 / 全屏时生效。\n普通窗口仍跟随壁纸；删除规则后恢复自动识别。", Location = new Point(16, 14), Size = new Size(580, 52) });
            rules = new DataGridView { Location = new Point(16, 78), Size = new Size(576, 265), BackgroundColor = Color.White,
                AutoGenerateColumns = false, AllowUserToAddRows = true, AllowUserToDeleteRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, RowHeadersWidth = 32 };
            rules.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            rules.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            rules.Columns.Add(new DataGridViewTextBoxColumn { Name = "Process", HeaderText = "进程名", FillWeight = 55 });
            DataGridViewComboBoxColumn choice = new DataGridViewComboBoxColumn { Name = "Rule", HeaderText = "使用规则", FillWeight = 45,
                DisplayMember = "Label", ValueMember = "Value", DataSource = new RuleChoice[] {
                    new RuleChoice("auto", "自动识别"), new RuleChoice("light", "固定浅色"),
                    new RuleChoice("dark", "固定深色"), new RuleChoice("hold", "保持进入前的明暗") } };
            rules.Columns.Add(choice);
            rules.DefaultValuesNeeded += delegate(object sender, DataGridViewRowEventArgs args) { args.Row.Cells[1].Value = "auto"; };
            foreach (System.Collections.Generic.KeyValuePair<string, string> entry in settings.ApplicationRules) rules.Rows.Add(entry.Key, entry.Value);
            applications.Controls.Add(rules);
            Button remove = new Button { Text = "删除选中规则", Location = new Point(16, 355), Size = new Size(155, 34) };
            remove.Click += delegate { if (rules.CurrentRow != null && !rules.CurrentRow.IsNewRow) rules.Rows.Remove(rules.CurrentRow); };
            applications.Controls.Add(remove);
            Button apply = new Button { Text = "保存并应用", Location = new Point(366, 626), Size = new Size(140, 34) };
            Button cancel = new Button { Text = "取消", Location = new Point(520, 626), Size = new Size(120, 34), DialogResult = DialogResult.Cancel };
            apply.Anchor = cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            apply.Click += delegate { ApplySettings(startupCommand, expected); };
            Controls.Add(apply); Controls.Add(cancel); AcceptButton = apply; CancelButton = cancel;
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(true);
            Shown += delegate {
                Rectangle work = Screen.FromControl(this).WorkingArea;
                if (Height > work.Height - 24) Height = Math.Max(300, work.Height - 24);
                if (Width > work.Width - 24) Width = Math.Max(320, work.Width - 24);
                Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
                Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
            };
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
                    MessageBox.Show(this, "保存未完成，请检查主界面错误信息后重试。", "LumaBar");
                    return;
                }
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "无法保存设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
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
        internal event Action HideRequested;
        internal event Action ExitRequested;
        internal event Action PauseRequested;
        internal event Action<string> ModeChanged;
        internal event Action RulesRequested;
        internal event Action PaceRequested;
        internal event Action SettingsRequested;
        internal event Action RetryRequested;
        internal bool AllowClose;
        private readonly Label status;
        private readonly Label brightness;
        private readonly Label theme;
        private readonly Label materialStatus;
        private readonly Button retry;
        private readonly ToolTip materialTip = new ToolTip();
        private string sceneText = "等待场景";
        private string basisText = "等待取样";
        private string materialText = "尚未应用";
        private readonly ProgressBar meter;
        private readonly Button pause;

        internal StatusWindow()
        {
            SuspendLayout();
            Text = "LumaBar";
            ClientSize = new Size(520, 550);
            MinimumSize = Size;
            MaximumSize = Size;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(22, 25, 32);
            ForeColor = Color.FromArgb(230, 235, 242);
            Font = new Font("Microsoft YaHei UI", 10);
            AddLabel("LumaBar 0.6", 28, 24, 310, 45, 25, Color.White);
            AddButton("设置…", 364, 30, 126, delegate { if (SettingsRequested != null) SettingsRequested(); });
            AddLabel("窗口铺满时跟随软件，普通窗口时跟随壁纸", 30, 76, 464, 30, 11, Color.FromArgb(157, 174, 197));
            theme = AddLabel("等待取样", 30, 129, 460, 32, 16, Color.FromArgb(132, 206, 195));
            status = AddLabel("切换到其他应用后开始检测", 30, 172, 460, 32, 10, ForeColor);
            brightness = AddLabel("亮度 —", 30, 212, 460, 25, 10, Color.FromArgb(157, 174, 197));
            meter = new ProgressBar { Location = new Point(32, 244), Size = new Size(456, 8), Maximum = 100 };
            Controls.Add(meter);
            modes = new ComboBox { Location = new Point(30, 274), Size = new Size(270, 30), DropDownStyle = ComboBoxStyle.DropDownList };
            modes.Items.AddRange(new object[] { "自动适应", "固定浅色", "固定深色", "仅检测（不修改）" });
            modes.SelectedIndex = 0;
            modes.SelectedIndexChanged += delegate { if (ModeChanged != null) ModeChanged(modes.SelectedItem.ToString()); };
            Controls.Add(modes);
            pause = AddButton("暂停并恢复", 316, 272, 174, delegate { if (PauseRequested != null) PauseRequested(); });
            AddButton("应用纠正…", 30, 319, 222, delegate { if (RulesRequested != null) RulesRequested(); });
            AddButton("切换节奏…", 268, 319, 222, delegate { if (PaceRequested != null) PaceRequested(); });
            materialStatus = AddLabel("等待场景与材质状态", 30, 365, 465, 68, 9, Color.FromArgb(132, 206, 195));
            retry = AddButton("重试玻璃效果", 30, 438, 460, delegate { if (RetryRequested != null) RetryRequested(); });
            retry.Text = "重新应用任务栏外观";
            AddButton("收起到托盘", 30, 490, 222, delegate { if (HideRequested != null) HideRequested(); });
            AddButton("退出并恢复", 268, 490, 222, delegate { if (ExitRequested != null) ExitRequested(); });
            FormClosed += delegate { materialTip.Dispose(); };
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
                    AutoScrollMinSize = ClientSize; AutoScroll = true; MinimumSize = Size.Empty;
                    Height = Math.Min(Height, Math.Max(300, work.Height - 24));
                    Width = Math.Min(Width, Math.Max(320, work.Width - 24));
                    Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
                    Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
                }
            };
        }
        private Label AddLabel(string text, int x, int y, int width, int height, float size, Color color)
        {
            Label label = new Label { Text = text, Location = new Point(x, y), Size = new Size(width, height), Font = new Font("Microsoft YaHei UI", size), ForeColor = color, AutoEllipsis = true };
            Controls.Add(label);
            return label;
        }
        private Button AddButton(string text, int x, int y, int width, Action action)
        {
            Button button = new Button { Text = text, Location = new Point(x, y), Size = new Size(width, 32), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(38, 45, 57), ForeColor = Color.White };
            button.FlatAppearance.BorderColor = Color.FromArgb(69, 81, 100);
            button.Click += delegate { action(); };
            Controls.Add(button);
            return button;
        }
        internal void UpdateSceneStatus(string scene, string basis)
        { sceneText = scene; basisText = basis; RefreshMaterialText(); }
        internal void UpdateMaterialStatus(string text, bool canRetry = false, bool allowAction = true)
        { materialText = text; retry.Visible = allowAction; retry.Text = canRetry ? "重试玻璃效果" : "重新应用任务栏外观"; RefreshMaterialText(); }
        private void RefreshMaterialText()
        {
            string text = "场景：" + sceneText + "\n依据：" + basisText + "\n材质：" + materialText;
            materialStatus.Text = text;
            materialTip.SetToolTip(materialStatus, text + "\n图标与文字明暗由 Windows 统一控制。");
        }
        internal void SetPaused(bool value) { pause.Text = value ? "继续适应" : "暂停并恢复"; }
        private readonly ComboBox modes;
        internal void SelectMode(string value) { modes.SelectedItem = value; }
        internal void UpdateStatus(string text, double? value, Tone current, string source = "窗口")
        {
            status.Text = text;
            theme.Text = "系统模式 · " + (current == Tone.Light ? "浅色" : "深色");
            brightness.Text = value.HasValue ? source + "亮度 " + value.Value.ToString("P0") + "  · 稳定后切换" : "亮度 —";
            meter.Value = value.HasValue ? Math.Max(0, Math.Min(100, (int)(value.Value * 100))) : 0;
        }
    }
}
