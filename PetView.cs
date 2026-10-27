using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace DynamicIsland;

public enum PetSpecies { None, Coco, Pipoca, Bu }

/// <summary>
/// The island's mascot, drawn from shapes on a 24×24 canvas (scale it with a Viewbox). Coco (the island's own
/// little coconut), Pipoca (a cat) and Bu (a ghost) share one face; a base mood follows what the island shows
/// and short reactions play on top of it.
/// </summary>
public sealed class PetView : Canvas
{
    public enum Mood { Idle, Dance, Working, Waiting, Sleep, Happy, Annoyed, Dizzy }

    public static readonly Color Cream = Color.FromRgb(0xF4, 0xEF, 0xE6);
    static readonly Brush Ink = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E));
    static readonly Brush Pink = Frozen(Color.FromRgb(0xFF, 0x8F, 0xA3));
    static readonly Brush Leaf = Frozen(Color.FromRgb(0x30, 0xC8, 0x5A));
    static readonly Brush LeafLight = Frozen(Color.FromRgb(0x62, 0xE0, 0x7E));
    static readonly Random Rng = new();

    /// <summary>No random blinks (deterministic snapshots).</summary>
    public static bool Still { get; set; }

    /// <summary>Raised when three quick pokes make it dizzy.</summary>
    public event Action? Dizzied;

    /// <summary>The mascot every PetView shows (the island's, the agent list's...).</summary>
    public static PetSpecies DefaultSpecies { get; private set; } = PetSpecies.Coco;
    static event Action? SpeciesChanged;

    public static void SetDefaultSpecies(PetSpecies species)
    {
        if (species == PetSpecies.None || species == DefaultSpecies) return;
        DefaultSpecies = species;
        SpeciesChanged?.Invoke();
    }

    public static readonly DependencyProperty TintColorProperty = DependencyProperty.Register(
        nameof(TintColor), typeof(Color), typeof(PetView),
        new PropertyMetadata(Colors.Transparent, (d, e) => ((PetView)d).Tint((Color)e.NewValue is { A: > 0 } c ? c : null)));

    /// <summary>Bindable tint; transparent means the natural colour.</summary>
    public Color TintColor
    {
        get => (Color)GetValue(TintColorProperty);
        set => SetValue(TintColorProperty, value);
    }

    readonly SolidColorBrush _fill = new(Cream);
    readonly TranslateTransform _hop = new(), _look = new();
    readonly RotateTransform _tilt = new(0, 12, 20);
    readonly ScaleTransform _squash = new(1, 1, 12, 23.5), _blink = new(1, 1, 12, 13.6), _eyeSize = new(1, 1, 12, 13.6);
    readonly Canvas _shell = new() { Width = 24, Height = 24 };
    readonly Canvas _eyes;
    readonly Path _happyEyes, _closedEyes, _annoyedEyes, _dizzyEyes;
    readonly TextBlock _z;
    readonly DispatcherTimer _blinkTimer, _reactionTimer;

    PetSpecies _species = PetSpecies.None;
    Mood _base = Mood.Idle, _shown = Mood.Idle;
    bool _active = true;
    int _pokes;
    DateTime _lastPoke;

    public PetView()
    {
        Width = Height = 24;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;

        var body = new Canvas { Width = 24, Height = 24 };
        var bodyTransforms = new TransformGroup();
        bodyTransforms.Children.Add(_squash);
        bodyTransforms.Children.Add(_tilt);
        bodyTransforms.Children.Add(_hop);
        body.RenderTransform = bodyTransforms;
        Children.Add(body);
        body.Children.Add(_shell);

        var face = new Canvas { Width = 24, Height = 24, RenderTransform = _look };
        body.Children.Add(face);
        _eyes = new Canvas { Width = 24, Height = 24 };
        var eyeTransforms = new TransformGroup();
        eyeTransforms.Children.Add(_eyeSize);
        eyeTransforms.Children.Add(_blink);
        _eyes.RenderTransform = eyeTransforms;
        face.Children.Add(_eyes);
        foreach (var x in new[] { 6.9, 14.4 })
        {
            _eyes.Children.Add(Place(new Ellipse { Width = 2.8, Height = 3.6, Fill = Ink }, x, 11.8));
            _eyes.Children.Add(Place(new Ellipse { Width = 1.0, Height = 1.0, Fill = Brushes.White }, x + 0.45, 12.35));
        }
        face.Children.Add(_happyEyes = Stroke("M6.8,14.4 Q8.25,12.1 9.7,14.4 M14.3,14.4 Q15.75,12.1 17.2,14.4", 1.4));
        face.Children.Add(_closedEyes = Stroke("M6.9,13.9 Q8.25,14.9 9.6,13.9 M14.4,13.9 Q15.75,14.9 17.1,13.9", 1.2));
        face.Children.Add(_annoyedEyes = Stroke("M7,12.3 L9.5,13.7 L7,15.1 M17,12.3 L14.5,13.7 L17,15.1", 1.3));
        face.Children.Add(_dizzyEyes = Stroke(
            "M8.5,13.7 A0.25,0.25 0 1 1 8.0,13.7 A0.5,0.5 0 1 1 9.0,13.7 A1.0,1.0 0 1 1 7.0,13.7 A1.4,1.4 0 1 1 9.8,13.7 " +
            "M16.0,13.7 A0.25,0.25 0 1 1 15.5,13.7 A0.5,0.5 0 1 1 16.5,13.7 A1.0,1.0 0 1 1 14.5,13.7 A1.4,1.4 0 1 1 17.3,13.7", 0.6));

        _z = new TextBlock { Text = "z", FontSize = 7, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Opacity = 0, RenderTransform = new TranslateTransform() };
        Children.Add(Place(_z, 19, -1));

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _blinkTimer.Tick += (_, _) => Blink();
        _reactionTimer = new DispatcherTimer();
        _reactionTimer.Tick += (_, _) =>
        {
            _reactionTimer.Stop();
            Show(_base);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            Poke();
            e.Handled = true;
        };
        Species = DefaultSpecies;
        void Sync() => Species = DefaultSpecies;
        Loaded += (_, _) =>
        {
            SpeciesChanged -= Sync;
            SpeciesChanged += Sync;
            Sync();
        };
        Unloaded += (_, _) => SpeciesChanged -= Sync;
        Show(Mood.Idle);
    }

    public PetSpecies Species
    {
        get => _species;
        set
        {
            if (_species == value || value == PetSpecies.None) return;
            _species = value;
            _shell.Children.Clear();
            switch (value)
            {
                case PetSpecies.Coco:
                    _shell.Children.Add(Fill("M12,6.2 C18.6,6.2 22.4,10.3 22.4,15.3 C22.4,20.5 18,23.5 12,23.5 C6,23.5 1.6,20.5 1.6,15.3 C1.6,10.3 5.4,6.2 12,6.2 Z", _fill));
                    _shell.Children.Add(new Path { Data = Geometry.Parse("M12,6.7 Q12.1,4.9 12.8,3.6"), Stroke = Leaf, StrokeThickness = 1.1, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                    _shell.Children.Add(Fill("M12.6,4.3 C10.7,2.3 8.4,2.6 7.6,3.4 C9.2,5.0 11.2,5.1 12.6,4.3 Z", Leaf));
                    _shell.Children.Add(Fill("M12.8,3.7 C14.1,1.4 16.6,0.9 17.7,1.6 C16.7,3.5 14.6,4.3 12.8,3.7 Z", LeafLight));
                    AddCheeks();
                    _shell.Children.Add(Stroke("M10.7,17.5 Q12,18.8 13.3,17.5", 0.95));
                    break;
                case PetSpecies.Pipoca:
                    foreach (var ear in new[] { "M3.4,10 L4.4,2.2 L10,7.2 Z", "M20.6,10 L19.6,2.2 L14,7.2 Z" })
                        _shell.Children.Add(new Path { Data = Geometry.Parse(ear), Fill = _fill, Stroke = _fill, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round });
                    _shell.Children.Add(Place(new Ellipse { Width = 21, Height = 17, Fill = _fill }, 1.5, 6.5));
                    AddCheeks();
                    _shell.Children.Add(Fill("M11.25,16.3 L12.75,16.3 L12,17.15 Z", Pink));
                    _shell.Children.Add(Stroke("M10.2,17.6 Q11.1,18.7 12,17.6 Q12.9,18.7 13.8,17.6", 0.9));
                    break;
                case PetSpecies.Bu:
                    _shell.Children.Add(Fill("M3,23.2 L3,13 C3,7.5 7,4.4 12,4.4 C17,4.4 21,7.5 21,13 L21,23.2 Q19.5,21.3 18,23.2 Q16.5,21.3 15,23.2 Q13.5,21.3 12,23.2 Q10.5,21.3 9,23.2 Q7.5,21.3 6,23.2 Q4.5,21.3 3,23.2 Z", _fill));
                    AddCheeks();
                    _shell.Children.Add(Place(new Ellipse { Width = 1.7, Height = 2.1, Fill = Ink }, 11.15, 16.9));
                    break;
            }
        }
    }

    void AddCheeks()
    {
        _shell.Children.Add(Place(new Ellipse { Width = 2.6, Height = 1.5, Fill = Pink, Opacity = 0.6 }, 4.3, 16.6));
        _shell.Children.Add(Place(new Ellipse { Width = 2.6, Height = 1.5, Fill = Pink, Opacity = 0.6 }, 17.1, 16.6));
    }

    static T Place<T>(T element, double x, double y) where T : UIElement
    {
        SetLeft(element, x);
        SetTop(element, y);
        return element;
    }

    static Path Fill(string data, Brush brush) => new() { Data = Geometry.Parse(data), Fill = brush };

    static Path Stroke(string data, double thickness) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = Ink,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Loops only run while the pet is on screen.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            Show(_reactionTimer.IsEnabled ? _shown : _base);
        }
    }

    public Mood BaseMood
    {
        get => _base;
        set
        {
            if (_base == value) return;
            _base = value;
            if (!_reactionTimer.IsEnabled) Show(value);
        }
    }

    public void Tint(Color? color)
    {
        var target = color is { } c ? Color.FromRgb((byte)((c.R + 255 * 1.4) / 2.4), (byte)((c.G + 255 * 1.4) / 2.4), (byte)((c.B + 255 * 1.4) / 2.4)) : Cream;
        if (_fill.Color == target) return;
        _fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(260)));
    }

    /// <summary>Eyes follow a point given relative to the pet's centre, in the pet's own units.</summary>
    public void LookAt(Vector toCursor)
    {
        if (_shown is Mood.Sleep or Mood.Dizzy) toCursor = default;
        var length = toCursor.Length;
        var reach = length < 1 ? 0 : Math.Min(1.3, length / 30);
        var x = length < 1 ? 0 : toCursor.X / length * reach;
        var y = length < 1 ? 0 : toCursor.Y / length * reach * 0.7;
        if (_base == Mood.Working && _shown == Mood.Working) y = 0.9;
        _look.X = x;
        _look.Y = y;
    }

    public void React(Mood mood, double seconds)
    {
        Show(mood);
        _reactionTimer.Stop();
        _reactionTimer.Interval = TimeSpan.FromSeconds(seconds);
        _reactionTimer.Start();
    }

    public void Poke()
    {
        var now = DateTime.Now;
        _pokes = now - _lastPoke < TimeSpan.FromSeconds(1.2) ? _pokes + 1 : 1;
        _lastPoke = now;
        if (_pokes >= 3)
        {
            _pokes = 0;
            React(Mood.Dizzy, 3);
            Dizzied?.Invoke();
        }
        else React(Mood.Annoyed, 1.2);
    }

    void Show(Mood mood)
    {
        _shown = mood;
        StopLoops();

        var eyes = mood switch
        {
            Mood.Happy or Mood.Dance => "happy",
            Mood.Sleep => "closed",
            Mood.Annoyed => "annoyed",
            Mood.Dizzy => "dizzy",
            _ => "open",
        };
        _eyes.Visibility = eyes == "open" ? Visibility.Visible : Visibility.Hidden;
        _happyEyes.Visibility = eyes == "happy" ? Visibility.Visible : Visibility.Hidden;
        _closedEyes.Visibility = eyes == "closed" ? Visibility.Visible : Visibility.Hidden;
        _annoyedEyes.Visibility = eyes == "annoyed" ? Visibility.Visible : Visibility.Hidden;
        _dizzyEyes.Visibility = eyes == "dizzy" ? Visibility.Visible : Visibility.Hidden;
        Animate(_eyeSize, ScaleTransform.ScaleXProperty, mood == Mood.Waiting ? 1.2 : 1, 0.2);
        Animate(_eyeSize, ScaleTransform.ScaleYProperty, mood == Mood.Waiting ? 1.2 : 1, 0.2);

        switch (mood)
        {
            case Mood.Happy:
                Hop(-6, 0.55, new BounceEase { Bounces = 2, Bounciness = 2.5 });
                break;
            case Mood.Annoyed:
                Spring(_squash, ScaleTransform.ScaleXProperty, 1.25);
                Spring(_squash, ScaleTransform.ScaleYProperty, 0.72);
                break;
            case Mood.Dizzy:
                Loop(_tilt, RotateTransform.AngleProperty, -15, 15, 0.18);
                break;
        }
        if (!_active) return;

        switch (mood)
        {
            case Mood.Idle:
                if (!Still) _blinkTimer.Start();
                // the ghost never quite touches the ground
                if (_species == PetSpecies.Bu) Loop(_hop, TranslateTransform.YProperty, 0, -1.4, 1.1);
                break;
            case Mood.Dance:
                Loop(_hop, TranslateTransform.YProperty, 0, -2.6, 0.24);
                Loop(_tilt, RotateTransform.AngleProperty, -8, 8, 0.48);
                break;
            case Mood.Working:
                Loop(_hop, TranslateTransform.YProperty, 0, -1.1, 0.3);
                if (!Still) _blinkTimer.Start();
                break;
            case Mood.Waiting:
                var jump = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(1.1) };
                jump.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                jump.KeyFrames.Add(new EasingDoubleKeyFrame(-4.5, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.18)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                jump.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.42)), new BounceEase { Bounces = 1, Bounciness = 3 }));
                Timeline.SetDesiredFrameRate(jump, 30);
                _hop.BeginAnimation(TranslateTransform.YProperty, jump);
                break;
            case Mood.Sleep:
                Loop(_squash, ScaleTransform.ScaleYProperty, 1, 0.93, 1.6);
                var drift = new DoubleAnimation(0, -5, TimeSpan.FromSeconds(2.2)) { RepeatBehavior = RepeatBehavior.Forever };
                var fade = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(2.2) };
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromPercent(0.3)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                Timeline.SetDesiredFrameRate(drift, 20);
                Timeline.SetDesiredFrameRate(fade, 20);
                ((TranslateTransform)_z.RenderTransform).BeginAnimation(TranslateTransform.YProperty, drift);
                _z.BeginAnimation(OpacityProperty, fade);
                break;
        }
    }

    void StopLoops()
    {
        _blinkTimer.Stop();
        _hop.BeginAnimation(TranslateTransform.YProperty, null);
        _tilt.BeginAnimation(RotateTransform.AngleProperty, null);
        _squash.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _squash.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _z.BeginAnimation(OpacityProperty, null);
        ((TranslateTransform)_z.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
        _hop.Y = 0;
        _tilt.Angle = 0;
        _squash.ScaleX = _squash.ScaleY = 1;
        _z.Opacity = 0;
    }

    void Blink()
    {
        _blinkTimer.Interval = TimeSpan.FromSeconds(2.5 + Rng.NextDouble() * 3.5);
        _blink.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.1, TimeSpan.FromMilliseconds(70)) { AutoReverse = true });
    }

    void Hop(double height, double seconds, IEasingFunction landing)
    {
        var jump = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(seconds + 0.16) };
        jump.KeyFrames.Add(new EasingDoubleKeyFrame(height, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.16)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        jump.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds + 0.16)), landing));
        _hop.BeginAnimation(TranslateTransform.YProperty, jump);
    }

    static void Loop(Animatable target, DependencyProperty property, double from, double to, double seconds)
    {
        var loop = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(loop, 30);
        target.BeginAnimation(property, loop);
    }

    static void Spring(Animatable target, DependencyProperty property, double squashed)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(squashed, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)), new ElasticEase { Oscillations = 2, Springiness = 4 }));
        target.BeginAnimation(property, anim);
    }

    static void Animate(Animatable target, DependencyProperty property, double to, double seconds) =>
        target.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromSeconds(seconds)) { EasingFunction = new BackEase { Amplitude = 0.6 } });
}