using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace DynamicIsland.Services;

/// <summary>The phone as a game controller: a virtual Xbox 360 or DualShock 4 pad that games see as a real one, made
/// by the ViGEmBus driver (installed once, it is what DS4Windows and Steam use). Plugged in on the first report,
/// unplugged after a minute without one or when the phone's link drops.</summary>
public static class VirtualPad
{
    /// <summary>One frame of the phone's pad. Buttons are Xbox names (A = Cross on a DualShock); sticks −32768…32767,
    /// up positive; triggers 0…255.</summary>
    public readonly record struct State(bool Ps4, int Buttons, short Lx, short Ly, short Rx, short Ry, byte Lt, byte Rt);

    [Flags]
    public enum Bits
    {
        A = 1, B = 2, X = 4, Y = 8, Lb = 16, Rb = 32, Back = 64, Start = 128, Ls = 256, Rs = 512,
        Up = 1024, Down = 2048, Left = 4096, Right = 8192, Guide = 16384,
    }

    public const string DriverUrl = "https://github.com/nefarius/ViGEmBus/releases/latest";

    static readonly object Gate = new();
    static ViGEmClient? _client;
    static IXbox360Controller? _xbox;
    static IDualShock4Controller? _ds4;
    static DateTime _last;
    static Timer? _idle;

    /// <summary>Why there is no pad (the driver missing, most often), or null when it works.</summary>
    public static string? Problem { get; private set; }
    static DateTime _failedAt;

