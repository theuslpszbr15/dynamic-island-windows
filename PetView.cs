using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace DynamicIsland;

public enum PetSpecies { None, Coco, Pipoca, Bu, Bit }

/// <summary>Accessory on top of the mascot. Auto follows the calendar (Christmas, festa junina, New Year).</summary>
public enum PetOutfit { None, Auto, Gorro, Festa, Noel, Palha, Oculos, Coroa }

/// <summary>
/// The island's mascot, drawn from shapes on a 24×24 canvas (scale it with a Viewbox). Coco (the island's own
/// little coconut), Pipoca (a cat) and Bu (a ghost) share one face; a base mood follows what the island shows
/// and short reactions play on top of it.
/// </summary>
public sealed class PetView : Canvas
{
    public enum Mood { Idle, Dance, Working, Waiting, Sleep, Happy, Annoyed, Dizzy, Patted }

    // PatPat (after the Minecraft mod): a pixel hand pats the head in 5 frames over 300 ms.
    // Our own sprite; per frame: hand lift above the (squashed) head, hand squash, body squash X/Y.
    static readonly double[] PatHandLift = [-3.4, -1.8, 0.5, -0.8, -2.8];
    static readonly double[] PatHandSquash = [1, 0.95, 0.8, 0.9, 1];
    static readonly double[] PatBodyX = [1, 1.03, 1.12, 1.06, 1];
    static readonly double[] PatBodyY = [1, 0.96, 0.82, 0.9, 1];
    const int PatFrameMs = 60;

    static readonly string[] HandPixels =
    [
        "......OOOOOOOCCO",
        ".....OSSSSSSSOCC",
        "...OOSSSSSSSSSOC",
        "..OSSSSSSSSSSSOC",
        ".OSSSSSSSSSSSDO.",
        "OSSDSSDSSDSSDO..",
        "OSSDSSDSSDSDO...",
        "OSSOSSOSSOSO....",
        ".OO.OO.OO.O.....",
    ];

    static readonly string[] HeartPixels = ["XX.XX", "XXXXX", ".XXX.", "..X.."];

    static readonly BitmapSource HandSprite = Sprite(HandPixels, new()
    {
        ['O'] = Color.FromRgb(0x3B, 0x2A, 0x2A), ['S'] = Color.FromRgb(0xFF, 0xD7, 0xB5),
        ['D'] = Color.FromRgb(0xE3, 0xA8, 0x84), ['C'] = Color.FromRgb(0x4A, 0x90, 0xE2),
    });

    static readonly BitmapSource HeartSprite = Sprite(HeartPixels, new() { ['X'] = Color.FromRgb(0xFF, 0x4D, 0x6D) });

