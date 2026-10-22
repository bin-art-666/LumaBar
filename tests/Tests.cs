using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace LumaBar
{
    internal sealed class FakeStore : IThemeStore
    {
        public ThemeValue Value = new ThemeValue(true, 1);
        public int Writes;
        public int Refreshes;
        public bool FailWrite;
        public ThemeValue Read() { return Value; }
        public void Write(ThemeValue value)
        {
            if (FailWrite) throw new IOException("Injected write failure");
            Value = value; Writes++;
        }
        public void Refresh() { Refreshes++; }
    }
    internal sealed class FakeJournal : ISessionJournal
    {
        private string saved;
        public SessionRecord Load() { return saved == null ? null : Files.Json.Deserialize<SessionRecord>(saved); }
        public void Save(SessionRecord record) { saved = Files.Json.Serialize(record); }
        public void Delete() { saved = null; }
    }
    internal static class Tests
    {
        private static int passed;
        private static void Check(bool result, string name)
        {
            if (!result) throw new Exception("FAIL: " + name);
            passed++;
            Console.WriteLine("PASS: " + name);
        }
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && (args[0] == "--render-settings" || args[0] == "--render-rules"))
                {
                    Application.EnableVisualStyles(); Native.EnableDpi();
                    Settings preview = new Settings(); preview.ApplicationRules.Add("Code", "dark"); preview.ApplicationRules.Add("WeChat", "light");
                    using (PreferencesWindow window = new PreferencesWindow(preview, null, delegate { return true; }))
                    {
                        if (args[0] == "--render-rules") ((TabControl)window.Controls[0]).SelectedIndex = 1;
                        window.Show(); Pump();
                        using (Bitmap bitmap = new Bitmap(window.Width, window.Height))
                        { window.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(args[1]); }
                    }
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--render")
                {
                    Application.EnableVisualStyles();
                    Native.EnableDpi();
                    using (StatusWindow window = new StatusWindow())
                    {
                        window.UpdateStatus("活动窗口：Code", 0.14, Tone.Dark);
                        window.Show(); Pump();
                        using (Bitmap bitmap = new Bitmap(window.Width, window.Height))
                        {
                            window.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                            bitmap.Save(args[1]);
                        }
                    }
                    Console.WriteLine("Rendered settings window.");
                    return 0;
                }
                if (args.Length == 1 && args[0] == "--native") { NativeTest(); return 0; }
                if (args.Length == 1 && args[0] == "--shell") { ShellTest(); return 0; }
                if (args.Length == 1 && args[0] == "--wallpaper")
                {
                    Native.EnableDpi();
                    WindowSample sample = WallpaperSampler.Read(Screen.PrimaryScreen.Bounds);
                    Check(sample.Valid && sample.IsWallpaper, "current wallpaper can be read without changing desktop settings");
                    Console.WriteLine("Wallpaper brightness={0:F3}, choice={1}", sample.Brightness, WallpaperAnalyzer.ToneOf(sample.Brightness));
                    return 0;
                }
                if (args.Length == 1 && (args[0] == "--smoke" || args[0] == "--smoke-startup"))
                {
                    Application.EnableVisualStyles(); Native.EnableDpi();
                    bool hidden = args[0] == "--smoke-startup";
                    using (TrayApp context = new TrayApp(new Settings(), new ThemeController(new FakeStore(), new FakeJournal()), true, hidden))
                    using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1200 })
                    {
                        timer.Tick += delegate {
                            timer.Stop();
                            if (hidden) Check(!context.StatusWindowVisible && context.StatusHandleCreated, "startup keeps window hidden with session callback handle ready");
                            context.ExitThread();
                        };
                        timer.Start(); Application.Run(context);
                    }
                    Console.WriteLine("Tray application started and exited using a fake theme store.");
                    return 0;
                }
                CoreTests();
                ExperienceTests();
                WallpaperTests();
                SourceSpeedTests();
                PreferencesTests();
                Console.WriteLine(passed + " checks passed. No real system settings were modified.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void PreferencesTests()
        {
            Check(StartupRegistration.Command(@"D:\My Apps\美化\LumaBar.exe") == "\"D:\\My Apps\\美化\\LumaBar.exe\" --startup", "startup quotes Unicode path with spaces and requests tray startup");
            bool rejected = false;
            try { StartupRegistration.Command("LumaBar.exe"); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "startup rejects relative executable path");
            Settings current = new Settings(); current.DarkThreshold = .40; current.ApplicationRules.Add("Code", "dark");
            bool saved = false;
            using (PreferencesWindow window = new PreferencesWindow(current, null, delegate(Action<Settings> change) { change(current); current.Validate(); saved = true; return true; }))
            {
                TabControl tabs = (TabControl)window.Controls[0];
                ComboBox pace = null; DataGridView grid = null;
                foreach (Control control in tabs.TabPages[0].Controls) if (control is ComboBox) pace = (ComboBox)control;
                foreach (Control control in tabs.TabPages[1].Controls) if (control is DataGridView) grid = (DataGridView)control;
                Check(pace.SelectedItem.ToString() == "均衡", "settings recognizes current timing preset");
                pace.SelectedItem = "灵敏";
                grid.Rows.Clear(); grid.Rows.Add("WeChat.exe", "light");
                window.Show(); Pump();
                foreach (Control control in window.Controls) if (control is Button && control.Text == "保存并应用") ((Button)control).PerformClick();
                Check(saved && current.SourceStableMilliseconds == 120, "settings save applies selected speed");
                Check(current.RuleFor("wechat") == "light" && current.RuleFor("Code") == "auto", "settings grid adds and removes normalized rules");
                Check(current.DarkThreshold == .40, "settings keeps advanced parameters outside the editor");
            }
        }

        private static void CoreTests()
        {
            Settings settings = new Settings(); settings.Validate();
            Check(Math.Abs(Brightness.Of(255, 255, 255) - 1) < 0.0001, "white is light");
            Check(Brightness.Of(25, 25, 25) < settings.DarkThreshold, "VS Code-like gray is dark");
            Check(Brightness.Median(new double[] { 0, 0.1, 0.12, 0.11, 1 }) == 0.11, "text outliers do not dominate median");
            Check(Brightness.Median(new double[] { 0.2, 0.4 }) > 0.29, "even sample median");
            bool rejected = false;
            try { new Settings { DarkThreshold = double.NaN }.Validate(); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "invalid config rejected");

            DecisionEngine engine = new DecisionEngine(settings);
            Check(engine.Observe(1, 0.1, Tone.Light, 0) == null, "no immediate switch on new window");
            Check(engine.Observe(1, 0.1, Tone.Light, 699) == null, "waits for stable window");
            Check(engine.Observe(1, 0.1, Tone.Light, 700) == Tone.Dark, "stable dark window switches");
            engine.Observe(2, 0.95, Tone.Dark, 800);
            Check(engine.Observe(2, 0.95, Tone.Dark, 1500) == null, "minimum interval prevents flicker");
            Check(engine.Observe(2, 0.95, Tone.Dark, 2500) == Tone.Light, "switches after cooldown");
            Check(engine.Observe(2, 0.5, Tone.Light, 4000) == null, "hysteresis preserves light in neutral band");
            Check(engine.Observe(2, 0.5, Tone.Dark, 4000) == null, "hysteresis preserves dark in neutral band");
            engine.Observe(3, 0.1, Tone.Light, 5000);
            Check(engine.Observe(4, 0.1, Tone.Light, 5700) == null, "different same-tone window restarts dwell");
            engine.Reset();
            Check(engine.Observe(4, 0.1, Tone.Light, 6400) == null, "invalid sample cancels pending decision");
            engine.Observe(4, 0.9, Tone.Light, 6500);
            Check(engine.Observe(4, 0.1, Tone.Light, 7000) == null, "brief bright frame cancels dark candidate");

            FakeStore store = new FakeStore(); FakeJournal journal = new FakeJournal();
            ThemeController controller = new ThemeController(store, journal);
            controller.Apply(Tone.Light);
            Check(store.Writes == 0 && journal.Load() == null, "same mode performs no registry write");
            controller.Refresh();
            Check(store.Writes == 0 && store.Refreshes == 1, "stale shell can be refreshed without rewriting matching preference");
            controller.Apply(Tone.Dark);
            Check(store.Value.Value == 0 && journal.Load().Original.Value == 1, "stores original before change");
            controller.Apply(Tone.Light); controller.Apply(Tone.Dark);
            Check(journal.Load().Original.Value == 1, "multiple transitions preserve first baseline");
            Check(controller.Restore() && store.Value.Value == 1 && journal.Load() == null, "exit restores original and clears journal");
            controller.Apply(Tone.Dark);
            store.Value = new ThemeValue(true, 1);
            Check(!controller.OwnsCurrent(), "external theme change detected");
            Check(!controller.Restore() && store.Value.Value == 1, "external choice is preserved");
            store.Value = new ThemeValue(false, 1);
            controller.Apply(Tone.Dark); controller.Restore();
            Check(!store.Value.Exists, "original absent value is removed on restore");

            store.Value = new ThemeValue(true, 0);
            journal.Save(new SessionRecord { Original = new ThemeValue(true, 1), LastWritten = new ThemeValue(true, 0), Pending = new ThemeValue(true, 1) });
            controller = new ThemeController(store, journal);
            Check(controller.Restore() && store.Value.Value == 1, "crash before second write restores from previous value");
            store.Value = new ThemeValue(true, 0);
            journal.Save(new SessionRecord { Original = new ThemeValue(true, 1), Pending = new ThemeValue(true, 0) });
            controller = new ThemeController(store, journal);
            Check(controller.Restore() && store.Value.Value == 1, "crash after first write restores pending value");
            journal.Save(new SessionRecord { Original = new ThemeValue(true, 1), Pending = new ThemeValue(true, 0) });
            controller = new ThemeController(store, journal);
            Check(!controller.Restore() && store.Value.Value == 1, "crash before first write leaves original untouched");
            store.FailWrite = true;
            rejected = false;
            try { controller.Apply(Tone.Dark); } catch (IOException) { rejected = true; }
            Check(rejected && journal.Load() != null && store.Value.Value == 1, "failed write leaves a recoverable journal");
            store.FailWrite = false; controller.Restore();

            string temp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LumaBar-test-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Files.AtomicWrite(temp, "first"); Files.AtomicWrite(temp, "second");
                Check(File.ReadAllText(temp) == "second" && !File.Exists(temp + ".tmp"), "journal file replaced atomically");
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static void NativeTest()
        {
            Application.EnableVisualStyles(); Native.EnableDpi();
            using (NotificationWindow receiver = new NotificationWindow())
            {
                IntPtr handle = receiver.Handle;
                Check(Native.NotifyThemeWindow(handle, true, 100), "theme notifications delivered to a real window");
                Check(receiver.Sections.Count == 2 && receiver.Sections[0] == "ImmersiveColorSet" && receiver.Sections[1] == "TraySettings", "shell receives both Unicode theme and tray notifications");
                receiver.Sections.Clear();
                Native.NotifyThemeWindow(handle, false, 100);
                Check(receiver.Sections.Count == 1 && receiver.Sections[0] == "ImmersiveColorSet", "non-shell windows receive only theme notification");
            }
            ThemeValue before = new RegistryThemeStore().Read();
            using (Form form = new Form { Text = "LumaBar synthetic sampler test", ClientSize = new Size(600, 400), StartPosition = FormStartPosition.CenterScreen, BackColor = Color.FromArgb(28, 28, 28), TopMost = true })
            {
                form.Show(); form.Activate(); Pump();
                System.Diagnostics.Stopwatch sampleTime = System.Diagnostics.Stopwatch.StartNew();
                WindowSample dark = WindowSampler.Read(new Settings(), true, form.Handle, false);
                sampleTime.Stop();
                Console.WriteLine("Window sampling elapsed ms: " + sampleTime.ElapsedMilliseconds);
                Console.WriteLine("Dark sample: valid={0}, value={1:F3}, count={2}, reason={3}, target={4}", dark.Valid, dark.Brightness, dark.Count, dark.Reason, dark.Window == form.Handle);
                Check(dark.Window == form.Handle && dark.Valid && dark.Brightness < 0.2, "native GDI reads actual dark test window");
                using (System.Diagnostics.Process own = System.Diagnostics.Process.GetCurrentProcess())
                {
                    uint beforeHandles = Native.GetGuiResources(own.Handle, 0);
                    Native.Point origin = new Native.Point(0, 0); Native.ClientToScreen(form.Handle, ref origin);
                    for (int frame = 0; frame < 12; frame++)
                        using (WindowPixels pixels = WindowPixels.Capture(origin, form.ClientSize.Width, form.ClientSize.Height))
                            if (pixels == null) throw new Exception("Thumbnail capture failed");
                    uint afterHandles = Native.GetGuiResources(own.Handle, 0);
                    Check(beforeHandles > 0 && afterHandles <= beforeHandles + 2, "repeated thumbnail sampling releases native GDI objects");
                }
                form.BackColor = Color.FromArgb(245, 245, 245); form.Refresh(); Pump();
                WindowSample light = WindowSampler.Read(new Settings(), true, form.Handle, false);
                Check(light.Window == form.Handle && light.Valid && light.Brightness > 0.85, "same app changing theme is detected");
                // A covering window must not be counted as part of the original window.
                WindowSample excluded = WindowSampler.Read(new Settings(), false, form.Handle, false);
                Check(!excluded.Valid, "production sampler ignores its own UI");
                string process = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                Settings ruleSettings = new Settings();
                ruleSettings.ApplicationRules[process] = "dark";
                ruleSettings.Validate();
                WindowSample forced = WindowSampler.Read(ruleSettings, true, form.Handle, false);
                Check(forced.Valid && forced.ExplicitRule && forced.Brightness == 0, "per-app dark rule overrides actual bright pixels");
                ruleSettings.ApplicationRules[process] = "hold";
                Check(!WindowSampler.Read(ruleSettings, true, form.Handle, false).Valid, "per-app hold rule prevents adaptation");
                using (Form dialog = new Form { Text = "Temporary dialog", ClientSize = new Size(300, 200), BackColor = Color.White, StartPosition = FormStartPosition.CenterParent })
                {
                    dialog.Show(form); dialog.Activate(); Pump();
                    WindowSample popup = WindowSampler.Read(new Settings(), true, dialog.Handle, false);
                    Check(popup.Window == dialog.Handle && !popup.Valid && popup.Reason.Contains("对话框"), "owned popup preserves main-window theme");
                    dialog.Close();
                }
                form.Activate(); Pump();
                using (Panel sidebar = new Panel { Dock = DockStyle.Left, Width = 130, BackColor = Color.FromArgb(28, 28, 28) })
                {
                    form.Controls.Add(sidebar); form.Refresh(); Pump();
                    WindowSample mixed = WindowSampler.Read(new Settings(), true, form.Handle, false);
                    Check(mixed.Valid && mixed.Brightness > 0.8, "dark sidebar does not outweigh white page");
                    form.Controls.Remove(sidebar);
                }
                form.BackColor = Color.FromArgb(28, 28, 28);
                using (Panel body = new Panel { Dock = DockStyle.Top, Height = 290, BackColor = Color.FromArgb(245, 245, 245) })
                {
                    form.Controls.Add(body); form.Refresh(); Pump();
                    WindowSample bottom = WindowSampler.Read(new Settings(), true, form.Handle, false);
                    Check(bottom.Valid && bottom.Brightness < new Settings().DarkThreshold, "dark lower region outweighs white upper content");
                    form.Controls.Remove(body);
                }
                int wallpaperReads = 0;
                Func<Rectangle, WindowSample> wallpaper = delegate(Rectangle monitor)
                {
                    wallpaperReads++;
                    return new WindowSample { IsWallpaper = true, Valid = true, Brightness = 0.9, WallpaperContext = long.MinValue };
                };
                WindowSample regular = WindowSampler.Read(new Settings(), true, form.Handle, true, wallpaper);
                Check(regular.Valid && regular.IsWallpaper && regular.Brightness == 0.9, "regular dark window follows bright wallpaper");
                ruleSettings.ApplicationRules[process] = "dark";
                Check(WindowSampler.Read(ruleSettings, true, form.Handle, true, wallpaper).IsWallpaper, "regular window ignores full-window app correction");
                form.WindowState = FormWindowState.Maximized; form.Refresh(); Pump();
                int priorReads = wallpaperReads;
                WindowSample maximized = WindowSampler.Read(new Settings(), true, form.Handle, true, wallpaper);
                Check(maximized.Valid && !maximized.IsWallpaper && maximized.Brightness < 0.2 && wallpaperReads == priorReads,
                    "maximized window follows app pixels without consulting wallpaper");
                form.WindowState = FormWindowState.Normal; form.Refresh(); Pump();
                WindowSample restored = WindowSampler.Read(new Settings(), true, form.Handle, true, wallpaper);
                Check(restored.IsWallpaper && restored.DecisionContext == regular.DecisionContext && restored.DecisionContext != maximized.DecisionContext,
                    "restoring window returns to stable wallpaper context");
                form.Close();
            }
            Check(before.Same(new RegistryThemeStore().Read()), "native sampler tests leave system theme unchanged");
            Console.WriteLine(passed + " native checks passed.");
        }
        private static void ExperienceTests()
        {
            double[] dark = { 0.1, 0.1, 0.1 }, light = { 0.95, 0.95, 0.95 };
            Check(Brightness.Regional(dark, light, light) < 0.44, "bottom-priority mixed-layout score is dark");
            Check(Brightness.Regional(light, dark, dark) > 0.60, "bottom-priority mixed-layout score is light");
            Settings settings = new Settings();
            settings.ApplicationRules["Code.EXE"] = "DARK"; settings.ApplicationRules["App.With.Dots.exe"] = "hold";
            settings.Validate();
            Check(settings.RuleFor("code") == "dark", "rules normalize executable suffix and case");
            Check(settings.RuleFor("App.With.Dots") == "hold", "process names containing dots stay intact");
            Settings copy = Files.Json.Deserialize<Settings>(Files.Json.Serialize(settings)); copy.Validate();
            Check(copy.RuleFor("CODE.exe") == "dark", "application preferences survive JSON round trip");
            string configPath = Path.Combine(Files.Root, "settings.json");
            string originalConfig = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
            try
            {
                copy.SetPace("舒缓"); Files.SaveSettings(copy);
                Settings reloaded = Files.ReadSettings();
                Check(reloaded.RuleFor("code") == "dark" && reloaded.ContentStableMilliseconds == 2000,
                    "app correction and pace persist together to disk and reload");
            }
            finally
            {
                if (originalConfig == null) File.Delete(configPath);
                else Files.AtomicWrite(configPath, originalConfig);
            }
            bool rejected = false;
            try { new Settings { ApplicationRules = new Dictionary<string, string> { { "code", "purple" } } }.Validate(); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "unsupported application rule rejected");
            foreach (string pace in new string[] { "灵敏", "均衡", "舒缓" })
            { Settings paced = new Settings(); paced.SetPace(pace); paced.Validate(); }
            Check(true, "all three timing presets are valid");
            DecisionEngine engine = new DecisionEngine(new Settings());
            engine.Observe(1, 0.1, Tone.Light, 0);
            engine.Observe(2, 0.1, Tone.Light, 350);
            engine.Observe(3, 0.1, Tone.Light, 700);
            Check(engine.Observe(3, 0.1, Tone.Light, 1050) == null, "rapid Alt-Tab candidates cannot cause a switch");
            Check(engine.Observe(3, 0.1, Tone.Light, 1400) == Tone.Dark, "settled destination eventually switches");
            engine = new DecisionEngine(new Settings());
            engine.Observe(1, 0.1, Tone.Dark, 0); engine.Observe(1, 0.1, Tone.Dark, 350); engine.Observe(1, 0.1, Tone.Dark, 700);
            engine.Observe(1, 0.95, Tone.Dark, 1050);
            Check(engine.Observe(1, 0.1, Tone.Dark, 1400) == null, "one bright content frame does not flash the taskbar");
            engine = new DecisionEngine(new Settings());
            bool switched = false;
            for (int i = 0; i < 18; i++)
                if (engine.Observe(1, i % 2 == 0 ? 0.1 : 0.9, Tone.Dark, i * 350).HasValue) switched = true;
            Check(!switched, "fluctuating media cannot establish a theme candidate");
            engine = new DecisionEngine(new Settings());
            for (int i = 0; i < 4; i++) engine.Observe(1, 0.1, Tone.Dark, i * 350);
            for (int i = 4; i <= 9; i++)
                Check(engine.Observe(1, 0.95, Tone.Dark, i * 350) == null, "same-window content change waits longer " + i);
            Check(engine.Observe(1, 0.95, Tone.Dark, 3500) == Tone.Light, "stable same-window theme change eventually switches");
            engine = new DecisionEngine(new Settings());
            engine.Observe(1, 0.1, Tone.Light, 0);
            Check(engine.Observe(1, 0.1, Tone.Light, 1400) == null, "sample gap invalidates a stale candidate");
            engine = new DecisionEngine(new Settings());
            engine.Observe(1, 0, Tone.Light, 0, true); engine.Observe(1, 0, Tone.Light, 350, true);
            Check(engine.Observe(1, 0, Tone.Light, 700, true) == Tone.Dark, "forced app rule still waits for a settled window");
        }
        private static void WallpaperTests()
        {
            Settings legacy = Files.Json.Deserialize<Settings>("{\"PauseOnFullscreen\":true,\"ApplicationRules\":{\"Code\":\"dark\"}}");
            legacy.Validate();
            Check(legacy.RuleFor("code") == "dark", "old fullscreen-pause setting is ignored while app corrections survive upgrade");
            Rectangle monitor = new Rectangle(0, 0, 1920, 1080), work = new Rectangle(0, 0, 1920, 1030);
            Check(!WindowCoverage.Expanded(false, new Rectangle(60, 70, 1200, 800), monitor, work), "floating window uses wallpaper");
            Check(!WindowCoverage.Expanded(false, new Rectangle(0, 0, 960, 1030), monitor, work), "half-screen snapped window uses wallpaper");
            Check(WindowCoverage.Expanded(true, new Rectangle(-8, -8, 1936, 1046), monitor, work), "native maximized window uses app");
            Check(WindowCoverage.Expanded(false, monitor, monitor, work), "borderless fullscreen uses app");
            Check(WindowCoverage.Expanded(false, work, monitor, work), "borderless work-area cover uses app");
            Check(WallpaperAnalyzer.ToneOf(0.9) == Tone.Light && WallpaperAnalyzer.ToneOf(0.1) == Tone.Dark, "wallpaper bright and dark classify directly");
            WallpaperDescription description = new WallpaperDescription { Monitor = new Rectangle(0, 0, 100, 100), VirtualDesktop = new Rectangle(0, 0, 100, 100), Background = Color.Black, Position = 4 };
            using (Bitmap light = new Bitmap(100, 100))
            {
                using (Graphics g = Graphics.FromImage(light)) g.Clear(Color.FromArgb(240, 240, 240));
                Check(WallpaperAnalyzer.Analyze(light, description) > 0.9, "bright original wallpaper yields light even with no visible desktop");
                using (Graphics g = Graphics.FromImage(light)) g.Clear(Color.FromArgb(20, 20, 20));
                Check(WallpaperAnalyzer.Analyze(light, description) < 0.1, "dark original wallpaper yields dark");
            }
            using (Bitmap wide = new Bitmap(300, 100))
            {
                using (Graphics g = Graphics.FromImage(wide)) { g.Clear(Color.White); g.FillRectangle(Brushes.Black, 100, 0, 100, 100); }
                Check(WallpaperAnalyzer.Analyze(wide, description) < 0.05, "fill respects dark center crop rather than bright hidden edges");
                description.Position = 2;
                Check(WallpaperAnalyzer.Analyze(wide, description) > 0.95, "stretch sees majority bright full image");
                description.Position = 3;
                Check(WallpaperAnalyzer.Analyze(wide, description) < 0.05, "fit includes actual dark letterbox background");
                description.Position = 5; description.VirtualDesktop = new Rectangle(-100, 0, 300, 100); description.Monitor = new Rectangle(-100, 0, 100, 100);
                Check(WallpaperAnalyzer.Analyze(wide, description) > 0.95, "span maps left monitor to its own bright wallpaper region");
                description.Monitor = new Rectangle(0, 0, 100, 100);
                Check(WallpaperAnalyzer.Analyze(wide, description) < 0.05, "span maps middle monitor to dark region with negative virtual origin");
            }
            description.Background = Color.White;
            Check(WallpaperAnalyzer.Analyze(null, description) > 0.99, "solid-color desktop follows background color");
            using (Bitmap small = new Bitmap(20, 20))
            {
                using (Graphics g = Graphics.FromImage(small)) g.Clear(Color.Black);
                description.Position = 0;
                Check(WallpaperAnalyzer.Analyze(small, description) > 0.99, "centered small image respects majority background");
                description.Position = 1;
                Check(WallpaperAnalyzer.Analyze(small, description) < 0.01, "tiled image covers monitor rather than leaving center borders");
            }
            DecisionEngine engine = new DecisionEngine(new Settings());
            WindowSample wallpaper = new WindowSample { IsWallpaper = true, Brightness = 0.55, WallpaperContext = long.MinValue };
            engine.Observe(wallpaper.DecisionContext, wallpaper.DecisionBrightness, Tone.Dark, 0, true);
            engine.Observe(wallpaper.DecisionContext, wallpaper.DecisionBrightness, Tone.Dark, 350, true);
            Check(engine.Observe(wallpaper.DecisionContext, wallpaper.DecisionBrightness, Tone.Dark, 700, true) == Tone.Light,
                "medium-bright wallpaper can leave a previous dark app theme");
        }

        private static void SourceSpeedTests()
        {
            Settings settings = new Settings();
            DecisionEngine engine = new DecisionEngine(settings);
            long paper = long.MinValue;
            engine.Observe(paper, 1, Tone.Dark, 0, true, true);
            engine.Observe(paper, 1, Tone.Dark, 75, true, true);
            engine.Observe(paper, 1, Tone.Dark, 150, true, true);
            Check(engine.Observe(paper, 1, Tone.Dark, 225, true, true) == Tone.Light, "initial wallpaper source confirms in 225ms at burst cadence");
            engine.Observe(42, 0.1, Tone.Light, 300, false, false);
            engine.Observe(42, 0.1, Tone.Light, 375, false, false);
            Check(engine.Observe(42, 0.1, Tone.Light, 525, false, false) == null, "source fast path still enforces short anti-flicker cooldown");
            Check(engine.Observe(42, 0.1, Tone.Light, 675, false, false) == Tone.Dark, "maximized dark app bypasses old 1800ms cooldown after source cooldown");
            engine.Observe(paper, 1, Tone.Dark, 750, true, true);
            engine.Observe(paper, 1, Tone.Dark, 825, true, true);
            Check(engine.Observe(paper, 1, Tone.Dark, 1125, true, true) == Tone.Light, "restoring window returns to wallpaper through fast path");

            engine = new DecisionEngine(settings);
            engine.Observe(paper, 1, Tone.Light, 0, true, true);
            engine.Observe(paper, 1, Tone.Light, 75, true, true);
            engine.Observe(paper, 1, Tone.Light, 150, true, true);
            engine.Observe(42, 0.1, Tone.Light, 225, false, false);
            Check(engine.Observe(paper, 1, Tone.Light, 300, true, true) == null, "brief maximize-and-restore does not flash a dark theme");

            engine = new DecisionEngine(settings);
            engine.Observe(42, 0.1, Tone.Light, 0, false, false);
            engine.Observe(42, 0.1, Tone.Light, 75, false, false);
            Check(engine.Observe(42, 0.1, Tone.Light, 225, false, false) == Tone.Dark, "first stable software source uses fast confirmation");
            engine.Observe(42, 0.95, Tone.Dark, 600, false, false);
            engine.Observe(42, 0.95, Tone.Dark, 950, false, false);
            Check(engine.Observe(42, 0.95, Tone.Dark, 1300, false, false) == null, "fast path is retired before later content changes");
            engine.Observe(42, 0.95, Tone.Dark, 1650, false, false);
            engine.Observe(42, 0.95, Tone.Dark, 2000, false, false);
            Check(engine.Observe(42, 0.95, Tone.Dark, 2350, false, false) == null, "same-window content retains longer dwell");
            Check(engine.Observe(42, 0.95, Tone.Dark, 2700, false, false) == Tone.Light, "stable content eventually switches with normal protection");
            Settings legacy = Files.Json.Deserialize<Settings>("{\"PollMilliseconds\":350,\"StableMilliseconds\":700,\"MinimumSwitchMilliseconds\":1800}");
            legacy.Validate();
            Check(legacy.SourceStableMilliseconds == 180 && legacy.SourceMinimumSwitchMilliseconds == 400,
                "old settings automatically gain fast source defaults");
        }

        private static void ShellTest()
        {
            Application.EnableVisualStyles(); Native.EnableDpi();
            using (Mutex mutex = new Mutex(false, @"Local\LumaBar-" + WindowsIdentity.GetCurrent().User.Value))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new Exception("Exit LumaBar before running the live shell regression test.");
                try
                {
                    IThemeStore store = new RegistryThemeStore();
                    ThemeController controller = new ThemeController(store, new FileJournal());
                    controller.Restore();
                    ThemeValue original = store.Read();
                    double? dark = null, light = null;
                    try
                    {
                        controller.Apply(Tone.Dark); controller.Refresh(); Settle();
                        dark = Native.TaskbarBrightness();
                        Console.WriteLine("Dark taskbar measured brightness: " + (dark.HasValue ? dark.Value.ToString("F3") : "unavailable"));
                        controller.Apply(Tone.Light); controller.Refresh(); Settle();
                        light = Native.TaskbarBrightness();
                        Console.WriteLine("Light taskbar measured brightness: " + (light.HasValue ? light.Value.ToString("F3") : "unavailable"));
                    }
                    finally { controller.Restore(); store.Refresh(); Settle(); }
                    Check(original.Same(store.Read()), "live shell test restored original registry preference");
                    Check(dark.HasValue && light.HasValue && light.Value - dark.Value >= 0.25,
                        "rendered taskbar background actually becomes lighter after theme switch");
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        private static void Settle()
        {
            for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(40); }
        }
        private static void Pump()
        {
            for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(40); }
        }
    }

    internal sealed class NotificationWindow : Form
    {
        internal readonly List<string> Sections = new List<string>();
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x001A && message.LParam != IntPtr.Zero)
                Sections.Add(Marshal.PtrToStringUni(message.LParam));
            base.WndProc(ref message);
        }
    }
}
