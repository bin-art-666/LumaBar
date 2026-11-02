using System;
using System.Drawing;
using System.Windows.Forms;
namespace LumaBar
{
    internal sealed class TintPicker : Form
    {
        private readonly Panel preview;
        private readonly TextBox hex;
        private readonly ModernSlider[] channels = new ModernSlider[3];
        private readonly Label[] values = new Label[3];
        private readonly Label error;
        private bool loading;
        internal Color SelectedColor { get; private set; }
        internal TintPicker(Color initial)
        {
            Text="LumaBar · 自定义染色";ClientSize=new Size(420,410);FormBorderStyle=FormBorderStyle.FixedDialog;
            MaximizeBox=MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;Ui.Window(this);
            Controls.Add(new Label{Text="自定义染色",Location=new Point(24,20),Size=new Size(360,32),Font=new Font(Ui.BodyFont.FontFamily,17,FontStyle.Bold)});
            preview=new Panel{Location=new Point(24,70),Size=new Size(372,66)};Controls.Add(preview);
            Controls.Add(new Label{Text="颜色值",Location=new Point(24,154),Size=new Size(90,27)});
            hex=new TextBox{Location=new Point(120,150),Size=new Size(276,30),Name="HexColor"};Controls.Add(hex);
            string[] labels={"红色 R","绿色 G","蓝色 B"};
            for(int index=0;index<3;index++)
            {
                int y=201+index*43;
                Controls.Add(new Label{Text=labels[index],Location=new Point(24,y),Size=new Size(90,28)});
                channels[index]=new ModernSlider{Location=new Point(120,y-5),Size=new Size(228,36),Minimum=0,Maximum=255};Controls.Add(channels[index]);
                values[index]=new Label{Location=new Point(352,y),Size=new Size(44,28),TextAlign=ContentAlignment.MiddleRight};Controls.Add(values[index]);
                channels[index].ValueChanged+=delegate{if(!loading)SetColor(Color.FromArgb(channels[0].Value,channels[1].Value,channels[2].Value));};
            }
            error=new Label{Location=new Point(24,330),Size=new Size(372,22),Font=new Font(Ui.BodyFont.FontFamily,9)};Controls.Add(error);
            ModernButton cancel=new ModernButton{Text="取消",Location=new Point(170,364),Size=new Size(100,32),DialogResult=DialogResult.Cancel};
            ModernButton accept=new ModernButton{Text="使用此颜色",Primary=true,Location=new Point(282,364),Size=new Size(114,32)};
            accept.Click+=delegate{Color parsed;if(!TryParse(hex.Text,out parsed)){error.Text="请输入 6 位颜色值，例如 #202630。";error.ForeColor=Color.Firebrick;hex.Focus();return;}SetColor(parsed);DialogResult=DialogResult.OK;Close();};
            Controls.Add(cancel);Controls.Add(accept);AcceptButton=accept;CancelButton=cancel;
            hex.TextChanged+=delegate{if(!loading){Color parsed;if(TryParse(hex.Text,out parsed))SetColor(parsed);}};
            hex.AccessibleName="颜色值";
            for(int index=0;index<3;index++) channels[index].AccessibleName=labels[index];
            Ui.OrderTabStops(this);
            Ui.Apply(this);SetColor(initial);AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
        }
        private static bool TryParse(string value,out Color color)
        {int rgb;string text=(value??"").Trim().TrimStart('#');bool valid=text.Length==6&&Int32.TryParse(text,System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out rgb);color=Color.Empty;if(!valid)return false;Int32.TryParse(text,System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out rgb);color=Color.FromArgb((rgb>>16)&255,(rgb>>8)&255,rgb&255);return true;}
        private void SetColor(Color value)
        {
            loading=true;SelectedColor=value;preview.BackColor=value;hex.Text="#"+value.R.ToString("X2")+value.G.ToString("X2")+value.B.ToString("X2");
            int[] rgb={value.R,value.G,value.B};for(int i=0;i<3;i++){channels[i].Value=rgb[i];values[i].Text=rgb[i].ToString();}error.Text="";loading=false;
        }
    }
}
