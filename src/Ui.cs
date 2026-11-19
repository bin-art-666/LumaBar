using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LumaBar
{
    internal static class Ui
    {
        internal static readonly bool Dark = ReadDark();
        private static readonly bool HighContrast = SystemInformation.HighContrast;
        internal static readonly Color Canvas = HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(32,33,36) : Color.FromArgb(242,243,245);
        internal static readonly Color Surface = HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(43,45,48) : Color.FromArgb(250,250,252);
        internal static readonly Color Text = HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(244,244,245) : Color.FromArgb(32,33,38);
        internal static readonly Color Muted = HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(172,176,184) : Color.FromArgb(83,87,96);
        internal static readonly Color Border = HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(64,66,71) : Color.FromArgb(218,220,225);
        internal static readonly Color Accent = HighContrast ? SystemColors.Highlight : Dark ? Color.FromArgb(50,212,235) : Color.FromArgb(0,113,132);
        internal static readonly Color AccentText = HighContrast ? SystemColors.HighlightText : Dark ? Color.FromArgb(13,31,35) : Color.White;
        internal static readonly Color AccentSoft = HighContrast ? SystemColors.Highlight : Dark ? Color.FromArgb(32,60,68) : Color.FromArgb(221,240,244);
        internal static readonly Color Hover = HighContrast ? SystemColors.Highlight : Dark ? Color.FromArgb(55,57,61) : Color.FromArgb(231,233,237);
        internal static readonly Color ErrorText = HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(255,180,171) : Color.FromArgb(156,37,40);
        internal static readonly Font BodyFont = new Font("Microsoft YaHei UI", 10);
        internal static readonly Icon AppIcon = LoadIcon();
        internal static readonly Image BrandImage = LoadBrand();
        private static Image LoadBrand()
        { using (System.IO.Stream stream = typeof(Ui).Assembly.GetManifestResourceStream("LumaBar.png")) using (Image image = Image.FromStream(stream)) return new Bitmap(image); }
        private static bool ReadDark()
        {
            string mode = Environment.GetEnvironmentVariable("LUMABAR_UI_THEME");
            if (mode == "dark" || mode == "light") return mode == "dark";
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
        }
        private static Icon LoadIcon()
        { using (System.IO.Stream stream = typeof(Ui).Assembly.GetManifestResourceStream("LumaBar.ico")) using (Icon value = new Icon(stream,32,32)) return (Icon)value.Clone(); }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        internal static int Chrome(Form window)
        {
            int dark = Dark ? 1 : 0, corner = 2, background = ColorTranslator.ToWin32(Canvas), text = ColorTranslator.ToWin32(Text), border = ColorTranslator.ToWin32(Border);
            int result = DwmSetWindowAttribute(window.Handle, 20, ref dark, 4); result |= DwmSetWindowAttribute(window.Handle, 33, ref corner, 4);
            result |= DwmSetWindowAttribute(window.Handle, 35, ref background, 4); result |= DwmSetWindowAttribute(window.Handle, 36, ref text, 4);
            result |= DwmSetWindowAttribute(window.Handle, 34, ref border, 4); return result;
        }
        internal static void Window(Form window)
        {
            window.Icon = AppIcon; window.Font = BodyFont; window.BackColor = Canvas; window.ForeColor = Text;
            window.HandleCreated += delegate { Chrome(window); };
            if (window.IsHandleCreated) Chrome(window);
        }
        internal static GraphicsPath Round(RectangleF rect, float radius)
        {
            float d = Math.Max(1, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
            GraphicsPath p = new GraphicsPath(); p.AddArc(rect.X,rect.Y,d,d,180,90); p.AddArc(rect.Right-d,rect.Y,d,d,270,90);
            p.AddArc(rect.Right-d,rect.Bottom-d,d,d,0,90); p.AddArc(rect.X,rect.Bottom-d,d,d,90,90); p.CloseFigure(); return p;
        }
        internal static void OrderTabStops(Control parent)
        {
            System.Collections.Generic.List<Control> ordered = new System.Collections.Generic.List<Control>();
            foreach (Control child in parent.Controls) ordered.Add(child);
            ordered.Sort(delegate(Control a, Control b) { int row = a.Top.CompareTo(b.Top); return row != 0 ? row : a.Left.CompareTo(b.Left); });
            for (int i = 0; i < ordered.Count; i++)
            {
                Control child = ordered[i]; child.TabIndex = i;
            }
        }
        internal static void Apply(Control root)
        {
            foreach (Control child in root.Controls)
            {
                TabPage page = child as TabPage;
                if (page != null) { page.UseVisualStyleBackColor = false; page.BackColor = Canvas; page.ForeColor = Text; }
                AppearanceEditor editor = child as AppearanceEditor;
                if (editor != null) { editor.BackColor = Surface; editor.ForeColor = Text; }
                Label label = child as Label;
                if (label != null)
                {
                    if (label.Font.FontFamily.Name != BodyFont.FontFamily.Name) label.Font = new Font(BodyFont.FontFamily,label.Font.Size,label.Font.Style);
                    Color old = label.ForeColor;
                    label.ForeColor = old == Color.DimGray || old == Color.FromArgb(157,174,197) ? Muted : old == Color.FromArgb(132,206,195) ? Accent : Text;
                }
                LinkLabel link = child as LinkLabel;
                if (link != null) { link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = Accent; link.LinkBehavior = LinkBehavior.HoverUnderline; }
                Button button = child as Button;
                if (button != null && !Object.Equals(button.Tag,"Tint")) { button.BackColor = Surface; button.ForeColor = Text; button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = Border; }
                CheckBox check = child as CheckBox;
                if (check != null) { check.ForeColor = Text; check.BackColor = Color.Transparent; }
                ComboBox combo = child as ComboBox;
                if (combo != null)
                {
                    combo.FlatStyle = FlatStyle.Flat; combo.BackColor = Surface; combo.ForeColor = Text;
                    combo.DrawMode = DrawMode.OwnerDrawFixed;
                    combo.ItemHeight = Math.Max(combo.Font.Height + 6, 24 * combo.DeviceDpi / 96);
                    if (!Object.Equals(combo.Tag,"UiCombo")) combo.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                        ComboBox box = (ComboBox)sender; bool selected = (e.State & DrawItemState.Selected) != 0;
                        using (Brush brush = new SolidBrush(selected ? AccentSoft : Surface)) e.Graphics.FillRectangle(brush,e.Bounds);
                        string caption = e.Index >= 0 && e.Index < box.Items.Count ? box.GetItemText(box.Items[e.Index]) : box.Text;
                        TextRenderer.DrawText(e.Graphics,caption,box.Font,new Rectangle(e.Bounds.X+6,e.Bounds.Y,e.Bounds.Width-12,e.Bounds.Height),box.Enabled ? SelectionText(selected) : Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
                    };
                    combo.Tag = "UiCombo";
                }
                TextBox input = child as TextBox;
                if (input != null) { input.BackColor = Surface; input.ForeColor = Text; input.BorderStyle = BorderStyle.FixedSingle; }
                DataGridView grid = child as DataGridView;
                if (grid != null)
                {
                    grid.EnableHeadersVisualStyles = false; grid.BackgroundColor = Surface; grid.BorderStyle = BorderStyle.None;
                    grid.GridColor = Border; grid.RowHeadersVisible = false; grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
                    grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
                    grid.ColumnHeadersDefaultCellStyle.BackColor = Canvas; grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
                    grid.ColumnHeadersDefaultCellStyle.Font = BodyFont; grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8);
                    grid.DefaultCellStyle.BackColor = Surface; grid.DefaultCellStyle.ForeColor = Text;
                    grid.DefaultCellStyle.SelectionBackColor = AccentSoft; grid.DefaultCellStyle.SelectionForeColor = SelectionText(true);
                    grid.DefaultCellStyle.Padding = new Padding(8,6,8,6); grid.RowTemplate.Height = 36;
                    grid.AlternatingRowsDefaultCellStyle.BackColor = Canvas;
                    foreach (DataGridViewColumn column in grid.Columns) { DataGridViewComboBoxColumn choice = column as DataGridViewComboBoxColumn; if (choice != null) choice.FlatStyle = FlatStyle.Flat; }
                }
                Apply(child);
            }
        }
        internal static ContextMenuStrip Menu()
        {
            ContextMenuStrip menu = new ContextMenuStrip { Font = BodyFont, Renderer = new UiMenuRenderer(), BackColor = Surface, ForeColor = Text, ShowImageMargin = false };
            menu.Opening += delegate { StyleMenu(menu); };
            menu.SizeChanged += delegate { ShapeMenu(menu); };
            return menu;
        }
        internal static Color SelectionText(bool selected) { return selected && HighContrast ? SystemColors.HighlightText : Text; }
        internal static void PaintRuleChoice(object sender, DataGridViewCellPaintingEventArgs e)
        {
            DataGridView grid = (DataGridView)sender;
            if (HighContrast || e.RowIndex < 0 || e.ColumnIndex < 0 || !(grid.Columns[e.ColumnIndex] is DataGridViewComboBoxColumn)) return;

            // Keep the native combo cell and its mouse target, but match the closed chevron to the row.
            float scale = Math.Max(1, e.CellStyle.Font.Height / 15f);
            Rectangle cell = e.CellBounds;
            int width = Math.Min(cell.Width - 2, (int)Math.Ceiling(23 * scale));
            if (width < 2 || cell.Height < 3) return;
            bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
            e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.Focus);
            using (Brush brush = new SolidBrush(selected ? e.CellStyle.SelectionBackColor : e.CellStyle.BackColor))
                e.Graphics.FillRectangle(brush, cell.Right - width - 1, cell.Top + 1, width, cell.Height - 2);
            float x = cell.Right - width / 2f - 1, y = cell.Top + cell.Height / 2f;
            SmoothingMode previous = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Muted, 1.4f * scale))
                e.Graphics.DrawLines(pen, new[] { new PointF(x-4*scale,y-2*scale), new PointF(x,y+2*scale), new PointF(x+4*scale,y-2*scale) });
            e.Graphics.SmoothingMode = previous;
            e.Paint(e.ClipBounds, e.PaintParts & DataGridViewPaintParts.Focus);
            e.Handled = true;
        }
        private static void ShapeMenu(ToolStrip menu)
        {
            if (menu.Width < 2 || menu.Height < 2) return;
            using (GraphicsPath path = Round(new RectangleF(0, 0, menu.Width, menu.Height), 8 * menu.DeviceDpi / 96f))
            {
                Region previous = menu.Region;
                menu.Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }
        private static void StyleMenu(ToolStripDropDownMenu menu)
        {
            menu.SuspendLayout();
            int pad = Math.Max(4, 5 * menu.DeviceDpi / 96);
            menu.Padding = new Padding(pad);
            bool checks = false;
            foreach (ToolStripItem item in menu.Items)
            {
                item.ForeColor = Text;
                ToolStripMenuItem branch = item as ToolStripMenuItem;
                if (branch == null) continue;
                branch.Padding = new Padding(pad * 2, pad, pad * 3, pad);
                checks |= branch.Checked || branch.CheckOnClick;
                ToolStripDropDownMenu submenu = branch.HasDropDownItems ? branch.DropDown as ToolStripDropDownMenu : null;
                if (submenu != null && !Object.Equals(branch.Tag, "Styled"))
                {
                    branch.Tag = "Styled";
                    submenu.Font = BodyFont; submenu.Renderer = new UiMenuRenderer();
                    submenu.BackColor = Surface; submenu.ForeColor = Text; submenu.ShowImageMargin = false;
                    submenu.Opening += delegate { StyleMenu(submenu); };
                    submenu.SizeChanged += delegate { ShapeMenu(submenu); };
                }
            }
            menu.ShowCheckMargin = checks;
            menu.ResumeLayout(true);
            ShapeMenu(menu);
        }
        internal static void Message(IWin32Window owner, string message, string title)
        {
            using (NoticeDialog dialog = new NoticeDialog(message, title, owner != null)) dialog.ShowDialog(owner);
        }
    }
    internal sealed class NoticeDialog : Form
    {
        internal NoticeDialog(string message, string title, bool owned)
        {
            SuspendLayout();
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = owned ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
            MaximizeBox = MinimizeBox = false;
            Ui.Window(this);
            ClientSize = new Size(472, 213);

            Controls.Add(new PictureBox { Image = Ui.BrandImage, Location = new Point(20, 14), Size = new Size(32, 32), SizeMode = PictureBoxSizeMode.Zoom });
            Controls.Add(new Label { Text = title, Location = new Point(64, 12), Size = new Size(388, 36), AutoEllipsis = true,
                Font = new Font(Ui.BodyFont.FontFamily, 15, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft });

            UiCard content = new UiCard { Location = new Point(20, 59), Size = new Size(432, 91), Padding = new Padding(14) };
            string paragraphs = (message ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine);
            TextBox body = new TextBox { Name = "NoticeBody", AccessibleName = "提示正文", Text = paragraphs, Multiline = true, ReadOnly = true,
                Dock = DockStyle.Fill };
            content.Controls.Add(body);
            Controls.Add(content);
            Controls.Add(new Panel { BackColor = Ui.Border, Location = new Point(20, 158), Size = new Size(432, 1) });
            ModernButton ok = new ModernButton { Text = "确定", Primary = true, Location = new Point(336, 168), Size = new Size(116, 34), DialogResult = DialogResult.OK };
            Controls.Add(ok);
            AcceptButton = ok; CancelButton = ok;
            Ui.Apply(this);
            body.BorderStyle = BorderStyle.None;
            Shown += delegate {
                int lines = body.GetLineFromCharIndex(body.TextLength) + 1;
                if (lines * body.Font.Height > body.ClientSize.Height - 2) body.ScrollBars = ScrollBars.Vertical;
            };
            Ui.OrderTabStops(this);
            ActiveControl = ok;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(true);
        }
    }
    internal sealed class ModernButton : Button
    {
        internal bool Primary, Selected, Navigation;
        private bool hover, down;
        internal ModernButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; Cursor = Cursors.Hand; SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Ui.Canvas : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Math.Max(DeviceDpi / 96f, Height / (Navigation ? 36f : 34f));
            bool highlighted = Selected || hover || down;
            Color fill = Primary && Enabled ? Ui.Accent : Selected ? Ui.AccentSoft : highlighted ? Ui.Hover : BackColor;
            Color text = Primary && Enabled ? Ui.AccentText : highlighted ? Ui.SelectionText(true) : ForeColor;
            using (GraphicsPath path = Ui.Round(new RectangleF(.5f,.5f,Width-1,Height-1),8*scale))
            using (Brush brush = new SolidBrush(fill)) using (Pen pen = new Pen(Focused ? Ui.Accent : Ui.Border))
            {
                e.Graphics.FillPath(brush,path);
                if (!Navigation || (Focused && ShowFocusCues)) e.Graphics.DrawPath(pen,path);
            }
            Rectangle caption = ClientRectangle;
            if (Navigation)
            {
                caption.X += (int)(16 * scale); caption.Width -= (int)(20 * scale);
                if (Selected)
                    using (GraphicsPath rail = Ui.Round(new RectangleF(3*scale, Height/2f-8*scale, 3*scale, 16*scale), 1.5f*scale))
                    using (Brush brush = new SolidBrush(Ui.SelectionText(true) == Ui.Text ? Ui.Accent : Ui.AccentText)) e.Graphics.FillPath(brush, rail);
            }
            TextRenderer.DrawText(e.Graphics,Text,Font,caption,Enabled ? text : Ui.Muted,(Navigation ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter)|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle,-4,-4), Enabled ? text : Ui.Muted, fill);
        }
    }
    internal sealed class ModernComboBox : ComboBox
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PaintInfo
        {
            internal IntPtr Dc;
            internal int Erase;
            internal Native.Rect Rect;
            internal int Restore, Update;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst=32)] internal byte[] Reserved;
        }
        [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool RestoreDC(IntPtr dc,int saved);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr window, out PaintInfo info);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr window, ref PaintInfo info);
        protected override void WndProc(ref Message message)
        {
            if (Width > 10 && Height > 10 && message.Msg == 0x000F)
            {
                PaintInfo info; IntPtr dc = BeginPaint(Handle,out info);
                try { using (Graphics graphics = Graphics.FromHdc(dc)) PaintBox(graphics); }
                finally { EndPaint(Handle,ref info); }
                message.Result = IntPtr.Zero; return;
            }
            if ((message.Msg == 0x0318 || message.Msg == 0x0317) && message.WParam != IntPtr.Zero)
            { int saved=SaveDC(message.WParam); try { using (Graphics graphics = Graphics.FromHdc(message.WParam)) PaintBox(graphics); } finally { RestoreDC(message.WParam,saved); } message.Result=IntPtr.Zero; return; }
            if (message.Msg == 0x0085) { message.Result=IntPtr.Zero; return; }
            base.WndProc(ref message);
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        private void PaintBox(Graphics graphics)
        {
            float scale=Math.Max(1,Font.Height/15f);
            graphics.SmoothingMode=SmoothingMode.AntiAlias;
            graphics.SetClip(new Rectangle(0,0,Width,Height),CombineMode.Intersect);
            using(Brush background=new SolidBrush(Parent == null ? Ui.Surface : Parent.BackColor)) graphics.FillRectangle(background,0,0,Width,Height);
            using(GraphicsPath path=Ui.Round(new RectangleF(.5f,.5f,Width-1,Height-1),6*scale))using(Brush brush=new SolidBrush(Enabled?Ui.Surface:Ui.Canvas))using(Pen pen=new Pen(Focused?Ui.Accent:Ui.Border))
            {graphics.FillPath(brush,path);graphics.DrawPath(pen,path);}
            int pad=(int)(10*scale), arrow=(int)(30*scale);
            string text=SelectedIndex>=0?GetItemText(SelectedItem):Text;
            TextRenderer.DrawText(graphics,text,Font,new Rectangle(pad,0,Math.Max(1,Width-pad-arrow),Height),Enabled?Ui.Text:Ui.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            if(Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(graphics,Rectangle.Inflate(ClientRectangle,-4,-4),Ui.Text,Ui.Surface);
            float x=Width-16*scale,y=Height/2f;
            using(Pen pen=new Pen(Ui.Muted,1.4f*scale))graphics.DrawLines(pen,new PointF[]{new PointF(x-4*scale,y-2*scale),new PointF(x,y+2*scale),new PointF(x+4*scale,y-2*scale)});
        }
    }
    internal sealed class ModernCheckBox : CheckBox
    {
        internal ModernCheckBox() { SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint,true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Ui.Surface : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Math.Max(1,Font.Height/15f), size = 16*scale, y = (Height-size)/2;
            using (GraphicsPath path = Ui.Round(new RectangleF(.5f,y,size,size),4*scale)) using (Brush brush = new SolidBrush(Checked ? Ui.Accent : Ui.Surface)) using (Pen pen = new Pen(Enabled ? Ui.Border : Ui.Muted,scale)) { e.Graphics.FillPath(brush,path); e.Graphics.DrawPath(pen,path); }
            if (Checked) using (Pen pen = new Pen(Ui.AccentText,2*scale)) { pen.StartCap = pen.EndCap = LineCap.Round; e.Graphics.DrawLines(pen,new PointF[] { new PointF(4*scale,y+8*scale),new PointF(7*scale,y+11*scale),new PointF(12*scale,y+5*scale) }); }
            TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle((int)(24*scale),0,Width-(int)(24*scale),Height),Enabled ? Ui.Text : Ui.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics,ClientRectangle,Ui.Accent,Parent.BackColor);
        }
    }
    internal sealed class ModernSlider : TrackBar
    {
        internal ModernSlider() { AutoSize = false; TickStyle = TickStyle.None; SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint,true); ValueChanged += delegate { Invalidate(); }; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Ui.Surface : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale=Math.Max(1,Font.Height/15f), left=10*scale, width=Math.Max(1,Width-20*scale), y=Height/2f;
            float value=left+width*(Value-Minimum)/Math.Max(1f,Maximum-Minimum);
            using(Pen track=new Pen(Ui.Border,4*scale))using(Pen fill=new Pen(Enabled?Ui.Accent:Ui.Muted,4*scale)){track.StartCap=track.EndCap=fill.StartCap=fill.EndCap=LineCap.Round;e.Graphics.DrawLine(track,left,y,left+width,y);e.Graphics.DrawLine(fill,left,y,value,y);}
            using(Brush brush=new SolidBrush(Ui.Surface))using(Pen edge=new Pen(Enabled?Ui.Accent:Ui.Muted,3*scale)){e.Graphics.FillEllipse(brush,value-7*scale,y-7*scale,14*scale,14*scale);e.Graphics.DrawEllipse(edge,value-7*scale,y-7*scale,14*scale,14*scale);}
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-2,-2),Ui.Accent,Parent == null ? Ui.Surface : Parent.BackColor);
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            long next = Value;
            switch(e.KeyCode)
            {
                case Keys.Left: case Keys.Down: next -= SmallChange; break;
                case Keys.Right: case Keys.Up: next += SmallChange; break;
                case Keys.PageDown: next -= LargeChange; break;
                case Keys.PageUp: next += LargeChange; break;
                case Keys.Home: next = Minimum; break;
                case Keys.End: next = Maximum; break;
                default: base.OnKeyDown(e); return;
            }
            Value = (int)Math.Max(Minimum,Math.Min(Maximum,next)); e.Handled = true; e.SuppressKeyPress = true;
            base.OnKeyDown(e);
        }
        private void ChangeAt(int x) { float pad=10*Math.Max(1,Font.Height/15f); Value=Math.Max(Minimum,Math.Min(Maximum,(int)Math.Round(Minimum+(Maximum-Minimum)*(x-pad)/Math.Max(1,Width-2*pad)))); }
        protected override void OnMouseDown(MouseEventArgs e) { if(Enabled&&e.Button==MouseButtons.Left){Focus();Capture=true;ChangeAt(e.X);}base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if(Capture&&Enabled)ChangeAt(e.X);base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Capture=false;base.OnMouseUp(e); }
    }
    internal sealed class ModernMeter : ProgressBar
    {
        internal ModernMeter() { SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint,true); }
        protected override void WndProc(ref Message m) { base.WndProc(ref m); if(m.Msg==0x0402)Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent.BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(GraphicsPath path=Ui.Round(new RectangleF(0,0,Width,Height),Height/2f))using(Brush brush=new SolidBrush(Ui.Border))e.Graphics.FillPath(brush,path);
            float width=Width*Value/(float)Math.Max(1,Maximum);
            if(width>1)using(GraphicsPath path=Ui.Round(new RectangleF(0,0,width,Height),Height/2f))using(Brush brush=new SolidBrush(Ui.Accent))e.Graphics.FillPath(brush,path);
        }
    }
    internal sealed class SettingsPages : TabControl
    {
        internal SettingsPages() { Appearance=TabAppearance.FlatButtons;SizeMode=TabSizeMode.Fixed;ItemSize=new Size(1,1);Padding=Point.Empty; }
        public override Rectangle DisplayRectangle { get { return new Rectangle(0,0,Width,Height); } }
    }
    internal sealed class UiCard : Panel
    {
        internal UiCard()
        {
            BackColor = Ui.Surface;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Ui.Canvas : Parent.BackColor);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Ui.Round(new RectangleF(.5f,.5f,Width-1,Height-1),12*DeviceDpi/96f))
            using (Brush brush = new SolidBrush(BackColor)) using (Pen pen = new Pen(Ui.Border))
            { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
        }
    }
    internal sealed class UiMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Ui.Surface); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(GraphicsPath path=Ui.Round(new RectangleF(.5f,.5f,e.ToolStrip.Width-1,e.ToolStrip.Height-1),8*e.ToolStrip.DeviceDpi/96f))using(Pen pen=new Pen(Ui.Border))e.Graphics.DrawPath(pen,path); }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if ((e.Item.Selected || e.Item.Pressed) && e.Item.Enabled)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = Ui.Round(new RectangleF(1,1,e.Item.Width-2,e.Item.Height-2),5*e.ToolStrip.DeviceDpi/96f))
                using (Brush brush = new SolidBrush(e.Item.Pressed ? Ui.AccentSoft : Ui.Hover)) e.Graphics.FillPath(brush,path);
            }
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor=e.Item.Enabled?Ui.SelectionText(e.Item.Selected || e.Item.Pressed):Ui.Muted;base.OnRenderItemText(e); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle box = e.ImageRectangle;
            float x = box.Left + box.Width / 2f, y = box.Top + box.Height / 2f;
            float scale = Math.Max(1, Math.Min(box.Width, box.Height) / 16f);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Ui.SelectionText(e.Item.Selected), 1.5f * scale))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                e.Graphics.DrawLines(pen, new[] { new PointF(x-4*scale,y), new PointF(x-1*scale,y+3*scale), new PointF(x+5*scale,y-3*scale) });
            }
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { using(Pen pen=new Pen(Ui.Border))e.Graphics.DrawLine(pen,6,e.Item.Height/2,e.Item.Width-6,e.Item.Height/2); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor=Ui.SelectionText(e.Item.Selected || e.Item.Pressed);base.OnRenderArrow(e); }
    }
}
