using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Collections.Generic;
class GenerateIcon
{
 static GraphicsPath Round(float x,float y,float w,float h,float r) { var p=new GraphicsPath();p.AddArc(x,y,r,r,180,90);p.AddArc(x+w-r,y,r,r,270,90);p.AddArc(x+w-r,y+h-r,r,r,0,90);p.AddArc(x,y+h-r,r,r,90,90);p.CloseFigure();return p; }
 static void Main(string[] args) {
  int[] sizes={16,20,24,32,40,48,64,128,256}; var data=new List<byte[]>();
  foreach(int size in sizes) {
   using(var high=new Bitmap(size*4,size*4)) using(var g=Graphics.FromImage(high)) {
    g.SmoothingMode=SmoothingMode.AntiAlias;g.ScaleTransform(size*4/256f,size*4/256f);g.Clear(Color.Transparent);
    using(var p=Round(8,8,240,240,60)) using(var b=new SolidBrush(Color.FromArgb(21,61,58)))g.FillPath(b,p);
    using(var b=new SolidBrush(Color.FromArgb(179,243,219)))g.FillEllipse(b,51,51,154,154);
    using(var p=new GraphicsPath()) {p.AddArc(51,51,154,154,270,180);p.CloseFigure();using(var b=new SolidBrush(Color.FromArgb(52,132,117)))g.FillPath(b,p);}
    using(var b=new SolidBrush(Color.FromArgb(243,254,249)))g.FillEllipse(b,173,48,20,20);
    using(var small=new Bitmap(size,size))using(var outg=Graphics.FromImage(small)){outg.InterpolationMode=InterpolationMode.HighQualityBicubic;outg.DrawImage(high,0,0,size,size);using(var m=new MemoryStream()){if(size==256){small.Save(m,ImageFormat.Png);data.Add(m.ToArray());}else{small.Save(m,ImageFormat.Bmp);byte[] bmp=m.ToArray();int maskBytes=((size+31)/32)*4*size;byte[] dib=new byte[bmp.Length-14+maskBytes];Array.Copy(bmp,14,dib,0,bmp.Length-14);Array.Copy(BitConverter.GetBytes(size*2),0,dib,8,4);for(int y=0;y<size;y++)for(int x=0;x<size;x++)if(small.GetPixel(x,y).A<128)dib[bmp.Length-14+(size-1-y)*((size+31)/32)*4+x/8]|=(byte)(0x80>>(x%8));data.Add(dib);}}if(size==256)small.Save(Path.Combine(args[0],"LumaBar.png"),ImageFormat.Png);}
   }
  }
  using(var w=new BinaryWriter(File.Create(Path.Combine(args[0],"LumaBar.ico")))){w.Write((ushort)0);w.Write((ushort)1);w.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;for(int i=0;i<sizes.Length;i++){w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)0);w.Write((byte)0);w.Write((ushort)1);w.Write((ushort)32);w.Write(data[i].Length);w.Write(offset);offset+=data[i].Length;}foreach(var bytes in data)w.Write(bytes);}
 }
}
