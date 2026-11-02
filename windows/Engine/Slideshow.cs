using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace ImagesToVideo.Engine;

public sealed class SlideshowOptions
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 30;
    public double Duration { get; set; } = 4.0;            // seconds each photo is on screen, incl. its transitions
    public double TransitionDuration { get; set; } = 1.0;
    public string Transition { get; set; } = "auto";       // a Transitions.Names entry, "auto", "random" or "none"
    public string Motion { get; set; } = "auto";           // a PanZoom.Presets entry or "auto"
    public double Zoom { get; set; } = 0.15;
    public string Easing { get; set; } = "linear";
    public string Fit { get; set; } = "auto";
    public string? Audio { get; set; }
    public int Crf { get; set; } = 18;
    public int Seed { get; set; } = 1;

    public SlideshowOptions Clone() => (SlideshowOptions)MemberwiseClone();
}

/// <summary>
/// The timeline: photo i starts at i * step (step = duration - transition) and its first
/// transition-duration seconds overlap the end of photo i-1. Motion runs through the transitions.
/// </summary>
public sealed class Slideshow
{
    private readonly IReadOnlyList<string> _files;
    private readonly SlideshowOptions _o;
    private readonly Dictionary<int, MovingPhoto> _cache = new();
    private readonly Frame _a, _b, _out;

    public string[] Motions { get; }
    public string[] TransitionNames { get; }
    public double Step { get; }
    public double TotalSeconds { get; }
    public int FrameCount => (int)Math.Round(TotalSeconds * _o.Fps);

    public Slideshow(IReadOnlyList<string> files, SlideshowOptions options)
    {
        if (files.Count == 0) throw new ArgumentException("Add at least one photo.");
        _files = files;
        _o = options.Clone();
        if (_o.Transition == "none" || files.Count == 1) _o.TransitionDuration = 0;
        if (_o.TransitionDuration * 2 > _o.Duration) _o.TransitionDuration = _o.Duration / 2;
        // H.264 with 4:2:0 needs even dimensions.
        _o.Width -= _o.Width % 2;
        _o.Height -= _o.Height % 2;

        var rng = new Random(_o.Seed);
        Motions = Enumerable.Range(0, files.Count)
            .Select(i => _o.Motion == "auto" ? PanZoom.AutoCycle[i % PanZoom.AutoCycle.Length] : _o.Motion).ToArray();
        TransitionNames = Enumerable.Range(0, Math.Max(0, files.Count - 1)).Select(i => _o.Transition switch
        {
            "auto" => Transitions.AutoCycle[i % Transitions.AutoCycle.Length],
            "random" => Transitions.Names[rng.Next(Transitions.Names.Length)],
            _ => _o.Transition,
        }).ToArray();
        Step = _o.Duration - _o.TransitionDuration;
        TotalSeconds = Step * (files.Count - 1) + _o.Duration;
        _a = new Frame(_o.Width, _o.Height);
        _b = new Frame(_o.Width, _o.Height);
        _out = new Frame(_o.Width, _o.Height);
    }

    private MovingPhoto Photo(int i)
    {
        if (!_cache.TryGetValue(i, out var p))
        {
            foreach (var k in _cache.Keys.Where(k => k < i - 1).ToList()) _cache.Remove(k); // keep current + previous
            p = new MovingPhoto(_files[i], _o.Width, _o.Height, Motions[i], _o.Zoom, _o.Easing, _o.Fit);
            _cache[i] = p;
        }
        return p;
    }

    /// <summary>Renders the frame at time t (seconds). The returned buffer is reused by the next call.</summary>
    public Frame RenderAt(double t)
    {
        t = Math.Clamp(t, 0, TotalSeconds);
        int i = Step > 0 ? Math.Min((int)(t / Step), _files.Count - 1) : 0;
        double into = t - i * Step;
        if (i > 0 && into < _o.TransitionDuration)
        {
            Photo(i - 1).Render(_a, (t - (i - 1) * Step) / _o.Duration);
            Photo(i).Render(_b, into / _o.Duration);
            Transitions.Apply(TransitionNames[i - 1], _a, _b, into / _o.TransitionDuration, _out);
        }
        else
        {
            Photo(i).Render(_out, into / _o.Duration);
        }
        return _out;
    }

    /// <summary>Streams every frame into FFmpeg (H.264 MP4, optional AAC music with fades).</summary>
    public async Task ExportAsync(string ffmpeg, string output, IProgress<double>? progress, CancellationToken token)
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{_o.Width}x{_o.Height}", "-r", _o.Fps.ToString(inv), "-i", "-",
        };
        if (!string.IsNullOrEmpty(_o.Audio))
        {
            double fadeStart = Math.Max(0, TotalSeconds - 2);
            args.AddRange(new[] { "-i", _o.Audio, "-map", "0:v", "-map", "1:a", "-c:a", "aac", "-b:a", "192k",
                "-af", $"afade=t=in:d=0.5,afade=t=out:st={fadeStart.ToString("0.00", inv)}:d=2", "-shortest" });
        }
        args.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", _o.Crf.ToString(inv), "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", output });

        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        var errors = proc.StandardError.ReadToEndAsync();
        var stdin = proc.StandardInput.BaseStream;
        int n = FrameCount;
        try
        {
            for (int f = 0; f < n; f++)
            {
                token.ThrowIfCancellationRequested();
                var frame = await Task.Run(() => RenderAt((double)f / _o.Fps), token);
                await stdin.WriteAsync(frame.Pixels, token);
                if (f % _o.Fps == 0 || f == n - 1) progress?.Report((f + 1.0) / n);
            }
            stdin.Close();
            await proc.WaitForExitAsync(token);
            if (proc.ExitCode != 0) throw new InvalidOperationException("FFmpeg failed: " + (await errors).Trim());
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            proc.WaitForExit(2000);
            try { File.Delete(output); } catch { }
            throw;
        }
    }
}

public static class FFmpegLocator
{
    /// <summary>Finds ffmpeg.exe: a saved path, PATH, then the WinGet install locations.</summary>
    public static string? Find(string? saved)
    {
        if (!string.IsNullOrEmpty(saved) && File.Exists(saved)) return saved;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var links = Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        if (File.Exists(links)) return links;
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var pkg in Directory.EnumerateDirectories(packages, "*FFmpeg*"))
            {
                var hit = Directory.EnumerateFiles(pkg, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
        }
        return null;
    }
}