    /// <summary>ViGEmBus is installed (its service is registered).</summary>
    public static bool DriverInstalled
    {
        get
        {
            try { using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus"); return key != null; }
            catch { return false; }
        }
    }

    /// <summary>Applies a frame, plugging the pad in first if needed. False when it can't (see <see cref="Problem"/>).</summary>
    public static bool Apply(State s)
    {
        lock (Gate)
        {
            _last = DateTime.UtcNow;
            // without the driver every frame would fail the same way: try again every 10 s, not 60 times a second
            if (Problem != null && _last - _failedAt < TimeSpan.FromSeconds(10)) return false;
            try
            {
                if (s.Ps4) ApplyDs4(Ds4(), s);
                else ApplyXbox(Xbox(), s);
                Problem = null;
                _idle ??= new Timer(_ => { if (DateTime.UtcNow - _last > TimeSpan.FromMinutes(1)) Release(); }, null, 15_000, 15_000);
                return true;
            }
            catch (Exception ex)
            {
                Problem = ex is Nefarius.ViGEm.Client.Exceptions.VigemBusNotFoundException
                    ? "Falta o driver ViGEmBus no PC" : "O controle virtual não ligou: " + ex.GetType().Name;
                _failedAt = DateTime.UtcNow;
                Release();
                return false;
            }
        }
    }

    static IXbox360Controller Xbox()
    {
        if (_xbox != null) return _xbox;
        if (_ds4 != null) Release();
        _client ??= new ViGEmClient();
        var pad = _client.CreateXbox360Controller();
        pad.AutoSubmitReport = false;
        pad.Connect();
        return _xbox = pad;
    }

    static IDualShock4Controller Ds4()
    {
        if (_ds4 != null) return _ds4;
        if (_xbox != null) Release();
        _client ??= new ViGEmClient();
        var pad = _client.CreateDualShock4Controller();
        pad.AutoSubmitReport = false;
        pad.Connect();
        return _ds4 = pad;
    }

    static bool On(State s, Bits bit) => (s.Buttons & (int)bit) != 0;

    static void ApplyXbox(IXbox360Controller pad, State s)
    {
        pad.SetButtonState(Xbox360Button.A, On(s, Bits.A));
        pad.SetButtonState(Xbox360Button.B, On(s, Bits.B));
        pad.SetButtonState(Xbox360Button.X, On(s, Bits.X));
        pad.SetButtonState(Xbox360Button.Y, On(s, Bits.Y));
        pad.SetButtonState(Xbox360Button.LeftShoulder, On(s, Bits.Lb));
        pad.SetButtonState(Xbox360Button.RightShoulder, On(s, Bits.Rb));
        pad.SetButtonState(Xbox360Button.Back, On(s, Bits.Back));
        pad.SetButtonState(Xbox360Button.Start, On(s, Bits.Start));
        pad.SetButtonState(Xbox360Button.LeftThumb, On(s, Bits.Ls));
        pad.SetButtonState(Xbox360Button.RightThumb, On(s, Bits.Rs));
        pad.SetButtonState(Xbox360Button.Up, On(s, Bits.Up));
        pad.SetButtonState(Xbox360Button.Down, On(s, Bits.Down));
        pad.SetButtonState(Xbox360Button.Left, On(s, Bits.Left));
        pad.SetButtonState(Xbox360Button.Right, On(s, Bits.Right));
        pad.SetButtonState(Xbox360Button.Guide, On(s, Bits.Guide));
        pad.SetAxisValue(Xbox360Axis.LeftThumbX, s.Lx);
        pad.SetAxisValue(Xbox360Axis.LeftThumbY, s.Ly);
        pad.SetAxisValue(Xbox360Axis.RightThumbX, s.Rx);
        pad.SetAxisValue(Xbox360Axis.RightThumbY, s.Ry);
        pad.SetSliderValue(Xbox360Slider.LeftTrigger, s.Lt);
        pad.SetSliderValue(Xbox360Slider.RightTrigger, s.Rt);
        pad.SubmitReport();
    }

    static void ApplyDs4(IDualShock4Controller pad, State s)
    {
        pad.SetButtonState(DualShock4Button.Cross, On(s, Bits.A));
        pad.SetButtonState(DualShock4Button.Circle, On(s, Bits.B));
        pad.SetButtonState(DualShock4Button.Square, On(s, Bits.X));
        pad.SetButtonState(DualShock4Button.Triangle, On(s, Bits.Y));
        pad.SetButtonState(DualShock4Button.ShoulderLeft, On(s, Bits.Lb));
        pad.SetButtonState(DualShock4Button.ShoulderRight, On(s, Bits.Rb));
        pad.SetButtonState(DualShock4Button.Share, On(s, Bits.Back));
        pad.SetButtonState(DualShock4Button.Options, On(s, Bits.Start));
        pad.SetButtonState(DualShock4Button.ThumbLeft, On(s, Bits.Ls));
        pad.SetButtonState(DualShock4Button.ThumbRight, On(s, Bits.Rs));
        pad.SetButtonState(DualShock4Button.TriggerLeft, s.Lt > 30);
        pad.SetButtonState(DualShock4Button.TriggerRight, s.Rt > 30);
        pad.SetSpecialButtonsFull((byte)(On(s, Bits.Guide) ? 1 : 0)); // bit 0: PS
        pad.SetDPadDirection(DPad(On(s, Bits.Up), On(s, Bits.Down), On(s, Bits.Left), On(s, Bits.Right)));
        // a DualShock's sticks are 0…255 with 128 in the middle, and down is up
        pad.SetAxisValue(DualShock4Axis.LeftThumbX, ToByte(s.Lx));
        pad.SetAxisValue(DualShock4Axis.LeftThumbY, (byte)(255 - ToByte(s.Ly)));
        pad.SetAxisValue(DualShock4Axis.RightThumbX, ToByte(s.Rx));
        pad.SetAxisValue(DualShock4Axis.RightThumbY, (byte)(255 - ToByte(s.Ry)));
        pad.SetSliderValue(DualShock4Slider.LeftTrigger, s.Lt);
        pad.SetSliderValue(DualShock4Slider.RightTrigger, s.Rt);
        pad.SubmitReport();
    }

    public static byte ToByte(short axis) => (byte)((axis + 32768) >> 8);

    public static DualShock4DPadDirection DPad(bool up, bool down, bool left, bool right) => (up, down, left, right) switch
    {
        (true, false, true, false) => DualShock4DPadDirection.Northwest,
        (true, false, false, true) => DualShock4DPadDirection.Northeast,
        (false, true, true, false) => DualShock4DPadDirection.Southwest,
        (false, true, false, true) => DualShock4DPadDirection.Southeast,
        (true, false, _, _) => DualShock4DPadDirection.North,
        (false, true, _, _) => DualShock4DPadDirection.South,
        (_, _, true, false) => DualShock4DPadDirection.West,
        (_, _, false, true) => DualShock4DPadDirection.East,
        _ => DualShock4DPadDirection.None,
    };

    /// <summary>Unplugs the pad (the game sees the controller go away).</summary>
    public static void Release()
    {
        lock (Gate)
        {
            try { _xbox?.Disconnect(); } catch { }
            try { _ds4?.Disconnect(); } catch { }
            _xbox = null;
            _ds4 = null;
            _idle?.Dispose();
            _idle = null;
        }
    }
}
