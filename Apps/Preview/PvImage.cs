using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacShell.Apps.Preview;

/// <summary>
/// A picture as NoDitherOS keeps it: premultiplied 32-bit pixels, 0xAARRGGBB (the same bytes as WPF's Pbgra32).
/// </summary>
public sealed class PvImage
{
    public readonly int W, H;
    public readonly uint[] Px;

    public PvImage(int w, int h) { W = Math.Max(1, w); H = Math.Max(1, h); Px = new uint[W * H]; }
    public PvImage(int w, int h, uint[] px) { W = w; H = h; Px = px; }

    public PvImage Copy() => new(W, H, (uint[])Px.Clone());

    public BitmapSource ToBitmap()
    {
        var b = BitmapSource.Create(W, H, 96, 96, PixelFormats.Pbgra32, null, Px, W * 4);
        b.Freeze();
        return b;
    }

    public static PvImage FromPbgra(BitmapSource src)
    {
        var img = new PvImage(src.PixelWidth, src.PixelHeight);
        src.CopyPixels(img.Px, img.W * 4, 0);
        return img;
    }
}

/// <summary>Tools › Adjust Color settings (preview.h Adjust).</summary>
public struct Adjust : IEquatable<Adjust>
{
    public float Black, White, Gamma;     // levels: 0..1, 0..1, 0.2..5 (1 = none)
    public float Exposure;                // EV, -2..2
    public float Contrast;                // -1..1
    public float Highlights, Shadows;     // -1..1
    public float Saturation;              // 0..2 (1 = none)
    public float Temperature, Tint;       // -1..1
    public float Sepia;                   // 0..1
    public float Sharpness;               // -1..1

    public static Adjust Identity => new() { White = 1, Gamma = 1, Saturation = 1 };
    public bool IsIdentity => Equals(Identity);

    public bool Equals(Adjust o) => Black == o.Black && White == o.White && Gamma == o.Gamma && Exposure == o.Exposure && Contrast == o.Contrast
        && Highlights == o.Highlights && Shadows == o.Shadows && Saturation == o.Saturation && Temperature == o.Temperature && Tint == o.Tint
        && Sepia == o.Sepia && Sharpness == o.Sharpness;
    public override bool Equals(object obj) => obj is Adjust a && Equals(a);
    public override int GetHashCode() => HashCode.Combine(Black, White, Gamma, Exposure, Contrast, Highlights, Saturation, Sharpness);

    public float this[int i]
    {
        get => i switch { 0 => Exposure, 1 => Contrast, 2 => Highlights, 3 => Shadows, 4 => Saturation, 5 => Temperature, 6 => Tint, 7 => Sepia, _ => Sharpness };
        set
        {
            switch (i)
            {
                case 0: Exposure = value; break;
                case 1: Contrast = value; break;
                case 2: Highlights = value; break;
                case 3: Shadows = value; break;
                case 4: Saturation = value; break;
                case 5: Temperature = value; break;
                case 6: Tint = value; break;
                case 7: Sepia = value; break;
                default: Sharpness = value; break;
            }
        }
    }
}

/// <summary>
/// Preview's picture operations (NoDitherOS image_ops.c). Every result is computed exactly and rounded to the
/// nearest 8-bit level: no dithering, no noise.
/// </summary>
public static class PvOps
{
    public static uint Div255(uint x) { x += 128; return (x + (x >> 8)) >> 8; }

    static void Unpack(uint p, out float r, out float g, out float b, out float a)
    {
        uint al = p >> 24;
        a = al / 255f;
        if (al == 0) { r = g = b = 0; return; }
        float k = 1f / al;
        r = ((p >> 16) & 0xFF) * k;
        g = ((p >> 8) & 0xFF) * k;
        b = (p & 0xFF) * k;
    }

    static uint To8(float v)
    {
        v = v * 255f + 0.5f;
        return v <= 0 ? 0 : v >= 255 ? 255 : (uint)v;
    }

    static uint Pack(float r, float g, float b, uint a)
    {
        uint R = To8(r), G = To8(g), B = To8(b);
        return a << 24 | Div255(R * a) << 16 | Div255(G * a) << 8 | Div255(B * a);
    }

    // ------------------------------------------------------------------ colour

