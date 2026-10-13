$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
public static class CabinetIconBuilder
{
    public static Bitmap Scale(Image source, int size)
    {
        Bitmap result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(result))
        using (ImageAttributes attributes = new ImageAttributes())
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        return result;
    }
    static byte[] Frame(Bitmap image, bool png)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            if (png) image.Save(stream, ImageFormat.Png);
            else using (BinaryWriter writer = new BinaryWriter(stream))
            {
                int size = image.Width, maskStride = ((size + 31) / 32) * 4;
                writer.Write(40); writer.Write(size); writer.Write(size * 2);
                writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(0);
                writer.Write(size * size * 4 + maskStride * size);
                writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                for (int y = size - 1; y >= 0; y--)
                    for (int x = 0; x < size; x++)
                    {
                        Color c = image.GetPixel(x, y);
                        writer.Write(c.B); writer.Write(c.G); writer.Write(c.R); writer.Write(c.A);
                    }
                for (int y = size - 1; y >= 0; y--)
                {
                    byte[] mask = new byte[maskStride];
                    for (int x = 0; x < size; x++)
                        if (image.GetPixel(x, y).A == 0) mask[x / 8] |= (byte)(0x80 >> (x % 8));
                    writer.Write(mask);
                }
            }
            return stream.ToArray();
        }
    }
    public static void Build(string directory)
    {
        int[] sizes = {16, 20, 24, 32, 40, 48, 64, 256};
        using (Image draft = Image.FromFile(Path.Combine(directory, "drafts", "01-two-drawer-cabinet.png")))
        using (Bitmap master = Scale(draft, 1024))
        {
            master.Save(Path.Combine(directory, "cabinet-1024.png"), ImageFormat.Png);
            byte[][] frames = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (Bitmap bitmap = Scale(master, sizes[i]))
                {
                    bitmap.Save(Path.Combine(directory, "cabinet-" + sizes[i] + ".png"), ImageFormat.Png);
                    frames[i] = Frame(bitmap, sizes[i] == 256);
                }
            using (BinaryWriter writer = new BinaryWriter(File.Create(Path.Combine(directory, "cabinet.ico"))))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                int offset = 6 + sizes.Length * 16;
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write(frames[i].Length); writer.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (byte[] frame in frames) writer.Write(frame);
            }
            using (Bitmap sheet = new Bitmap(900, 700))
            using (Graphics g = Graphics.FromImage(sheet))
            using (Font title = new Font("Segoe UI", 17, FontStyle.Bold))
            using (Font label = new Font("Segoe UI", 10))
            {
                int[] previewSizes = {16, 24, 32, 48, 256};
                int[] centers = {65, 170, 280, 400, 675};
                for (int row = 0; row < 2; row++)
                {
                    int top = row * 350;
                    Color background = row == 0 ? Color.FromArgb(243,243,243) : Color.FromArgb(32,32,32);
                    using (Brush bg = new SolidBrush(background)) g.FillRectangle(bg, 0, top, 900, 350);
                    using (Brush ink = new SolidBrush(row == 0 ? Color.FromArgb(32,32,32) : Color.FromArgb(243,243,243)))
                    {
                        g.DrawString("Cabinet / " + (row == 0 ? "light taskbar" : "dark taskbar"), title, ink, 22, top + 12);
                        for (int i = 0; i < previewSizes.Length; i++)
                        {
                            int size = previewSizes[i];
                            using (Bitmap bitmap = Scale(master, size))
                                g.DrawImageUnscaled(bitmap, centers[i] - size / 2, top + 180 - size / 2);
                            string text = size + " px";
                            SizeF width = g.MeasureString(text, label);
                            g.DrawString(text, label, ink, centers[i] - width.Width / 2, top + 318);
                        }
                    }
                }
                sheet.Save(Path.Combine(directory, "preview.png"), ImageFormat.Png);
            }
        }
    }
}
'@
[CabinetIconBuilder]::Build($PSScriptRoot)
Write-Output 'Created 1024px master, eight ICO frames, size PNGs, and preview.png.'
