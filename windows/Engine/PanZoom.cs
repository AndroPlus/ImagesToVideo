using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImagesToVideo.Engine;

/// <summary>
/// Pan &amp; Zoom ("Ken Burns"). Same model as the Python engine and the REA findings: a preset is a crop
/// rectangle (x, y, w, h) in fractions of the picture, keyframed at the start and end of the clip.
/// </summary>
public static class PanZoom
{
    public static readonly string[] Presets = { "zoom_in", "zoom_out", "pan_left", "pan_right", "zoom_to_bottom_right", "none" };
    public static readonly string[] AutoCycle = { "zoom_in", "pan_right", "zoom_out", "pan_left", "zoom_to_bottom_right" };
    public static readonly string[] Easings = { "linear", "smooth", "sine" };

    public readonly record struct Rect(double X, double Y, double W, double H);

    public static (Rect Start, Rect End) PresetRects(string name, double z)
    {
        double s = 1 - z;
        Rect full = new(0, 0, 1, 1), centre = new(z / 2, z / 2, s, s);
        return name switch
        {
            "zoom_in" => (full, centre),
            "zoom_out" => (centre, full),
            "pan_left" => (new Rect(z, z / 2, s, s), new Rect(0, z / 2, s, s)),
            "pan_right" => (new Rect(0, z / 2, s, s), new Rect(z, z / 2, s, s)),
            "zoom_to_bottom_right" => (full, new Rect(z, z, s, s)),
            _ => (full, full),
        };
    }

    public static double Ease(string easing, double t) => easing switch
    {
        "smooth" => t * t * (3 - 2 * t),
        "sine" => 0.5 - 0.5 * Math.Cos(Math.PI * t),
        _ => t,
    };
}

/// <summary>One photo prepared for animation, on a canvas big enough that the most zoomed-in rectangle
/// still has output resolution.</summary>
public sealed class MovingPhoto
{
    private readonly Frame _canvas;
    private readonly PanZoom.Rect _start, _end;
    private readonly string _easing;

    public MovingPhoto(string path, int outW, int outH, string preset, double zoom, string easing, string fit)
    {
        (_start, _end) = PanZoom.PresetRects(preset, zoom);
        _easing = easing;
        double scale = preset == "none" ? 1 : 1 / (1 - zoom);
        int cw = (int)Math.Ceiling(outW * scale), ch = (int)Math.Ceiling(outH * scale);
        _canvas = PhotoLoader.Fit(PhotoLoader.Load(path, Math.Max(cw, ch) * 2), cw, ch, fit);
    }

    public void Render(Frame target, double t)
    {
        double k = PanZoom.Ease(_easing, Math.Clamp(t, 0, 1));
        double L(double a, double b) => a + (b - a) * k;
        double x = L(_start.X, _end.X), y = L(_start.Y, _end.Y), w = L(_start.W, _end.W), h = L(_start.H, _end.H);
        target.SampleFrom(_canvas, x * _canvas.Width, y * _canvas.Height, w * _canvas.Width, h * _canvas.Height);
    }
}

public static class PhotoLoader
{
    /// <summary>Decodes an image with EXIF rotation applied, limited to about maxSide pixels on the long side.</summary>
    public static Frame Load(string path, int maxSide)
    {
        var frame = BitmapFrame.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        int rawW = frame.PixelWidth, rawH = frame.PixelHeight;
        var rotation = Rotation.Rotate0;
        try
        {
            if (frame.Metadata is BitmapMetadata md && md.GetQuery("System.Photo.Orientation") is ushort o)
                rotation = o switch { 3 => Rotation.Rotate180, 6 => Rotation.Rotate90, 8 => Rotation.Rotate270, _ => Rotation.Rotate0 };
        }
        catch { /* formats without EXIF */ }

        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri(Path.GetFullPath(path));
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (Math.Max(rawW, rawH) > maxSide)
        {
            if (rawW >= rawH) img.DecodePixelWidth = maxSide; else img.DecodePixelHeight = maxSide;
        }
        img.Rotation = rotation;
        img.EndInit();

        var bgra = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        var result = new Frame(bgra.PixelWidth, bgra.PixelHeight);
        bgra.CopyPixels(result.Pixels, result.Width * 4, 0);
        // Composite any transparency onto black so PNG cut-outs don't show garbage.
        var p = result.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            int a = p[i + 3];
            if (a == 255) continue;
            p[i] = (byte)(p[i] * a / 255); p[i + 1] = (byte)(p[i + 1] * a / 255); p[i + 2] = (byte)(p[i + 2] * a / 255); p[i + 3] = 255;
        }
        return result;
    }

    /// <summary>
    /// cover: crop to fill. blur: whole picture over a blurred, darkened copy of itself.
    /// auto: cover unless aspect ratios differ by more than 25% (e.g. portrait photos in a landscape video).
    /// </summary>
    public static Frame Fit(Frame img, int w, int h, string fit)
    {
        double target = (double)w / h, actual = (double)img.Width / img.Height;
        if (fit == "auto") fit = Math.Max(target, actual) / Math.Min(target, actual) > 1.25 ? "blur" : "cover";
        if (fit == "cover") return Cover(img, w, h, 0.45);

        var bg = Cover(img, Math.Max(1, w / 8), Math.Max(1, h / 8), 0.5);
        bg.Blur(2);
        bg.Darken(0.55);
        var canvas = bg.Resize(w, h);
        double s = Math.Min((double)w / img.Width, (double)h / img.Height);
        int fw = Math.Max(1, (int)Math.Round(img.Width * s)), fh = Math.Max(1, (int)Math.Round(img.Height * s));
        canvas.Paste(img.Resize(fw, fh), (w - fw) / 2, (h - fh) / 2);
        return canvas;
    }

    private static Frame Cover(Frame img, int w, int h, double centreY)
    {
        double target = (double)w / h;
        int cw = img.Width, ch = img.Height;
        if ((double)img.Width / img.Height > target) cw = (int)Math.Round(img.Height * target);
        else ch = (int)Math.Round(img.Width / target);
        int x = (img.Width - cw) / 2, y = (int)Math.Round((img.Height - ch) * centreY);
        return img.Crop(x, y, cw, ch).Resize(w, h);
    }
}
