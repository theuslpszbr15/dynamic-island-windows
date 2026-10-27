using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

public sealed class FileItem(string path)
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path;
    public ImageSource? Icon { get; } = Native.ShellIcon(path);
}

public sealed class AgentSession : INotifyPropertyChanged
{
    public static readonly Color Working = Color.FromRgb(0x0A, 0x84, 0xFF);
    public static readonly Color Waiting = Color.FromRgb(0xFF, 0x9F, 0x0A);
    public static readonly Color Done = Color.FromRgb(0x30, 0xD1, 0x58);
    public static readonly Color Idle = Color.FromRgb(0x8E, 0x8E, 0x93);

    static readonly Dictionary<Color, SolidColorBrush> Brushes = new();

    public AgentSession(string key, string agent, string project)
    {
        Key = key;
        Agent = agent;
        Project = project;
    }

    public string Key { get; }
    public string Agent { get; }
    public string Project { get; }
    public string Title => string.IsNullOrEmpty(Project) ? Agent : $"{Agent} · {Project}";

    public string State { get; private set; } = "idle";
    public string Detail { get; private set; } = "";
    public DateTime StartedAt { get; private set; } = DateTime.Now;
    public DateTime UpdatedAt { get; private set; } = DateTime.Now;

    public bool IsActive => State is "working" or "waiting";

    public Color StateColor => State switch
    {
        "working" => Working,
        "waiting" => Waiting,
        "done" => Done,
        _ => Idle,
    };

    public SolidColorBrush OrbBrush
    {
        get
        {
            if (!Brushes.TryGetValue(StateColor, out var brush))
            {
                brush = new SolidColorBrush(StateColor);
                brush.Freeze();
                Brushes[StateColor] = brush;
            }
            return brush;
        }
    }

    public string StateLabel => State switch
    {
        "working" => "Trabalhando",
        "waiting" => "Aguardando você",
        "done" => "Concluído",
        _ => "Ocioso",
    };

    public string Elapsed
    {
        get
        {
            var span = (State == "working" ? DateTime.Now : UpdatedAt) - StartedAt;
            return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
                 : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds:00}s"
                 : $"{span.Seconds}s";
        }
    }

    public void Update(string state, string? detail)
    {
        if (state == "working" && !IsActive) StartedAt = DateTime.Now;
        State = state;
        if (!string.IsNullOrEmpty(detail)) Detail = detail;
        UpdatedAt = DateTime.Now;
        Raise(null);
    }

    public void Tick() => Raise(nameof(Elapsed));

    public ObservableCollection<AgentStep> Steps { get; } = [];
    public string FileName { get; private set; } = "";
    public IReadOnlyList<DiffLine> DiffLines { get; private set; } = [];
    public bool HasDiff => DiffLines.Count > 0;

    /// <summary>Keeps the last few step kinds (Read, Edit, Terminal…) and the latest edit's diff.</summary>
    public void Record(string? step, string? file, string? diff)
    {
        if (State == "done") step = "done";
        if (step != null)
        {
            var last = Steps.LastOrDefault();
            if (last?.Kind == "done" && step != "done") Steps.Clear();
            last = Steps.LastOrDefault();
            if (last?.Kind != step)
            {
                if (last != null) last.IsCurrent = false;
                Steps.Add(new AgentStep(step) { IsCurrent = step != "done" });
                while (Steps.Count > 4) Steps.RemoveAt(0);
            }
        }
        if (step == "edit" && !string.IsNullOrEmpty(file))
        {
            FileName = System.IO.Path.GetFileName(file);
            DiffLines = diff?.Split('\n').Where(l => l.Length > 0).Select(l => new DiffLine(l[1..], l[0] == '+')).ToList() ?? [];
        }
        Raise(null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record DiffLine(string Text, bool Added)
{
    public string Sign => Added ? "+" : "−";
}

public sealed class AgentStep(string kind) : INotifyPropertyChanged
{
    bool _current;

    public string Kind { get; } = kind;

    public string Label => Kind switch
    {
        "read" => "Leitura",
        "edit" => "Edição",
        "run" => "Terminal",
        "search" => "Busca",
        "web" => "Web",
        "task" => "Subagente",
        "done" => "Concluído",
        _ => "Pensando",
    };

    public bool IsCurrent
    {
        get => _current;
        set
        {
            _current = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Damped spring used to animate the island's size.</summary>
sealed class Spring(double value)
{
    public double Value = value;
    public double Target = value;
    public double Velocity;
    public double Stiffness = 300;
    public double Damping = 26;

    public bool Settled => Math.Abs(Target - Value) < 0.2 && Math.Abs(Velocity) < 1.5;

    public void Step(double dt)
    {
        const double h = 1 / 480.0;
        while (dt > 0)
        {
            var s = Math.Min(h, dt);
            Velocity += (Stiffness * (Target - Value) - Damping * Velocity) * s;
            Value += Velocity * s;
            dt -= s;
        }
    }

    public void Snap()
    {
        Value = Target;
        Velocity = 0;
    }
}

static class ColorUtil
{
    /// <summary>A vivid, light accent sampled from album art.</summary>
    public static Color Accent(BitmapSource source)
    {
        try
        {
            BitmapSource small = source.PixelWidth > 24
                ? new TransformedBitmap(source, new ScaleTransform(24.0 / source.PixelWidth, 24.0 / source.PixelHeight))
                : source;
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bgra.CopyPixels(px, stride, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (var i = 0; i < px.Length; i += 4)
            {
                double B = px[i] / 255.0, G = px[i + 1] / 255.0, R = px[i + 2] / 255.0;
                double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                var sat = max <= 0 ? 0 : (max - min) / max;
                var weight = 0.03 + sat * sat * max;
                r += R * weight; g += G * weight; b += B * weight; total += weight;
            }
            ToHsv(r / total, g / total, b / total, out var hue, out var s, out var v);
            if (s < 0.12) return Color.FromRgb(0xE5, 0xE5, 0xEA);
            return FromHsv(hue, Math.Clamp(s * 1.25, 0.35, 0.8), Math.Max(v, 0.9));
        }
        catch { return Services.MediaService.DefaultAccent; }
    }

    static void ToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }
        h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        if (h < 0) h += 360;
    }

    static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
