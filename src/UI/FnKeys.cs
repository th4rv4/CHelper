using CHelper.Native;
using CHelper.Hardware;

namespace CHelper.UI;

/// <summary>Handles BIOS Fn-key events (on the UI thread) and shows the OSD.</summary>
public sealed class FnKeys
{
    // Keyboard colours cycled by Fn + numpad "/"
    private static readonly Color[] Colors =
    {
        Color.White, Color.FromArgb(255, 32, 32), Color.FromArgb(255, 128, 0), Color.FromArgb(255, 220, 0),
        Color.FromArgb(40, 220, 80), Color.FromArgb(0, 210, 210), Color.FromArgb(40, 110, 255),
        Color.FromArgb(150, 80, 255), Color.FromArgb(255, 60, 170),
    };

    private const int Step = 25;

    private readonly MainForm _main;
    private readonly OsdForm _osd = new();
    private readonly Controller _c = Controller.Instance;
    private int _lastOnBrightness = 75;

    public FnKeys(MainForm main)
    {
        _main = main;
    }

    /// <summary>Call on the UI thread.</summary>
    public void Handle(int code)
    {
        var s = Settings.Current;
        switch (code)
        {
            case BiosEvents.FnEsc:
                if (!s.FnEscOpensHelper) return;
                if (_main.Visible && Form.ActiveForm == _main) _main.Hide();
                else _main.ShowNearTray();
                break;

            case BiosEvents.KbBrightnessDown:
                SetBrightness(Math.Max(0, RoundToStep(s.KeyboardBrightness) - Step));
                break;

            case BiosEvents.KbBrightnessUp:
                SetBrightness(Math.Min(100, RoundToStep(s.KeyboardBrightness) + Step));
                break;

            case BiosEvents.KbToggle:
                if (s.KeyboardBrightness > 0)
                {
                    _lastOnBrightness = s.KeyboardBrightness;
                    SetBrightness(0);
                }
                else
                {
                    SetBrightness(_lastOnBrightness > 0 ? _lastOnBrightness : 75);
                }
                break;

            case BiosEvents.NumLockOn:
            case BiosEvents.NumLockOff:
                ShowLockFromBios(Keys.NumLock, code == BiosEvents.NumLockOn);
                break;

            case BiosEvents.CapsLockOn:
            case BiosEvents.CapsLockOff:
                ShowLockFromBios(Keys.CapsLock, code == BiosEvents.CapsLockOn);
                break;

            case BiosEvents.CameraOff:
            case BiosEvents.CameraOn:
                bool camOn = code == BiosEvents.CameraOn;
                Task.Run(() => _c.SyncCamera(camOn));
                _osd.ShowOsd("camera", camOn ? "Camera On" : "Camera Off");
                break;

            case BiosEvents.TouchpadToggle:
            case BiosEvents.TouchpadA:
            case BiosEvents.TouchpadB:
                PressTouchpad();
                break;

            case BiosEvents.PowerModeKey:
                CycleMode(+1);
                break;

            case BiosEvents.KbColor:
                CycleLighting(s);
                break;
        }
    }

    /// <summary>
    /// Fn + numpad "/": steps through the static colours, then the effects, then back to white.
    /// </summary>
    private void CycleLighting(AppSettings s)
    {
        // Where are we now? Colours are 0..n-1, effects follow.
        var effects = new[] { KbEffect.Breathing, KbEffect.ColorCycle, KbEffect.CpuHeat };
        int count = Colors.Length + effects.Length;
        int current = s.KeyboardEffect == KbEffect.Static
            ? Array.FindIndex(Colors, c => c.ToArgb() == Color.FromArgb(s.KeyboardColor).ToArgb())
            : Colors.Length + Array.IndexOf(effects, s.KeyboardEffect);
        int next = (current + 1) % count;

        if (next < Colors.Length)
        {
            var color = Colors[next];
            Task.Run(() => _c.SetKeyboardLook(KbEffect.Static, color));
            _osd.ShowOsd("backlight-up", $"Keyboard {ColorName(color)}");
        }
        else
        {
            var effect = effects[next - Colors.Length];
            // Breathing keeps the last static colour (or white); the others pick their own colours.
            Task.Run(() => _c.SetKeyboardLook(effect, null));
            _osd.ShowOsd(effect switch
            {
                KbEffect.Breathing => "fx-breathing",
                KbEffect.ColorCycle => "fx-cycle",
                _ => "fx-heat",
            }, effect switch
            {
                KbEffect.Breathing => "Breathing",
                KbEffect.ColorCycle => "Color Cycle",
                _ => "Heat",
            });
        }
    }

    // ---------------- touchpad ----------------
    // The touchpad key only reports "pressed"; Windows keeps the real state in the registry and switches it on
    // Win+Ctrl+F24. Each press flips the state we want and shows it straight away; one background worker then
    // brings Windows to that state. Fast presses no longer queue up a second of work each, and an even number
    // of presses that arrive while it is busy simply cancel out.

    private volatile bool _touchpadWanted = true;
    private int _touchpadBusy; // 1 while the worker runs

    /// <summary>Call on the UI thread.</summary>
    private void PressTouchpad()
    {
        bool busy = Volatile.Read(ref _touchpadBusy) == 1;
        bool current = busy ? _touchpadWanted : Touchpad.IsEnabled() ?? _touchpadWanted;
        bool want = !current;
        _touchpadWanted = want;
        _osd.ShowOsd("touchpad", want ? "Touchpad On" : "Touchpad Off");
        if (Interlocked.Exchange(ref _touchpadBusy, 1) == 0) Task.Run(SyncTouchpad);
    }

