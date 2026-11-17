using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DynamicIsland;

/// <summary>
/// The voice glow from QuickVoice (the user's earlier Jarvis): colored lobes along the island's bottom edge that rise
/// and bloom with the voice and, while Jarvis thinks, gather into a beam that sweeps back and forth. Adapted for WPF
/// from voice-glow (https://libraries.dev/voice.html, MIT, © 2026 Jakub Antalik), with springs instead of plain
/// easing so it overshoots a little and settles.
/// </summary>
internal sealed class VoiceGlow : Grid
{
    /// <summary>Center, inner pair, middle pair, outer pair (voice-glow's lobe layout); Band: 0 low, 1 mid, 2 high.</summary>
    static readonly (int Slot, double W, double H, int Band)[] Lobes =
        [(0, 74, 46, 0), (-1, 54, 40, 1), (1, 54, 40, 1), (-2, 48, 32, 2), (2, 48, 32, 2), (-3, 42, 26, 1), (3, 42, 26, 1)];

    public static readonly (string Key, string Label)[] Variants =
        [("colorful", "Colorido"), ("ocean", "Oceano"), ("sunset", "Pôr do sol"), ("forest", "Floresta"), ("candy", "Doce"), ("ice", "Gelo"), ("gold", "Ouro"), ("mono", "Cinza")];

    static readonly Dictionary<string, (string[] Dark, string[] Light)> Palettes = new()
    {
        ["colorful"] = (["#ff4678", "#3cbeff", "#af46ff", "#3cdc82", "#ff9628", "#5a64ff", "#28c8be"], ["#ffc915", "#7ec4ff", "#b428e6", "#eb64a0", "#ffb07a", "#9aa0ff", "#7fd9ee"]),
        ["mono"] = (["#d7d7d7", "#b4b4b4", "#bebebe", "#a0a0a0", "#aaaaaa", "#969696", "#9b9b9b"], ["#3c3c3c", "#5a5a5a", "#555555", "#6e6e6e", "#696969", "#7d7d7d", "#787878"]),
        ["ocean"] = (["#508cff", "#28c8e6", "#785aff", "#1eaad2", "#a050f0", "#3c6eff", "#28beb4"], ["#2864f0", "#14a0c8", "#5a3ce6", "#1482b4", "#8232dc", "#2850e6", "#149696"]),
        ["sunset"] = (["#ff6e3c", "#ffb428", "#ff3c5a", "#ffd250", "#f0468c", "#ff8c32", "#e6326e"], ["#eb501e", "#e6960a", "#e61e46", "#e1af1e", "#d7286e", "#eb6e14", "#cd1e5a"]),
        ["forest"] = (["#46dc78", "#28c8b4", "#8ce650", "#1eaa8c", "#beeb46", "#32be6e", "#1e9678"], ["#1eaa50", "#149682", "#5ab41e", "#148264", "#82b414", "#1e9650", "#14785a"]),
        ["candy"] = (["#ff5aaa", "#ff78dc", "#d250ff", "#ff96be", "#b46eff", "#ff468c", "#e664f0"], ["#eb288c", "#e646be", "#b428e6", "#eb64a0", "#9646e6", "#e61e6e", "#c83cd2"]),
        ["ice"] = (["#96e6ff", "#5ac8ff", "#bef0ff", "#78beff", "#a0dcfa", "#50aaff", "#c8ebff"], ["#1ea0dc", "#1482d2", "#3cb4e6", "#2878dc", "#32a0dc", "#146edc", "#46aae6"]),
        ["gold"] = (["#ffc846", "#ffaa28", "#ffdc6e", "#f0961e", "#ffeb8c", "#e6a028", "#fad25a"], ["#c88c0a", "#be7800", "#d2a01e", "#b46e00", "#cdaa28", "#af7305", "#c39614"]),
    };

    // voice-glow's pill preset and input chain.
    const double Threshold = 0.015, Idle = 0.18, Breathe = 3.2, Reach = 1.35, Spread = 1.1;
    const double ProcessingLevel = 0.55, ProcessingDuration = 1.1, ProcessingCurve = 2.1;

    /// <summary>Where the sound comes from; null draws only the idle breathing.</summary>
    public Func<(double Level, double Low, double Mid, double High)>? Source { get; set; }
    public bool Listening { get => _listening; set { _listening = value; Wake(); } }
    public bool Processing { get => _processing; set { _processing = value; Wake(); } }
    public double CornerRadius
    {
        get => _corner;
        set { _corner = value; Clip = new RectangleGeometry(new Rect(RenderSize), value, value); }
    }

