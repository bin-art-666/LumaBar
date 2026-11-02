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
        internal static readonly Color Canvas = Dark ? Color.FromArgb(28,31,35) : Color.FromArgb(245,247,247);
        internal static readonly Color Surface = Dark ? Color.FromArgb(37,41,45) : Color.White;
        internal static readonly Color Text = Dark ? Color.FromArgb(239,243,242) : Color.FromArgb(30,43,41);
        internal static readonly Color Muted = Dark ? Color.FromArgb(169,182,178) : Color.FromArgb(100,116,111);
        internal static readonly Color Border = Dark ? Color.FromArgb(66,76,72) : Color.FromArgb(218,227,222);
        internal static readonly Color Accent = Dark ? Color.FromArgb(139,224,194) : Color.FromArgb(15,112,91);
        internal static readonly Color AccentSoft = Dark ? Color.FromArgb(42,72,63) : Color.FromArgb(226,244,236);
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
                if (page != null) { page.UseVisualStyleBackColor = false; page.BackColor = Surface; page.ForeColor = Text; }
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
                        TextRenderer.DrawText(e.Graphics,caption,box.Font,new Rectangle(e.Bounds.X+6,e.Bounds.Y,e.Bounds.Width-12,e.Bounds.Height),box.Enabled ? Text : Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
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
                    grid.DefaultCellStyle.SelectionBackColor = AccentSoft; grid.DefaultCellStyle.SelectionForeColor = Text;
                    grid.DefaultCellStyle.Padding = new Padding(8,6,8,6); grid.RowTemplate.Height = 36;
                    grid.AlternatingRowsDefaultCellStyle.BackColor = Canvas;
                    foreach (DataGridViewColumn column in grid.Columns) { DataGridViewComboBoxColumn choice = column as DataGridViewComboBoxColumn; if (choice != null) choice.FlatStyle = FlatStyle.Flat; }
                }
                Apply(child);
            }
        }
        internal static ContextMenuStrip Menu()
        {
            ContextMenuStrip menu = new ContextMenuStrip { Font = BodyFont, Renderer = new UiMenuRenderer(), BackColor = Surface, ForeColor = Text, Padding = new Padding(6), ImageScalingSize = new Size(24,24), ShowImageMargin = true };
            menu.Opening += delegate { StyleMenu(menu); };
            menu.Opened += delegate { using(GraphicsPath path=Round(new RectangleF(0,0,menu.Width,menu.Height),9)) { Region old=menu.Region;menu.Region=new Region(path);if(old!=null)old.Dispose(); } };
            return menu;
        }
        private static void StyleMenu(ToolStrip menu)
        {
            menu.Font = BodyFont; menu.Renderer = new UiMenuRenderer(); menu.BackColor = Surface; menu.ForeColor = Text;
            foreach (ToolStripItem item in menu.Items)
            {
                item.ForeColor = Text; item.Padding = new Padding(8,6,12,6);
                ToolStripMenuItem branch = item as ToolStripMenuItem;
                if (branch != null && branch.HasDropDownItems)
                {
                    StyleMenu(branch.DropDown);
                    if (!Object.Equals(branch.Tag,"Styled")) { branch.Tag = "Styled"; branch.DropDownOpening += delegate { StyleMenu(branch.DropDown); }; }
                }
            }
        }
        internal static Image Glyph(string code)
        {
            Bitmap image = new Bitmap(32,32);
            using (Graphics g = Graphics.FromImage(image)) using (Pen pen = new Pen(Accent,1.6f))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.ScaleTransform(1.6f,1.6f); pen.StartCap=pen.EndCap=LineCap.Round; pen.LineJoin=LineJoin.Round;
                if(code == "\uE713")
                {
                    PointF[] points=new PointF[24]; for(int i=0;i<24;i++){double angle=i*Math.PI/12;double radius=i%4<2?8:6;points[i]=new PointF(10+(float)(radius*Math.Cos(angle)),10+(float)(radius*Math.Sin(angle)));}
                    g.DrawPolygon(pen,points);g.DrawEllipse(pen,7,7,6,6);
                }
                else if(code == "\uE769") { g.DrawLine(pen,6,4,6,16);g.DrawLine(pen,14,4,14,16); }
                else if(code == "\uE72C") {g.DrawArc(pen,4,4,12,12,50,270);g.DrawLines(pen,new PointF[]{new PointF(11,3),new PointF(16,4),new PointF(15,9)});}
                else if(code == "\uE790") {g.DrawRectangle(pen,3,4,14,12);g.DrawLine(pen,9,4,9,16);g.DrawLine(pen,3,9,17,9);}
                else if(code == "\uE916") {g.DrawArc(pen,3,4,14,14,180,180);g.DrawLine(pen,10,11,14,7);g.DrawLine(pen,5,16,15,16);}
                else if(code == "\uE8BB") {g.DrawLines(pen,new PointF[]{new PointF(8,3),new PointF(3,3),new PointF(3,17),new PointF(8,17)});g.DrawLine(pen,8,10,17,10);g.DrawLines(pen,new PointF[]{new PointF(13,6),new PointF(17,10),new PointF(13,14)});}
                else {for(int y=5;y<=15;y+=5){g.DrawLine(pen,7,y,17,y);g.DrawLine(pen,3,y,3.5f,y);}}
            }
            return image;
        }
        internal static void Message(IWin32Window owner, string message, string title)
        {
            using (Form dialog = new Form { Text = title, ClientSize = new Size(470,230), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false })
            {
                Window(dialog);
                dialog.Controls.Add(new PictureBox { Image = BrandImage, Location = new Point(24,24), Size = new Size(40,40), SizeMode = PictureBoxSizeMode.Zoom });
                dialog.Controls.Add(new Label { Text = title, Location = new Point(82,27), Size = new Size(365,36), Font = new Font(BodyFont.FontFamily,15,FontStyle.Bold) });
                dialog.Controls.Add(new Label { Text = message, Location = new Point(26,82), Size = new Size(420,90) });
                ModernButton ok = new ModernButton { Text = "确定", Primary = true, Location = new Point(330,180), Size = new Size(116,34), DialogResult = DialogResult.OK };
                dialog.Controls.Add(ok); dialog.AcceptButton = ok; dialog.CancelButton = ok; Apply(dialog); dialog.AutoScaleDimensions = new SizeF(96,96); dialog.AutoScaleMode = AutoScaleMode.Dpi; dialog.ShowDialog(owner);
            }
        }
    }
    internal sealed class ModernButton : Button
    {
        internal bool Primary, Selected;
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
            Color fill = Primary ? Ui.Accent : Selected || hover || down ? Ui.AccentSoft : BackColor;
            Color text = Primary ? (Ui.Dark ? Color.FromArgb(20,45,36) : Color.White) : Selected ? Ui.Accent : ForeColor;
            using (GraphicsPath path = Ui.Round(new RectangleF(.5f,.5f,Width-1,Height-1),7*Math.Max(1,Height/32f)))
            using (Brush brush = new SolidBrush(fill)) using (Pen pen = new Pen(Focused ? Ui.Accent : Ui.Border)) { e.Graphics.FillPath(brush,path); e.Graphics.DrawPath(pen,path); }
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,Enabled ? text : Ui.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
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
            if (Checked) using (Pen pen = new Pen(Ui.Dark ? Color.FromArgb(20,45,36) : Color.White,2*scale)) { pen.StartCap = pen.EndCap = LineCap.Round; e.Graphics.DrawLines(pen,new PointF[] { new PointF(4*scale,y+8*scale),new PointF(7*scale,y+11*scale),new PointF(12*scale,y+5*scale) }); }
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
    internal sealed class UiMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Ui.Surface); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(GraphicsPath path=Ui.Round(new RectangleF(.5f,.5f,e.ToolStrip.Width-1,e.ToolStrip.Height-1),8))using(Pen pen=new Pen(Ui.Border))e.Graphics.DrawPath(pen,path); }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if(e.Item.Selected&&e.Item.Enabled){e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(GraphicsPath path=Ui.Round(new RectangleF(2,1,e.Item.Width-4,e.Item.Height-2),6))using(Brush brush=new SolidBrush(Ui.AccentSoft))e.Graphics.FillPath(brush,path);}
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor=e.Item.Enabled?Ui.Text:Ui.Muted;base.OnRenderItemText(e); }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { using(Pen pen=new Pen(Ui.Border))e.Graphics.DrawLine(pen,8,e.Item.Height/2,e.Item.Width-8,e.Item.Height/2); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor=Ui.Muted;base.OnRenderArrow(e); }
    }
}
