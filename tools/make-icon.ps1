# Draws assets\WinApkLauncher.ico: a blue rounded square with three app tiles and a
# "play" triangle. Every size is drawn separately (no downscaling), sizes up to 64 px as
# 32-bit BMP entries and 256 px as PNG, which is what Windows, the .NET SDK and Inno Setup
# all read.
#
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\assets\WinApkLauncher.ico')
)
$ErrorActionPreference = 'Stop'

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class LauncherIcon
{
    static GraphicsPath Round(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = Math.Min(2 * r, Math.Min(w, h));
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Bitmap Draw(int px)
    {
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float s = px;
            float m = px <= 24 ? 0.5f : s * 0.04f;
            using (var bg = Round(m, m, s - 2 * m, s - 2 * m, s * 0.22f))
            using (var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(0, s),
                       Color.FromArgb(0x2B, 0x88, 0xD8), Color.FromArgb(0x00, 0x5A, 0x9E)))
                g.FillPath(brush, bg);

            // 2 x 2 grid: three tiles and a play triangle.
            float a = s * 0.22f, gap = s * 0.08f, t = (s - 2 * a - gap) / 2f, r = t * 0.24f;
            float b = a + t + gap;
            using (var white = new SolidBrush(Color.White))
            {
                foreach (var pt in new[] { new PointF(a, a), new PointF(b, a), new PointF(a, b) })
                    using (var tile = Round(pt.X, pt.Y, t, t, r)) g.FillPath(white, tile);
                using (var tri = new GraphicsPath())
                {
                    float x0 = b + t * 0.12f, x1 = b + t * 0.98f, y0 = b + t * 0.04f, y1 = b + t * 0.96f;
                    tri.AddPolygon(new[] { new PointF(x0, y0), new PointF(x1, (y0 + y1) / 2f), new PointF(x0, y1) });
                    g.FillPath(white, tri);
                    using (var pen = new Pen(Color.White, Math.Max(1f, t * 0.12f)) { LineJoin = LineJoin.Round })
                        g.DrawPath(pen, tri);
                }
            }
        }
        return bmp;
    }

    static byte[] Dib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int maskStride = ((w + 31) / 32) * 4;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32);
            bw.Write(0); bw.Write(w * h * 4 + maskStride * h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = h - 1; y >= 0; y--)
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
                }
            for (int y = h - 1; y >= 0; y--)
            {
                var row = new byte[maskStride];
                for (int x = 0; x < w; x++)
                    if (bmp.GetPixel(x, y).A == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                bw.Write(row);
            }
            return ms.ToArray();
        }
    }

    public static void WriteIco(string path, int[] sizes)
    {
        var images = new List<byte[]>();
        foreach (var px in sizes)
            using (var bmp = Draw(px))
            {
                if (px >= 256)
                    using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); images.Add(ms.ToArray()); }
                else images.Add(Dib(bmp));
            }
        using (var fs = new FileStream(path, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int px = sizes[i];
                bw.Write((byte)(px >= 256 ? 0 : px)); bw.Write((byte)(px >= 256 ? 0 : px));
                bw.Write((byte)0); bw.Write((byte)0); bw.Write((short)1); bw.Write((short)32);
                bw.Write(images[i].Length); bw.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) bw.Write(img);
        }
    }
}
'@

$Out = [System.IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
[LauncherIcon]::WriteIco($Out, [int[]](16, 20, 24, 32, 40, 48, 64, 256))
Write-Host "wrote $Out"
