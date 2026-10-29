using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// 生成 ClipNotify 托盘图标：蓝色圆角底 + 白色剪贴板 + 绿色对勾
class MakeIcon
{
    static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "clipnotify.ico";
        int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
        var frames = new List<Bitmap>();
        foreach (int s in sizes)
        {
            var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                DrawIcon(g, s);
            }
            frames.Add(bmp);
        }

        using (var fs = File.Create(outPath))
        {
            fs.WriteByte(0); fs.WriteByte(0);          // reserved
            fs.WriteByte(1); fs.WriteByte(0);          // type: icon
            fs.WriteByte((byte)frames.Count); fs.WriteByte(0); // count

            int offset = 6 + 16 * frames.Count;
            var pngs = new List<byte[]>();
            foreach (var bmp in frames)
            {
                byte w = (byte)(bmp.Width >= 256 ? 0 : bmp.Width);
                byte h = (byte)(bmp.Height >= 256 ? 0 : bmp.Height);
                byte[] png;
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    png = ms.ToArray();
                }
                fs.WriteByte(w); fs.WriteByte(h);      // size
                fs.WriteByte(0); fs.WriteByte(0);      // palette
                fs.WriteByte(1); fs.WriteByte(0);      // planes
                fs.WriteByte(32); fs.WriteByte(0);     // bpp
                int len = png.Length;
                fs.WriteByte((byte)(len & 0xFF));
                fs.WriteByte((byte)((len >> 8) & 0xFF));
                fs.WriteByte((byte)((len >> 16) & 0xFF));
                fs.WriteByte((byte)((len >> 24) & 0xFF));
                int ofs = offset;
                fs.WriteByte((byte)(ofs & 0xFF));
                fs.WriteByte((byte)((ofs >> 8) & 0xFF));
                fs.WriteByte((byte)((ofs >> 16) & 0xFF));
                fs.WriteByte((byte)((ofs >> 24) & 0xFF));
                offset += len;
                pngs.Add(png);
            }
            foreach (var png in pngs) fs.Write(png, 0, png.Length);
        }
        Console.WriteLine("ICO_OK " + outPath);
    }

    // 以 64x64 为基准绘制（所有尺寸按比例缩放）
    static void DrawIcon(Graphics g, int s)
    {
        float k = s / 64f;

        // 蓝色圆角背景
        var bg = new RectangleF(5f * k, 5f * k, 54f * k, 54f * k);
        using (var path = RoundedRect(bg, 13f * k))
        using (var brush = new SolidBrush(Color.FromArgb(255, 74, 124, 255)))
            g.FillPath(brush, path);

        // 剪贴板板身（白）
        using (var brush = new SolidBrush(Color.White))
            g.FillRectangle(brush, new RectangleF(17f * k, 13f * k, 30f * k, 38f * k));

        // 夹子
        using (var brush = new SolidBrush(Color.White))
            g.FillRectangle(brush, new RectangleF(27f * k, 8f * k, 10f * k, 7f * k));

        // 绿色对勾
        using (var pen = new Pen(Color.FromArgb(255, 0, 168, 100), 5.5f * k))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            g.DrawLines(pen, new PointF[]
            {
                new PointF(22f * k, 31f * k),
                new PointF(29f * k, 38f * k),
                new PointF(42f * k, 24f * k)
            });
        }
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2f;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
