using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace LumaBar
{
    internal static partial class Tests
    {
        private static void GlowTests()
        {
            GlowSavedPreferencesTests();
            Settings legacy = Files.Json.Deserialize<Settings>("{}"); legacy.Validate();
            Check(legacy.MouseGlow, "existing settings enable the requested mouse glow without changing glass");
            Check(legacy.MouseGlowRadius == 100 && legacy.MouseGlowStrength == 25, "old settings retain original glow radius and strength");
            foreach (Settings invalid in new[] { new Settings { MouseGlowRadius = 4 }, new Settings { MouseGlowRadius = 201 }, new Settings { MouseGlowStrength = -1 }, new Settings { MouseGlowStrength = 101 } })
            {
                bool rejected = false; try { invalid.Validate(); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "out-of-range glow parameters are rejected");
            }
            string glass = Files.Json.Serialize(legacy.Glass);
            using (PreferencesWindow preferences = new PreferencesWindow(legacy, null, delegate(Action<Settings> change) { change(legacy); return true; }))
            {
                TabControl pages = (TabControl)preferences.Controls[0];
                Control.ControlCollection controls = pages.TabPages[0].Controls;
                CheckBox toggle = (CheckBox)controls.Find("MouseGlow", true)[0];
                TrackBar radiusControl = (TrackBar)controls.Find("MouseGlowRadius", true)[0];
                TrackBar strengthControl = (TrackBar)controls.Find("MouseGlowStrength", true)[0];
                Check(radiusControl.Minimum == 5 && radiusControl.SmallChange == 1, "radius allows a compact five-percent glow with one-percent keyboard steps");
                int previewStrength = -1;
                preferences.GlowPreviewChanged += delegate(bool on, int radiusValue, int intensity) { previewStrength = intensity; };
                strengthControl.Value = 0;
                Check(previewStrength == 0 && strengthControl.Minimum == 0, "zero strength is available and immediately previews no glow");
                Check(toggle.Checked && toggle.AccessibleName == "任务栏鼠标光效", "mouse glow has an independent accessible setting");
                radiusControl.Value = 160; strengthControl.Value = 75;
                Check(controls.Find("MouseGlowRadiusValue", true)[0].Text == "160%" && controls.Find("MouseGlowStrengthValue", true)[0].Text == "75%", "glow slider readouts follow edits immediately");
                preferences.Show(); Pump();
                ((Button)pages.TabPages[0].Controls["ResetMouseGlow"]).PerformClick();
                Check(radiusControl.Value == 100 && strengthControl.Value == 25, "glow reset restores original radius and strength");
                radiusControl.Value = 150; strengthControl.Value = 60;
                toggle.Checked = false;
                Check(!radiusControl.Enabled && !strengthControl.Enabled && radiusControl.Value == 150 && strengthControl.Value == 60,
                    "disabled glow retains values and disables its sliders");
                foreach (Control control in preferences.Controls)
                    if (control is Button && control.Text == "保存并应用") ((Button)control).PerformClick();
                Check(!legacy.MouseGlow && Files.Json.Serialize(legacy.Glass) == glass, "disabling glow preserves all glass settings");
                Check(!Files.Json.Deserialize<Settings>(Files.Json.Serialize(legacy)).MouseGlow, "disabled glow survives a settings round trip");
                Settings reloaded = Files.Json.Deserialize<Settings>(Files.Json.Serialize(legacy));
                Check(reloaded.MouseGlowRadius == 150 && reloaded.MouseGlowStrength == 60, "edited radius and strength save and reload together");
            }
            new Settings { MouseGlowStrength = 0 }.Validate();
            foreach (bool light in new[] { false, true })
            using (Bitmap zero = TaskbarGlowWindow.CreateSprite(96, light, 0))
            using (Bitmap faint = TaskbarGlowWindow.CreateSprite(96, light, 5))
            {
                bool transparent = true;
                for (int y = 0; y < zero.Height; y++)
                    for (int x = 0; x < zero.Width; x++) transparent &= zero.GetPixel(x, y).A == 0;
                Check(transparent, "zero intensity produces an entirely transparent sprite in both themes");
                Check(faint.GetPixel(48, 48).A <= 5, "five percent is a faint glow below two percent peak opacity");
            }
            using (PreferencesWindow cancel = new PreferencesWindow(legacy, null, delegate { throw new Exception("cancel must not save"); }))
            {
                ((TrackBar)cancel.Controls.Find("MouseGlowRadius", true)[0]).Value = 200;
                cancel.Dispose();
                Check(legacy.MouseGlowRadius == 150, "unsaved glow edits leave persisted settings unchanged");
            }
            using (Bitmap sprite = TaskbarGlowWindow.CreateSprite(192, false))
            {
                Check(sprite.GetPixel(0, 0).A == 0 && sprite.GetPixel(96, 0).A == 0, "glow sprite edges are fully transparent");
                Check(sprite.GetPixel(96, 96).A > sprite.GetPixel(140, 96).A && sprite.GetPixel(96, 96).A <= 64, "glow softly decreases from a translucent center");
                Check(TaskbarGlowWindow.Clip(new Point(-1910, 1060), new Rectangle(-1920, 1040, 1920, 40), sprite.Size)
                    == new Rectangle(-1920, 1040, 106, 40), "glow clips at taskbar edges on negative-origin monitors");
            }
            using (Bitmap weak = TaskbarGlowWindow.CreateSprite(192, false, 10))
            using (Bitmap strong = TaskbarGlowWindow.CreateSprite(192, false, 80))
            {
                Check(strong.GetPixel(96, 96).A > weak.GetPixel(96, 96).A * 6 && strong.GetPixel(0, 0).A == 0,
                    "strength adjusts native sprite alpha while preserving transparent edges");
            }
            foreach (bool light in new[] { false, true })
            using (Bitmap bright = TaskbarGlowWindow.CreateSprite(192, light, 100))
            {
                Color center = bright.GetPixel(96, 96), middle = bright.GetPixel(144, 96), outer = bright.GetPixel(168, 96);
                Check(center.A >= 230 && center.R >= 250 && center.G >= 250 && center.B >= 250,
                    "maximum glow has a bright neutral-white center in both themes");
                Check(middle.A >= 140 && outer.A >= 40 && bright.GetPixel(191, 96).A == 0,
                    "strong glow lights a broad area and still fades to transparent edges");
            }
            Check(TaskbarGlow.Diameter(new Rectangle(0, 0, 1920, 48), 200) == 2 * TaskbarGlow.Diameter(new Rectangle(0, 0, 1920, 48), 100),
                "radius doubles actual glow extent at 200 percent");
            Settings compact = new Settings { MouseGlowRadius = 5 }; compact.Validate();
            Check(TaskbarGlow.Diameter(new Rectangle(0, 0, 1920, 48), compact.MouseGlowRadius) == 10,
                "minimum radius produces a ten-pixel sprite instead of retaining the old fifty-percent floor");
            using (Bitmap tiny = TaskbarGlowWindow.CreateSprite(10, false, 100))
                Check(tiny.GetPixel(5, 5).A > 200 && tiny.GetPixel(0, 0).A == 0, "compact glow retains its bright center and transparent corners");
            using (TaskbarGlowWindow window = new TaskbarGlowWindow())
            {
                CreateParams style = (CreateParams)typeof(TaskbarGlowWindow).GetProperty("CreateParams", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window, null);
                Check((style.ExStyle & 0x080800A0) == 0x080800A0 && !window.Enabled && !window.ShowInTaskbar,
                    "glow window is layered, nonactivating, input-transparent and absent from taskbar");
                Message hit = Message.Create(window.Handle, 0x84, IntPtr.Zero, IntPtr.Zero);
                object[] args = { hit };
                typeof(TaskbarGlowWindow).GetMethod("WndProc", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, args);
                Check(((Message)args[0]).Result == new IntPtr(-1), "glow hit testing passes input through");
            }
            bool enabled = true, covered = false;
            int radius = 100, strength = 25;
            long now = 0;
            Point pointer = new Point(-4980, -4980);
            Rectangle target = new Rectangle(-5000, -5000, 300, 48);
            IntPtr foreground = Native.GetForegroundWindow();
            uint before = Native.GetGuiResources(Process.GetCurrentProcess().Handle, 0);
            using (TaskbarGlow glow = new TaskbarGlow(delegate { return enabled; }, delegate { return false; },
                delegate { return pointer; }, delegate(Point p) { return !covered && target.Contains(p) ? target : Rectangle.Empty; }, delegate { return now; }, delegate { return radius; }, delegate { return strength; }))
            {
                for (int i = 0; i < 12; i++) { now += 16; glow.Advance(); }
                Check(glow.Active && glow.PresentCount > 1, "hover creates and fades in the native glow layer");
                int renders = glow.PresentCount;
                for (int i = 0; i < 20; i++) { now += 40; glow.Advance(); }
                Check(glow.PresentCount == renders, "stationary hover stops redrawing after fade-in");
                radius = 200; now += 40; glow.Advance();
                Check(glow.PresentCount == ++renders, "saved radius changes refresh a stationary native glow");
                strength = 80; now += 40; glow.Advance();
                Check(glow.PresentCount == ++renders, "saved strength changes refresh a stationary native glow");
                strength = 0; now += 40; glow.Advance();
                Check(!glow.Active && glow.PresentCount == renders, "zero strength removes the native overlay without drawing another frame");
                strength = 80;
                for (int i = 0; i < 12; i++) { now += 16; glow.Advance(); }
                Check(glow.Active, "raising intensity from zero resumes the glow");
                renders = glow.PresentCount;
                for (int i = 0; i < 100; i++) { pointer.X = -4970 + i; now += 16; glow.Advance(); }
                Check(glow.PresentCount == renders + 100 && Native.GetForegroundWindow() == foreground,
                    "moving glow follows each position without stealing foreground focus");
                Check(Native.GetGuiResources(Process.GetCurrentProcess().Handle, 0) <= before + 12, "repeated glow frames do not accumulate GDI objects");
                pointer.Y = -4900; now += 16; glow.Advance();
                Check(glow.Active, "leaving taskbar begins a fade rather than an abrupt disappearance");
                for (int i = 0; i < 16; i++) { now += 16; glow.Advance(); }
                Check(!glow.Active, "leave fade disposes the overlay completely");
                pointer = new Point(-4980, -4980); now += 16; glow.Advance();
                covered = true; now += 16; glow.Advance();
                Check(!glow.Active, "covering menu immediately clears the glow");
                covered = false; now += 16; glow.Advance();
                enabled = false; now += 16; glow.Advance();
                Check(!glow.Active, "pause or disable clears the glow immediately");
            }
        }
        private static void GlowSavedPreferencesTests()
        {
            string path = System.IO.Path.Combine(Files.Root, "settings.json");
            string previous = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
            Settings initial = new Settings { MouseGlowRadius = 200, MouseGlowStrength = 100 };
            initial.Validate();
            try
            {
                using (TrayApp app = new TrayApp(initial, new ThemeController(new FakeStore(), new FakeJournal()), false, true,
                    @"Local\LumaBar-Glow-Save-Test-" + Guid.NewGuid().ToString("N")))
                {
                    BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                    object glow = typeof(TrayApp).GetField("glow", flags).GetValue(app);
                    Func<bool> readEnabled = (Func<bool>)typeof(TaskbarGlow).GetField("enabled", flags).GetValue(glow);
                    Func<int> readStrength = (Func<int>)typeof(TaskbarGlow).GetField("strength", flags).GetValue(glow);
                    Func<int> readRadius = (Func<int>)typeof(TaskbarGlow).GetField("radius", flags).GetValue(glow);
                    MethodInfo save = typeof(TrayApp).GetMethod("SavePreferences", flags);
                    Check(readStrength() == 100 && readRadius() == 200, "production glow callbacks initially read startup settings");
                    bool saved = (bool)save.Invoke(app, new object[] { new Action<Settings>(delegate(Settings next) {
                        next.MouseGlow = false; next.MouseGlowStrength = 0; next.MouseGlowRadius = 5;
                    }) });
                    Check(saved && !readEnabled() && readStrength() == 0 && readRadius() == 5,
                        "saving replacement settings immediately changes production glow callbacks without restart");
                    Check(initial.MouseGlowStrength == 100 && initial.MouseGlowRadius == 200,
                        "glow follows new settings rather than mutating its captured startup object");
                    saved = (bool)save.Invoke(app, new object[] { new Action<Settings>(delegate(Settings next) {
                        next.MouseGlow = true; next.MouseGlowStrength = 10; next.MouseGlowRadius = 125;
                    }) });
                    Check(saved && readEnabled() && readStrength() == 10 && readRadius() == 125,
                        "repeated saves continue to update production glow callbacks");
                    Settings disk = Files.ReadSettings();
                    Check(disk.MouseGlowStrength == readStrength() && disk.MouseGlowRadius == readRadius(),
                        "rendering callbacks and persisted settings agree after save");
                    app.ExitThread();
                }
            }
            finally
            {
                if (previous != null) System.IO.File.WriteAllText(path, previous);
                else if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
        }
    }
}
