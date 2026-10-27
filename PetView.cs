using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace DynamicIsland;

/// <summary>
/// Pipoca, the island's little cat. Drawn from shapes on a 24×24 canvas (scale it with a Viewbox);
/// a base mood follows what the island shows, short reactions play on top of it.
/// </summary>
public sealed class PetView : Canvas
{
    public enum Mood { Idle, Dance, Working, Waiting, Sleep, Happy, Annoyed, Dizzy }

    public static readonly Color Cream = Color.FromRgb(0xF4, 0xEF, 0xE6);
    static readonly Brush Ink = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E));
    static readonly Brush Pink = Frozen(Color.FromRgb(0xFF, 0x8F, 0xA3));
    static readonly Random Rng = new();

    /// <summary>No random blinks (deterministic snapshots).</summary>
    public static bool Still { get; set; }

    readonly SolidColorBrush _fill = new(Cream);
    readonly TranslateTransform _hop = new(), _look = new();
    readonly RotateTransform _tilt = new(0, 12, 20);
    readonly ScaleTransform _squash = new(1, 1, 12, 23.5), _blink = new(1, 1, 12, 13.6), _eyeSize = new(1, 1, 12, 13.6);
    readonly Ellipse _eyeL, _eyeR;
    readonly Path _happyEyes, _closedEyes, _annoyedEyes, _dizzyEyes;
    readonly TextBlock _z;
    readonly DispatcherTimer _blinkTimer, _reactionTimer;

    Mood _base = Mood.Idle, _shown = Mood.Idle;
    bool _active = true;
    int _pokes;
    DateTime _lastPoke;

    public PetView()
    {
        Width = Height = 24;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        RenderTransformOrigin = new Point(0.5, 1);

        var body = new Canvas { Width = 24, Height = 24 };
        var bodyTransforms = new TransformGroup();
        bodyTransforms.Children.Add(_squash);
        bodyTransforms.Children.Add(_tilt);
        bodyTransforms.Children.Add(_hop);
        body.RenderTransform = bodyTransforms;
        Children.Add(body);

        // ears first so the head covers their base
        foreach (var ear in new[] { "M3.4,10 L4.4,2.2 L10,7.2 Z", "M20.6,10 L19.6,2.2 L14,7.2 Z" })
            body.Children.Add(new Path
            {
                Data = Geometry.Parse(ear),
                Fill = _fill,
                Stroke = _fill,
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
            });
        body.Children.Add(Place(new Ellipse { Width = 21, Height = 17, Fill = _fill }, 1.5, 6.5));
        body.Children.Add(Place(new Ellipse { Width = 2.6, Height = 1.5, Fill = Pink, Opacity = 0.6 }, 4.3, 16.6));
        body.Children.Add(Place(new Ellipse { Width = 2.6, Height = 1.5, Fill = Pink, Opacity = 0.6 }, 17.1, 16.6));
        body.Children.Add(new Path { Data = Geometry.Parse("M11.25,16.3 L12.75,16.3 L12,17.15 Z"), Fill = Pink });
        body.Children.Add(Stroke("M10.2,17.6 Q11.1,18.7 12,17.6 Q12.9,18.7 13.8,17.6", 0.9));

        var face = new Canvas { Width = 24, Height = 24, RenderTransform = _look };
        body.Children.Add(face);
        var eyes = new Canvas { Width = 24, Height = 24 };
        var eyeTransforms = new TransformGroup();
        eyeTransforms.Children.Add(_eyeSize);
        eyeTransforms.Children.Add(_blink);
        eyes.RenderTransform = eyeTransforms;
        face.Children.Add(eyes);
        _eyeL = Place(new Ellipse { Width = 2.7, Height = 3.5, Fill = Ink }, 6.9, 11.85);
        _eyeR = Place(new Ellipse { Width = 2.7, Height = 3.5, Fill = Ink }, 14.4, 11.85);
        eyes.Children.Add(_eyeL);
        eyes.Children.Add(_eyeR);
        face.Children.Add(_happyEyes = Stroke("M6.8,14.4 Q8.25,12.1 9.7,14.4 M14.3,14.4 Q15.75,12.1 17.2,14.4", 1.4));
        face.Children.Add(_closedEyes = Stroke("M6.9,13.9 Q8.25,14.9 9.6,13.9 M14.4,13.9 Q15.75,14.9 17.1,13.9", 1.2));
        face.Children.Add(_annoyedEyes = Stroke("M7,12.3 L9.5,13.7 L7,15.1 M17,12.3 L14.5,13.7 L17,15.1", 1.3));
        face.Children.Add(_dizzyEyes = Stroke("M7,12.4 L9.5,15 M9.5,12.4 L7,15 M14.5,12.4 L17,15 M17,12.4 L14.5,15", 1.2));

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
        Show(Mood.Idle);
    }

    static T Place<T>(T element, double x, double y) where T : UIElement
    {
        SetLeft(element, x);
        SetTop(element, y);
        return element;
    }

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
            React(Mood.Dizzy, 2.6);
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
        _eyeL.Visibility = _eyeR.Visibility = eyes == "open" ? Visibility.Visible : Visibility.Hidden;
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
