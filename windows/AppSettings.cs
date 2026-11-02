using System.IO;
using System.Text.Json;
using ImagesToVideo.Engine;

namespace ImagesToVideo;

/// <summary>Last-used options and the FFmpeg location, in %APPDATA%\ImagesToVideo\settings.json.</summary>
public sealed class AppSettings
{
    public SlideshowOptions Options { get; set; } = new();
    public string? FFmpegPath { get; set; }
    public string? LastFolder { get; set; }

    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImagesToVideo", "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var copy = new AppSettings { Options = Options.Clone(), FFmpegPath = FFmpegPath, LastFolder = LastFolder };
            copy.Options.Audio = null; // music is per project, not a preference
            File.WriteAllText(FilePath, JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
