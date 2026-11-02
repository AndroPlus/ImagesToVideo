using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImagesToVideo.Engine;
using Microsoft.Win32;

namespace ImagesToVideo;

public sealed class PhotoItem : INotifyPropertyChanged
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    private int _index;
    public int Index { get => _index; set { _index = value; PropertyChanged?.Invoke(this, new(nameof(Index))); } }
    private ImageSource? _thumb;
    public ImageSource? Thumbnail { get => _thumb; set { _thumb = value; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".jfif", ".heic" };
    private static readonly (string Label, int W, int H)[] Sizes =
    {
        ("1920 × 1080 (Full HD)", 1920, 1080), ("1280 × 720 (HD)", 1280, 720), ("3840 × 2160 (4K)", 3840, 2160),
        ("1080 × 1920 (vertical)", 1080, 1920), ("1080 × 1080 (square)", 1080, 1080),
    };
    private static readonly int[] FpsChoices = { 24, 30, 60 };

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<PhotoItem> _photos = new();
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / 15) };
    private Slideshow? _preview;
    private bool _previewDirty = true, _loading = true, _exporting;
    private double? _pendingTime;
    private CancellationTokenSource? _exportCts;
    private string? _lastOutput;
    private string? _ffmpeg;

    public MainWindow()
    {
        InitializeComponent();
        PhotoList.ItemsSource = _photos;
        _photos.CollectionChanged += (_, _) => { Renumber(); InvalidatePreview(); };

        foreach (var s in Sizes) SizeBox.Items.Add(s.Label);
        foreach (var f in FpsChoices) FpsBox.Items.Add($"{f} fps");
        TransitionBox.Items.Add("Auto (mix)");
        TransitionBox.Items.Add("Random");
        TransitionBox.Items.Add("None (cut)");
        foreach (var t in Transitions.Names) TransitionBox.Items.Add(Pretty(t));
        MotionBox.Items.Add("Auto (mix)");
        foreach (var m in PanZoom.Presets) MotionBox.Items.Add(m == "none" ? "None (still)" : Pretty(m));
        foreach (var e in PanZoom.Easings) EasingBox.Items.Add(Pretty(e));
        foreach (var f in new[] { "Auto", "Cover (crop to fill)", "Blur (whole photo)" }) FitBox.Items.Add(f);
        LoadOptions(_settings.Options);
        _loading = false;
        UpdateLabels();

        _ffmpeg = FFmpegLocator.Find(_settings.FFmpegPath);
        UpdateFFmpegText();
        _playTimer.Tick += (_, _) => PlayTick();
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && !(Keyboard.FocusedElement is TextBox)) { TogglePlay(); e.Handled = true; }
        };
        Closing += (_, e) =>
        {
            if (_exporting && MessageBox.Show(this, "An export is running. Cancel it and quit?", "ImagesToVideo",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) e.Cancel = true;
            else _exportCts?.Cancel();
            _settings.Options = ReadOptions();
            _settings.Save();
        };
        UpdateUi();
        Status(_ffmpeg != null ? "Add photos to get started." : "FFmpeg wasn't found. Install it (winget install Gyan.FFmpeg) or use Locate….");

        // Photos or folders passed on the command line ("Open with", shortcuts, scripts) are added at start.
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length > 0) Loaded += (_, _) => AddFiles(args);
    }

    private static string Pretty(string id) => char.ToUpper(id[0]) + id[1..].Replace('_', ' ');

    // ================================================================== options

    private void LoadOptions(SlideshowOptions o)
    {
        int si = Array.FindIndex(Sizes, s => s.W == o.Width && s.H == o.Height);
        SizeBox.SelectedIndex = si < 0 ? 0 : si;
        int fi = Array.IndexOf(FpsChoices, o.Fps);
        FpsBox.SelectedIndex = fi < 0 ? 1 : fi;
        DurationSlider.Value = o.Duration;
        TransitionSlider.Value = o.TransitionDuration;
        TransitionBox.SelectedIndex = o.Transition switch
        {
            "auto" => 0, "random" => 1, "none" => 2, _ => Math.Max(0, Array.IndexOf(Transitions.Names, o.Transition) + 3),
        };
        MotionBox.SelectedIndex = o.Motion == "auto" ? 0 : Math.Max(0, Array.IndexOf(PanZoom.Presets, o.Motion) + 1);
        ZoomSlider.Value = o.Zoom;
        EasingBox.SelectedIndex = Math.Max(0, Array.IndexOf(PanZoom.Easings, o.Easing));
        FitBox.SelectedIndex = o.Fit switch { "cover" => 1, "blur" => 2, _ => 0 };
        SetMusic(o.Audio);
    }

    private SlideshowOptions ReadOptions()
    {
        var size = Sizes[Math.Max(0, SizeBox.SelectedIndex)];
        return new SlideshowOptions
        {
            Width = size.W, Height = size.H,
            Fps = FpsChoices[Math.Max(0, FpsBox.SelectedIndex)],
            Duration = DurationSlider.Value,
            TransitionDuration = TransitionSlider.Value,
            Transition = TransitionBox.SelectedIndex switch
            {
                0 => "auto", 1 => "random", 2 => "none", var i => Transitions.Names[i - 3],
            },
            Motion = MotionBox.SelectedIndex <= 0 ? "auto" : PanZoom.Presets[MotionBox.SelectedIndex - 1],
            Zoom = ZoomSlider.Value,
            Easing = PanZoom.Easings[Math.Max(0, EasingBox.SelectedIndex)],
            Fit = FitBox.SelectedIndex switch { 1 => "cover", 2 => "blur", _ => "auto" },
            Audio = _music,
        };
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // Keep transitions at most half a photo long.
        if (TransitionSlider.Value > DurationSlider.Value / 2) TransitionSlider.Value = Math.Round(DurationSlider.Value / 2, 1);
        UpdateLabels();
        InvalidatePreview();
    }

    private void UpdateLabels()
    {
        DurationLabel.Text = $"Seconds per photo: {DurationSlider.Value:0.0}";
        TransitionDurationLabel.Text = $"Transition length: {TransitionSlider.Value:0.0} s";
        ZoomLabel.Text = $"Zoom amount: {ZoomSlider.Value * 100:0}%";
        TransitionSlider.IsEnabled = TransitionBox.SelectedIndex != 2;
        ZoomSlider.IsEnabled = EasingBox.IsEnabled = MotionBox.SelectedIndex != PanZoom.Presets.Length;
    }

    // ================================================================== photo list

    private async void AddFiles(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p).Where(IsImage).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(p) && IsImage(p))
                files.Add(p);
        }
        if (files.Count == 0) { Status("No supported images found."); return; }
        var added = files.Select(f => new PhotoItem { Path = f }).ToList();
        foreach (var item in added) _photos.Add(item);
        Status($"Added {added.Count} photo{(added.Count == 1 ? "" : "s")}.");
        foreach (var item in added)
        {
            try { item.Thumbnail = await Task.Run(() => LoadThumbnail(item.Path)); }
            catch { /* unreadable image: the export will report it */ }
        }
    }

    private static bool IsImage(string path) => ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static BitmapSource LoadThumbnail(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri(Path.GetFullPath(path));
        img.DecodePixelWidth = 128;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private void Renumber()
    {
        for (int i = 0; i < _photos.Count; i++) _photos[i].Index = i + 1;
        UpdateUi();
    }

    private void AddPhotos_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add photos", Multiselect = true, InitialDirectory = _settings.LastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Filter = "Images|" + string.Join(";", ImageExts.Select(x => "*" + x)) + "|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.LastFolder = Path.GetDirectoryName(dialog.FileNames[0]);
        AddFiles(dialog.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Add a folder of photos", InitialDirectory = _settings.LastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) };
        if (dialog.ShowDialog(this) != true) return;
        _settings.LastFolder = dialog.FolderName;
        AddFiles(new[] { dialog.FolderName });
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths);
    }

    private List<PhotoItem> Selected() => PhotoList.SelectedItems.Cast<PhotoItem>().OrderBy(p => _photos.IndexOf(p)).ToList();

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var ordered = delta < 0 ? sel : Enumerable.Reverse(sel).ToList();
        foreach (var p in ordered)
        {
            int i = _photos.IndexOf(p), j = i + delta;
            if (j < 0 || j >= _photos.Count || sel.Contains(_photos[j])) continue;
            _photos.Move(i, j);
        }
        foreach (var p in sel) PhotoList.SelectedItems.Add(p);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var sorted = _photos.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < sorted.Count; i++) _photos.Move(_photos.IndexOf(sorted[i]), i);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in Selected()) _photos.Remove(p);
    }

    private void PhotoList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) Remove_Click(sender, e);
    }

    private void PhotoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateUi();
        // Jump the preview to the middle of the selected photo.
        if (PhotoList.SelectedItem is PhotoItem p && _preview != null && !_playTimer.IsEnabled)
            Scrubber.Value = Math.Min(Scrubber.Maximum, _photos.IndexOf(p) * _preview.Step + ReadOptions().Duration / 2);
    }

    // ================================================================== preview

    private void InvalidatePreview()
    {
        if (_loading) return;
        _previewDirty = true;
        RequestPreview(Scrubber.Value);
    }

    private SlideshowOptions PreviewOptions()
    {
        var o = ReadOptions();
        double scale = Math.Min(960.0 / o.Width, 540.0 / o.Height);
        o.Width = (int)(o.Width * scale);
        o.Height = (int)(o.Height * scale);
        return o;
    }

    private async void RequestPreview(double time)
    {
        if (_photos.Count == 0)
        {
            PreviewImage.Source = null;
            PreviewHint.Visibility = Visibility.Visible;
            Scrubber.Maximum = 1;
            TimeText.Text = "0:00 / 0:00";
            PreviewInfo.Text = "";
            return;
        }
        _pendingTime = time;
        if (!await _renderGate.WaitAsync(0)) return; // a render is running; it will pick up _pendingTime
        try
        {
            while (_pendingTime is { } t)
            {
                _pendingTime = null;
                if (_previewDirty || _preview == null)
                {
                    _previewDirty = false;
                    var files = _photos.Select(p => p.Path).ToList();
                    var options = PreviewOptions();
                    _preview = await Task.Run(() => new Slideshow(files, options));
                    Scrubber.Maximum = _preview.TotalSeconds;
                    PreviewInfo.Text = $"{_photos.Count} photos · {_preview.TotalSeconds:0.#} s · motions: {string.Join(", ", _preview.Motions.Take(4).Select(Pretty))}" +
                                       (_preview.TransitionNames.Length > 0 ? $" · transitions: {string.Join(", ", _preview.TransitionNames.Take(4).Select(Pretty))}" : "");
                }
                var show = _preview;
                Engine.Frame frame;
                try { frame = await Task.Run(() => show.RenderAt(t)); }
                catch (Exception ex) { Status($"Preview failed: {ex.Message}"); break; }
                var bmp = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Width * 4);
                bmp.Freeze();
                PreviewImage.Source = bmp;
                PreviewHint.Visibility = Visibility.Collapsed;
                TimeText.Text = $"{Fmt(t)} / {Fmt(_preview.TotalSeconds)}";
            }
        }
        finally { _renderGate.Release(); }
    }

    private static string Fmt(double s) => $"{(int)s / 60}:{(int)s % 60:00}";

    private void Scrubber_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading) RequestPreview(e.NewValue);
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_photos.Count == 0) return;
        if (_playTimer.IsEnabled) { _playTimer.Stop(); PlayGlyph.Text = ""; return; }
        if (Scrubber.Value >= Scrubber.Maximum - 0.05) Scrubber.Value = 0;
        _playStarted = DateTime.Now - TimeSpan.FromSeconds(Scrubber.Value);
        _playTimer.Start();
        PlayGlyph.Text = "";
    }

    private DateTime _playStarted;

    private void PlayTick()
    {
        double t = (DateTime.Now - _playStarted).TotalSeconds;
        if (t >= Scrubber.Maximum) { t = Scrubber.Maximum; TogglePlay(); }
        Scrubber.Value = t; // renders are coalesced, so slow frames are skipped rather than queued
    }

    // ================================================================== music / ffmpeg

    private string? _music;

    private void SetMusic(string? path)
    {
        _music = string.IsNullOrEmpty(path) || !File.Exists(path) ? null : path;
        MusicText.Text = _music != null ? Path.GetFileName(_music) : "None";
        MusicText.ToolTip = _music;
    }

    private void ChooseMusic_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose music", Filter = "Audio|*.mp3;*.m4a;*.aac;*.wav;*.flac;*.ogg;*.wma|All files|*.*" };
        if (dialog.ShowDialog(this) == true) SetMusic(dialog.FileName);
    }

    private void ClearMusic_Click(object sender, RoutedEventArgs e) => SetMusic(null);

    private void LocateFFmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Locate ffmpeg.exe", Filter = "ffmpeg.exe|ffmpeg.exe|Programs|*.exe" };
        if (dialog.ShowDialog(this) != true) return;
        _ffmpeg = _settings.FFmpegPath = dialog.FileName;
        _settings.Save();
        UpdateFFmpegText();
        UpdateUi();
    }

    private void UpdateFFmpegText()
    {
        FFmpegText.Text = _ffmpeg != null ? "Found ✓" : "Not found";
        FFmpegText.ToolTip = _ffmpeg ?? "Install with: winget install Gyan.FFmpeg";
    }

    // ================================================================== export

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_photos.Count == 0 || _exporting) return;
        if (_ffmpeg == null) { LocateFFmpeg_Click(sender, e); if (_ffmpeg == null) return; }
        var dialog = new SaveFileDialog
        {
            Title = "Export video", Filter = "MP4 video|*.mp4", DefaultExt = ".mp4",
            FileName = $"Slideshow {DateTime.Now:yyyy-MM-dd HHmm}.mp4",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        if (dialog.ShowDialog(this) != true) return;
        await ExportTo(dialog.FileName);
    }

    /// <summary>Renders the full-quality video to path, with progress and cancel.</summary>
    private async Task ExportTo(string path)
    {
        _playTimer.Stop();
        PlayGlyph.Text = "";
        _exporting = true;
        _exportCts = new CancellationTokenSource();
        UpdateUi();
        OpenButton.Visibility = ShowButton.Visibility = Visibility.Collapsed;
        Progress.Visibility = CancelButton.Visibility = Visibility.Visible;
        Progress.Value = 0;
        var files = _photos.Select(p => p.Path).ToList();
        var options = ReadOptions();
        var watch = Stopwatch.StartNew();
        try
        {
            var show = await Task.Run(() => new Slideshow(files, options));
            Status($"Exporting {options.Width}×{options.Height} · {show.TotalSeconds:0.#} s…");
            var progress = new Progress<double>(v =>
            {
                Progress.Value = v;
                double eta = v > 0.02 ? watch.Elapsed.TotalSeconds * (1 - v) / v : double.NaN;
                Status($"Exporting… {v * 100:0}%" + (double.IsNaN(eta) ? "" : $" · about {Math.Ceiling(eta)} s left"));
            });
            await show.ExportAsync(_ffmpeg!, path, progress, _exportCts.Token);
            _lastOutput = path;
            Status($"Exported {Path.GetFileName(path)} ({show.TotalSeconds:0.#} s of video in {watch.Elapsed.TotalSeconds:0} s).");
            OpenButton.Visibility = ShowButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { Status("Export canceled."); }
        catch (Exception ex) { Status($"Export failed: {ex.Message}"); }
        finally
        {
            _exporting = false;
            Progress.Visibility = CancelButton.Visibility = Visibility.Collapsed;
            UpdateUi();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _exportCts?.Cancel();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutput != null) Process.Start(new ProcessStartInfo(_lastOutput) { UseShellExecute = true });
    }

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutput != null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true });
    }

    // ================================================================== chrome

    private void Status(string text) => StatusText.Text = text;

    private void UpdateUi()
    {
        bool has = _photos.Count > 0, sel = PhotoList.SelectedItems.Count > 0;
        EmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        ExportButton.IsEnabled = has && !_exporting;
        UpButton.IsEnabled = DownButton.IsEnabled = RemoveButton.IsEnabled = sel && !_exporting;
        SortButton.IsEnabled = _photos.Count > 1 && !_exporting;
        PlayButton.IsEnabled = has;
    }
}