    private void SyncTouchpad()
    {
        try
        {
            while (true)
            {
                int toggles = 0;
                bool? actual = Touchpad.IsEnabled();
                // Windows itself may handle the key on some setups; give it a moment before stepping in.
                if (actual.HasValue && actual != _touchpadWanted) actual = WaitForChange(actual.Value, 150);

                while (actual != _touchpadWanted && toggles < 6)
                {
                    bool? before = actual;
                    Touchpad.Toggle();
                    toggles++;
                    if (!before.HasValue) break; // state unknown: one toggle per press is all we can do
                    actual = WaitForChange(before.Value, 800);
                }

                Log.Info($"Touchpad {(_touchpadWanted ? "on" : "off")} (registry {actual?.ToString() ?? "?"}, {toggles} toggle(s) by C-Helper)");

                Volatile.Write(ref _touchpadBusy, 0);
                // A press may have landed between the last check and releasing the worker.
                if (Touchpad.IsEnabled() is not bool now || now == _touchpadWanted) return;
                if (Interlocked.Exchange(ref _touchpadBusy, 1) != 0) return;
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _touchpadBusy, 0);
            Log.Error("Touchpad toggle failed", ex);
        }
    }

    /// <summary>Polls the registry until the state differs from <paramref name="from"/> (or the time runs out).</summary>
    private static bool? WaitForChange(bool from, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool? now = from;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(25);
            now = Touchpad.IsEnabled();
            if (now != from) break;
        }
        return now;
    }

    /// <summary>Caps / Num / Scroll Lock popup (call on the UI thread).</summary>
    public void ShowLock(Keys key)
    {
        // The hook fires before Windows flips the toggle, so read the state a moment later.
        // Num / Caps Lock wait a little longer so the BIOS notice (exact state) usually arrives first.
        var t = new System.Windows.Forms.Timer { Interval = key is Keys.NumLock or Keys.CapsLock ? 150 : 80 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            ShowLockState(key);
        };
        t.Start();
    }

    // When the BIOS reported a lock change, trust it over Windows (whose state reading lags on fast presses).
    private readonly Dictionary<Keys, DateTime> _biosLock = new();

    private void ShowLockFromBios(Keys key, bool on)
    {
        _biosLock[key] = DateTime.UtcNow;
        ShowLockPopup(key, on);
    }

    private void ShowLockState(Keys key)
    {
        // Num / Caps Lock: the BIOS notice (same press) already showed the right state.
        if (_biosLock.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(1)) return;
        bool on = Control.IsKeyLocked(key);
        Log.Info($"{key} is now {(on ? "on" : "off")} (from Windows)");
        ShowLockPopup(key, on);
    }

    private void ShowLockPopup(Keys key, bool on)
    {
        var (icon, title) = key switch
        {
            Keys.CapsLock => ("capslock", "Caps Lock"),
            Keys.NumLock => ("numlock", "Num Lock"),
            _ => ("scrolllock", "Scroll Lock"),
        };
        _osd.ShowOsd(icon, $"{title} {(on ? "On" : "Off")}");
    }

    private static int RoundToStep(int v) => (int)Math.Round(v / (double)Step) * Step;

    private void SetBrightness(int percent)
    {
        int before = Settings.Current.KeyboardBrightness;
        Task.Run(() => _c.SetKeyboardBrightness(percent));
        _osd.ShowOsd(percent < before ? "backlight-down" : "backlight-up", percent == 0 ? "Keyboard Off" : "Keyboard", percent == 0 ? null : percent);
    }

    private static readonly PowerMode[] CycleOrder =
        { PowerMode.Quiet, PowerMode.PowerSaving, PowerMode.Entertainment, PowerMode.Performance };

    /// <summary>Step to the next (or previous) power mode with its popup (Fn+F4, Ctrl+Shift+F5). UI thread.</summary>
    public void CycleMode(int step)
    {
        int i = Array.IndexOf(CycleOrder, _c.CurrentMode);
        var next = CycleOrder[((i < 0 ? 0 : i) + step + CycleOrder.Length) % CycleOrder.Length];
        Task.Run(() => _c.SetPowerMode(next));
        ShowMode(next);
    }

    /// <summary>Popup for a power mode change made outside the window (tray menu).</summary>
    /// <summary>A one-line popup (e.g. "Update Available · v0.9.1"). Runs on the UI thread.</summary>
    public void ShowText(string icon, string text) => _osd.ShowOsd(icon, text);

    public void ShowMode(PowerMode mode)
    {
        var (icon, name) = mode switch
        {
            PowerMode.Quiet => ("moon", "Quiet"),
            PowerMode.PowerSaving => ("leaf", "Power Saving"),
            PowerMode.Entertainment => ("controller", "Balanced"),
            _ => ("turbo", "Turbo"),
        };
        _osd.ShowOsd(icon, name);
    }

    private static string ColorName(Color c) => c.ToArgb() switch
    {
        _ when c.ToArgb() == Color.White.ToArgb() => "White",
        _ when c.R > 200 && c.G < 60 && c.B < 60 => "Red",
        _ when c.R > 200 && c.G > 100 && c.G < 160 => "Orange",
        _ when c.R > 200 && c.G > 200 => "Yellow",
        _ when c.G > 200 && c.R < 60 && c.B < 100 => "Green",
        _ when c.G > 200 && c.B > 200 => "Cyan",
        _ when c.B > 200 && c.R < 60 => "Blue",
        _ when c.B > 200 && c.R > 100 && c.G < 100 && c.R < 200 => "Purple",
        _ => "Pink",
    };
}
