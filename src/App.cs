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
        private bool? timingSource;
        private long burstUntil;
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
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开 LumaBar", null, delegate { ShowWindow(); });
            menu.Items.Add("设置…", null, delegate { ShowSettings(); });
            pauseItem = new ToolStripMenuItem("暂停并恢复原主题", null, delegate { TogglePause(); });
            menu.Items.Add(pauseItem);
            menu.Items.Add("重新同步任务栏", null, delegate { Resync(); });
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
            // Create a handle for session callbacks even when logging in to tray.
            IntPtr handle = window.Handle;
            if (!startHidden) window.Show();
            controller.Refresh(); // Also repair stale visuals when the registry already matches.
            timer.Start();
            Files.Log("Started. No images or window titles are recorded.");
        }

        private void ShowWindow() { window.Show(); window.WindowState = FormWindowState.Normal; window.Activate(); }
        private void Resync()
        {
            try { controller.Refresh(); window.UpdateStatus("已重新发送主题与任务栏设置通知", null, controller.Current.Tone); }
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
                timingSource = null; burstUntil = 0;
                timer.Interval = settings.PollMilliseconds;
                window.UpdateStatus("设置已保存，立即生效", null, controller.Current.Tone);
                return true;
            }
            catch (Exception error) { Report(error); return false; }
        }
        private void ShowSettings()
        {
            try
            {
                using (PreferencesWindow preferences = new PreferencesWindow(settings, StartupRegistration.Read(), SavePreferences))
                    preferences.ShowDialog(window);
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
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
        {
            if (args.Reason == SessionSwitchReason.SessionLock)
                Dispatch(delegate { sessionLocked = true; ReleaseForIdle(); });
            else if (args.Reason == SessionSwitchReason.SessionUnlock)
                Dispatch(delegate { sessionLocked = false; engine.Reset(); });
        }
        private void OnPowerMode(object sender, PowerModeChangedEventArgs args)
        {
            if (args.Mode == PowerModes.Suspend)
                Dispatch(delegate { suspended = true; ReleaseForIdle(); });
            else if (args.Mode == PowerModes.Resume)
                Dispatch(delegate { suspended = false; engine.Reset(); });
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
            engine.Reset();
            try { controller.Restore(); }
            catch (Exception error) { paused = true; Report(error); }
        }

        private void TogglePause()
        {
            try
            {
                if (!paused) controller.Restore();
                paused = !paused;
                pauseItem.Text = paused ? "恢复自动适应" : "暂停并恢复原主题";
                window.SetPaused(paused);
                engine.Reset();
                Tick();
            }
            catch (Exception error) { Report(error); }
        }
        private void ChangeMode(string next)
        {
            try
            {
                controller.Restore();
                engine.Reset();
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
                    return;
                }
                if (!controller.OwnsCurrent())
                {
                    paused = true;
                    controller.Restore(); // Leaves external changes in place.
                    pauseItem.Text = "恢复自动适应";
                    window.SetPaused(true);
                    window.UpdateStatus("检测到外部主题更改，已暂停", null, controller.Current.Tone);
                    return;
                }
                Tone current = controller.Current.Tone;
                if (mode == "固定浅色" || mode == "固定深色")
                {
                    controller.Apply(mode == "固定浅色" ? Tone.Light : Tone.Dark);
                    window.UpdateStatus(mode, null, controller.Current.Tone);
                    return;
                }
                WindowSample sample = WindowSampler.Read(settings);
                if (sample.IsApplication && !String.IsNullOrEmpty(sample.ProcessName))
                { lastApplication = sample.ProcessName; lastApplicationAt = clock.ElapsedMilliseconds; }
                if (sample.Valid)
                {
                    long now = clock.ElapsedMilliseconds;
                    if (!timingSource.HasValue || timingSource.Value != sample.IsWallpaper) burstUntil = now + 1000;
                    timingSource = sample.IsWallpaper;
                    // Burst only while entering/leaving a maximized/fullscreen
                    // window; ordinary content keeps the slower steady cadence.
                    timer.Interval = now < burstUntil ? Math.Min(75, settings.PollMilliseconds) : settings.PollMilliseconds;
                    Tone? decision = engine.Observe(sample.DecisionContext, sample.DecisionBrightness, current, now, sample.ExplicitRule || sample.IsWallpaper, sample.IsWallpaper);
                    if (decision.HasValue && mode != "仅检测（不修改）") controller.Apply(decision.Value);
                }
                else { engine.Reset(); timer.Interval = settings.PollMilliseconds; }
                window.UpdateStatus(sample.Reason, sample.Valid && !sample.ExplicitRule ? (double?)sample.Brightness : null, controller.Current.Tone, sample.IsWallpaper ? "壁纸" : "窗口");
                tray.Text = "LumaBar · " + (mode == "仅检测（不修改）" ? "仅检测" : controller.Current.Tone == Tone.Light ? "浅色" : "深色");
            }
            catch (Exception error)
            {
                paused = true;
                pauseItem.Text = "恢复自动适应";
                window.SetPaused(true);
                try { controller.Restore(); } catch (Exception restoreError) { Files.Log(restoreError.ToString()); }
                Report(error);
            }
        }
        private void Report(Exception error)
        {
            Files.Log(error.ToString());
            window.UpdateStatus("错误：" + error.Message, null, Tone.Dark);
            tray.ShowBalloonTip(5000, "LumaBar", error.Message.Length > 200 ? error.Message.Substring(0, 200) : error.Message, ToolTipIcon.Warning);
        }
        protected override void ExitThreadCore()
        {
            if (exiting) return;
            timer.Stop();
            try { controller.Restore(); }
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
            exiting = true;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.SessionEnding -= OnSessionEnding;
            SystemEvents.PowerModeChanged -= OnPowerMode;
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
            ClientSize = new Size(660, 540);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(247, 249, 252);
            TabControl tabs = new TabControl { Location = new Point(20, 20), Size = new Size(620, 448) };
            TabPage general = new TabPage("常规") { BackColor = Color.White };
            TabPage applications = new TabPage("应用规则") { BackColor = Color.White };
            tabs.TabPages.Add(general); tabs.TabPages.Add(applications); Controls.Add(tabs);
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
            Button apply = new Button { Text = "保存并应用", Location = new Point(366, 486), Size = new Size(140, 34) };
            Button cancel = new Button { Text = "取消", Location = new Point(520, 486), Size = new Size(120, 34), DialogResult = DialogResult.Cancel };
            apply.Click += delegate { ApplySettings(startupCommand, expected); };
            Controls.Add(apply); Controls.Add(cancel); AcceptButton = apply; CancelButton = cancel;
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(true);
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
        internal bool AllowClose;
        private readonly Label status;
        private readonly Label brightness;
        private readonly Label theme;
        private readonly ProgressBar meter;
        private readonly Button pause;

        internal StatusWindow()
        {
            SuspendLayout();
            Text = "LumaBar";
            ClientSize = new Size(520, 490);
            MinimumSize = Size;
            MaximumSize = Size;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(22, 25, 32);
            ForeColor = Color.FromArgb(230, 235, 242);
            Font = new Font("Microsoft YaHei UI", 10);
            AddLabel("LumaBar 0.4", 28, 24, 310, 45, 25, Color.White);
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
            AddLabel("最大化 / 全屏跟随软件，普通窗口与桌面跟随壁纸。\n任务栏与开始菜单联动。过渡由 Windows 原生处理。\n壁纸原图在内存中分析，不受窗口遮挡影响。", 30, 369, 465, 65, 9, Color.FromArgb(157, 174, 197));
            AddButton("收起到托盘", 30, 440, 222, delegate { if (HideRequested != null) HideRequested(); });
            AddButton("退出并恢复", 268, 440, 222, delegate { if (ExitRequested != null) ExitRequested(); });
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (!AllowClose && args.CloseReason == CloseReason.UserClosing)
                { args.Cancel = true; if (HideRequested != null) HideRequested(); }
            };
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(true);
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
