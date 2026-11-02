namespace ImagesToVideo.Engine;

/// <summary>A BGRA32 pixel buffer (stride = Width * 4).</summary>
public sealed class Frame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public Frame(int width, int height, byte[]? pixels = null)
    {
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
    }

    /// <summary>
    /// Resamples the source rectangle (x, y, w, h in source pixels, fractional allowed) into this frame with
    /// bilinear filtering. Sub-pixel boxes are what keep slow pans from stepping.
    /// </summary>
    public void SampleFrom(Frame src, double x, double y, double w, double h)
    {
        double sx = w / Width, sy = h / Height;
        int sw = src.Width, sh = src.Height;
        byte[] s = src.Pixels, d = Pixels;
        int dw = Width;
        Parallel.For(0, Height, row =>
        {
            double fy = Math.Clamp(y + (row + 0.5) * sy - 0.5, 0, sh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, sh - 1);
            double ty = fy - y0;
            int o = row * dw * 4;
            for (int col = 0; col < dw; col++)
            {
                double fx = Math.Clamp(x + (col + 0.5) * sx - 0.5, 0, sw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, sw - 1);
                double tx = fx - x0;
                int a = (y0 * sw + x0) * 4, b = (y0 * sw + x1) * 4, c = (y1 * sw + x0) * 4, e = (y1 * sw + x1) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    double top = s[a + ch] + (s[b + ch] - s[a + ch]) * tx;
                    double bot = s[c + ch] + (s[e + ch] - s[c + ch]) * tx;
                    d[o + ch] = (byte)(top + (bot - top) * ty + 0.5);
                }
                d[o + 3] = 255;
                o += 4;
            }
        });
    }

    /// <summary>Area-averaging downscale (box filter) to the given size; good quality for large reductions.</summary>
    public Frame Downscale(int w, int h)
    {
        var dst = new Frame(w, h);
        double rx = (double)Width / w, ry = (double)Height / h;
        byte[] s = Pixels, d = dst.Pixels;
        int sw = Width;
        Parallel.For(0, h, row =>
        {
            int ya = (int)(row * ry), yb = Math.Max(ya + 1, Math.Min(Height, (int)((row + 1) * ry)));
            for (int col = 0; col < w; col++)
            {
                int xa = (int)(col * rx), xb = Math.Max(xa + 1, Math.Min(sw, (int)((col + 1) * rx)));
                long b = 0, g = 0, r = 0, n = 0;
                for (int yy = ya; yy < yb; yy++)
                {
                    int i = (yy * sw + xa) * 4;
                    for (int xx = xa; xx < xb; xx++, i += 4) { b += s[i]; g += s[i + 1]; r += s[i + 2]; n++; }
                }
                int o = (row * w + col) * 4;
                d[o] = (byte)(b / n); d[o + 1] = (byte)(g / n); d[o + 2] = (byte)(r / n); d[o + 3] = 255;
            }
        });
        return dst;
    }

    /// <summary>Resize to exactly w x h: box filter when shrinking a lot, bilinear otherwise.</summary>
    public Frame Resize(int w, int h)
    {
        if (Width >= w * 2 && Height >= h * 2) return Downscale(w, h);
        var dst = new Frame(w, h);
        dst.SampleFrom(this, 0, 0, Width, Height);
        return dst;
    }

    public Frame Crop(int x, int y, int w, int h)
    {
        var dst = new Frame(w, h);
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(Pixels, ((y + row) * Width + x) * 4, dst.Pixels, row * w * 4, w * 4);
        return dst;
    }

    public void Paste(Frame src, int x, int y)
    {
        for (int row = 0; row < src.Height; row++)
        {
            int ty = y + row;
            if (ty < 0 || ty >= Height) continue;
            int sx = Math.Max(0, -x), tx = Math.Max(0, x), n = Math.Min(src.Width - sx, Width - tx);
            if (n > 0) Buffer.BlockCopy(src.Pixels, (row * src.Width + sx) * 4, Pixels, (ty * Width + tx) * 4, n * 4);
        }
    }

    /// <summary>Three passes of a horizontal+vertical box blur, roughly Gaussian.</summary>
    public void Blur(int radius)
    {
        var tmp = new byte[Pixels.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxPass(Pixels, tmp, Width, Height, radius, horizontal: true);
            BoxPass(tmp, Pixels, Width, Height, radius, horizontal: false);
        }
    }

    private static void BoxPass(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        Parallel.For(0, lines, line =>
        {
            for (int ch = 0; ch < 4; ch++)
            {
                int Idx(int k) => (horizontal ? line * w + k : k * w + line) * 4 + ch;
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += src[Idx(Math.Clamp(k, 0, len - 1))];
                for (int k = 0; k < len; k++)
                {
                    dst[Idx(k)] = (byte)(sum / (2 * r + 1));
                    sum += src[Idx(Math.Min(k + r + 1, len - 1))] - src[Idx(Math.Max(k - r, 0))];
                }
            }
        });
    }

    public void Darken(double factor)
    {
        var p = Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            p[i] = (byte)(p[i] * factor); p[i + 1] = (byte)(p[i + 1] * factor); p[i + 2] = (byte)(p[i + 2] * factor);
        }
    }
}