    static float SrgbToLin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    static float LinToSrgb(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;

    public static PvImage AdjustApply(PvImage src, Adjust a)
    {
        var d = new PvImage(src.W, src.H);
        if (a.IsIdentity) { Array.Copy(src.Px, d.Px, src.Px.Length); return d; }
        // per-channel part (levels, gamma, exposure) as a 256-entry table on the straight 8-bit value
        var lut = new float[256];
        float span = Math.Max(0.01f, a.White - a.Black), ev = MathF.Pow(2f, a.Exposure);
        for (int i = 0; i < 256; i++)
        {
            float v = (i / 255f - a.Black) / span;
            v = Math.Clamp(v, 0f, 1f);
            if (a.Gamma != 1) v = MathF.Pow(v, 1f / a.Gamma);
            if (a.Exposure != 0) v = LinToSrgb(Math.Min(1f, SrgbToLin(v) * ev));
            lut[i] = v;
        }
        float con = 1 + a.Contrast * (a.Contrast > 0 ? 1.2f : 0.8f);
        float tr = 1 + 0.18f * a.Temperature, tb = 1 - 0.18f * a.Temperature, tg = 1 - 0.14f * a.Tint;
        Parallel.For(0, src.H, y =>
        {
            int row = y * src.W;
            for (int x = 0; x < src.W; x++)
            {
                uint p = src.Px[row + x], al = p >> 24;
                if (al == 0) { d.Px[row + x] = 0; continue; }
                Unpack(p, out float r, out float g, out float b, out _);
                r = lut[To8(r)]; g = lut[To8(g)]; b = lut[To8(b)];
                float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                if (a.Shadows != 0)
                {
                    float w = (1 - l) * (1 - l) * (1 - l) * 0.6f * a.Shadows;
                    r += w * (a.Shadows > 0 ? 1 - r : r); g += w * (a.Shadows > 0 ? 1 - g : g); b += w * (a.Shadows > 0 ? 1 - b : b);
                }
                if (a.Highlights != 0)
                {
                    float w = l * l * l * 0.6f * a.Highlights;
                    r += w * (a.Highlights > 0 ? 1 - r : r); g += w * (a.Highlights > 0 ? 1 - g : g); b += w * (a.Highlights > 0 ? 1 - b : b);
                }
                if (a.Contrast != 0) { r = (r - 0.5f) * con + 0.5f; g = (g - 0.5f) * con + 0.5f; b = (b - 0.5f) * con + 0.5f; }
                if (a.Saturation != 1)
                {
                    l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    r = l + (r - l) * a.Saturation; g = l + (g - l) * a.Saturation; b = l + (b - l) * a.Saturation;
                }
                if (a.Temperature != 0 || a.Tint != 0) { r *= tr; g *= tg; b *= tb; }
                if (a.Sepia > 0)
                {
                    float sr = 0.393f * r + 0.769f * g + 0.189f * b, sg = 0.349f * r + 0.686f * g + 0.168f * b, sb = 0.272f * r + 0.534f * g + 0.131f * b;
                    r += (sr - r) * a.Sepia; g += (sg - g) * a.Sepia; b += (sb - b) * a.Sepia;
                }
                d.Px[row + x] = Pack(r, g, b, al);
            }
        });
        if (a.Sharpness != 0)
        {
            // unsharp mask (3x3 box) on the adjusted picture; negative = soften
            var t = d.Copy();
            float k = a.Sharpness * 1.5f;
            Parallel.For(1, Math.Max(1, d.H - 1), y =>
            {
                for (int x = 1; x < d.W - 1; x++)
                {
                    uint p = t.Px[y * t.W + x];
                    uint o = p & 0xFF000000u;
                    for (int sh = 0; sh < 24; sh += 8)
                    {
                        int sum = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++) sum += (int)((t.Px[(y + dy) * t.W + x + dx] >> sh) & 0xFF);
                        float c = (p >> sh) & 0xFF, blur = sum / 9f;
                        float v = c + k * (c - blur);
                        int vi = (int)(v + 0.5f);
                        vi = Math.Clamp(vi, 0, (int)(p >> 24));   // stays premultiplied
                        o |= (uint)vi << sh;
                    }
                    d.Px[y * d.W + x] = o;
                }
            });
        }
        return d;
    }

    /// <summary>R, G, B, luminance.</summary>
    public static uint[][] Histogram(PvImage s)
    {
        var h = new uint[4][];
        for (int i = 0; i < 4; i++) h[i] = new uint[256];
        foreach (var p in s.Px)
        {
            if ((p >> 24) == 0) continue;
            Unpack(p, out float r, out float g, out float b, out _);
            uint R = To8(r), G = To8(g), B = To8(b);
            h[0][R]++; h[1][G]++; h[2][B]++;
            h[3][(R * 54 + G * 183 + B * 19 + 128) >> 8]++;
        }
        return h;
    }

