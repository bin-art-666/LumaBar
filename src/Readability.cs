using System;
using System.Collections.Generic;
using System.Drawing;

namespace LumaBar
{
    internal sealed class BackdropReading
    {
        internal double Brightness, Low, High;
    }

    public sealed class MonitorAppearanceSet
    {
        public bool Enabled;
        public TaskbarAppearance Desktop = new TaskbarAppearance();
        public TaskbarAppearance Window = new TaskbarAppearance();
        public TaskbarAppearance Maximized = new TaskbarAppearance { Material = "normal", ShowLine = true };
        internal TaskbarAppearance For(MaterialScene scene)
        { return scene == MaterialScene.Desktop ? Desktop : scene == MaterialScene.Window ? Window : Maximized; }
        internal void Validate()
        {
            if (Desktop == null || Window == null || Maximized == null) throw new ArgumentException("显示器外观设置不能为空。");
            Desktop.Validate(); Window.Validate(); Maximized.Validate();
        }
    }

    internal sealed class DisplayBackdrop
    {
        internal string DeviceName;
        internal string Label;
        internal IntPtr Taskbar;
        internal BackdropReading Reading;
    }

    internal sealed class MaterialTarget
    {
        internal string DeviceName;
        internal IntPtr Taskbar;
        internal TaskbarAppearance Appearance;
        internal Tone Tone;
        internal bool Protected;
        internal string Detail;
    }

    internal sealed class MaterialPlan
    {
        internal bool Enabled;
        internal MaterialScene Scene;
        internal Tone Tone;
        internal readonly List<MaterialTarget> Targets = new List<MaterialTarget>();
        internal string Key
        {
            get
            {
                string key = Enabled + "|" + Scene + "|" + Tone;
                foreach (MaterialTarget target in Targets)
                    key += "|" + target.Taskbar.ToInt64() + "|" + target.DeviceName + "|" + Files.Json.Serialize(target.Appearance);
                return key;
            }
        }
        internal string TopologyKey
        {
            get { string value = ""; foreach (MaterialTarget target in Targets) value += target.DeviceName + ":" + target.Taskbar.ToInt64() + "|"; return value; }
        }
        internal bool Custom
        {
            get { foreach (MaterialTarget target in Targets) if (target.Appearance.Material != "normal") return true; return false; }
        }
        internal string Description
        {
            get
            {
                List<string> values = new List<string>();
                foreach (MaterialTarget target in Targets) values.Add(target.Detail);
                return String.Join("；", values.ToArray());
            }
        }
    }

    internal static class ReadabilityPolicy
    {
        internal static TaskbarAppearance Adapt(TaskbarAppearance source, Tone glyphTone, BackdropReading reading, bool enabled)
        {
            TaskbarAppearance result = source.Copy();
            if (!enabled || !result.AutomaticColor || result.Material == "normal" || result.Material == "opaque") return result;
            // Use the darker/brighter tails rather than the median: a photo or split wallpaper
            // can have a safe median while the clock sits above the opposite extreme.
            // This is a conservative brightness heuristic, not a WCAG contrast certification.
            double low = reading == null ? 0 : reading.Low;
            double high = reading == null ? 1 : reading.High;
            double tint = glyphTone == Tone.Light ? Brightness.Of(248, 250, 255) : Brightness.Of(20, 24, 32);
            double required = glyphTone == Tone.Light ? (0.55 - low) / Math.Max(.001, tint - low) : (high - .38) / Math.Max(.001, high - tint);
            int minimum = (int)Math.Ceiling(Math.Max(0, Math.Min(1, required)) * 100);
            if (high - low > .45) minimum = Math.Max(22, minimum);
            result.TintOpacity = Math.Max(result.TintOpacity, minimum);
            return result;
        }
        internal static MaterialPlan Build(GlassSettings settings, MaterialScene scene, Tone tone, IList<DisplayBackdrop> displays, string activeDisplay)
        {
            MaterialPlan plan = new MaterialPlan { Enabled = settings.Enabled, Scene = scene, Tone = tone };
            BackdropReading common = null;
            foreach (DisplayBackdrop display in displays) if (display.DeviceName == activeDisplay) common = display.Reading;
            foreach (DisplayBackdrop display in displays)
            {
                TaskbarAppearance original = settings.For(scene, settings.PerMonitor ? display.DeviceName : null);
                TaskbarAppearance adjusted = Adapt(original, tone, settings.PerMonitor ? display.Reading : common, settings.AutoContrast);
                bool protection = original.TintOpacity < adjusted.TintOpacity;
                string label = MaterialNames.Name(adjusted.Material);
                string detail = (display.Label ?? display.DeviceName) + "：" + label;
                if (adjusted.Material != "normal") detail += " " + (adjusted.Material == "opaque" ? 100 : adjusted.TintOpacity) + "%";
                if (protection) detail += "（对比度保护）";
                plan.Targets.Add(new MaterialTarget { DeviceName = display.DeviceName, Taskbar = display.Taskbar, Appearance = adjusted, Tone = tone, Protected = protection, Detail = detail });
            }
            return plan;
        }
        internal static MaterialPlan Capture(GlassSettings settings, MaterialScene scene, Tone tone)
        {
            if (!settings.Enabled) return new MaterialPlan { Enabled = false, Scene = scene, Tone = tone };
            List<DisplayBackdrop> displays = new List<DisplayBackdrop>();
            string active = System.Windows.Forms.Screen.FromHandle(Native.GetForegroundWindow()).DeviceName;
            foreach (IntPtr taskbar in TaskbarGeometry.Windows())
            {
                System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromHandle(taskbar);
                string label = "显示器";
                System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens;
                for (int index = 0; index < screens.Length; index++)
                    if (screens[index].DeviceName == screen.DeviceName) label = "显示器 " + (index + 1) + (screen.Primary ? "（主屏）" : "");
                TaskbarAppearance appearance = settings.For(scene, settings.PerMonitor ? screen.DeviceName : null);
                bool analyze = settings.AutoContrast && appearance.AutomaticColor && appearance.Material != "normal" && appearance.Material != "opaque";
                WindowSample wallpaper = analyze ? WallpaperSampler.ReadTaskbar(screen.Bounds) : null;
                displays.Add(new DisplayBackdrop { DeviceName = screen.DeviceName, Label = label, Taskbar = taskbar, Reading = wallpaper == null ? null : wallpaper.Backdrop });
            }
            return Build(settings, scene, tone, displays, active);
        }
    }

