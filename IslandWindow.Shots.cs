using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Services;

namespace DynamicIsland;

// Smart screenshots: after Win+Shift+S, PrtScn or Win+PrtScn the island shows the picture with what to do with it:
// Salvar (or Copiar, when it only went to a file), Celular, Ler texto (Windows' OCR) and Traduzir. A picture that
// lands both in the clipboard and in the Screenshots folder (the Snipping Tool does both) is one card.
public partial class IslandWindow
{
    sealed class ScreenShot
    {
        public required BitmapSource Image { get; init; }
        public string? Path { get; set; }
        public bool InClipboard { get; set; }
        public bool IsPrint { get; set; }
        public DateTime At { get; } = DateTime.Now;
    }

    ScreenShot? _shot, _shotShown;
    FileSystemWatcher? _shotWatch;
    DateTime _ownImageUntil;

    static readonly string[] CaptureTools = ["ScreenClippingHost", "SnippingTool", "ScreenSketch", "ShareX", "Greenshot", "Lightshot", "Flameshot"];

    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    /// <summary>(Re)starts watching the Screenshots folder, where Win+PrtScn and the Snipping Tool save.</summary>
    public void ApplySmartShots()
    {
        _shotWatch?.Dispose();
        _shotWatch = null;
        if (!_settings.SmartShots || SnapshotMode || ScreenshotsFolder() is not { } folder || !Directory.Exists(folder)) return;
        try
        {
            _shotWatch = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName };
            _shotWatch.Filters.Add("*.png");
            _shotWatch.Filters.Add("*.jpg");
            _shotWatch.Created += (_, e) => Dispatcher.InvokeAsync(() => OnShotFileAsync(e.FullPath));
            _shotWatch.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Warn("Shots", ex.Message); }
    }

    /// <summary>Windows' "Capturas de tela" folder (it can be moved, e.g. to OneDrive), or Pictures\Screenshots.</summary>
    static string? ScreenshotsFolder()
    {
        var id = new Guid("b7bede81-df94-4682-a7d8-57a52620b86f");
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path) == 0)
        {
            try { return Marshal.PtrToStringUni(path); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return pictures.Length > 0 ? System.IO.Path.Combine(pictures, "Screenshots") : null;
    }

    async Task OnShotFileAsync(string path)
    {
        if (!_settings.SmartShots || string.Equals(path, _shotIgnore, StringComparison.OrdinalIgnoreCase)) return;
        BitmapSource? image = null;
        for (var i = 0; i < 12 && image == null; i++)
        {
            await Task.Delay(i == 0 ? 300 : 250); // the tool is still writing it
            try { image = LoadPicture(path); } catch { }
        }
        if (image == null) return;
        if (SameShot(image) is { } shot)
        {
            shot.Path = path;
            shot.IsPrint = true;
            RefreshShot(shot);
            return;
        }
        _shot = new ScreenShot { Image = image, Path = path, IsPrint = true };
        ShowShot(_shot);
    }

    /// <summary>From <see cref="OnClipboard"/>: false when the picture isn't this card's business (the old pill shows).</summary>
    bool OnShotClipboard()
    {
        if (!_settings.SmartShots || _userHidden || DateTime.Now < _ownImageUntil) return _settings.SmartShots;
        if (ClipboardPicture() is not { } image) return false;
        var print = FromCaptureTool();
        if (SameShot(image) is { } shot)
        {
            shot.InClipboard = true;
            shot.IsPrint |= print;
            RefreshShot(shot);
            return true;
        }
        _shot = new ScreenShot { Image = image, InClipboard = true, IsPrint = print };
        ShowShot(_shot);
        return true;
    }

    /// <summary>The same picture arriving the other way (clipboard, then the saved file) a moment later.</summary>
    ScreenShot? SameShot(BitmapSource image) =>
        _shot is { } shot && DateTime.Now - shot.At < TimeSpan.FromSeconds(4)
        && shot.Image.PixelWidth == image.PixelWidth && shot.Image.PixelHeight == image.PixelHeight ? shot : null;

    /// <summary>Updates the card if it is still the one on screen; a card already used or gone stays gone.</summary>
    void RefreshShot(ScreenShot shot)
    {
        if (_transient == Transient.Action && ReferenceEquals(_shotShown, shot)) ShowShot(shot);
    }

    void ShowShot(ScreenShot shot)
    {
        var actions = new List<CardAction>();
        if (shot.Path == null) actions.Add(new("\uE74E", "Salvar", () => SaveShot(shot)));
        else if (!shot.InClipboard) actions.Add(new("\uE8C8", "Copiar", () => CopyShot(shot)));
        else actions.Add(new("\uE8E5", "Abrir", () => OpenPath(shot.Path)));
        if ((_settings.PhoneLink && _settings.PhoneFiles) || SnapshotMode) actions.Add(new("\uE8EA", "Celular", () => _ = SendShotToPhoneAsync(shot)));
        actions.Add(new("\uE8D2", "Ler texto", () => _ = ReadShotAsync(shot, translate: false)));
        actions.Add(new("\uF2B7", "Traduzir", () => _ = ReadShotAsync(shot, translate: true)));
        var where = shot.Path != null ? (shot.InClipboard ? "copiado e salvo" : "salvo em Capturas de tela") : "copiado";
        _shotShown = shot;
        ShowActions(shot.IsPrint ? "Print" : "Imagem copiada", $"{shot.Image.PixelWidth} × {shot.Image.PixelHeight} · {where}", actions, shot.Image);
    }

    void SaveShot(ScreenShot shot)
    {
        try
        {
            var folder = ScreenshotsFolder() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, $"Captura de tela {DateTime.Now:yyyy-MM-dd HHmmss}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(shot.Image));
            _shotIgnore = path;
            using (var stream = File.Create(path)) encoder.Save(stream);
            shot.Path = path;
            ShowInfo("\uE74E", "Print salvo em Capturas de tela", "Mostrar", glyphColor: AgentSession.Done, action: () => ShowInFolder(path), seconds: 4);
        }
        catch (Exception ex)
        {
            Log.Warn("Shots", ex.Message);
            ShowInfo("\uE74E", "Não deu para salvar o print", "", glyphColor: Meter.Red, seconds: 3);
        }
    }

    /// <summary>The file this app just saved: the folder watcher must not offer it back as a new print.</summary>
    string? _shotIgnore;

    void CopyShot(ScreenShot shot)
    {
        try
        {
            _ownImageUntil = DateTime.Now.AddSeconds(2);
            Clipboard.SetImage(shot.Image);
            shot.InClipboard = true;
            ShowInfo("\uE8C8", "Print copiado", "", glyphColor: AgentSession.Done, seconds: 1.6);
        }
        catch { ShowInfo("\uE8C8", "Não deu para copiar agora", "", glyphColor: Meter.Red, seconds: 2.5); }
    }

    static void OpenPath(string? path)
    {
        if (path == null) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    async Task SendShotToPhoneAsync(ScreenShot shot)
    {
        try { await SendFilesToPhoneAsync([shot.Path ?? SaveClipboardImage(shot.Image)]); }
        catch (Exception ex) { Log.Error("SendShot", ex); }
    }

    async Task ReadShotAsync(ScreenShot shot, bool translate)
    {
        if (translate && !JarvisBrain.HasKey)
        {
            ShowInfo("\uF2B7", "Tradução: falta a chave da Groq", "Configurar", glyphColor: JarvisColor,
                action: () => ((App)Application.Current).ShowSettings("jarvis"), seconds: 6);
            return;
        }
        ShowInfo(translate ? "\uF2B7" : "\uE8D2", translate ? "Traduzindo o print…" : "Lendo o texto do print…", "", glyphColor: JarvisColor, seconds: 30);
        string? text;
        using (var bitmap = ScreenOcr.FromBitmapSource(shot.Image)) text = await ScreenOcr.ReadAsync(bitmap);
        if (text == null)
        {
            ShowOcrMissing();
            return;
        }
        if (text.Length == 0)
        {
            ShowInfo("\uE8D2", "Não achei texto no print", "", glyphColor: JarvisColor, seconds: 2.5);
            return;
        }
        if (!translate)
        {
            ShowOcrText(text, "Texto copiado do print");
            return;
        }
        _translationSource = text;
        if (await Translator.TranslateAsync(text, _settings.TranslateTo) is not { } translation)
        {
            ShowInfo("\uF2B7", "Não consegui traduzir agora", "tente de novo", glyphColor: Meter.Red, seconds: 3);
            return;
        }
        ShowTranslation(translation, replace: false);
    }

    /// <summary>The picture in the clipboard: the PNG the capture tools also put there (keeps it exact), else the bitmap
    /// read as opaque (a screenshot's bitmap often carries an empty alpha channel).</summary>
    static BitmapSource? ClipboardPicture()
    {
        try
        {
            if (Clipboard.GetData("PNG") is MemoryStream png)
            {
                var frame = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                frame.Freeze();
                return frame;
            }
            if (Clipboard.GetImage() is not { } image) return null;
            var opaque = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
            var copy = new WriteableBitmap(opaque);
            copy.Freeze();
            return copy;
        }
        catch { return null; }
    }

    static BitmapSource LoadPicture(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>A made-up screenshot for the snapshots: a small report with a chart.</summary>
    static BitmapSource DemoScreenshot()
    {
        const int w = 1280, h = 720;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x8C)), null, new Rect(0, 0, w, 90));
            var face = new Typeface("Segoe UI Semibold");
            dc.DrawText(new FormattedText("Relatório de vendas · 3º trimestre", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, face, 40, Brushes.White, 1), new Point(48, 20));
            var bars = new[] { 0.45, 0.62, 0.38, 0.8, 0.7, 0.92 };
            var colors = new[] { Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0x30, 0xD1, 0x58) };
            for (var i = 0; i < bars.Length; i++)
                dc.DrawRoundedRectangle(new SolidColorBrush(colors[i % 2]), null, new Rect(80 + i * 190, 660 - bars[i] * 480, 120, bars[i] * 480), 10, 10);
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The clipboard was written by a screen capture tool (or by nobody, as PrtScn does).</summary>
    static bool FromCaptureTool()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return true;
        if (GetWindowThreadProcessId(owner, out var pid) == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return CaptureTools.Any(t => process.ProcessName.Equals(t, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}
