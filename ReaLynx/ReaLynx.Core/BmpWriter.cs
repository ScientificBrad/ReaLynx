using ReaLynx.Core.Cartridge;
using ReaLynx.Core.Mikey;

namespace ReaLynx.Core;

/// <summary>
/// Dependency-free 32-bit BGRA BMP screenshot writer with optional rotation support.
/// </summary>
public static class BmpWriter
{
    public static (uint[] Pixels, int Width, int Height) GetRotatedFramebuffer(uint[] src, CartRotation rotation)
    {
        int sw = MikeyChip.ScreenWidth;
        int sh = MikeyChip.ScreenHeight;

        if (rotation == CartRotation.None)
        {
            uint[] copy = new uint[src.Length];
            Array.Copy(src, copy, src.Length);
            return (copy, sw, sh);
        }

        int dw = sh; // 102
        int dh = sw; // 160
        uint[] dst = new uint[dw * dh];

        if (rotation == CartRotation.Left) // 90 deg CCW
        {
            for (int y = 0; y < sh; y++)
            {
                for (int x = 0; x < sw; x++)
                {
                    int dx = y;
                    int dy = sw - 1 - x;
                    dst[dy * dw + dx] = src[y * sw + x];
                }
            }
        }
        else // CartRotation.Right: 90 deg CW
        {
            for (int y = 0; y < sh; y++)
            {
                for (int x = 0; x < sw; x++)
                {
                    int dx = sh - 1 - y;
                    int dy = x;
                    dst[dy * dw + dx] = src[y * sw + x];
                }
            }
        }

        return (dst, dw, dh);
    }

    public static void SaveBgra32Bmp(string path, uint[] pixels, int width, int height)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        int imageSize = width * height * 4;
        int fileSize = 54 + imageSize;

        // BITMAPFILEHEADER (14 bytes)
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write(0);
        bw.Write(54);

        // BITMAPINFOHEADER (40 bytes)
        bw.Write(40);
        bw.Write(width);
        bw.Write(-height); // Top-down DIB
        bw.Write((short)1);
        bw.Write((short)32);
        bw.Write(0); // BI_RGB
        bw.Write(imageSize);
        bw.Write(2835);
        bw.Write(2835);
        bw.Write(0);
        bw.Write(0);

        for (int i = 0; i < pixels.Length; i++)
        {
            bw.Write(pixels[i]);
        }
    }
}