    public static void AutoLevels(PvImage src, ref Adjust a)
    {
        var h = Histogram(src);
        ulong total = 0;
        for (int i = 0; i < 256; i++) total += h[3][i];
        ulong cut = total / 200, acc = 0;   // 0.5 % at each end
        int lo, hi;
        for (lo = 0; lo < 255 && (acc += h[3][lo]) <= cut; lo++) { }
        acc = 0;
        for (hi = 255; hi > 0 && (acc += h[3][hi]) <= cut; hi--) { }
        if (hi - lo < 16) return;
        a.Black = lo / 255f;
        a.White = hi / 255f;
    }

    // ------------------------------------------------------------------ geometry

    public static PvImage Rotate(PvImage s, bool cw)
    {
        var d = new PvImage(s.H, s.W);
        for (int y = 0; y < s.H; y++)
            for (int x = 0; x < s.W; x++)
            {
                int nx = cw ? s.H - 1 - y : y, ny = cw ? x : s.W - 1 - x;
                d.Px[ny * d.W + nx] = s.Px[y * s.W + x];
            }
        return d;
    }

    public static PvImage Flip(PvImage s, bool horizontal)
    {
        var d = new PvImage(s.W, s.H);
        for (int y = 0; y < s.H; y++)
            for (int x = 0; x < s.W; x++)
            {
                int nx = horizontal ? s.W - 1 - x : x, ny = horizontal ? y : s.H - 1 - y;
                d.Px[ny * d.W + nx] = s.Px[y * s.W + x];
            }
        return d;
    }

