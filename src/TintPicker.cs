using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace LumaBar
{
    internal sealed class TintPicker : Form
    {
        private readonly UiCard preview;
        private readonly TextBox hex;
        private readonly ModernSlider[] channels = new ModernSlider[3];
        private readonly Label[] values = new Label[3];
        private readonly Label hint;
        private bool loading;
        internal Color SelectedColor { get; private set; }

        internal TintPicker(Color initial)
        {
            SuspendLayout();
            Text = "LumaBar · 自定义染色";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Ui.Window(this);
            ClientSize = new Size(452, 376);

            Controls.Add(new Label { Text = "自定义染色", Location = new Point(20, 12), Size = new Size(412, 32),
                Font = new Font(Ui.BodyFont.FontFamily, 15, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft });
            UiCard color = new UiCard { Location = new Point(20, 52), Size = new Size(412, 88) };
            Controls.Add(color);
            preview = new UiCard { Name = "ColorPreview", AccessibleName = "颜色预览", Location = new Point(12, 12), Size = new Size(116, 64) };
            color.Controls.Add(preview);
            color.Controls.Add(new Label { Text = "HEX 颜色值", Location = new Point(144, 9), Size = new Size(252, 24), TextAlign = ContentAlignment.MiddleLeft });
            UiCard field = new UiCard { BackColor = Ui.Canvas, Location = new Point(144, 37), Size = new Size(256, 37) };
            color.Controls.Add(field);
            hex = new TextBox { Name = "HexColor", AccessibleName = "HEX 颜色值", Font = new Font("Consolas", 11) };
            field.Controls.Add(hex);
            field.Layout += delegate {
                int inset = Math.Max(8, field.Height / 4);
                hex.SetBounds(inset, (field.Height - hex.PreferredHeight) / 2, Math.Max(1, field.Width - 2 * inset), hex.PreferredHeight);
            };
            field.Paint += delegate(object sender, PaintEventArgs args) {
                if (!hex.Focused) return;
                args.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var border = Ui.Round(new RectangleF(.5f, .5f, field.Width - 1, field.Height - 1), 12 * field.DeviceDpi / 96f))
                using (Pen pen = new Pen(Ui.Accent)) args.Graphics.DrawPath(pen, border);
            };
            hex.GotFocus += delegate { field.Invalidate(); };
            hex.LostFocus += delegate { field.Invalidate(); };

            UiCard rgb = new UiCard { Location = new Point(20, 152), Size = new Size(412, 124) };
            Controls.Add(rgb);
            string[] labels = { "红色 R", "绿色 G", "蓝色 B" };
            for (int index = 0; index < channels.Length; index++)
            {
                int y = 9 + index * 36;
                rgb.Controls.Add(new Label { Text = labels[index], Location = new Point(12, y), Size = new Size(75, 32), TextAlign = ContentAlignment.MiddleLeft });
                channels[index] = new ModernSlider { Name = "ColorChannel" + index, AccessibleName = labels[index],
                    Location = new Point(88, y), Size = new Size(256, 32), Minimum = 0, Maximum = 255 };
                rgb.Controls.Add(channels[index]);
                values[index] = new Label { Location = new Point(354, y), Size = new Size(44, 32), TextAlign = ContentAlignment.MiddleRight };
                rgb.Controls.Add(values[index]);
                channels[index].ValueChanged += delegate {
                    if (!loading) SetColor(Color.FromArgb(channels[0].Value, channels[1].Value, channels[2].Value));
                };
            }

            hint = new Label { Name = "ColorHint", Location = new Point(20, 282), Size = new Size(412, 28),
                Font = new Font(Ui.BodyFont.FontFamily, 9), TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(hint);
            Controls.Add(new Panel { BackColor = Ui.Border, Location = new Point(20, 320), Size = new Size(412, 1) });
            ModernButton cancel = new ModernButton { Text = "取消", Location = new Point(196, 330), Size = new Size(100, 34), DialogResult = DialogResult.Cancel };
            ModernButton accept = new ModernButton { Text = "使用此颜色", Primary = true, Location = new Point(308, 330), Size = new Size(124, 34) };
            accept.Click += delegate {
                Color parsed;
                if (!TryParse(hex.Text, out parsed))
                {
                    hint.Text = "颜色值无效，请输入 6 位十六进制值。";
                    hint.ForeColor = Ui.ErrorText;
                    hex.Focus();
                    return;
                }
                SetColor(parsed);
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(cancel); Controls.Add(accept);
            AcceptButton = accept; CancelButton = cancel;
            hex.TextChanged += delegate {
                Color parsed;
                if (!loading && TryParse(hex.Text, out parsed)) SetColor(parsed);
            };

            Ui.Apply(this);
            hex.BorderStyle = BorderStyle.None;
            hex.BackColor = field.BackColor;
            field.PerformLayout();
            Ui.OrderTabStops(this);
            Ui.OrderTabStops(rgb);
            SetColor(initial);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(true);
        }

        private static bool TryParse(string value, out Color color)
        {
            int rgb;
            string text = (value ?? "").Trim().TrimStart('#');
            if (text.Length != 6 || !Int32.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
            {
                color = Color.Empty;
                return false;
            }
            color = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            return true;
        }

        private void SetColor(Color value)
        {
            loading = true;
            SelectedColor = value;
            preview.BackColor = value;
            hex.Text = "#" + value.R.ToString("X2") + value.G.ToString("X2") + value.B.ToString("X2");
            int[] rgb = { value.R, value.G, value.B };
            for (int index = 0; index < rgb.Length; index++)
            {
                channels[index].Value = rgb[index];
                values[index].Text = rgb[index].ToString();
            }
            hint.Text = "输入 6 位十六进制颜色值，例如 #202630。";
            hint.ForeColor = Ui.Muted;
            loading = false;
        }
    }
}