    readonly Canvas _lobes = new() { IsHitTestVisible = false };
    readonly Ellipse[] _shapes = new Ellipse[7];
    readonly Ellipse _core = new() { IsHitTestVisible = false };
    readonly Border _stroke = new() { IsHitTestVisible = false, BorderThickness = new Thickness(1.6) };
    bool _listening, _processing, _running, _enabled = true;
    double _corner = 17;
    TimeSpan _lastFrame;
    double _time, _scanTime, _sinceDraw;
    readonly Spring _level = new(), _low = new(), _mid = new(), _high = new(), _presence = new(), _scan = new();

    public VoiceGlow()
    {
        IsHitTestVisible = false;
        for (var i = 0; i < _shapes.Length; i++)
        {
            _shapes[i] = new Ellipse();
            _lobes.Children.Add(_shapes[i]);
        }
        _lobes.Children.Add(_core);
        _stroke.OpacityMask = new RadialGradientBrush
        {
            Center = new Point(0.5, 1), GradientOrigin = new Point(0.5, 1), RadiusX = 0.6, RadiusY = 1.4,
            GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Color.FromArgb(0x80, 255, 255, 255), 0.55), new GradientStop(Colors.Transparent, 1) },
        };
        Children.Add(_lobes);
        Children.Add(_stroke);
        Opacity = 0;
        SizeChanged += (_, _) => Clip = new RectangleGeometry(new Rect(RenderSize), _corner, _corner);
        Apply("colorful", light: false);
    }

    /// <summary>A color variant ("colorful", "ocean", "gold"…) or "off".</summary>
    public void Apply(string variant, bool light)
    {
        _enabled = variant != "off";
        if (!Palettes.TryGetValue(variant, out var palette)) palette = Palettes["colorful"];
        var colors = (light ? palette.Light : palette.Dark).Select(c => (Color)ColorConverter.ConvertFromString(c)).ToArray();
        for (var i = 0; i < _shapes.Length; i++)
        {
            var c = colors[i];
            _shapes[i].Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xE0, c.R, c.G, c.B), 0),
                    new GradientStop(Color.FromArgb(0x78, c.R, c.G, c.B), 0.35),
                    new GradientStop(Color.FromArgb(0x24, c.R, c.G, c.B), 0.7),
                    new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1),
                },
            };
        }
        var coreColor = light ? Color.FromRgb(0xC5, 0x8B, 0xFF) : Colors.White;
        _core.Fill = new RadialGradientBrush(Color.FromArgb(0xC0, coreColor.R, coreColor.G, coreColor.B), Color.FromArgb(0, coreColor.R, coreColor.G, coreColor.B));
        var line = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        foreach (var (color, offset) in new[] { (colors[5], 0.1), (colors[3], 0.25), (colors[1], 0.4), (colors[0], 0.5), (colors[2], 0.6), (colors[4], 0.75), (colors[6], 0.9) })
            line.GradientStops.Add(new GradientStop(color, offset));
        _stroke.BorderBrush = line;
        Wake();
    }

    void Wake()
    {
        if (_running || !_enabled || !(_listening || _processing || _presence.Value > 0.002)) return;
        _running = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += Frame;
    }

    void Frame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 1 / 20.0);
        _lastFrame = now;
        if (dt <= 0) return;
        _time += dt;

        var input = _listening && Source is { } source ? source() : default;
        // Rising is quick, falling is slower; both overshoot a touch, which is what makes it feel elastic.
        _level.Step(Gate(input.Level, Threshold), dt, 150, 45, 0.42);
        _low.Step(Gate(input.Low, Threshold * 0.6), dt, 170, 40, 0.38);
        _mid.Step(Gate(input.Mid, Threshold * 0.6), dt, 190, 42, 0.36);
        _high.Step(Gate(input.High, Threshold * 0.6), dt, 210, 46, 0.34);
        _presence.Step(_enabled && (_listening || _processing) ? 1 : 0, dt, 60, 30, 0.8);
        _scan.Step(_processing ? 1 : 0, dt, 45, 30, 0.7);
        _scanTime = _processing || _scan.Value > 0.01 ? _scanTime + dt : 0;

        Opacity = Math.Clamp(_presence.Value, 0, 1);
        // The island is a layered window, repainted whole on every change: 30 frames a second is smooth and half the cost.
        _sinceDraw += dt;
        if (_sinceDraw >= 1 / 31.0)
        {
            _sinceDraw = 0;
            Draw();
        }

        if (_listening || _processing || _presence.Value > 0.002 || Math.Abs(_presence.Velocity) > 0.01) return;
        Opacity = 0;
        _running = false;
        CompositionTarget.Rendering -= Frame;
    }

    void Draw()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var gather = Smooth(Math.Clamp(_scan.Value, 0, 1));
        var breath = 0.5 + 0.5 * Math.Sin(2 * Math.PI * _time / Breathe);
        var voice = Math.Max(0, _level.Value);
        var withIdle = voice + (1 - Math.Min(voice, 1)) * Idle * breath;
        var held = Math.Max(withIdle, ProcessingLevel * gather);
        var intensity = 0.15 + 0.85 * held;

        // Processing: the lobes gather and the beam sweeps, ping-pong, with an ease-in-out of power 2.1.
        var normalized = _scanTime / ProcessingDuration;
        var pass = Math.Floor(normalized);
        var u = normalized - pass;
        var eased = u < 0.5 ? 0.5 * Math.Pow(2 * u, ProcessingCurve) : 1 - 0.5 * Math.Pow(2 - 2 * u, ProcessingCurve);
        var direction = pass % 2 == 0 ? 2 * eased - 1 : 1 - 2 * eased;
        var sweep = gather * (w / 2 - 60) * direction;

        var ring = w * 0.62;
        var slot = ring / 7;
        double[] bands = [Math.Max(0, _low.Value), Math.Max(0, _mid.Value), Math.Max(0, _high.Value)];
        for (var i = 0; i < Lobes.Length; i++)
        {
            var (s, lw, lh, band) = Lobes[i];
            var x = s * slot + Math.Sin(_time * 0.7 + i * 1.3) * 5;  // a slow drift so it never looks frozen
            var position = Math.Max(0, 1 - Math.Pow(x / (ring / 2 + 4), 2));
            var lobeLevel = (0.6 + 0.7 * bands[band]) * position;
            var width = lw * 2.4 * (0.85 + Spread * intensity) * (1 - 0.45 * gather);
            var height = lh * 1.15 * (0.5 + Reach * intensity) * lobeLevel;
            var cx = w / 2 + x * (1 - 0.6 * gather) + sweep;
            var shape = _shapes[i];
            shape.Width = Math.Max(0, width);
            shape.Height = Math.Max(0, height * 2);  // an ellipse centered on the bottom edge: its upper half shows
            Canvas.SetLeft(shape, cx - width / 2);
            Canvas.SetTop(shape, h - height + 2);
        }
        var coreW = 90 * (0.6 + 0.8 * intensity) * (1 - 0.3 * gather);
        var coreH = 16 * (0.4 + intensity);
        _core.Width = coreW;
        _core.Height = coreH * 2;
        Canvas.SetLeft(_core, w / 2 + sweep - coreW / 2);
        Canvas.SetTop(_core, h - coreH + 1);

        _stroke.CornerRadius = new System.Windows.CornerRadius(_corner);
        _stroke.Opacity = Math.Clamp(0.2 + 0.9 * intensity, 0, 1);
        if (_stroke.OpacityMask is RadialGradientBrush mask) mask.Center = mask.GradientOrigin = new Point(0.5 + sweep / w, 1);
    }

    /// <summary>voice-glow's noise gate: nothing below the threshold, then an exponential knee.</summary>
    static double Gate(double value, double threshold)
    {
        if (value <= threshold) return 0;
        var n = (value - threshold) / Math.Max(0.001, 1 - threshold);
        return Math.Clamp((1 - Math.Exp(-3 * n)) / (1 - Math.Exp(-3)), 0, 1);
    }

    static double Smooth(double x) => x * x * (3 - 2 * x);

    /// <summary>A damped spring toward a target: stiffer when rising (attack) than when falling (release).</summary>
    sealed class Spring
    {
        public double Value { get; private set; }
        public double Velocity { get; private set; }

        public void Step(double target, double dt, double attack, double release, double damping)
        {
            var k = target > Value ? attack : release;
            var c = 2 * damping * Math.Sqrt(k);
            Velocity += (k * (target - Value) - c * Velocity) * dt;
            Value += Velocity * dt;
        }
    }
}
