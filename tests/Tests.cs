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
    internal static partial class Tests
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
                if (args.Length == 2 && (args[0] == "--render-settings" || args[0] == "--render-rules" || args[0] == "--render-appearance"))
                {
                    Application.EnableVisualStyles(); Native.EnableDpi();
                    Settings preview = new Settings(); preview.ApplicationRules.Add("Code", "dark"); preview.ApplicationRules.Add("WeChat", "light");
                    using (PreferencesWindow window = new PreferencesWindow(preview, null, delegate { return true; }))
                    {
                        if (args[0] == "--render-rules") ((TabControl)window.Controls[0]).SelectedIndex = 1;
                        if (args[0] == "--render-appearance") ((TabControl)window.Controls[0]).SelectedIndex = 2;
                        window.Show(); Pump();
                        using (Bitmap bitmap = new Bitmap(window.Width, window.Height))
                        { window.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(args[1]); }
                    }
                    return 0;
                }
                if (args.Length == 2 && (args[0] == "--render" || args[0] == "--render-fallback"))
                {
                    Application.EnableVisualStyles();
                    Native.EnableDpi();
                    using (StatusWindow window = new StatusWindow())
                    {
                        window.UpdateStatus("桌面壁纸：优先分析任务栏所在区域", 0.82, Tone.Light, "壁纸");
                        window.UpdateSceneStatus("桌面", "任务栏区域壁纸偏亮 82%");
                        window.UpdateMaterialStatus(args[0] == "--render-fallback" ? "已降级为原生材质 · 组件连接失败（自动明暗继续）" : "玻璃效果已应用 · 显示器 1：透明玻璃 14%", args[0] == "--render-fallback");
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
                if (args.Length == 1 && args[0] == "--events") { EventNativeTest(); return 0; }
                if (args.Length == 1 && args[0] == "--refresh-speed") { RefreshSpeedTest(); return 0; }
                if (args.Length == 1 && args[0] == "--readability") { ReadabilityNativeTest(); return 0; }
                if (args.Length == 1 && args[0] == "--wallpaper-cache")
                {
                    Rectangle screen = Screen.PrimaryScreen.Bounds;
                    WindowSample first = WallpaperSampler.ReadTaskbar(screen);
                    WindowSample cached = WallpaperSampler.ReadTaskbar(screen);
                    Check(first.Valid && cached.Valid && first.DecisionContext == cached.DecisionContext, "unchanged wallpaper reuses its decision context");
                    WallpaperSampler.Invalidate();
                    WindowSample refreshed = WallpaperSampler.ReadTaskbar(screen);
                    Check(refreshed.Valid && refreshed.DecisionContext != first.DecisionContext, "wallpaper notification invalidates cache and starts a fresh source confirmation");
                    Check(Math.Abs(refreshed.Brightness - first.Brightness) < .001, "cache invalidation preserves taskbar-region wallpaper mapping");
                    return 0;
                }
                if (args.Length == 1 && args[0] == "--shell") { ShellTest(); return 0; }
                if (args.Length == 1 && args[0] == "--materials") { MaterialsShellTest(); return 0; }
                if (args.Length == 1 && args[0] == "--material-watchdog") { MaterialWatchdogTest(); return 0; }
                if (args.Length == 2 && args[0] == "--material-child")
                {
                    MaterialController child = new MaterialController();
                    GlassSettings glass = new GlassSettings();
                    glass.Desktop = new TaskbarAppearance { Material = "opaque", AutomaticColor = false, Color = args[1] == "light" ? "#FFFFFF" : "#000000" };
                    ApplyAndWait(child, glass, Tone.Light);
                    Console.WriteLine("MATERIAL_READY"); Console.Out.Flush(); Console.ReadLine();
                    // Deliberately bypass Dispose to exercise the native process-death watchdog.
                    Environment.Exit(0);
                }
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
                MaterialTests();
                EventSpeedTests();
                StabilityTests();
                Console.WriteLine(passed + " checks passed. No real system settings were modified.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void MaterialTests()
        {
            Settings migrated = Files.Json.Deserialize<Settings>("{\"PollMilliseconds\":350}"); migrated.Validate();
            Check(migrated.Glass.Enabled && migrated.Glass.TaskbarRegion, "legacy settings receive adaptive glass and taskbar-region defaults");
            Check(migrated.Glass.Desktop.Material == "blur" && migrated.Glass.Maximized.Material == "normal", "desktop glass preserves native maximized default");
            migrated.Glass.Desktop.TintOpacity = 51;
            Check(migrated.Glass.Window.TintOpacity == 22, "scene configurations do not share mutable settings");
            Settings copy = Files.Json.Deserialize<Settings>(Files.Json.Serialize(migrated)); copy.Validate();
            Check(copy.Glass.Desktop.TintOpacity == 51 && copy.Glass.Maximized.ShowLine, "glass scene parameters survive settings round trip");
            TaskbarAppearance custom = new TaskbarAppearance { AutomaticColor = false, Color = "#123456", TintOpacity = 100 };
            Check(custom.Abgr(Tone.Light) == 0xFF563412U, "custom RGB tint is converted to native ABGR correctly");
            custom.TintOpacity = 0;
            Check((custom.Abgr(Tone.Dark) >> 24) == 0, "clear tint can be fully transparent");
            custom.Material = "opaque";
            Check((custom.Abgr(Tone.Dark) >> 24) == 255, "opaque material ignores transparency slider");
            custom.AutomaticColor = true;
            Check((custom.Abgr(Tone.Light) & 255) > (custom.Abgr(Tone.Dark) & 255), "automatic glass tint follows system light and dark tone");
            bool rejected = false;
            try { new TaskbarAppearance { Color = "#wrong!" }.Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "malformed tint cannot reach the native component");
            rejected = false;
            try { new TaskbarAppearance { BlurRadius = 101 }.Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "out-of-range blur is rejected");
            Check(TrayApp.SceneFor(new WindowSample { IsWallpaper = true, IsDesktop = true }) == MaterialScene.Desktop &&
                TrayApp.SceneFor(new WindowSample { IsWallpaper = true }) == MaterialScene.Window &&
                TrayApp.SceneFor(new WindowSample { Valid = true }) == MaterialScene.Maximized, "desktop, floating and expanded samples route to separate material scenes");
            Check(TrayApp.SceneFor(new WindowSample { Scene = MaterialScene.Maximized }) == MaterialScene.Maximized,
                "ignored or held maximized applications still select maximized material");
            GlassSettings original = new GlassSettings();
            using (AppearanceEditor editor = new AppearanceEditor(original))
            {
                ((ComboBox)editor.Controls["Preset"]).SelectedIndex = 1;
                Check(original.Desktop.Material == "blur" && editor.Value.Desktop.Material == "clear", "preview edits do not mutate saved settings before apply");
                ((ComboBox)editor.Controls["Scene"]).SelectedIndex = 2;
                ((ComboBox)editor.Controls["Preset"]).SelectedIndex = 3;
                Check(editor.Value.Desktop.Material == "clear" && editor.Value.Maximized.Material == "acrylic", "switching editor scenes preserves independent unsaved choices");
            }
            using (Bitmap image = new Bitmap(100, 100))
            {
                using (Graphics graphics = Graphics.FromImage(image)) { graphics.Clear(Color.Black); graphics.FillRectangle(Brushes.White, 0, 90, 100, 10); }
                WallpaperDescription description = new WallpaperDescription { Position = 2, Monitor = new Rectangle(0, 0, 100, 100), VirtualDesktop = new Rectangle(0, 0, 100, 100) };
                Check(WallpaperAnalyzer.Analyze(image, description) < .1, "mixed wallpaper whole-screen median is dark");
                description.SampleArea = new Rectangle(0, 90, 100, 10);
                Check(WallpaperAnalyzer.Analyze(image, description) > .9, "bright taskbar strip overrides dark upper wallpaper");
                description.Monitor = new Rectangle(-100, 0, 100, 100); description.VirtualDesktop = description.Monitor;
                description.SampleArea = new Rectangle(-100, 90, 100, 10);
                Check(WallpaperAnalyzer.Analyze(image, description) > .9, "taskbar strip mapping supports monitors with negative coordinates");
            }
        }

        private static void MaterialsShellTest()
        {
            Application.EnableVisualStyles(); Native.EnableDpi();
            using (Mutex mutex = new Mutex(false, @"Local\LumaBar-" + WindowsIdentity.GetCurrent().User.Value))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new Exception("Exit LumaBar before running material regression.");
                try
                {
                    ThemeValue before = new RegistryThemeStore().Read();
                    using (MaterialController materials = new MaterialController())
                    {
                        GlassSettings glass = new GlassSettings();
                        glass.Desktop = new TaskbarAppearance { Material = "opaque", AutomaticColor = false, Color = "#000000", ShowLine = false };
                        ApplyAndWait(materials, glass, Tone.Dark); Settle();
                        double? dark = Native.TaskbarBrightness();
                        glass.Desktop.Color = "#FFFFFF";
                        ApplyAndWait(materials, glass, Tone.Light); Settle();
                        double? light = Native.TaskbarBrightness();
                        Console.WriteLine("Material pixel brightness: dark={0}, light={1}", dark, light);
                        Check(dark.HasValue && light.HasValue && light.Value - dark.Value > .5, "native glass backend actually changes taskbar pixels");
                        glass.Desktop.Material = "blur"; glass.Desktop.TintOpacity = 22;
                        ApplyAndWait(materials, glass, Tone.Light);
                        Check(materials.Status.StartsWith("玻璃效果已应用"), "custom blur brush accepted by Explorer");
                        glass.Desktop.Material = "acrylic";
                        ApplyAndWait(materials, glass, Tone.Light);
                        Check(materials.Status.StartsWith("玻璃效果已应用"), "acrylic brush accepted by Explorer");
                        ThemeController theme = new ThemeController(new RegistryThemeStore(), new FileJournal());
                        theme.Restore();
                        try
                        {
                            glass.Desktop.Material = "blur"; glass.Desktop.AutomaticColor = true;
                            theme.Apply(Tone.Dark); ApplyAndWait(materials, glass, Tone.Dark); Settle();
                            Check(new RegistryThemeStore().Read().Tone == Tone.Dark, "glass and light-dark controller work together in dark mode");
                            theme.Apply(Tone.Light); ApplyAndWait(materials, glass, Tone.Light); Settle();
                            Check(new RegistryThemeStore().Read().Tone == Tone.Light && materials.Status.StartsWith("玻璃效果已应用"), "glass can be reapplied after the system switches to light mode");
                        }
                        finally { materials.Restore(); theme.Restore(); }
                        materials.Restore(); Settle();
                        Check(materials.Status == "系统原生材质", "taskbar materials restore before exit");
                    }
                    Check(before.Same(new RegistryThemeStore().Read()), "material regression does not change system theme preference");
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        private static void ApplyAndWait(MaterialController controller, GlassSettings glass, Tone tone)
        {
            long completed = controller.CompletedUpdates;
            controller.Apply(glass, MaterialScene.Desktop, tone, true);
            System.Diagnostics.Stopwatch wait = System.Diagnostics.Stopwatch.StartNew();
            while (controller.CompletedUpdates == completed && wait.ElapsedMilliseconds < 45000) { Application.DoEvents(); Thread.Sleep(40); }
            if (controller.CompletedUpdates == completed || controller.Status.StartsWith("玻璃效果未应用")) throw new Exception(controller.Status);
        }

        private static void MaterialWatchdogTest()
        {
            Native.EnableDpi();
            double? baseline = Native.TaskbarBrightness();
            if (!baseline.HasValue) throw new Exception("Taskbar pixels unavailable.");
            bool light = baseline.Value < .5;
            System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath,
                "--material-child " + (light ? "light" : "dark")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true };
            using (System.Diagnostics.Process child = System.Diagnostics.Process.Start(info))
            {
                try
                {
                    System.Threading.Tasks.Task<string> ready = child.StandardOutput.ReadLineAsync();
                    System.Diagnostics.Stopwatch wait = System.Diagnostics.Stopwatch.StartNew();
                    while (!ready.IsCompleted && wait.ElapsedMilliseconds < 45000) { Application.DoEvents(); Thread.Sleep(50); }
                    if (!ready.IsCompleted || ready.Result != "MATERIAL_READY") throw new Exception("Watchdog child failed: " + (child.HasExited ? child.StandardError.ReadToEnd() : "timeout"));
                    Settle();
                    double? changed = Native.TaskbarBrightness();
                    Check(changed.HasValue && Math.Abs(changed.Value - baseline.Value) > .3, "watchdog child visibly changes taskbar material");
                    // End the child without running its restoration code.
                    child.StandardInput.WriteLine("exit"); child.StandardInput.Flush();
                    if (!child.WaitForExit(5000)) throw new Exception("Watchdog child did not exit.");
                    double? restored = null;
                    for (int i = 0; i < 50; i++)
                    {
                        Application.DoEvents(); Thread.Sleep(100); restored = Native.TaskbarBrightness();
                        if (restored.HasValue && Math.Abs(restored.Value - baseline.Value) < .08) break;
                    }
                    Console.WriteLine("Watchdog taskbar brightness: before={0}, after={1}", baseline, restored);
                    Check(restored.HasValue && Math.Abs(restored.Value - baseline.Value) < .08, "native watchdog restores original taskbar when managed process exits without cleanup");
                }
                finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
            }
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
                Check(saved && current.SourceStableMilliseconds == 80, "settings save applies selected speed");
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
            // Retain the 0.5 timing regression as well as the faster preset tests below.
            Settings settings = new Settings { TimingVersion = 1, SourceStableMilliseconds = 180, SourceMinimumSwitchMilliseconds = 400 };
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
            Check(legacy.SourceStableMilliseconds == 100 && legacy.SourceMinimumSwitchMilliseconds == 250,
                "old settings automatically gain fast source defaults");
        }

        private static void EventSpeedTests()
        {
            ProbeSchedule schedule = new ProbeSchedule();
            Check(schedule.Interval(0, 350) == 350, "idle probing retains economical polling interval");
            Check(schedule.Signal(10) && schedule.Interval(10, 350) == 50, "window event requests an immediate probe and 50ms burst");
            Check(!schedule.Signal(25) && !schedule.Signal(60), "event storms are coalesced rather than restarting every timer tick");
            Check(schedule.Interval(1061, 350) == 350, "burst ends after window events settle");
            schedule.Observe(2000, false, 41);
            schedule.Observe(3300, false, 42);
            Check(schedule.Interval(3300, 350) == 50, "switching two maximized apps also starts fast probing");
            Check(!WindowEvents.Relevant(0x800B, new IntPtr(1), -4, 0, new IntPtr(1)) &&
                !WindowEvents.Relevant(0x800B, new IntPtr(2), 0, 0, new IntPtr(1)), "background-window and child-content geometry events are ignored");
            Check(WindowEvents.Relevant(0x800B, new IntPtr(1), 0, 0, new IntPtr(1)), "foreground window resize can trigger fast probing");

            Settings balanced = new Settings(); balanced.Validate();
            DecisionEngine engine = new DecisionEngine(balanced);
            engine.Observe(1, .1, Tone.Light, 0, false, false);
            Check(engine.Observe(1, .1, Tone.Light, 50, false, false) == null, "new fast path still rejects a single short bright-dark transition");
            Check(engine.Observe(1, .1, Tone.Light, 100, false, false) == Tone.Dark, "balanced source confirms after 100ms instead of 180ms");
            engine.Observe(2, 1, Tone.Dark, 150, false, false);
            Check(engine.Observe(2, 1, Tone.Dark, 250, false, false) == null, "faster maximized app changes still respect source cooldown");
            Check(engine.Observe(2, 1, Tone.Dark, 350, false, false) == Tone.Light, "different maximized app avoids the old 700ms normal dwell");
            engine.Observe(2, 1, Tone.Light, 700, false, false);
            engine.Observe(2, 1, Tone.Light, 1050, false, false);
            engine.Observe(2, 1, Tone.Light, 1400, false, false);
            engine.Observe(2, .1, Tone.Light, 1750, false, false);
            engine.Observe(2, .1, Tone.Light, 1800, false, false);
            engine.Observe(2, .1, Tone.Light, 1850, false, false);
            Check(engine.Observe(2, .1, Tone.Light, 2000, false, false) == null, "rapid probing does not shorten settled-window content protection");

            Settings sensitive = new Settings(); sensitive.SetPace("灵敏");
            engine = new DecisionEngine(sensitive);
            engine.Observe(3, 1, Tone.Dark, 0, true, true);
            Check(engine.Observe(3, 1, Tone.Dark, 50, true, true) == null && engine.Observe(3, 1, Tone.Dark, 100, true, true) == Tone.Light,
                "sensitive wallpaper path confirms at the next 50ms sample after 80ms dwell");
            string previous = "{\"PollMilliseconds\":200,\"StableMilliseconds\":450,\"ContentStableMilliseconds\":900,\"MinimumSwitchMilliseconds\":1200,\"SourceStableMilliseconds\":120,\"SourceMinimumSwitchMilliseconds\":250,\"Glass\":{\"Desktop\":{\"Material\":\"clear\",\"TintOpacity\":14}}}";
            Settings migrated = Files.Json.Deserialize<Settings>(previous); migrated.Validate();
            Check(migrated.SourceStableMilliseconds == 80 && migrated.SourceMinimumSwitchMilliseconds == 200 && migrated.Glass.Desktop.TintOpacity == 14,
                "existing sensitive preset upgrades while preserving user's glass appearance");
            Settings custom = new Settings { SourceStableMilliseconds = 230, SourceMinimumSwitchMilliseconds = 550 }; custom.Validate();
            Check(custom.SourceStableMilliseconds == 230 && custom.SourceMinimumSwitchMilliseconds == 550, "custom advanced source timing is preserved");
            Settings roundTrip = Files.Json.Deserialize<Settings>(Files.Json.Serialize(migrated)); roundTrip.Validate();
            Check(roundTrip.TimingVersion == 1 && roundTrip.SourceStableMilliseconds == 80, "timing migration is stable after save and reload");

            // Compare first-decision timing on the same deterministic input and 50ms cadence.
            Console.WriteLine("Source confirmation at 50ms cadence: old balanced={0}ms, new balanced={1}ms", ConfirmAt(180), ConfirmAt(100));
        }
        private static int ConfirmAt(int dwell)
        {
            DecisionEngine engine = new DecisionEngine(new Settings { SourceStableMilliseconds = dwell });
            for (int time = 0; time <= 1000; time += 50)
                if (engine.Observe(1, .1, Tone.Light, time, false, false) == Tone.Dark) return time;
            throw new Exception("No source decision");
        }

        [DllImport("user32.dll")] private static extern void NotifyWinEvent(uint eventType, IntPtr window, int objectId, int childId);
        private static void EventNativeTest()
        {
            Application.EnableVisualStyles(); Native.EnableDpi();
            ThemeValue before = new RegistryThemeStore().Read();
            using (NotificationWindow window = new NotificationWindow())
            {
                IntPtr handle = window.Handle;
                int callbacks = 0;
                using (WindowEvents events = new WindowEvents(delegate { callbacks++; }, true))
                {
                    Check(events.Available, "all foreground, minimize and geometry event hooks register");
                    System.Diagnostics.Stopwatch wait = System.Diagnostics.Stopwatch.StartNew();
                    // Send a synthetic accessibility event; this does not activate a window or change theme.
                    NotifyWinEvent(0x0003, handle, 0, 0);
                    while (callbacks == 0 && wait.ElapsedMilliseconds < 3000) { Application.DoEvents(); Thread.Sleep(5); }
                    Console.WriteLine("Native event delivery elapsed: {0}ms", wait.ElapsedMilliseconds);
                    Check(callbacks > 0, "out-of-context window events reach the UI message pump");
                }
                int afterDispose = callbacks;
                NotifyWinEvent(0x0003, handle, 0, 0); Pump();
                Check(callbacks == afterDispose, "disposed window event hooks stop delivering callbacks");
                using (WindowEvents events = new WindowEvents(delegate { callbacks++; }))
                {
                    NotifyWinEvent(0x0003, handle, 0, 0); Pump();
                    Check(callbacks == afterDispose, "production event hooks skip LumaBar's own UI events");
                }
            }
            Check(before.Same(new RegistryThemeStore().Read()), "event hook regression does not change system theme");
        }

        private static void RefreshSpeedTest()
        {
            Application.EnableVisualStyles(); Native.EnableDpi();
            ThemeValue before = new RegistryThemeStore().Read();
            using (NotificationWindow receiver = new NotificationWindow())
            {
                IntPtr handle = receiver.Handle;
                System.Diagnostics.Stopwatch foreground = System.Diagnostics.Stopwatch.StartNew();
                Native.RefreshTaskbars();
                Console.WriteLine("Taskbar-first synchronous refresh elapsed: {0}ms", foreground.ElapsedMilliseconds);
                // Multiple refreshes must coalesce safely and still notify normal windows.
                Native.RefreshTaskbars(); Native.RefreshTaskbars();
                System.Diagnostics.Stopwatch wait = System.Diagnostics.Stopwatch.StartNew();
                while (!Native.WaitForThemeRefresh(0) && wait.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(5); }
                Application.DoEvents();
                Check(Native.WaitForThemeRefresh(0), "coalesced background theme notifications finish within their bounded budget");
                Check(receiver.Sections.Contains("ImmersiveColorSet"), "normal windows still receive theme notification from the background worker");
                Check(!receiver.Sections.Contains("TraySettings"), "background normal-window notification keeps the original message contract");
            }
            Check(before.Same(new RegistryThemeStore().Read()), "refresh-speed test leaves system theme preference unchanged");
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