    internal static class MaterialNames
    {
        internal static string Name(string material)
        { return material == "normal" ? "系统原生" : material == "clear" ? "透明玻璃" : material == "blur" ? "模糊玻璃" : material == "acrylic" ? "亚克力" : "纯色"; }
        internal static string Scene(MaterialScene scene)
        { return scene == MaterialScene.Desktop ? "桌面" : scene == MaterialScene.Window ? "普通窗口 / 分屏" : "最大化 / 全屏"; }
    }

    // A compositor animation may still be running after the logical window state changes.
    // Keep the old appearance through a short settling window; do not animate tint values.
    internal sealed class VisualTransitionGate
    {
        internal const int SettleMilliseconds = 220;
        internal const int QuietMilliseconds = 80;
        internal bool Waiting { get; private set; }
        private bool known;
        private MaterialScene settled, pending;
        private long source, since, lastMotion, lastAt;
        private Rectangle frame;
        private int samples;
        internal bool Observe(MaterialScene scene, long window, Rectangle bounds, long now)
        {
            if (!known || (scene != MaterialScene.Maximized && settled != MaterialScene.Maximized))
            { Accept(scene); return true; }
            if (scene == settled) { Waiting = false; samples = 0; return true; }
            if (!Waiting || pending != scene || source != window || now - lastAt > 200)
            {
                Waiting = true; pending = scene; source = window;
                frame = bounds; since = lastMotion = now; samples = 0;
            }
            if (frame != bounds) { frame = bounds; lastMotion = now; }
            samples++; lastAt = now;
            if (samples < 2 || now - since < SettleMilliseconds || now - lastMotion < QuietMilliseconds) return false;
            Accept(scene); return true;
        }
        private void Accept(MaterialScene scene) { known = true; settled = scene; Waiting = false; samples = 0; }
        internal void Reset() { known = Waiting = false; samples = 0; }
    }

    internal sealed class SceneCoordinator
    {
        internal bool Ready { get; private set; }
        internal MaterialScene Current { get; private set; }
        private MaterialScene pending;
        private long context, since, lastAt = -100000;
        private int samples;
        internal void Observe(MaterialScene scene, long source, Tone? wanted, Tone actual, long now, int dwell)
        {
            if (samples == 0 || pending != scene || context != source || now - lastAt > 1200)
            { pending = scene; context = source; since = now; samples = 0; }
            samples++; lastAt = now;
            Ready = false;
            if (samples < 2 || now - since < dwell || (wanted.HasValue && wanted.Value != actual)) return;
            Current = pending; Ready = true;
        }
        internal void ResetPending() { samples = 0; Ready = false; }
    }
}
