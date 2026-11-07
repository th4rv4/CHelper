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
    private bool _touchpadOn = true; // assumed on at start; the EC toggles it and only reports "toggled"

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

            case BiosEvents.CameraOff:
            case BiosEvents.CameraOn:
                bool camOn = code == BiosEvents.CameraOn;
                Task.Run(() => _c.SyncCamera(camOn));
                _osd.ShowOsd("camera", camOn ? "Camera On" : "Camera Off");
                break;

            case BiosEvents.TouchpadToggle:
                Task.Run(ToggleTouchpad);
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
                _ => "CPU Heat",
            });
        }
    }

    /// <summary>
    /// The touchpad key only reports "toggled". Windows does the actual switching (Win+Ctrl+F24) and keeps the
    /// real state in the registry, so read it, toggle ourselves if nothing changed, and show the true state.
    /// </summary>
    private void ToggleTouchpad()
    {
        try
        {
            bool? before = Touchpad.IsEnabled();
            Thread.Sleep(400);
            bool? after = Touchpad.IsEnabled();
            string how = "by Windows";
            if (before.HasValue && after == before)
            {
                Touchpad.Toggle();
                how = "by C-Helper (Win+Ctrl+F24)";
                Thread.Sleep(400);
                after = Touchpad.IsEnabled();
            }
            bool on = after ?? !_touchpadOn;
            _touchpadOn = on;
            Log.Info($"Touchpad {(on ? "on" : "off")} (toggled {how}, registry {before?.ToString() ?? "?"} -> {after?.ToString() ?? "?"})");
            _main.BeginInvoke(new Action(() => _osd.ShowOsd("touchpad", on ? "Touchpad On" : "Touchpad Off")));
        }
        catch (Exception ex)
        {
            Log.Error("Touchpad toggle failed", ex);
        }
    }

    /// <summary>Caps / Num / Scroll Lock popup (call on the UI thread).</summary>
    public void ShowLock(Keys key)
    {
        // The hook fires before Windows flips the toggle, so read the state a moment later.
        var t = new System.Windows.Forms.Timer { Interval = 80 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            ShowLockState(key);
        };
        t.Start();
    }

    private void ShowLockState(Keys key)
    {
        bool on = Control.IsKeyLocked(key);
        Log.Info($"{key} is now {(on ? "on" : "off")}");
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

    /// <summary>Popup for a power mode change made outside the window (tray menu).</summary>
    public void ShowMode(PowerMode mode)
    {
        var (icon, name) = mode switch
        {
            PowerMode.Quiet => ("moon", "Quiet"),
            PowerMode.PowerSaving => ("leaf", "Power Saving"),
            PowerMode.Entertainment => ("controller", "Entertainment"),
            _ => ("turbo", "Performance"),
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
