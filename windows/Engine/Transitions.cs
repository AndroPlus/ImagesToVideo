namespace ImagesToVideo.Engine;

/// <summary>
/// The seven transitions from the Python engine, ported 1:1. Each writes the blend of outgoing frame a and
/// incoming frame b at progress p (0..1) into dst.
/// </summary>
public static class Transitions
{
    public static readonly string[] Names = { "fade", "fade_black", "wipe", "push", "cross_zoom", "circle", "pixelate" };
    public static readonly string[] AutoCycle = { "fade", "cross_zoom", "wipe", "circle", "push", "fade_black" };

    private static double Smooth(double p) => p * p * (3 - 2 * p);

    public static void Apply(string name, Frame a, Frame b, double p, Frame dst)
    {
        switch (name)
        {
            case "fade_black":
                if (p < 0.5) Mix(a, null, Smooth(p * 2), dst);
                else Mix(null, b, Smooth(p * 2 - 1), dst);
                break;
            case "wipe": Wipe(a, b, p, dst); break;
            case "push": Push(a, b, p, dst); break;
            case "cross_zoom": CrossZoom(a, b, p, dst); break;
            case "circle": Circle(a, b, p, dst); break;
            case "pixelate": Pixelate(a, b, p, dst); break;
            default: Mix(a, b, Smooth(p), dst); break;
        }
    }

    /// <summary>Uniform blend toward b with weight w; a null frame stands for black.</summary>
    private static void Mix(Frame? a, Frame? b, double w, Frame dst)
    {
        byte[]? pa = a?.Pixels, pb = b?.Pixels;
        byte[] d = dst.Pixels;
        int wi = (int)(w * 256);
        Parallel.For(0, dst.Height, row =>
        {
            int start = row * dst.Width * 4, end = start + dst.Width * 4;
            for (int i = start; i < end; i++)
            {
                int va = pa?[i] ?? 0, vb = pb?[i] ?? 0;
                d[i] = (byte)(va + (((vb - va) * wi) >> 8));
            }
        });
    }

    private static void MaskMix(Frame a, Frame b, Frame dst, Func<int, int, double> weight)
    {
        byte[] pa = a.Pixels, pb = b.Pixels, d = dst.Pixels;
        int w = dst.Width;
        Parallel.For(0, dst.Height, row =>
        {
            for (int col = 0, i = row * w * 4; col < w; col++, i += 4)
            {
                int wi = (int)(weight(col, row) * 256);
                for (int ch = 0; ch < 4; ch++) d[i + ch] = (byte)(pa[i + ch] + (((pb[i + ch] - pa[i + ch]) * wi) >> 8));
            }
        });
    }

    private static void Wipe(Frame a, Frame b, double p, Frame dst)
    {
        const double feather = 0.06;
        double edge = Smooth(p) * (1 + feather) - feather;
        int w = dst.Width;
        MaskMix(a, b, dst, (col, _) => Math.Clamp((edge + feather - (double)col / (w - 1)) / feather, 0, 1));
    }

    private static void Push(Frame a, Frame b, double p, Frame dst)
    {
        int w = dst.Width, shift = (int)Math.Round(Smooth(p) * w);
        for (int row = 0; row < dst.Height; row++)
        {
            int o = row * w * 4;
            Buffer.BlockCopy(a.Pixels, o + shift * 4, dst.Pixels, o, (w - shift) * 4);
            Buffer.BlockCopy(b.Pixels, o, dst.Pixels, o + (w - shift) * 4, shift * 4);
        }
    }

    [ThreadStatic] private static Frame? _za, _zb;

    private static void CrossZoom(Frame a, Frame b, double p, Frame dst)
    {
        const double strength = 0.35;
        double s = Smooth(p);
        _za = _za is { } fa && fa.Width == dst.Width && fa.Height == dst.Height ? fa : new Frame(dst.Width, dst.Height);
        _zb = _zb is { } fb && fb.Width == dst.Width && fb.Height == dst.Height ? fb : new Frame(dst.Width, dst.Height);
        Zoom(a, 1 + strength * s, _za);
        Zoom(b, 1 + strength * (1 - s), _zb);
        Mix(_za, _zb, s, dst);
    }

    private static void Zoom(Frame src, double factor, Frame dst)
    {
        double cw = src.Width / factor, ch = src.Height / factor;
        dst.SampleFrom(src, (src.Width - cw) / 2, (src.Height - ch) / 2, cw, ch);
    }

    private static void Circle(Frame a, Frame b, double p, Frame dst)
    {
        const double feather = 0.04;
        int w = dst.Width, h = dst.Height;
        double radius = Smooth(p) * (Math.Sqrt((double)w / h * w / h + 1) / 2 + feather);
        MaskMix(a, b, dst, (col, row) =>
        {
            double dx = (col - w / 2.0) / h, dy = (row - h / 2.0) / h;
            return Math.Clamp((radius - Math.Sqrt(dx * dx + dy * dy)) / feather, 0, 1);
        });
    }

    private static void Pixelate(Frame a, Frame b, double p, Frame dst)
    {
        const int maxBlock = 48;
        var src = p < 0.5 ? a : b;
        double level = 1 - Math.Abs(p * 2 - 1);
        int block = Math.Max(1, (int)Math.Round(1 + level * (maxBlock - 1)));
        int w = dst.Width, h = dst.Height;
        Parallel.For(0, h, row =>
        {
            int sy = Math.Min(h - 1, row / block * block + block / 2);
            for (int col = 0; col < w; col++)
            {
                int sx = Math.Min(w - 1, col / block * block + block / 2);
                Buffer.BlockCopy(src.Pixels, (sy * w + sx) * 4, dst.Pixels, (row * w + col) * 4, 4);
            }
        });
    }
}