    public static Int32Rect Intersect(Int32Rect r, int w, int h)
    {
        int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y), x1 = Math.Min(w, r.X + r.Width), y1 = Math.Min(h, r.Y + r.Height);
        return x1 > x0 && y1 > y0 ? new Int32Rect(x0, y0, x1 - x0, y1 - y0) : Int32Rect.Empty;
    }

    /// <summary>Exact ellipse coverage (0..255) of pixel (x, y) in a w × h box: 16 × 16 samples on the edge.</summary>
    public static byte OvalCoverage(int x, int y, int w, int h)
    {
        double rx = w / 2.0, ry = h / 2.0;
        double fx = (x + 0.5 - rx) / rx, fy = (y + 0.5 - ry) / ry, d = Math.Sqrt(fx * fx + fy * fy);
        double margin = 1.5 / Math.Min(rx, ry);
        if (d < 1 - margin) return 255;
        if (d > 1 + margin) return 0;
        int n = 0;
        for (int sy = 0; sy < 16; sy++)
            for (int sx = 0; sx < 16; sx++)
            {
                double px = (x + (sx + 0.5) / 16 - rx) / rx, py = (y + (sy + 0.5) / 16 - ry) / ry;
                if (px * px + py * py <= 1) n++;
            }
        return (byte)((n * 255 + 128) / 256);
    }

    public static PvImage Crop(PvImage s, Int32Rect r, bool oval)
    {
        r = Intersect(r, s.W, s.H);
        if (r.IsEmpty) return null;
        var d = new PvImage(r.Width, r.Height);
        for (int y = 0; y < r.Height; y++) Array.Copy(s.Px, (r.Y + y) * s.W + r.X, d.Px, y * d.W, r.Width);
        if (oval)
            for (int y = 0; y < r.Height; y++)
                for (int x = 0; x < r.Width; x++)
                {
                    uint cov = OvalCoverage(x, y, r.Width, r.Height), v = d.Px[y * d.W + x], o = 0;
                    for (int sh = 0; sh < 32; sh += 8) o |= Div255(((v >> sh) & 0xFF) * cov) << sh;
                    d.Px[y * d.W + x] = o;
                }
        return d;
    }

    /// <summary>Box filter (exact area average) down, bilinear on premultiplied values up.</summary>
    public static PvImage Resize(PvImage s, int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var d = new PvImage(w, h);
        if (w <= s.W && h <= s.H)
        {
            double sx = (double)s.W / w, sy = (double)s.H / h;
            Parallel.For(0, h, y =>
            {
                double y0 = y * sy, y1 = y0 + sy;
                Span<double> acc = stackalloc double[4];
                for (int x = 0; x < w; x++)
                {
                    double x0 = x * sx, x1 = x0 + sx, area = 0;
                    acc.Clear();
                    for (int yy = (int)y0; yy < s.H && yy < y1; yy++)
                    {
                        double wy = Math.Min(y1, yy + 1.0) - Math.Max(y0, yy);
                        for (int xx = (int)x0; xx < s.W && xx < x1; xx++)
                        {
                            double wx = Math.Min(x1, xx + 1.0) - Math.Max(x0, xx), wgt = wx * wy;
                            uint p = s.Px[yy * s.W + xx];
                            for (int k = 0; k < 4; k++) acc[k] += wgt * ((p >> (8 * k)) & 0xFF);
                            area += wgt;
                        }
                    }
                    uint o = 0;
                    for (int k = 0; k < 4; k++) o |= (uint)(acc[k] / area + 0.5) << (8 * k);
                    d.Px[y * w + x] = o;
                }
            });
            return d;
        }
        Parallel.For(0, h, y =>
        {
            float fy = (y + 0.5f) * s.H / h - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(fy), 0, s.H - 1), y1 = Math.Min(y0 + 1, s.H - 1);
            float ty = Math.Clamp(fy - y0, 0f, 1f);
            for (int x = 0; x < w; x++)
            {
                float fx = (x + 0.5f) * s.W / w - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(fx), 0, s.W - 1), x1 = Math.Min(x0 + 1, s.W - 1);
                float tx = Math.Clamp(fx - x0, 0f, 1f);
                uint a = s.Px[y0 * s.W + x0], b = s.Px[y0 * s.W + x1], c = s.Px[y1 * s.W + x0], e = s.Px[y1 * s.W + x1];
                uint o = 0;
                for (int k = 0; k < 32; k += 8)
                {
                    float v = (((a >> k) & 0xFF) * (1 - tx) + ((b >> k) & 0xFF) * tx) * (1 - ty) + (((c >> k) & 0xFF) * (1 - tx) + ((e >> k) & 0xFF) * tx) * ty;
                    o |= (uint)(v + 0.5f) << k;
                }
                d.Px[y * w + x] = o;
            }
        });
        return d;
    }

    /// <summary>Premultiplied "over": src on top of dst, exactly rounded.</summary>
    public static void Over(PvImage dst, uint[] src)
    {
        for (int i = 0; i < dst.Px.Length; i++)
        {
            uint s = src[i], sa = s >> 24;
            if (sa == 0) continue;
            if (sa == 255) { dst.Px[i] = s; continue; }
            uint d = dst.Px[i], k = 255 - sa, o = 0;
            for (int sh = 0; sh < 32; sh += 8) o |= Math.Min(255u, ((s >> sh) & 0xFF) + Div255(((d >> sh) & 0xFF) * k)) << sh;
            dst.Px[i] = o;
        }
    }

    // ------------------------------------------------------------------ polygon coverage (lasso)

    /// <summary>Pixels whose centre is inside the polygon (non-zero winding): 255, others 0.</summary>
    public static byte[] FillPolygon(IReadOnlyList<Point> pts, int w, int h, out Int32Rect bounds)
    {
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y), minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        int x0 = Math.Max(0, (int)Math.Floor(minX)), x1 = Math.Min(w, (int)Math.Ceiling(maxX));
        int y0 = Math.Max(0, (int)Math.Floor(minY)), y1 = Math.Min(h, (int)Math.Ceiling(maxY));
        bounds = Int32Rect.Empty;
        if (x1 <= x0 || y1 <= y0) return null;
        int bw = x1 - x0, bh = y1 - y0;
        var m = new byte[bw * bh];
        var xs = new List<(double x, int dir)>();
        for (int y = y0; y < y1; y++)
        {
            double cy = y + 0.5;
            xs.Clear();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                if (a.Y == b.Y) continue;
                bool up = a.Y < b.Y;
                double lo = up ? a.Y : b.Y, hi = up ? b.Y : a.Y;
                if (cy < lo || cy >= hi) continue;
                xs.Add((a.X + (cy - a.Y) * (b.X - a.X) / (b.Y - a.Y), up ? 1 : -1));
            }
            xs.Sort((p, q) => p.x.CompareTo(q.x));
            int wind = 0;
            for (int i = 0; i + 1 < xs.Count; i++)
            {
                wind += xs[i].dir;
                if (wind == 0) continue;
                int sx = Math.Max(x0, (int)Math.Ceiling(xs[i].x - 0.5)), ex = Math.Min(x1 - 1, (int)Math.Ceiling(xs[i + 1].x - 0.5) - 1);
                for (int x = sx; x <= ex; x++) m[(y - y0) * bw + x - x0] = 255;
            }
        }
        bounds = new Int32Rect(x0, y0, bw, bh);
        return m;
    }
}
