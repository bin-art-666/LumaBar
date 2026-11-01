using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace LumaBar
{
    internal sealed class FakeMaterialBackend : IMaterialBackend
    {
        internal uint Pid = 100;
        internal int Connections, Applies, Restores;
        internal bool FailConnect, FailApply, FailRestore;
        internal ManualResetEvent ConnectEntered, ConnectContinue;
        internal readonly List<MaterialTarget> Applied = new List<MaterialTarget>();
        public uint ExplorerPid { get { return Pid; } }
        public void Connect()
        {
            Connections++;
            if (ConnectEntered != null) ConnectEntered.Set();
            if (ConnectContinue != null && !ConnectContinue.WaitOne(3000)) throw new TimeoutException("Test connect timeout");
            if (FailConnect) throw new IOException("Injected unavailable component");
        }
        public void Apply(MaterialTarget target)
        {
            Applies++;
            if (FailApply) throw new IOException("Injected partial apply failure");
            Applied.Add(target);
        }
        public void Restore()
        {
            Restores++;
            if (FailRestore) throw new IOException("Injected restore failure");
        }
        public void Dispose() { }
    }

    internal static partial class Tests
    {
        private static void StabilityTests()
        {
            ContrastTests(); DisplayEditorTests(); SceneCoordinationTests(); MaterialRecoveryTests();
            using (StatusWindow window = new StatusWindow())
            {
                int retry = 0; window.RetryRequested += delegate { retry++; };
                window.UpdateSceneStatus("桌面", "壁纸偏亮 82%");
                window.UpdateMaterialStatus("已降级为原生材质", true);
                window.Show(); Pump();
                Button action = null;
                foreach (Control control in window.Controls) if (control is Button && control.Text == "重试玻璃效果") action = (Button)control;
                if (action != null) action.PerformClick();
                Check(retry == 1, "fallback state exposes a working one-click retry action");
                window.UpdateMaterialStatus("仅检测", false, false);
                Check(action != null && !action.Visible, "monitor-only status hides material modification actions");
            }
        }
        private static void ContrastTests()
        {
            TaskbarAppearance clear = new TaskbarAppearance { Material = "clear", TintOpacity = 8 };
            BackdropReading mixed = new BackdropReading { Brightness = .5, Low = 0, High = 1 };
            TaskbarAppearance light = ReadabilityPolicy.Adapt(clear, Tone.Light, mixed, true);
            TaskbarAppearance dark = ReadabilityPolicy.Adapt(clear, Tone.Dark, mixed, true);
            Check(light.TintOpacity >= 55 && dark.TintOpacity >= 65, "mixed wallpaper increases tint for both global glyph modes");
            Check(clear.TintOpacity == 8, "contrast protection never mutates saved user opacity");
            Check(ReadabilityPolicy.Adapt(clear, Tone.Light, new BackdropReading { Low = .8, High = .95 }, true).TintOpacity == 8,
                "uniform bright wallpaper retains clear glass with dark glyphs");
            Check(ReadabilityPolicy.Adapt(clear, Tone.Dark, new BackdropReading { Low = .02, High = .1 }, true).TintOpacity == 8,
                "uniform dark wallpaper retains clear glass with light glyphs");
            Check(ReadabilityPolicy.Adapt(clear, Tone.Dark, mixed, false).TintOpacity == 8, "contrast protection can be disabled");
            TaskbarAppearance manual = clear.Copy(); manual.AutomaticColor = false;
            Check(ReadabilityPolicy.Adapt(manual, Tone.Dark, mixed, true).TintOpacity == 8, "manual tint remains under user control");
            TaskbarAppearance native = new TaskbarAppearance { Material = "normal", TintOpacity = 8 };
            Check(ReadabilityPolicy.Adapt(native, Tone.Dark, mixed, true).TintOpacity == 8, "native materials are not overridden by contrast policy");
            Check(ReadabilityPolicy.Adapt(clear, Tone.Dark, null, true).TintOpacity >= 65, "unreadable wallpaper gets a conservative tint fallback");
            using (Bitmap image = new Bitmap(100, 40))
            {
                using (Graphics graphics = Graphics.FromImage(image)) { graphics.Clear(Color.Black); graphics.FillRectangle(Brushes.White, 50, 0, 50, 40); }
                BackdropReading actual = WallpaperAnalyzer.AnalyzeDetails(image, new WallpaperDescription { Monitor = new Rectangle(0, 0, 100, 40), VirtualDesktop = new Rectangle(0, 0, 100, 40), Position = 2 });
                Check(actual.Low < .001 && actual.High > .999 && Math.Abs(actual.Brightness - .5) < .001, "wallpaper analysis retains bright and dark tails instead of median alone");
            }
            GlassSettings settings = new GlassSettings { Desktop = clear.Copy() };
            List<DisplayBackdrop> displays = TwoDisplays();
            MaterialPlan plan = ReadabilityPolicy.Build(settings, MaterialScene.Desktop, Tone.Dark, displays, "left");
            Check(plan.Targets[0].Appearance.TintOpacity > plan.Targets[1].Appearance.TintOpacity, "bright and dark monitors receive independent protective tint");
            Check(plan.Targets[0].Tone == Tone.Dark && plan.Targets[1].Tone == Tone.Dark, "per-monitor background protection respects globally shared glyph mode");
            settings.MonitorOverrides.Add("right", new MonitorAppearanceSet { Enabled = true, Desktop = new TaskbarAppearance { Material = "acrylic", AutomaticColor = false, TintOpacity = 48 } });
            plan = ReadabilityPolicy.Build(settings, MaterialScene.Desktop, Tone.Dark, displays, "left");
            Check(plan.Targets[0].Appearance.Material == "clear" && plan.Targets[1].Appearance.Material == "acrylic", "one display override does not change the other display's material");
            settings.PerMonitor = false;
            plan = ReadabilityPolicy.Build(settings, MaterialScene.Desktop, Tone.Dark, displays, "left");
            Check(plan.Targets[0].Appearance.Material == plan.Targets[1].Appearance.Material && plan.Targets[0].Appearance.TintOpacity == plan.Targets[1].Appearance.TintOpacity,
                "disabling per-monitor mode uses common appearance and backdrop reading");
            Settings saved = new Settings { Glass = settings };
            Settings loaded = Files.Json.Deserialize<Settings>(Files.Json.Serialize(saved)); loaded.Validate();
            Check(loaded.Glass.MonitorOverrides["RIGHT"].Desktop.Material == "acrylic" && !loaded.Glass.PerMonitor, "per-monitor overrides and global switches survive settings round trip");
        }
        private static List<DisplayBackdrop> TwoDisplays()
        {
            return new List<DisplayBackdrop> {
                new DisplayBackdrop { DeviceName = "left", Taskbar = new IntPtr(1), Reading = new BackdropReading { Brightness = .95, Low = .9, High = 1 } },
                new DisplayBackdrop { DeviceName = "right", Taskbar = new IntPtr(2), Reading = new BackdropReading { Brightness = .05, Low = .02, High = .1 } }
            };
        }
        private static void ReadabilityNativeTest()
        {
            Native.EnableDpi();
            GlassSettings settings = new GlassSettings();
            Tone tone = new RegistryThemeStore().Read().Tone;
            MaterialPlan plan = ReadabilityPolicy.Capture(settings, MaterialScene.Desktop, tone);
            Check(plan.Targets.Count == TaskbarGeometry.Windows().Count && plan.Targets.Count > 0, "live per-monitor planning covers all discovered taskbars");
            foreach (MaterialTarget target in plan.Targets)
                Check(target.Tone == tone && target.Appearance.TintOpacity >= settings.Desktop.TintOpacity, "live contrast plan preserves global glyph mode and user opacity floor");
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            for (int iteration = 0; iteration < 5; iteration++) ReadabilityPolicy.Capture(settings, MaterialScene.Desktop, tone);
            Console.WriteLine("Physical displays={0}, taskbars={1}, cached readability plan average={2:F1}ms", Screen.AllScreens.Length, plan.Targets.Count, clock.Elapsed.TotalMilliseconds / 5);
        }
        private static void DisplayEditorTests()
        {
            GlassSettings original = new GlassSettings { Desktop = new TaskbarAppearance { Material = "clear", TintOpacity = 14 } };
            using (AppearanceEditor editor = new AppearanceEditor(original, new string[] { "left", "right" }))
            {
                ComboBox display = (ComboBox)editor.Controls["Display"];
                ComboBox preset = (ComboBox)editor.Controls["Preset"];
                CheckBox custom = (CheckBox)editor.Controls["UseOverride"];
                display.SelectedIndex = 2;
                Check(!custom.Checked && !preset.Enabled, "display without override visibly inherits defaults");
                custom.Checked = true; preset.SelectedIndex = 3;
                Check(editor.Value.MonitorOverrides["right"].Desktop.Material == "acrylic" && editor.Value.Desktop.Material == "clear",
                    "display editor changes only the selected monitor");
                display.SelectedIndex = 0;
                Check(editor.Value.MonitorOverrides["right"].Desktop.Material == "acrylic" && editor.Value.Desktop.TintOpacity == 14,
                    "switching back to defaults preserves pending monitor edits");
                Check(original.MonitorOverrides.Count == 0 && original.Desktop.TintOpacity == 14, "canceling per-monitor preview can retain the original configuration");
                display.SelectedIndex = 2; custom.Checked = false;
                Check(!editor.Value.MonitorOverrides["right"].Enabled && editor.Value.For(MaterialScene.Desktop, "right").Material == "clear",
                    "disabling a monitor override restores inheritance without losing its saved preset");
            }
        }
        private static void SceneCoordinationTests()
        {
            SceneCoordinator scene = new SceneCoordinator();
            scene.Observe(MaterialScene.Desktop, 1, Tone.Light, Tone.Dark, 0, 100);
            scene.Observe(MaterialScene.Desktop, 1, Tone.Light, Tone.Dark, 100, 100);
            Check(!scene.Ready, "glass scene waits while the requested system tone is unconfirmed");
            scene.Observe(MaterialScene.Desktop, 1, Tone.Light, Tone.Light, 150, 100);
            Check(scene.Ready && scene.Current == MaterialScene.Desktop, "glass scene commits once valid samples and matching tone are confirmed");
            scene.Observe(MaterialScene.Maximized, 2, Tone.Dark, Tone.Light, 200, 100);
            scene.Observe(MaterialScene.Maximized, 2, Tone.Dark, Tone.Light, 300, 100);
            Check(scene.Current == MaterialScene.Desktop, "maximize does not apply a new material with the old glyph mode");
            scene.Observe(MaterialScene.Maximized, 2, Tone.Dark, Tone.Dark, 350, 100);
            Check(scene.Current == MaterialScene.Maximized, "confirmed maximize switches material with the new glyph mode");
            scene.Observe(MaterialScene.Desktop, 3, Tone.Dark, Tone.Dark, 400, 100);
            scene.Observe(MaterialScene.Maximized, 2, Tone.Dark, Tone.Dark, 450, 100);
            Check(scene.Current == MaterialScene.Maximized, "brief restore-and-maximize does not flash the desktop material");
            scene.ResetPending();
            Check(!scene.Ready, "resume must reconfirm scene after pause or session suspension");
            scene.Observe(MaterialScene.Maximized, 4, null, Tone.Dark, 600, 100);
            scene.Observe(MaterialScene.Maximized, 4, null, Tone.Dark, 700, 100);
            Check(scene.Ready, "held applications can confirm material without forcing a theme change");
        }
        private static MaterialPlan TestPlan(Tone tone)
        { return ReadabilityPolicy.Build(new GlassSettings(), MaterialScene.Desktop, tone, TwoDisplays(), "left"); }
        private static void WorkerApply(MaterialController controller, MaterialPlan plan)
        {
            long before = controller.CompletedUpdates;
            controller.Apply(plan, true);
            WaitUntil(delegate { return controller.CompletedUpdates > before; });
        }
        private static void WaitUntil(Func<bool> condition)
        {
            System.Diagnostics.Stopwatch wait = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && wait.ElapsedMilliseconds < 5000) Thread.Sleep(5);
            if (!condition()) throw new Exception("Material worker did not complete");
        }
        private static void MaterialRecoveryTests()
        {
            bool restoredTheme = false;
            bool failed = false;
            try { TrayApp.RestoreBoth(delegate { throw new IOException("material restore failed"); }, delegate { restoredTheme = true; }); }
            catch (AggregateException) { failed = true; }
            Check(failed && restoredTheme, "material restoration failure does not prevent original system theme restoration");
            int restorationErrors = 0;
            try { TrayApp.RestoreBoth(delegate { throw new IOException("material"); }, delegate { throw new IOException("theme"); }); }
            catch (AggregateException error) { restorationErrors = error.InnerExceptions.Count; }
            Check(restorationErrors == 2, "both restoration failures remain visible for diagnostics and retry");
            FakeMaterialBackend backend = new FakeMaterialBackend { FailConnect = true };
            using (MaterialController controller = new MaterialController(backend))
            {
                WorkerApply(controller, TestPlan(Tone.Dark));
                Check(controller.Snapshot.State == MaterialState.Fallback && controller.Snapshot.CanRetry, "connection failure publishes native fallback with a retry action");
                WorkerApply(controller, TestPlan(Tone.Light));
                Check(backend.Connections == 1, "fallback suppresses repeated connection attempts during ordinary theme changes");
                backend.FailConnect = false;
                controller.Retry(); WorkerApply(controller, TestPlan(Tone.Dark));
                Check(controller.Snapshot.State == MaterialState.Applied && backend.Applied.Count == 2, "one retry reconnects and applies both display targets");
                controller.Restore();
                Check(controller.Snapshot.State == MaterialState.Native && backend.Restores == 1, "pause restores all managed display materials");
            }
            backend = new FakeMaterialBackend { FailApply = true };
            using (MaterialController controller = new MaterialController(backend))
            {
                WorkerApply(controller, TestPlan(Tone.Light));
                Check(backend.Restores == 1 && controller.Snapshot.State == MaterialState.Fallback, "partial apply failure restores all taskbars before claiming native fallback");
                FakeStore store = new FakeStore();
                ThemeController theme = new ThemeController(store, new FakeJournal()); theme.Apply(Tone.Dark);
                Check(store.Value.Tone == Tone.Dark, "material fallback leaves the independent light-dark controller functional");
            }
            backend = new FakeMaterialBackend { FailApply = true, FailRestore = true };
            using (MaterialController controller = new MaterialController(backend))
            {
                WorkerApply(controller, TestPlan(Tone.Dark));
                Check(controller.Snapshot.State == MaterialState.RestoreFailed && controller.Snapshot.CanRetry,
                    "restore failure is explicitly unconfirmed rather than falsely reported as native fallback");
                backend.FailApply = backend.FailRestore = false;
                controller.Retry(); WorkerApply(controller, TestPlan(Tone.Dark));
                Check(controller.Snapshot.State == MaterialState.Applied, "retry recovers after a transient restoration failure");
            }
            backend = new FakeMaterialBackend { FailConnect = true };
            using (MaterialController controller = new MaterialController(backend))
            {
                WorkerApply(controller, TestPlan(Tone.Light));
                backend.Pid = 200; backend.FailConnect = false;
                WorkerApply(controller, TestPlan(Tone.Light));
                Check(backend.Connections == 2 && controller.Snapshot.State == MaterialState.Applied, "Explorer process change automatically retries a failed material connection");
            }
            backend = new FakeMaterialBackend { FailConnect = true };
            using (MaterialController controller = new MaterialController(backend))
            {
                WorkerApply(controller, TestPlan(Tone.Light)); backend.FailConnect = false;
                MaterialPlan topology = TestPlan(Tone.Light); topology.Targets[1].Taskbar = new IntPtr(3);
                WorkerApply(controller, topology);
                Check(backend.Connections == 2 && controller.Snapshot.State == MaterialState.Applied, "taskbar topology change can recover fallback without a manual restart");
            }
            using (ManualResetEvent entered = new ManualResetEvent(false))
            using (ManualResetEvent proceed = new ManualResetEvent(false))
            {
                backend = new FakeMaterialBackend { ConnectEntered = entered, ConnectContinue = proceed };
                using (MaterialController controller = new MaterialController(backend))
                {
                    controller.Apply(TestPlan(Tone.Dark), true);
                    Check(entered.WaitOne(2000) && controller.Snapshot.State == MaterialState.Connecting, "pending initialization is shown as connecting, not applied");
                    controller.InvalidatePending(); controller.Apply(TestPlan(Tone.Light), true); proceed.Set();
                    WaitUntil(delegate { return controller.CompletedUpdates >= 2; });
                    Check(backend.Applied.Count == 2 && backend.Applied[0].Tone == Tone.Light && backend.Applied[1].Tone == Tone.Light,
                        "stale dark material is discarded when a newer tone arrives during connection");
                }
            }
        }
    }
}
