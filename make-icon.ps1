# Génère dist\BatteryManager.ico (icône de batterie) utilisé comme icône de l'application.
# Le dessin est identique à celui de la classe TrayIcons dans BatteryManager.cs.
param([string]$Output = (Join-Path $PSScriptRoot 'dist\BatteryManager.ico'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$code = @'
using System;
using System.Collections.Generic;
using System.Drawing;
// (les modes de lissage sont qualifies directement ci-dessous)
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class BatteryIcon
{
    static readonly Color Green = Color.FromArgb(76, 175, 80);

    public static void Write(string path)
    {
        int[] sizes = new int[] { 16, 20, 24, 32, 48, 64 };
        List<byte[]> images = new List<byte[]>();
        foreach (int size in sizes) images.Add(RenderDib(size));

        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] image in images) w.Write(image);
        }
    }

    // Trames DIB 32 bits (et non PNG) : le shell Windows n'affiche pas l'icône d'une
    // info-bulle quand la trame est compressée en PNG.
    static byte[] RenderDib(int size)
    {
        using (Bitmap bitmap = new Bitmap(size, size))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);

            float scale = size / 16f;
            using (SolidBrush bodyBrush = new SolidBrush(Green))
            using (SolidBrush fillBrush = new SolidBrush(Green))
            {
                // Même dessin que PaintBattery() dans BatteryManager.cs : rectangle plein + borne.
                float left = 1.5f * scale;
                float top = 4f * scale;
                float width = 11f * scale;
                float height = 8f * scale;
                g.FillRectangle(fillBrush, left, top, width, height);
                g.FillRectangle(bodyBrush, left + width, top + 2.5f * scale, 1.5f * scale, 3f * scale);
            }

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                BitmapData data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    w.Write(40);
                    w.Write(size);
                    w.Write(size * 2);
                    w.Write((ushort)1);
                    w.Write((ushort)32);
                    w.Write(0);
                    w.Write(data.Stride * size);
                    w.Write(0);
                    w.Write(0);
                    w.Write(0);
                    w.Write(0);

                    byte[] row = new byte[data.Stride];
                    for (int y = 0; y < size; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        for (int x = 0; x < size; x++)
                        {
                            byte b = row[x * 4];
                            byte gr = row[x * 4 + 1];
                            byte r = row[x * 4 + 2];
                            byte a = row[x * 4 + 3];
                            w.Write((byte)(b * a / 255));
                            w.Write((byte)(gr * a / 255));
                            w.Write((byte)(r * a / 255));
                            w.Write((byte)255);
                        }
                    }

                    int maskStride = ((size + 31) / 32) * 4;
                    byte[] mask = new byte[maskStride];
                    for (int y = 0; y < size; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        Array.Clear(mask, 0, mask.Length);
                        for (int x = 0; x < size; x++)
                            if (row[x * 4 + 3] < 128) mask[x / 8] |= (byte)(0x80 >> (x % 8));
                        w.Write(mask);
                    }
                }
                finally { bitmap.UnlockBits(data); }
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
'@

Add-Type -TypeDefinition $code -Language CSharp -ReferencedAssemblies 'System.Drawing'

$dir = Split-Path -Parent $Output
New-Item -ItemType Directory -Force -Path $dir | Out-Null
[BatteryIcon]::Write($Output)
Write-Host "Icône générée : $Output" -ForegroundColor Green
