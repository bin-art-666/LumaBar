using System;
using System.Drawing;
using System.Windows.Forms;

namespace LumaBar
{
    internal sealed class AppearanceEditor : UserControl
    {
        private readonly CheckBox enabled;
        private readonly CheckBox region;
        private readonly CheckBox contrast;
        private readonly CheckBox perMonitor;
        private readonly CheckBox useOverride;
        private readonly ComboBox display;
        private string currentDisplay;
        private readonly ComboBox scene;
        private readonly ComboBox material;
        private readonly ComboBox preset;
        private readonly CheckBox automatic;
        private readonly CheckBox line;
        private readonly TrackBar opacity;
        private readonly TrackBar blur;
        private readonly Label opacityText;
        private readonly Label blurText;
        private readonly Button color;
        private readonly Panel preview;
        private readonly GlassSettings edited;
        private bool loading;
        private int currentScene;
        internal event Action<GlassSettings> PreviewRequested;

        internal AppearanceEditor(GlassSettings settings, string[] deviceNames = null)
        {
            edited = Files.Json.Deserialize<GlassSettings>(Files.Json.Serialize(settings));
            Size = new Size(600, 550); BackColor = Color.White; AutoScroll = true;
            enabled = new ModernCheckBox { Text = "启用自适应玻璃任务栏", Location = new Point(20, 12), Size = new Size(285, 28), Checked = edited.Enabled };
            Controls.Add(enabled);
            region = new ModernCheckBox { Text = "优先判断任务栏后方壁纸的明暗", Location = new Point(20, 44), Size = new Size(550, 28), Checked = edited.TaskbarRegion };
            Controls.Add(region);
            contrast = new ModernCheckBox { Text = "复杂背景自动增强对比度（自动染色时）", Location = new Point(20, 76), Size = new Size(555, 28), Checked = edited.AutoContrast };
            perMonitor = new ModernCheckBox { Text = "根据各屏壁纸分别调节染色", Location = new Point(20, 108), Size = new Size(555, 28), Checked = edited.PerMonitor };
            Controls.Add(contrast); Controls.Add(perMonitor);
            AddLabel("显示器", 20, 150, 90);
            display = Combo(115, 146, 460, new object[] { new DisplayChoice(null, "所有显示器（默认）") }); display.Name = "Display";
            if (deviceNames == null) deviceNames = Array.ConvertAll(Screen.AllScreens, delegate(Screen screen) { return screen.DeviceName; });
            for (int index = 0; index < deviceNames.Length; index++)
                display.Items.Add(new DisplayChoice(deviceNames[index], "显示器 " + (index + 1) + (deviceNames[index] == Screen.PrimaryScreen.DeviceName ? " · 主屏" : "")));
            display.SelectedIndex = 0;
            useOverride = new ModernCheckBox { Text = "为选中显示器单独设置以下外观", Location = new Point(20, 184), Size = new Size(555, 28), Name = "UseOverride" };
            Controls.Add(useOverride);
            AddLabel("应用场景", 20, 228, 90);
            scene = Combo(115, 224, 195, new object[] { "桌面", "普通窗口 / 分屏", "最大化 / 全屏" });
            scene.Name = "Scene";
            preset = Combo(330, 224, 245, new object[] { "选择外观预设…", "清透玻璃", "柔和磨砂", "浓厚亚克力", "系统默认" });
            preset.Name = "Preset";
            preset.SelectedIndex = 0;
            AddLabel("材质", 20, 273, 90);
            material = Combo(115, 269, 195, new object[] { "系统默认", "透明玻璃", "模糊玻璃", "亚克力", "纯色" });
            automatic = new ModernCheckBox { Text = "染色跟随自动明暗", Location = new Point(330, 270), Size = new Size(245, 28) };
            Controls.Add(automatic);
            opacityText = AddLabel("染色不透明度", 20, 322, 170);
            opacity = Slider(190, 314, 385);
            blurText = AddLabel("模糊强度", 20, 374, 170);
            blur = Slider(190, 366, 385);
            color = new ModernButton { Tag = "Tint", Text = "自定义染色…", Location = new Point(20, 418), Size = new Size(160, 32) };
            Controls.Add(color);
            line = new ModernCheckBox { Text = "显示任务栏顶部分隔线", Location = new Point(205, 419), Size = new Size(350, 28) };
            Controls.Add(line);
            preview = new Panel { Location = new Point(20, 466), Size = new Size(555, 42) };
            preview.Paint += PaintPreview; Controls.Add(preview);
            Label hint = AddLabel("当前场景临时预览；保存后保留，取消恢复。", 20, 515, 565);
            hint.ForeColor = Color.DimGray; hint.Font = new Font(Font.FontFamily, 9);
            scene.SelectedIndexChanged += delegate { if (loading) return; Store(); currentScene = scene.SelectedIndex; LoadScene(); Notify(); };
            material.SelectedIndexChanged += Changed;
            automatic.CheckedChanged += Changed; line.CheckedChanged += Changed;
            enabled.CheckedChanged += Changed; region.CheckedChanged += Changed; contrast.CheckedChanged += Changed;
            display.SelectedIndexChanged += delegate {
                if (loading) return; Store(); currentDisplay = ((DisplayChoice)display.SelectedItem).Key; LoadScene(); Notify();
            };
            perMonitor.CheckedChanged += delegate {
                if (loading) return; Store();
                if (!perMonitor.Checked) { loading = true; display.SelectedIndex = 0; currentDisplay = null; loading = false; }
                LoadScene(); Notify();
            };
            useOverride.CheckedChanged += delegate {
                if (loading || currentDisplay == null) return;
                MonitorAppearanceSet custom;
                if (!edited.MonitorOverrides.TryGetValue(currentDisplay, out custom))
                {
                    custom = new MonitorAppearanceSet { Desktop = edited.Desktop.Copy(), Window = edited.Window.Copy(), Maximized = edited.Maximized.Copy() };
                    edited.MonitorOverrides.Add(currentDisplay, custom);
                }
                custom.Enabled = useOverride.Checked; LoadScene(); Notify();
            };
            opacity.ValueChanged += Changed; blur.ValueChanged += Changed;
            preset.SelectedIndexChanged += delegate {
                if (preset.SelectedIndex <= 0 || loading) return;
                TaskbarAppearance value = CurrentAppearance;
                value.AutomaticColor = true; value.ShowLine = false;
                if (preset.SelectedIndex == 1) { value.Material = "clear"; value.TintOpacity = 8; value.BlurRadius = 0; }
                if (preset.SelectedIndex == 2) { value.Material = "blur"; value.TintOpacity = 22; value.BlurRadius = 24; }
                if (preset.SelectedIndex == 3) { value.Material = "acrylic"; value.TintOpacity = 45; value.BlurRadius = 24; }
                if (preset.SelectedIndex == 4) { value.Material = "normal"; value.ShowLine = true; }
                LoadScene(); Notify();
            };
            color.Click += delegate {
                using (TintPicker dialog = new TintPicker(ColorTranslator.FromHtml(CurrentAppearance.Color)))
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        CurrentAppearance.Color = "#" + dialog.SelectedColor.R.ToString("X2") + dialog.SelectedColor.G.ToString("X2") + dialog.SelectedColor.B.ToString("X2");
                        automatic.Checked = false; Store(); UpdateControls(); Notify();
                    }
            };
            loading = true; scene.SelectedIndex = 0; loading = false; LoadScene();
            AccessibleName = "任务栏外观设置";
            display.AccessibleName = "显示器"; scene.AccessibleName = "应用场景";
            preset.AccessibleName = "外观预设"; material.AccessibleName = "任务栏材质";
            opacity.AccessibleName = "染色不透明度"; blur.AccessibleName = "模糊强度";
            Ui.OrderTabStops(this);
            Font = Ui.BodyFont; BackColor = Ui.Surface; ForeColor = Ui.Text; Ui.Apply(this);
        }
        private bool OverrideEnabled
        {
            get { MonitorAppearanceSet value; return currentDisplay != null && edited.MonitorOverrides.TryGetValue(currentDisplay, out value) && value.Enabled; }
        }
        private TaskbarAppearance CurrentAppearance { get { return edited.For((MaterialScene)currentScene, currentDisplay); } }
        private sealed class DisplayChoice
        {
            internal readonly string Key;
            private readonly string label;
            internal DisplayChoice(string key, string label) { Key = key; this.label = label; }
            public override string ToString() { return label; }
        }
        private static readonly string[] materials = { "normal", "clear", "blur", "acrylic", "opaque" };
        private void Store()
        {
            if (loading) return;
            edited.Enabled = enabled.Checked; edited.TaskbarRegion = region.Checked;
            edited.AutoContrast = contrast.Checked; edited.PerMonitor = perMonitor.Checked;
            if (currentDisplay != null && !OverrideEnabled) return;
            TaskbarAppearance value = CurrentAppearance;
            value.Material = materials[Math.Max(0, material.SelectedIndex)]; value.AutomaticColor = automatic.Checked;
            value.TintOpacity = opacity.Value; value.BlurRadius = blur.Value; value.ShowLine = line.Checked;
        }
        private void LoadScene()
        {
            loading = true;
            useOverride.Checked = OverrideEnabled;
            TaskbarAppearance value = CurrentAppearance;
            material.SelectedIndex = Array.IndexOf(materials, value.Material); automatic.Checked = value.AutomaticColor;
            opacity.Value = value.TintOpacity; blur.Value = value.BlurRadius; line.Checked = value.ShowLine; preset.SelectedIndex = 0;
            loading = false; UpdateControls();
        }
        private void Changed(object sender, EventArgs args) { if (loading) return; Store(); UpdateControls(); Notify(); }
        private void Notify()
        {
            if (PreviewRequested != null) PreviewRequested(Value);
        }
        internal GlassSettings Value
        {
            get { Store(); edited.Validate(); return Files.Json.Deserialize<GlassSettings>(Files.Json.Serialize(edited)); }
        }
        private void UpdateControls()
        {
            string value = materials[Math.Max(0, material.SelectedIndex)];
            bool editable = enabled.Checked && (currentDisplay == null || OverrideEnabled);
            bool custom = editable && value != "normal";
            display.Enabled = enabled.Checked && perMonitor.Checked;
            useOverride.Enabled = enabled.Checked && perMonitor.Checked && currentDisplay != null;
            scene.Enabled = enabled.Checked;
            preset.Enabled = material.Enabled = editable;
            automatic.Enabled = line.Enabled = custom;
            color.Enabled = custom && !automatic.Checked;
            opacity.Enabled = custom && value != "opaque"; blur.Enabled = custom && value == "blur";
            opacityText.Text = "染色不透明度  " + opacity.Value + "%"; blurText.Text = "模糊强度  " + blur.Value;
            preview.Invalidate();
        }
        private void PaintPreview(object sender, PaintEventArgs args)
        {
            TaskbarAppearance appearance = CurrentAppearance;
            // A small light/dark swatch explains automatic tint; the real taskbar is the live preview.
            for (int half = 0; half < 2; half++)
            {
                Rectangle bounds = new Rectangle(half * preview.Width / 2, 0, preview.Width / 2 + 1, preview.Height);
                Tone tone = half == 0 ? Tone.Light : Tone.Dark;
                using (Brush background = new SolidBrush(half == 0 ? Color.FromArgb(219, 228, 238) : Color.FromArgb(35, 47, 65))) args.Graphics.FillRectangle(background, bounds);
                uint abgr = appearance.Abgr(tone);
                using (Brush tint = new SolidBrush(Color.FromArgb((int)(abgr >> 24), (int)(abgr & 255), (int)((abgr >> 8) & 255), (int)((abgr >> 16) & 255)))) args.Graphics.FillRectangle(tint, bounds);
                TextRenderer.DrawText(args.Graphics, half == 0 ? "浅色 · 深色图标" : "深色 · 浅色图标", Font, bounds, half == 0 ? Color.FromArgb(25, 30, 38) : Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
        private Label AddLabel(string text, int x, int y, int width)
        { Label label = new Label { Text = text, Location = new Point(x, y), Size = new Size(width, 28) }; Controls.Add(label); return label; }
        private ComboBox Combo(int x, int y, int width, object[] items)
        { ComboBox box = new ModernComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x, y), Size = new Size(width, 30) }; box.Items.AddRange(items); Controls.Add(box); return box; }
        private TrackBar Slider(int x, int y, int width)
        { TrackBar slider = new ModernSlider { Minimum = 0, Maximum = 100, TickFrequency = 10, Location = new Point(x, y), Size = new Size(width, 45) }; Controls.Add(slider); return slider; }
    }
}