    static BitmapSource Sprite(string[] rows, Dictionary<char, Color> palette)
    {
        int w = rows[0].Length, h = rows.Length;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (!palette.TryGetValue(rows[y][x], out var c)) continue;
                var i = (y * w + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.B, c.G, c.R, 255);
            }
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static readonly Color Cream = Color.FromRgb(0xF4, 0xEF, 0xE6);
    /// <summary>Bit's hoodie when nothing tints it.</summary>
    public static readonly Color Hoodie = Color.FromRgb(0x3D, 0x44, 0x4D);
    static readonly Brush Ink = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E));
    static readonly Brush BitFace = Frozen(Color.FromRgb(0x1F, 0x23, 0x28));
    static readonly Brush BitRim = Frozen(Color.FromRgb(0x5A, 0x63, 0x6E));
    static readonly Brush Pink = Frozen(Color.FromRgb(0xFF, 0x8F, 0xA3));
    static readonly Brush Leaf = Frozen(Color.FromRgb(0x30, 0xC8, 0x5A));
    static readonly Brush LeafLight = Frozen(Color.FromRgb(0x62, 0xE0, 0x7E));
    static readonly Random Rng = new();

    /// <summary>No random blinks (deterministic snapshots).</summary>
    public static bool Still { get; set; }

    /// <summary>Raised when three quick pokes make it dizzy.</summary>
    public event Action? Dizzied;

    /// <summary>The mascot every PetView shows (the island's, the agent list's...).</summary>
    public static PetSpecies DefaultSpecies { get; private set; } = PetSpecies.Bit;
    static event Action? SpeciesChanged;

    public static void SetDefaultSpecies(PetSpecies species)
    {
        if (species == PetSpecies.None || species == DefaultSpecies) return;
        DefaultSpecies = species;
        SpeciesChanged?.Invoke();
    }

    public static PetOutfit DefaultOutfit { get; private set; } = PetOutfit.Auto;

    public static void SetDefaultOutfit(PetOutfit outfit)
    {
        if (outfit == DefaultOutfit) return;
        DefaultOutfit = outfit;
        SpeciesChanged?.Invoke();
    }

    public static PetOutfit Resolve(PetOutfit outfit, DateTime date) => outfit != PetOutfit.Auto ? outfit : date switch
    {
        { Month: 12, Day: 31 } or { Month: 1, Day: 1 } => PetOutfit.Festa,
        { Month: 12 } => PetOutfit.Noel,
        { Month: 6 or 7 } => PetOutfit.Palha,
        _ => PetOutfit.None,
    };

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
    // eyes and expression lines: dark on light bodies, white on Bit's dark face
    readonly SolidColorBrush _faceInk = new(Color.FromRgb(0x1C, 0x1C, 0x1E));
    readonly TranslateTransform _hop = new(), _look = new();
    readonly RotateTransform _tilt = new(0, 12, 20);
    readonly ScaleTransform _squash = new(1, 1, 12, 23.5), _blink = new(1, 1, 12, 13.6), _eyeSize = new(1, 1, 12, 13.6);
    readonly Canvas _shell = new() { Width = 24, Height = 24 };
    readonly Canvas _hat = new() { Width = 24, Height = 24 }, _glasses = new() { Width = 24, Height = 24 };
    readonly Canvas _eyes;
    readonly Path _happyEyes, _closedEyes, _annoyedEyes, _dizzyEyes;
    readonly TextBlock _z;
    readonly DispatcherTimer _blinkTimer, _reactionTimer, _patTimer;
    readonly Image _hand;
    readonly ScaleTransform _handSquash = new(1, 1);
    readonly TranslateTransform _handMove = new();
    bool _patting;

    PetSpecies _species = PetSpecies.None;
    PetOutfit _outfit = PetOutfit.None;
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
        face.Children.Add(_happyEyes = FaceStroke("M6.8,14.4 Q8.25,12.1 9.7,14.4 M14.3,14.4 Q15.75,12.1 17.2,14.4", 1.4));
        face.Children.Add(_closedEyes = FaceStroke("M6.9,13.9 Q8.25,14.9 9.6,13.9 M14.4,13.9 Q15.75,14.9 17.1,13.9", 1.2));
        face.Children.Add(_annoyedEyes = FaceStroke("M7,12.3 L9.5,13.7 L7,15.1 M17,12.3 L14.5,13.7 L17,15.1", 1.3));
        face.Children.Add(_dizzyEyes = FaceStroke(
            "M8.5,13.7 A0.25,0.25 0 1 1 8.0,13.7 A0.5,0.5 0 1 1 9.0,13.7 A1.0,1.0 0 1 1 7.0,13.7 A1.4,1.4 0 1 1 9.8,13.7 " +
            "M16.0,13.7 A0.25,0.25 0 1 1 15.5,13.7 A0.5,0.5 0 1 1 16.5,13.7 A1.0,1.0 0 1 1 14.5,13.7 A1.4,1.4 0 1 1 17.3,13.7", 0.6));
        face.Children.Add(_glasses);
        body.Children.Add(_hat);

        _z = new TextBlock { Text = "z", FontSize = 7, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Opacity = 0, RenderTransform = new TranslateTransform() };
        Children.Add(Place(_z, 19, -1));

        _hand = new Image
        {
            Source = HandSprite, Width = 14.4, Height = 8.1, Stretch = Stretch.Fill, Opacity = 0, IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 1),
        };
        var handTransforms = new TransformGroup();
        handTransforms.Children.Add(_handSquash);
        handTransforms.Children.Add(_handMove);
        _hand.RenderTransform = handTransforms;
        RenderOptions.SetBitmapScalingMode(_hand, BitmapScalingMode.NearestNeighbor);
        Children.Add(_hand);
        _patTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PatFrameMs * 5 + 30) };
        _patTimer.Tick += (_, _) =>
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Pat();
            else EndPatting();
        };

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _blinkTimer.Tick += (_, _) => Blink();
        _reactionTimer = new DispatcherTimer();
        _reactionTimer.Tick += (_, _) =>
        {
            _reactionTimer.Stop();
            Show(_base);
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            BeginPatting();
            e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_patting) EndPatting();
            else Poke();
            e.Handled = true;
        };
        MouseLeave += (_, _) => EndPatting();
        Species = DefaultSpecies;
        Outfit = Resolve(DefaultOutfit, DateTime.Today);
        void Sync()
        {
            Species = FixedSpecies ?? DefaultSpecies;
            Outfit = Resolve(DefaultOutfit, DateTime.Today);
        }
        Loaded += (_, _) =>
        {
            SpeciesChanged -= Sync;
            SpeciesChanged += Sync;
            Sync();
        };
        Unloaded += (_, _) => SpeciesChanged -= Sync;
        Show(Mood.Idle);
    }

    /// <summary>Always this mascot, whatever the island's choice (previews in the settings).</summary>
    public PetSpecies? FixedSpecies
    {
        get => _fixed;
        set
        {
            _fixed = value;
            if (value is { } species) Species = species;
        }
    }
    PetSpecies? _fixed;

    public PetSpecies Species
    {
        get => _species;
        set
        {
            if (_species == value || value == PetSpecies.None) return;
            _species = value;
            _shell.Children.Clear();
            _eyes.Children.Clear();
            var bit = value == PetSpecies.Bit;
            _faceInk.Color = bit ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1E);
            _fill.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _fill.Color = bit ? Hoodie : Cream;
            if (bit)
            {
                // big white eyes, pupils looking slightly inward
                foreach (var x in new[] { 8.25, 15.75 })
                {
                    _eyes.Children.Add(Place(new Ellipse { Width = 4.2, Height = 4.8, Fill = Brushes.White }, x - 2.1, 11.2));
                    _eyes.Children.Add(Place(new Ellipse { Width = 2.1, Height = 2.7, Fill = Ink }, x - 1.05 + (x < 12 ? 0.35 : -0.35), 12.5));
                    _eyes.Children.Add(Place(new Ellipse { Width = 0.9, Height = 0.9, Fill = Brushes.White }, x - 0.6 + (x < 12 ? 0.35 : -0.35), 12.75));
                }
            }
            else
            {
                foreach (var x in new[] { 6.9, 14.4 })
                {
                    _eyes.Children.Add(Place(new Ellipse { Width = 2.8, Height = 3.6, Fill = Ink }, x, 11.8));
                    _eyes.Children.Add(Place(new Ellipse { Width = 1.0, Height = 1.0, Fill = Brushes.White }, x + 0.45, 12.35));
                }
            }
            switch (value)
            {
                case PetSpecies.Bit:
                    // hoodie body peeking under the head, with its drawstrings
                    _shell.Children.Add(Fill("M6.2,18.6 C6.2,21.8 8.2,23.6 12,23.6 C15.8,23.6 17.8,21.8 17.8,18.6 Z", _fill));
                    _shell.Children.Add(new Path { Data = Geometry.Parse("M10.7,18.8 L10.5,21 M13.3,18.8 L13.5,21"), Stroke = Brushes.White, Opacity = 0.85, StrokeThickness = 0.55, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                    // hood with cat ears
                    foreach (var ear in new[] { "M3.6,10.4 L4.6,2.2 L10.4,6.4 Z", "M20.4,10.4 L19.4,2.2 L13.6,6.4 Z" })
                        _shell.Children.Add(new Path { Data = Geometry.Parse(ear), Fill = _fill, Stroke = _fill, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round });
                    _shell.Children.Add(Fill("M5.4,4.6 L6.1,8 L8.4,6.4 Z M18.6,4.6 L17.9,8 L15.6,6.4 Z", Pink));
                    _shell.Children.Add(new Path
                    {
                        Data = Geometry.Parse("M12,4.4 C18.5,4.4 21.6,8.5 21.6,13.2 C21.6,17.7 17.7,20.2 12,20.2 C6.3,20.2 2.4,17.7 2.4,13.2 C2.4,8.5 5.5,4.4 12,4.4 Z"),
                        Fill = _fill, Stroke = BitRim, StrokeThickness = 0.45,
                    });
                    // the face inside the hood
                    _shell.Children.Add(new Path
                    {
                        Data = Geometry.Parse("M12,7.3 C16.9,7.3 19.2,10.1 19.2,13.4 C19.2,16.7 16.3,18.6 12,18.6 C7.7,18.6 4.8,16.7 4.8,13.4 C4.8,10.1 7.1,7.3 12,7.3 Z"),
                        Fill = BitFace, Stroke = BitRim, StrokeThickness = 0.35,
                    });
                    _shell.Children.Add(Place(new Ellipse { Width = 2.2, Height = 1.2, Fill = Pink, Opacity = 0.45 }, 5.6, 16.2));
                    _shell.Children.Add(Place(new Ellipse { Width = 2.2, Height = 1.2, Fill = Pink, Opacity = 0.45 }, 16.2, 16.2));
                    _shell.Children.Add(Fill("M11.35,15.95 L12.65,15.95 L12,16.7 Z", Pink));
                    _shell.Children.Add(new Path { Data = Geometry.Parse("M10.9,17.1 Q11.45,17.7 12,17.1 Q12.55,17.7 13.1,17.1"), Stroke = BitRim, StrokeThickness = 0.55, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                    break;
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
            DrawOutfit();
        }
    }

    public PetOutfit Outfit
    {
        get => _outfit;
        set
        {
            if (_outfit == value) return;
            _outfit = value;
            DrawOutfit();
        }
    }

    // Hats are drawn with their brim on y = 0 and moved onto the top of each body.
    void DrawOutfit()
    {
        _hat.Children.Clear();
        _glasses.Children.Clear();
        var top = _species switch { PetSpecies.Pipoca => 7.4, PetSpecies.Bu => 5.4, PetSpecies.Bit => 6.2, _ => 7.0 };
        _hat.RenderTransform = new TranslateTransform(0, top);
        static Brush B(uint argb) => Frozen(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        var white = B(0xFFF8F8F8);

        switch (_outfit)
        {
            case PetOutfit.Gorro:
                _hat.Children.Add(Fill("M5.6,0 C5.6,-6.2 18.4,-6.2 18.4,0 Z", B(0xFF0A84FF)));
                _hat.Children.Add(new Path { Data = Geometry.Parse("M8.4,-4 L8.9,-1 M12,-4.9 L12,-1 M15.6,-4 L15.1,-1"), Stroke = B(0x40FFFFFF), StrokeThickness = 0.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                _hat.Children.Add(Place(new Rectangle { Width = 14.4, Height = 2.6, RadiusX = 1.3, RadiusY = 1.3, Fill = B(0xFF64B5FF) }, 4.8, -1.4));
                _hat.Children.Add(Place(new Ellipse { Width = 3.2, Height = 3.2, Fill = white }, 10.4, -7.6));
                break;
            case PetOutfit.Festa:
                _hat.Children.Add(Fill("M8.4,0.4 L12.6,-9.2 L16.2,0.4 Z", B(0xFFBF5AF2)));
                _hat.Children.Add(Fill("M9.9,-3.4 L14.8,-3.4 L15.3,-2 L9.4,-2 Z M11.5,-7 L13.6,-7 L14,-5.8 L11,-5.8 Z", B(0xFFFFD60A)));
                _hat.Children.Add(Place(new Ellipse { Width = 2.8, Height = 2.8, Fill = B(0xFFFF375F) }, 11.2, -10.6));
                break;
            case PetOutfit.Noel:
                _hat.Children.Add(Fill("M5.4,0 C5.6,-6 14.6,-8.4 20.2,-3.6 C18.6,-3.2 18.6,-1.6 18.6,0 Z", B(0xFFE5242B)));
                _hat.Children.Add(Place(new Rectangle { Width = 15, Height = 2.8, RadiusX = 1.4, RadiusY = 1.4, Fill = white }, 4.5, -1.5));
                _hat.Children.Add(Place(new Ellipse { Width = 3.2, Height = 3.2, Fill = white }, 18.8, -5.2));
                break;
            case PetOutfit.Palha:
                _hat.Children.Add(Place(new Ellipse { Width = 22, Height = 3.6, Fill = B(0xFFE9C46A) }, 1, -2.2));
                _hat.Children.Add(Fill("M7.2,-0.8 C7.2,-6.2 16.8,-6.2 16.8,-0.8 Z", B(0xFFD9A441)));
                _hat.Children.Add(Place(new Rectangle { Width = 9.6, Height = 1.3, Fill = B(0xFFE5242B) }, 7.2, -2.3));
                break;
            case PetOutfit.Coroa:
                _hat.Children.Add(Fill("M6.6,-0.2 L6.2,-5.4 L9.4,-2.6 L12,-6.6 L14.6,-2.6 L17.8,-5.4 L17.4,-0.2 Z", B(0xFFFFD60A)));
                foreach (var x in new[] { 6.2, 11.3, 17.0 })
                    _hat.Children.Add(Place(new Ellipse { Width = 1.4, Height = 1.4, Fill = B(0xFFFF375F) }, x - 0.3, x == 11.3 ? -7.6 : -6.4));
                break;
            case PetOutfit.Oculos:
                var lens = B(0xF01C1C1E);
                _glasses.Children.Add(Place(new Rectangle { Width = 5, Height = 3.6, RadiusX = 1.6, RadiusY = 1.6, Fill = lens }, 5.8, 11.9));
                _glasses.Children.Add(Place(new Rectangle { Width = 5, Height = 3.6, RadiusX = 1.6, RadiusY = 1.6, Fill = lens }, 13.2, 11.9));
                _glasses.Children.Add(Place(new Rectangle { Width = 2.6, Height = 0.8, Fill = lens }, 10.7, 12.6));
                _glasses.Children.Add(Place(new Rectangle { Width = 1.6, Height = 0.6, RadiusX = 0.3, RadiusY = 0.3, Fill = B(0x99FFFFFF) }, 6.7, 12.6));
                _glasses.Children.Add(Place(new Rectangle { Width = 1.6, Height = 0.6, RadiusX = 0.3, RadiusY = 0.3, Fill = B(0x99FFFFFF) }, 14.1, 12.6));
                break;
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

    Path FaceStroke(string data, double thickness)
    {
        var path = Stroke(data, thickness);
        path.Stroke = _faceInk;
        return path;
    }

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
        Color target;
        if (_species == PetSpecies.Bit)
            // a dark hoodie in the state's colour
            target = color is { } h ? Color.FromRgb((byte)(h.R * 0.42 + 0x22 * 0.58), (byte)(h.G * 0.42 + 0x26 * 0.58), (byte)(h.B * 0.42 + 0x2B * 0.58)) : Hoodie;
        else
            target = color is { } c ? Color.FromRgb((byte)((c.R + 255 * 1.4) / 2.4), (byte)((c.G + 255 * 1.4) / 2.4), (byte)((c.B + 255 * 1.4) / 2.4)) : Cream;
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

    /// <summary>Starts patting; keeps patting while the button stays down with Shift.</summary>
    public void BeginPatting()
    {
        _patting = true;
        Pat();
        _patTimer.Start();
    }

    public void EndPatting()
    {
        if (!_patting) return;
        _patting = false;
        _patTimer.Stop();
        _hand.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(260) });
    }

    double HeadTop => _species switch { PetSpecies.Coco => 6.2, PetSpecies.Pipoca => 6.6, _ => 4.4 };

    // The body squashes towards its feet (y 23.5), so the head comes down and the hand follows it.
    double[] PatHandY => PatHandLift.Select((lift, i) => (23.5 - HeadTop) * (1 - PatBodyY[i]) + lift).ToArray();

    /// <summary>One pat: the hand comes down on the head, the mascot squashes and a heart floats up.</summary>
    public void Pat()
    {
        _pokes = 0;
        React(Mood.Patted, 1.1);
        SetLeft(_hand, 12 - _hand.Width / 2 + 0.8);
        SetTop(_hand, HeadTop - _hand.Height + 0.6);
        _hand.BeginAnimation(OpacityProperty, null);
        _hand.Opacity = 1;
        Frames(_handMove, TranslateTransform.YProperty, PatHandY);
        Frames(_handSquash, ScaleTransform.ScaleYProperty, PatHandSquash);
        Frames(_squash, ScaleTransform.ScaleXProperty, PatBodyX);
        Frames(_squash, ScaleTransform.ScaleYProperty, PatBodyY);
        if (!_patting) EndPattingSoon();
        Heart();
    }

    void EndPattingSoon() =>
        _hand.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(PatFrameMs * 5 + 120) });

    // Sprite-like: each value holds for one frame, no tweening between them.
    static void Frames(Animatable target, DependencyProperty property, double[] values)
    {
        var frames = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(PatFrameMs * values.Length), FillBehavior = FillBehavior.Stop };
        for (var i = 0; i < values.Length; i++)
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(values[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(PatFrameMs * i))));
        target.BeginAnimation(property, frames);
    }

    void Heart()
    {
        var heart = new Image { Source = HeartSprite, Width = 3.5, Height = 2.8, IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
        RenderOptions.SetBitmapScalingMode(heart, BitmapScalingMode.NearestNeighbor);
        Children.Add(Place(heart, 4 + Rng.NextDouble() * 14, HeadTop - 1.5));
        var rise = new DoubleAnimation(0, -7, TimeSpan.FromMilliseconds(700)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(700)) { BeginTime = TimeSpan.Zero };
        fade.Completed += (_, _) => Children.Remove(heart);
        ((TranslateTransform)heart.RenderTransform).BeginAnimation(TranslateTransform.YProperty, rise);
        heart.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>For snapshots: freeze on one pat frame (0–4).</summary>
    public void ShowPatFrame(int frame)
    {
        Show(Mood.Patted);
        SetLeft(_hand, 12 - _hand.Width / 2 + 0.8);
        SetTop(_hand, HeadTop - _hand.Height + 0.6);
        _hand.Opacity = 1;
        _handMove.Y = PatHandY[frame];
        _handSquash.ScaleY = PatHandSquash[frame];
        _squash.ScaleX = PatBodyX[frame];
        _squash.ScaleY = PatBodyY[frame];
    }

    void Show(Mood mood)
    {
        _shown = mood;
        StopLoops();

        var eyes = mood switch
        {
            Mood.Happy or Mood.Dance or Mood.Patted => "happy",
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