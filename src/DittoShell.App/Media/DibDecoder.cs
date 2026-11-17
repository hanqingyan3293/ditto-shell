using System.IO;
using System.Windows.Media.Imaging;

namespace DittoShell.App.Media;

/// <summary>
/// Ditto 存的图片是 CF_DIB（裸 DIB，从 BITMAPINFOHEADER 开始，没有 BMP 文件头）。
/// 这里补一个 14 字节 BITMAPFILEHEADER，再交给 WPF 解码。
/// </summary>
public static class DibDecoder
{
    public static BitmapSource? Decode(byte[] dib, int decodeWidth = 0)
    {
        if (dib is null || dib.Length < 40) return null;

        try
        {
            int biSize = BitConverter.ToInt32(dib, 0);
            if (biSize <= 0 || biSize > dib.Length) return null;

            short biBitCount = BitConverter.ToInt16(dib, 14);
            int biClrUsed = BitConverter.ToInt32(dib, 32);

            int paletteEntries = biClrUsed > 0
                ? biClrUsed
                : (biBitCount <= 8 ? 1 << biBitCount : 0);

            int offBits = 14 + biSize + paletteEntries * 4;
            int fileSize = 14 + dib.Length;

            var bmp = new byte[fileSize];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
            BitConverter.GetBytes(offBits).CopyTo(bmp, 10);
            Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);

            using var ms = new MemoryStream(bmp);
            var frame = BitmapFrame.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            BitmapSource result = frame;
            if (decodeWidth > 0 && frame.PixelWidth > decodeWidth)
            {
                double scale = (double)decodeWidth / frame.PixelWidth;
                var tb = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
                tb.Freeze();
                result = tb;
            }

            if (result.CanFreeze) result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }
}
