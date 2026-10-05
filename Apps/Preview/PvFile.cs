using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacShell.Apps.Preview;

/// <summary>
/// Reading and writing pictures. Every conversion is done here with exact rounding (16-bit pictures are rounded
/// to the nearest 8-bit level, never dithered); Windows' codecs only decode and encode.
/// </summary>
public static class PvFile
{
    public static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".jfif", ".wdp", ".jxr", ".avif" };

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());

    public static PvImage Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = dec.Frames[0];
        var img = Convert(frame);
        int orient = Orientation(frame);
        return orient > 1 ? Orient(img, orient) : img;
    }

    public static PvImage Convert(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var img = new PvImage(w, h);
        var f = src.Format;
        if (f == PixelFormats.Pbgra32) { src.CopyPixels(img.Px, w * 4, 0); return img; }
        if (f == PixelFormats.Rgb48 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64 || f == PixelFormats.Gray16)
        {
            int ch = f == PixelFormats.Gray16 ? 1 : f == PixelFormats.Rgb48 ? 3 : 4;
            var row = new ushort[w * ch];
            for (int y = 0; y < h; y++)
            {
                src.CopyPixels(new Int32Rect(0, y, w, 1), row, w * ch * 2, 0);
                for (int x = 0; x < w; x++)
                {
                    uint r, g, b, a = 255;
                    if (ch == 1) r = g = b = To8(row[x]);
                    else { r = To8(row[x * ch]); g = To8(row[x * ch + 1]); b = To8(row[x * ch + 2]); }
                    if (ch == 4)
                    {
                        a = To8(row[x * 4 + 3]);
                        if (f == PixelFormats.Prgba64 && row[x * 4 + 3] > 0)
                        {
                            // premultiplied 16-bit: straight colour first, then our own exact 8-bit premultiply
                            double k = 65535.0 / row[x * 4 + 3];
                            r = To8((ushort)Math.Min(65535, Math.Round(row[x * 4] * k)));
                            g = To8((ushort)Math.Min(65535, Math.Round(row[x * 4 + 1] * k)));
                            b = To8((ushort)Math.Min(65535, Math.Round(row[x * 4 + 2] * k)));
                        }
                    }
                    img.Px[y * w + x] = Premul(r, g, b, a);
                }
            }
            return img;
        }
        // everything else becomes straight BGRA first (palette lookups, gray and 24-bit expansion: no rounding involved)
        BitmapSource bgra = f == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        bgra.CopyPixels(img.Px, w * 4, 0);
        for (int i = 0; i < img.Px.Length; i++)
        {
            uint p = img.Px[i], a = p >> 24;
            if (a == 255) continue;
            img.Px[i] = Premul((p >> 16) & 0xFF, (p >> 8) & 0xFF, p & 0xFF, a);
        }
        return img;
    }

    static uint To8(ushort v) => (uint)((v * 255 + 32767) / 65535);
    static uint Premul(uint r, uint g, uint b, uint a) => a << 24 | PvOps.Div255(r * a) << 16 | PvOps.Div255(g * a) << 8 | PvOps.Div255(b * a);

    /// <summary>EXIF orientation (1 = as stored).</summary>
    static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata md) return 1;
            foreach (var q in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}", "System.Photo.Orientation" })
            {
                try
                {
                    object v = q.StartsWith("System.") ? md.GetQuery(q) : md.ContainsQuery(q) ? md.GetQuery(q) : null;
                    if (v != null) return System.Convert.ToInt32(v);
                }
                catch { }
            }
        }
        catch { }
        return 1;
    }

    static PvImage Orient(PvImage img, int o) => o switch
    {
        2 => PvOps.Flip(img, true),
        3 => PvOps.Rotate(PvOps.Rotate(img, true), true),
        4 => PvOps.Flip(img, false),
        5 => PvOps.Flip(PvOps.Rotate(img, true), true),
        6 => PvOps.Rotate(img, true),
        7 => PvOps.Flip(PvOps.Rotate(img, false), true),
        8 => PvOps.Rotate(img, false),
        _ => img,
    };

    // ------------------------------------------------------------------ writing

    /// <summary>Straight (un-premultiplied) BGRA, exactly rounded; <paramref name="onWhite"/> flattens onto white (JPEG).</summary>
    static uint[] Straight(PvImage img, bool onWhite)
    {
        var o = new uint[img.Px.Length];
        for (int i = 0; i < o.Length; i++)
        {
            uint p = img.Px[i], a = p >> 24;
            if (onWhite)
            {
                uint k = 255 - a, q = 0xFF000000u;
                for (int sh = 0; sh < 24; sh += 8) q |= Math.Min(255u, ((p >> sh) & 0xFF) + k) << sh;
                o[i] = q;
            }
            else if (a == 255 || a == 0) o[i] = a == 0 ? 0 : p;
            else
            {
                uint q = a << 24;
                for (int sh = 0; sh < 24; sh += 8) q |= Math.Min(255u, ((((p >> sh) & 0xFF) * 255) + a / 2) / a) << sh;
                o[i] = q;
            }
        }
        return o;
    }

    public static bool HasAlpha(PvImage img)
    {
        foreach (var p in img.Px) if ((p >> 24) != 255) return true;
        return false;
    }

    public static byte[] EncodePng(PvImage img)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(img.W, img.H, 72, 72, PixelFormats.Bgra32, null, Straight(img, false), img.W * 4)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static byte[] EncodeJpeg(PvImage img, int quality)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        var px = Straight(img, true);
        // JPEG has no alpha: hand the encoder 24-bit colour (an exact repack)
        var rgb = new byte[img.W * img.H * 3];
        for (int i = 0; i < px.Length; i++) { rgb[i * 3] = (byte)px[i]; rgb[i * 3 + 1] = (byte)(px[i] >> 8); rgb[i * 3 + 2] = (byte)(px[i] >> 16); }
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(img.W, img.H, 72, 72, PixelFormats.Bgr24, null, rgb, img.W * 3)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Opaque pictures as 24-bit colour, others with their alpha: both exact repacks, nothing rounded.</summary>
    static BitmapSource Lossless(PvImage img)
    {
        var px = Straight(img, false);
        if (HasAlpha(img)) return BitmapSource.Create(img.W, img.H, 72, 72, PixelFormats.Bgra32, null, px, img.W * 4);
        var rgb = new byte[img.W * img.H * 3];
        for (int i = 0; i < px.Length; i++) { rgb[i * 3] = (byte)px[i]; rgb[i * 3 + 1] = (byte)(px[i] >> 8); rgb[i * 3 + 2] = (byte)(px[i] >> 16); }
        return BitmapSource.Create(img.W, img.H, 72, 72, PixelFormats.Bgr24, null, rgb, img.W * 3);
    }

    public static byte[] EncodeBmp(PvImage img)
    {
        var enc = new BmpBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(Lossless(img)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static byte[] EncodeTiff(PvImage img)
    {
        var enc = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };   // lossless
        enc.Frames.Add(BitmapFrame.Create(Lossless(img)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ the clipboard

    public static void CopyToClipboard(PvImage img, string name)
    {
        try
        {
            var data = new DataObject();
            var png = new MemoryStream(EncodePng(img));
            data.SetData("PNG", png);
            data.SetImage(BitmapSource.Create(img.W, img.H, 96, 96, PixelFormats.Bgra32, null, Straight(img, false), img.W * 4));
            if (!string.IsNullOrEmpty(name)) data.SetText(name);
            Clipboard.SetDataObject(data, true);
        }
        catch { }
    }

    public static PvImage FromClipboard()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return null;
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream ms)
            {
                ms.Position = 0;
                var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return Convert(dec.Frames[0]);
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource bs) return Convert(bs);
            if (Clipboard.ContainsFileDropList())
                foreach (string f in Clipboard.GetFileDropList())
                    if (IsImage(f)) return Load(f);
        }
        catch { }
        return null;
    }

    public static bool ClipboardHasPicture()
    {
        try { return Clipboard.ContainsImage() || Clipboard.ContainsData("PNG"); } catch { return false; }
    }
}
