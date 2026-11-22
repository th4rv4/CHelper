using System.Diagnostics;
using System.Net.NetworkInformation;
using CHelper.Hardware;
using CHelper.Native;
using Microsoft.Win32;

namespace CHelper.UI;

public sealed class MainForm : Form
{
    // Layout in 96-DPI units; everything goes through S() so it scales with the display.
    private const int ContentWidth = 508;
    private const int Pad = 16;

    // UI order of the modes
    private static readonly PowerMode[] ModeOrder =
        { PowerMode.Quiet, PowerMode.PowerSaving, PowerMode.Entertainment, PowerMode.Performance };
    private static readonly GpuMode[] GpuOrder =
        { GpuMode.Eco, GpuMode.Standard, GpuMode.Optimized, GpuMode.Ultimate };
    private static readonly FanMode[] FanOrder =
        { FanMode.Automatic, FanMode.Maximum, FanMode.Custom };

    private readonly Controller _c = Controller.Instance;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };

    private Font _fBody, _fSmall, _fHead, _fBig;
    private Label _stats, _modeHint, _gpuHint, _fanHint, _screenHint, _kbHint, _chargeHint;
    private Label _batCharge, _batRateLabel, _batRate, _batHealth;
    private TileButton _fullCharge;
    private TileButton _stockBtn;
    private bool _stockFound; // kept here so the button survives the window being rebuilt
    private const int StockW = 150;
    private Label _optionsHint;
    private string _clamshellHint = ""; // the Clamshell part of the Options hint

    // Wi-Fi Driver Refresh and AdGuard DNS, on the last row of Options; the Options hint shows Wi-Fi trouble first.
    private TileButton _dnsBtn;
    private (int Adapters, int On) _dns;
    private bool _dnsBusy;
    // Network changes (an adapter coming back after a refresh, a new Ethernet / tethering): put AdGuard DNS on it.
    private readonly System.Windows.Forms.Timer _dnsApply = new() { Interval = 5000 };

    // Wi-Fi Driver Refresh in Options; red while the Wi-Fi card is stopped or missing (see WifiDriver).
    private const string WifiLabel = "Wi-Fi Driver Refresh";
    private TileButton _wifiBtn;
    private WifiStatus _wifi;
    private bool _wifiChecking, _wifiFixing, _wifiNotified;
    private DateTime _wifiChecked;
    // What the button and hint show; kept here so they survive the window being rebuilt.
    private (bool Visible, string Text, Color Accent, bool Active, bool Enabled, int Progress, string Hint, Color HintColor) _wifiUi =
        (true, WifiLabel, Theme.Turbo, false, true, -1, null, default);
    private readonly System.Windows.Forms.Timer _wifiDone = new() { Interval = 5000 };
    // After sleep or a network drop: look again once things have settled.
    private readonly System.Windows.Forms.Timer _wifiRecheck = new() { Interval = 15_000 };

    /// <summary>The Wi-Fi card was found stopped (once each time it breaks).</summary>
    public event Action WifiStopped;
    private StepSlider _charge;
    private SegRow _modes, _gpu, _fans, _screen, _options, _options2, _kbEffects, _kbSpeed;
    private TileButton _kbTimeoutAC, _kbTimeoutDC;
    private readonly ToolTip _tips = new() { InitialDelay = 400 };
    private FanCurveForm _curves;
    private TileButton _soundBtn;
    private bool _soundFound; // kept here so the button survives the window being rebuilt
    private SoundPage _soundPage;
    private int _footerTop; // where the footer starts; the Sound page fills the window above it

    private static readonly KbEffect[] EffectOrder = { KbEffect.Static, KbEffect.Breathing, KbEffect.ColorCycle, KbEffect.CpuHeat };
    private static readonly KbSpeed[] SpeedOrder = { KbSpeed.Slow, KbSpeed.Normal, KbSpeed.Fast };
    private static readonly HeatSource[] HeatOrder = { HeatSource.Cpu, HeatSource.Gpu, HeatSource.Hottest };
    private Label _kbSpeedLabel;
    private Label _verLabel;
    private TileButton _updateBtn;
    private UpdateInfo _update;
    private bool _checking, _installing;

    /// <summary>The once-a-day check found a version the popup hasn't been shown for yet.</summary>
    public event Action<UpdateInfo> UpdateFound;

    // First check 30 s after start, then once a day.
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 30_000 };
    private static readonly int[] TimeoutSteps = { 0, 15, 30, 60, 300 };
    private static readonly Color[] Rainbow =
        { Color.Red, Color.Yellow, Color.Lime, Color.Cyan, Color.Blue, Color.Magenta, Color.Red };
    private static readonly Color[] Heat =
        { KeyboardEffects.HeatColor(45), KeyboardEffects.HeatColor(65), KeyboardEffects.HeatColor(85) };
    private StepSlider _kbSlider;
    private TileButton _kbColor;
    private int[] _screenValues = Array.Empty<int>();
    private int _y;
    private bool _startupEnabled;

    public event Action QuitRequested;

    public MainForm()
    {
        Text = "C-Helper";
        Icon = Theme.AppIcon;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;

        BuildUi();
        ApplyScale(this);

        _c.StateChanged += OnStateChanged;
        _c.SensorsChanged += () =>
        {
            if (IsHandleCreated && !IsDisposed && Visible) BeginInvoke(new Action(UpdateStats));
        };
        _poll.Tick += (_, _) =>
        {
            if (!Visible) return;
            Task.Run(() =>
            {
                _c.RefreshSensors();
                _c.RefreshDgpuPresence();
            });
        };
        _poll.Start();
        _trim.Tick += (_, _) =>
        {
            _trim.Stop();
            if (!Visible) Task.Run(() => Memory.Trim("hidden"));
        };
        _updateTimer.Tick += (_, _) =>
        {
            if (_updateTimer.Interval == 30_000)
            {
                Updater.CleanUp();
                _ = CheckStockSoftwareAsync();
                _ = CheckWifiAsync("startup");
            }
            _updateTimer.Interval = 24 * 60 * 60 * 1000;
            if (Settings.Current.CheckForUpdates) _ = CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
        Task.Run(() =>
        {
            _startupEnabled = Startup.IsEnabled();
            OnStateChanged();
        });
        _ = CheckSoundAsync();

        _wifiDone.Tick += (_, _) =>
        {
            _wifiDone.Stop();
            ShowWifiState();
        };
        _wifiRecheck.Tick += (_, _) =>
        {
            _wifiRecheck.Stop();
            _ = CheckWifiAsync("recheck");
        };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _dnsApply.Tick += (_, _) =>
        {
            _dnsApply.Stop();
            _ = ApplyDnsAsync();
        };
        _dnsApply.Start(); // first look shortly after start-up
        Disposed += (_, _) =>
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        };
    }

    private int S(float v) => (int)Math.Round(v * DeviceDpi / 96f * Theme.MainScale);

    /// <summary>Hands the main window's size factor to the custom-drawn controls (tiles, sliders).</summary>
    private static void ApplyScale(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is TileButton t) t.UiScale = Theme.MainScale;
            else if (c is StepSlider s) s.UiScale = Theme.MainScale;
            ApplyScale(c);
        }
    }

    protected override void WndProc(ref Message m)
    {
        // Keyboard-layout changes go straight to Windows (see InputLanguageGuard).
        if (InputLanguageGuard.IsLayoutMessage(m.Msg)) { DefWndProc(ref m); return; }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // Moved to a monitor with a different scale: rebuild at the new size.
        Log.Info($"Window rebuilt for {DeviceDpi} DPI");
        bool soundOpen = _soundPage is { Visible: true };
        SuspendLayout();
        var old = Controls.Cast<Control>().ToList();
        Controls.Clear();
        old.ForEach(c => c.Dispose());
        _soundPage = null;
        BuildUi();
        ApplyScale(this);
        ResumeLayout();
        if (soundOpen) OpenSound();
        RefreshState();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            ShowHome(); // reopens on the home page
            return;
        }
        base.OnFormClosing(e);
    }

    // Hidden to the tray: give memory back to Windows shortly after (if it stays hidden).
    private readonly System.Windows.Forms.Timer _trim = new() { Interval = 1500 };

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            _trim.Stop();
            _trim.Start();
            return;
        }
        _trim.Stop();
        RefreshState();
        Task.Run(() => _c.RefreshSensors());
        if ((DateTime.Now - _wifiChecked).TotalSeconds > 30) _ = CheckWifiAsync("window opened");
    }

    // ---------------- layout ----------------

    private void BuildUi()
    {
        _fBody = Theme.Px(S(12.5f));
        _fSmall = Theme.Px(S(11.5f));
        _fHead = Theme.Px(S(13.5f), semibold: true);
        _fBig = Theme.Px(S(16f), semibold: true);
        Font = _fBody;

        _y = 12;
        _stats = new Label
        {
            Location = new Point(S(Pad), S(_y)),
            Size = new Size(S(ContentWidth), S(18)),
            ForeColor = Theme.Muted,
            Font = _fSmall,
        };
        Controls.Add(_stats);
        _y += 28;

        _modeHint = Section("gauge", "Power Mode");
        _modes = Row(66, true, 26,
            new("Quiet", "moon", Theme.Eco),
            new("Power Saving", "leaf", Theme.Eco),
            new("Balanced", "controller", Theme.Standard),
            new("Turbo", "turbo", Theme.Turbo));
        _modes.ButtonClicked += i => Run(() => _c.SetPowerMode(ModeOrder[i]));

        _gpuHint = Section("gpu", "GPU Mode");
        _gpu = Row(66, true, 26,
            new("Eco", "eco", Theme.Eco),
            new("Standard", "standard", Theme.Standard),
            new("Optimized", "optimized", Theme.Optimized),
            new("dGPU Only", "ultimate", Theme.Turbo));
        _gpu.ButtonClicked += i => OnGpuClicked(GpuOrder[i]);

        _fanHint = Section("fan", "Fans");
        _fans = Row(40, true, 0,
            new("Automatic"), new("Maximum", null, Theme.Turbo), new("Custom"), new("Edit Curves"));
        _fans.ButtonClicked += i =>
        {
            if (i == FanOrder.Length) OpenCurves();
            else Run(() => _c.SetFanMode(FanOrder[i]));
        };

        _screenHint = Section("screen", "Laptop Screen");
        BuildScreenRow();

        _kbHint = Section("keyboard", "Keyboard Lighting");
        _kbEffects = Row(38, false, 16,
            new("Static", "fx-static"), new("Breathing", "fx-breathing"),
            new("Color Cycle", "fx-cycle"), new("Heat", "fx-heat"));
        _kbEffects.ButtonClicked += i => Run(() => _c.SetKeyboardEffect(EffectOrder[i]));
        _y -= 4;

        _kbColor = new TileButton
        {
            Location = new Point(S(Pad), S(_y)),
            Size = new Size(S(52), S(40)),
            Font = _fBody,
        };
        _kbColor.Click += (_, _) => PickColor();
        Controls.Add(_kbColor);
        _kbSlider = new StepSlider
        {
            Location = new Point(S(Pad + 60), S(_y - 2)),
            Size = new Size(S(ContentWidth - 60), S(44)),
            Font = _fSmall,
            Accent = Theme.Standard,
            Values = Enumerable.Range(0, 21).Select(i => i * 5).ToArray(),
            LabelEvery = 5,
            Format = v => v == 0 ? "Off" : $"{v}%",
        };
        _kbSlider.ValuePreview += v =>
        {
            _kbHint.Text = v == 0 ? "Off" : $"Brightness {v}%";
            _c.PreviewKeyboardBrightness(v);
        };
        _kbSlider.ValueCommitted += v => Run(() => _c.SetKeyboardBrightness(v));
        Controls.Add(_kbSlider);
        _y += 40 + 10;

        // Speed (animated effects) and the backlight timer (plugged in / on battery) on one line
        const int timeoutW = 224;
        _kbSpeedLabel = new Label
        {
            Text = "Speed",
            Location = new Point(S(Pad), S(_y + 7)),
            Size = new Size(S(50), S(18)),
            ForeColor = Theme.Muted,
            Font = _fSmall,
        };
        Controls.Add(_kbSpeedLabel);
        _kbSpeed = new SegRow(S(32), S(6), _fBody, false, 0, new("Slow"), new("Normal"), new("Fast"))
        {
            Location = new Point(S(Pad + 52), S(_y)),
            Width = S(ContentWidth - 52 - timeoutW - 12),
        };
        // Speed for Breathing / Color Cycle; for Heat the same row picks the temperature source.
        _kbSpeed.ButtonClicked += i =>
        {
            if (Settings.Current.KeyboardEffect == KbEffect.CpuHeat) Run(() => _c.SetKeyboardHeatSource(HeatOrder[i]));
            else Run(() => _c.SetKeyboardSpeed(SpeedOrder[i]));
        };
        Controls.Add(_kbSpeed);
        _kbTimeoutAC = TimeoutButton("plugged", Pad + ContentWidth - timeoutW, pluggedIn: true);
        _kbTimeoutDC = TimeoutButton("battery-saver", Pad + ContentWidth - timeoutW + 116, pluggedIn: false);
        _y += 32 + 12;

        _optionsHint = Section("settings", "Options");
        _optionsHint.Left = S(Pad + 110);
        // Remove Stock App at the right end of the title row; only shown while the stock software is still there.
        _stockBtn = new TileButton
        {
            Text = "Remove Stock App",
            Vertical = false,
            Font = _fSmall,
            Accent = Theme.Turbo,
            Location = new Point(S(Pad + ContentWidth - StockW), S(_y - 28 - 3)),
            Size = new Size(S(StockW), S(26)),
            Visible = _stockFound,
        };
        _stockBtn.Click += (_, _) => OpenStockSoftware();
        _tips.SetToolTip(_stockBtn, "The stock Control Center is still on this laptop. C-Helper doesn't need it.");
        Controls.Add(_stockBtn);
        LayoutOptionsTitle();
        // Options grid: 3 + 3 + 2 (Wi-Fi Driver Refresh and AdGuard DNS share the last row).
        _options = Row(46, false, 18,
            new("Camera", "camera"), new("Auto Mode", "automation"), new("Startup", "startup"));
        _options.ButtonClicked += OnOptionClicked;
        _y -= 12 - 8; // rows of one grid sit closer together
        _options2 = Row(46, false, 18,
            new("Fn+Esc", "keyboard"), new("Clamshell", "clamshell"), new("Full Charge Once", "plugged", Theme.Accent));
        _options2.ButtonClicked += i =>
        {
            if (i < 2) OnOptionClicked(i + 3);
            else Run(() =>
            {
                if (_c.FullChargeOnce) _c.CancelFullChargeOnce("cancelled");
                else _c.StartFullChargeOnce();
            });
        };
        _fullCharge = _options2[2];
        _tips.SetToolTip(_fullCharge, "Charge to 100% this time. Your charge limit comes back by itself when you unplug.");
        _y -= 12 - 8;
        _tips.SetToolTip(_options2[1], "Clamshell: on the charger, closing the lid keeps the laptop running. Unplug it with the lid closed and it sleeps.");
        Lid.Changed -= OnLidChanged; // the layout can be rebuilt
        Lid.Changed += OnLidChanged;

        int half = (ContentWidth - 6) / 2;
        _wifiBtn = new TileButton
        {
            Text = WifiLabel,
            IconName = "wifi",
            IconSize = 18,
            Vertical = false,
            Font = _fBody,
            Accent = Theme.Turbo,
            Location = new Point(S(Pad), S(_y)),
            Size = new Size(S(half), S(46)),
        };
        _wifiBtn.Click += (_, _) =>
        {
            if (_wifiUi.Visible && _wifiUi.Enabled) _ = FixWifiAsync();
        };
        _tips.SetToolTip(_wifiBtn,
            "Removes every Intel Wi-Fi driver on the laptop and reinstalls the last one that worked, then adds your saved Wi-Fi networks back. Use it when Wi-Fi stops or disappears.");
        Controls.Add(_wifiBtn);
        _dnsBtn = new TileButton
        {
            Text = "AdGuard DNS",
            IconName = "shield",
            IconSize = 18,
            Vertical = false,
            Font = _fBody,
            Accent = Theme.Standard,
            Location = new Point(S(Pad + half + 6), S(_y)),
            Size = new Size(S(ContentWidth - half - 6), S(46)),
        };
        _dnsBtn.Click += (_, _) => _ = ToggleDnsAsync();
        _tips.SetToolTip(_dnsBtn,
            "Blocks ads and trackers for the whole laptop with AdGuard's DNS (with DNS over HTTPS), on Wi-Fi, Ethernet and tethering. Comes back by itself after a Wi-Fi Driver Refresh.");
        Controls.Add(_dnsBtn);
        _y += 46 + 12;
        ApplyWifiUi();
        ApplyDnsUi();

        _chargeHint = Section("battery-saver", "Battery Charge Limit");
        _charge = new StepSlider
        {
            Location = new Point(S(Pad), S(_y - 4)),
            Size = new Size(S(ContentWidth), S(46)),
            Font = _fSmall,
            Enabled = false,
        };
        _charge.ValuePreview += v => _chargeHint.Text = ChargeText(v, null);
        _charge.ValueCommitted += v => Run(() => _c.SetChargeLimit(v));
        Controls.Add(_charge);
        _y += 46 + 6;

        // Charge · Discharging/Charging W · Health (G-Helper style readouts)
        Controls.Add(new Panel
        {
            Location = new Point(S(Pad), S(_y)),
            Size = new Size(S(ContentWidth), Math.Max(1, S(1))),
            BackColor = Theme.Line,
        });
        _y += 10;
        int colW = ContentWidth / 3;
        (_, _batCharge) = Readout(0, colW, "Charge");
        (_batRateLabel, _batRate) = Readout(1, colW, "Battery");
        (_, _batHealth) = Readout(2, colW, "Health");
        _y += 46;

        // Footer
        _footerTop = _y;
        Controls.Add(new Panel
        {
            Location = new Point(0, S(_y)),
            Size = new Size(S(ContentWidth + Pad * 2), Math.Max(1, S(1))),
            BackColor = Theme.Line,
        });
        _y += 10;

        // Version · update status; click to check now. Replaced by an Update button when a newer release exists.
        _verLabel = new Label
        {
            Text = Updater.CurrentText,
            Location = new Point(S(Pad), S(_y + 9)),
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = _fSmall,
            Cursor = Cursors.Hand,
        };
        _verLabel.Click += (_, _) => _ = CheckForUpdatesAsync(manual: true);
        Controls.Add(_verLabel);

        _updateBtn = new TileButton
        {
            IconName = "startup",
            IconSize = 14,
            Vertical = false,
            Active = true,
            Accent = Theme.Accent,
            Font = _fSmall,
            Location = new Point(S(Pad), S(_y + 1)),
            Size = new Size(S(176), S(32)),
            Visible = false,
        };
        _updateBtn.Click += (_, _) => _ = InstallUpdateAsync();
        Controls.Add(_updateBtn);

        // Sound panel (Sound Blaster Studio); only on laptops where Creative's sound engine is installed.
        _soundBtn = new TileButton
        {
            Text = "Sound",
            IconName = "speaker",
            IconSize = 14,
            Vertical = false,
            Borderless = true,
            Font = _fSmall,
            Location = new Point(S(Pad + 196), S(_y + 2)),
            Size = new Size(S(80), S(30)),
            Visible = _soundFound,
            Accent = Theme.Eco,
        };
        _soundBtn.Click += (_, _) => OpenSound();
        Controls.Add(_soundBtn);

        var log = new TileButton
        {
            Text = "Log",
            IconName = "log",
            IconSize = 14,
            Vertical = false,
            Borderless = true,
            Font = _fSmall,
            Location = new Point(S(Pad + 196 + (_soundFound ? 88 : 0)), S(_y + 2)),
            Size = new Size(S(64), S(30)),
        };
        log.Click += (_, _) => OpenLog();
        Controls.Add(log);

        var quit = new TileButton
        {
            Text = "Quit",
            IconName = "quit",
            IconSize = 16,
            Vertical = false,
            Font = _fBody,
            Location = new Point(S(Pad + ContentWidth - 96), S(_y)),
            Size = new Size(S(96), S(34)),
        };
        quit.Click += (_, _) => QuitRequested?.Invoke();
        Controls.Add(quit);
        _y += 34 + 12;

        ClientSize = new Size(S(ContentWidth + Pad * 2), S(_y));
    }

    // ---------------- updates ----------------

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checking || _installing) return;
        _checking = true;
        if (manual) _verLabel.Text = $"{Updater.CurrentText} · Checking…";
        try
        {
            var found = await Task.Run(() => Updater.CheckAsync());
            if (IsDisposed) return;
            _update = found;
            if (found == null)
            {
                _verLabel.Text = $"{Updater.CurrentText} · Up To Date";
                return;
            }

            string v = found.Version.ToString(3);
            Log.Info($"Update available: {v}");
            ShowUpdateButton($"Update To v{v}");
            if (!manual && Settings.Current.UpdateNotified != v)
            {
                Settings.Current.UpdateNotified = v;
                Settings.Save();
                UpdateFound?.Invoke(found);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            if (!IsDisposed && _update == null) _verLabel.Text = $"{Updater.CurrentText} · Couldn't Check";
        }
        finally
        {
            _checking = false;
        }
    }

    private void ShowUpdateButton(string text)
    {
        _updateBtn.Text = text;
        _updateBtn.Progress = -1;
        _updateBtn.Enabled = true;
        _updateBtn.Visible = true;
        _verLabel.Visible = false;
    }

    private async Task InstallUpdateAsync()
    {
        if (_update == null || _installing) return;
        _installing = true;
        _updateBtn.Enabled = false;
        _updateBtn.Text = "Downloading 0%";
        _updateBtn.Progress = 0;
        try
        {
            var progress = new Progress<int>(p =>
            {
                if (IsDisposed) return;
                _updateBtn.Text = $"Downloading {p}%";
                _updateBtn.Progress = p;
            });
            string setup = await Task.Run(() => Updater.DownloadAsync(_update, progress));
            if (IsDisposed) return;

            _updateBtn.Text = "Installing…";
            _updateBtn.Progress = 100;
            Updater.RunInstaller(setup);
            // Quit cleanly (fans back to the BIOS); the installer starts the new version when it's done.
            QuitRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            _installing = false;
            if (!IsDisposed) ShowUpdateButton("Update Failed · Retry");
        }
    }

    private string FanHint()
    {
        var s = Settings.Current;
        if (s.Fan == FanMode.Custom)
            return _c.CurveCpuDuty >= 0 ? $"Custom Curves · CPU {_c.CpuFan.Temp}°C → {_c.CurveCpuDuty}%" : "Custom Curves";
        if (s.Fan == FanMode.Maximum) return "Overrides The Power Mode";
        return _c.CurrentMode == PowerMode.Quiet ? "Quiet Curve" : "Follows Power Mode";
    }

    private string KeyboardHint()
    {
        var s = Settings.Current;
        if (s.KeyboardBrightness == 0) return "Off";
        string speed = s.KeyboardSpeed.ToString();
        return s.KeyboardEffect switch
        {
            KbEffect.Breathing => $"Breathing · {speed} · {s.KeyboardBrightness}%",
            KbEffect.ColorCycle => $"Color Cycle · {speed} · {s.KeyboardBrightness}%",
            KbEffect.CpuHeat => _c.Effects.HeatLabel.Length > 0 ? $"Heat · {_c.Effects.HeatLabel}" : "Heat",
            _ => $"Brightness {s.KeyboardBrightness}%",
        };
    }

    private static string TimeoutText(int seconds) => seconds >= 60 ? $"{seconds / 60} min" : $"{seconds} s";

    private TileButton TimeoutButton(string icon, int x, bool pluggedIn)
    {
        var b = new TileButton
        {
            Vertical = false,
            IconName = icon,
            IconSize = 16,
            Font = _fBody,
            Location = new Point(S(x), S(_y)),
            Size = new Size(S(108), S(32)),
        };
        b.Click += (_, _) => CycleTimeout(pluggedIn);
        _tips.SetToolTip(b, pluggedIn
            ? "Keyboard light turns off after this long idle, while plugged in. Click to change."
            : "Keyboard light turns off after this long idle, on battery. Click to change.");
        Controls.Add(b);
        return b;
    }

    private void CycleTimeout(bool pluggedIn)
    {
        var s = Settings.Current;
        int current = pluggedIn ? s.KeyboardTimeoutAC : s.KeyboardTimeoutDC;
        int i = Array.IndexOf(TimeoutSteps, current);
        int next = TimeoutSteps[(i + 1) % TimeoutSteps.Length];
        Run(() => _c.SetKeyboardTimeout(pluggedIn, next));
    }

    private void OpenStockSoftware()
    {
        using var form = new StockSoftwareForm();
        form.Changed += () =>
        {
            _ = CheckStockSoftwareAsync();
            // Stopping (or starting) the stock hotkey service switches the BIOS key events off: turn them back on.
            Task.Delay(1500).ContinueWith(_ => _c.ReconnectHotkeys("stock software changed"));
        };
        form.ShowDialog(this);
    }

    /// <summary>Shows the Remove Stock App button only while something from the stock software is left.</summary>
    private async Task CheckStockSoftwareAsync()
    {
        try
        {
            bool any = await Task.Run(() => StockSoftware.AnythingToRemove(StockSoftware.Detect()));
            if (IsDisposed) return;
            _stockFound = any;
            if (_stockBtn is { IsDisposed: false }) _stockBtn.Visible = any;
            LayoutOptionsTitle();
            if (any) Log.Info("Stock Control Center software found");
        }
        catch (Exception ex)
        {
            Log.Error("Checking for the stock software failed", ex);
        }
    }

    /// <summary>Right end of the Options title row: Remove Stock App, then the hint in the space left.</summary>
    private void OnLidChanged(bool closed)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(RefreshState));
    }

    private void LayoutOptionsTitle()
    {
        if (_optionsHint is not { IsDisposed: false }) return;
        int right = Pad + ContentWidth;
        if (_stockFound)
        {
            _stockBtn.Left = S(right - StockW);
            right -= StockW + 10;
        }
        _optionsHint.Width = Math.Max(0, S(right) - _optionsHint.Left);
    }

    // ---------------- Wi-Fi ----------------

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) RecheckWifiSoon();
    }

    private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) RecheckWifiSoon();
    }

    private void RecheckWifiSoon()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
            _wifiRecheck.Stop();
            _wifiRecheck.Start();
        }));
    }

    /// <summary>
    /// Looks at the Wi-Fi card: turns Wi-Fi Driver Refresh red (and shows the popup, once) while it's stopped, keeps a copy of the
    /// working driver while it's fine.
    /// </summary>
    private async Task CheckWifiAsync(string reason)
    {
        if (_wifiChecking || _wifiFixing) return;
        _wifiChecking = true;
        _wifiChecked = DateTime.Now;
        try
        {
            var status = await Task.Run(() =>
            {
                var s = WifiDriver.Check();
                WifiDriver.SaveWorkingDriver(s);
                return s;
            });
            if (IsDisposed) return;
            bool wasBroken = WifiBroken(_wifi);
            _wifi = status;
            if (WifiBroken(status))
            {
                bool missing = status.State == WifiState.Missing;
                if (!wasBroken)
                    Log.Info(missing ? $"Wi-Fi card missing ({reason})" : $"Wi-Fi stopped ({reason}): {status.Name} · Code {status.Problem}");
                ShowWifiState();
                if (!_wifiNotified)
                {
                    _wifiNotified = true;
                    WifiStopped?.Invoke();
                }
            }
            else
            {
                _wifiNotified = false;
                if (!_wifiDone.Enabled) ShowWifiState();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Wi-Fi check failed", ex);
        }
        finally
        {
            _wifiChecking = false;
        }
    }

    /// <summary>The card is listed with an error, or it was seen before and is gone.</summary>
    private static bool WifiBroken(WifiStatus s) => s?.State is WifiState.Stopped or WifiState.Missing;

    private async Task FixWifiAsync()
    {
        if (_wifiFixing) return;
        if (_wifi == null) await CheckWifiAsync("refresh clicked");
        if (IsDisposed || _wifiFixing || _wifi == null || _wifi.State == WifiState.NotFound) return;
        if (!WifiBroken(_wifi) && MessageBox.Show(this,
                "Wi-Fi Driver Refresh removes every Intel Wi-Fi driver on this laptop and reinstalls the last one that worked.\n\n" +
                "Wi-Fi drops for a few seconds while it runs. Refresh now?",
                "C-Helper", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _wifiFixing = true;
        _wifiDone.Stop();
        var start = _wifi;
        var progress = new Progress<(int Percent, string Step)>(p =>
        {
            if (!IsDisposed && _wifiFixing) ShowWifi(true, $"Refreshing… {p.Percent}%", Theme.Turbo, false, false, p.Percent, p.Step);
        });
        ShowWifi(true, "Refreshing… 0%", Theme.Turbo, false, false, 0, "Starting…");
        try
        {
            var after = await Task.Run(() => WifiDriver.Repair(start, progress));
            if (IsDisposed) return;
            _wifiFixing = false;
            _wifi = after;
            _wifiChecked = DateTime.Now;
            if (after.State == WifiState.Working)
            {
                _wifiNotified = false;
                ShowWifi(true, "Done", Theme.Accent, true, true, -1, "Wi-Fi Working Again", Theme.Accent);
                _wifiDone.Start();
                _dnsApply.Stop();
                _dnsApply.Start(); // the reinstalled adapter starts with automatic DNS
                _ = Task.Run(() => WifiDriver.SaveWorkingDriver(after));
            }
            else
            {
                ShowWifi(true, WifiLabel, Theme.Turbo, true, true, -1, "Still Stopped · Shut Down Fully", Theme.Warn);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Wi-Fi driver refresh failed", ex);
            if (!IsDisposed) ShowWifi(true, WifiLabel, Theme.Turbo, true, true, -1, "Refresh Failed · See Log", Theme.Warn);
        }
        finally
        {
            _wifiFixing = false;
        }
    }

    /// <summary>The button for the last check: red with the problem while the card is broken, plain otherwise.</summary>
    private void ShowWifiState()
    {
        if (_wifi?.State == WifiState.NotFound) ShowWifi(false);
        else if (WifiBroken(_wifi))
            ShowWifi(true, WifiLabel, Theme.Turbo, true, true, -1,
                _wifi.State == WifiState.Missing ? "Wi-Fi Card Missing" : $"Wi-Fi Stopped · Code {_wifi.Problem}", Theme.Warn);
        else ShowWifi(true, WifiLabel, Theme.Turbo, false, true);
    }

    private void ShowWifi(bool visible, string text = null, Color accent = default, bool active = false, bool enabled = true,
        int progress = -1, string hint = null, Color hintColor = default)
    {
        _wifiUi = (visible, text, accent, active, enabled, progress, hint, hintColor);
        ApplyWifiUi();
    }

    private void ApplyWifiUi()
    {
        if (_wifiBtn is not { IsDisposed: false }) return;
        var u = _wifiUi;
        // Stays in its place in the Network row; without an Intel Wi-Fi card it just can't be clicked.
        _wifiBtn.Text = u.Visible ? u.Text : WifiLabel;
        _wifiBtn.Accent = u.Accent == default ? Theme.Turbo : u.Accent;
        _wifiBtn.Active = u.Visible && u.Active;
        _wifiBtn.Enabled = u.Visible && u.Enabled;
        _wifiBtn.Progress = u.Visible ? u.Progress : -1;
        UpdateNetHint();
    }

    /// <summary>
    /// Options hint: Wi-Fi trouble or refresh progress first (on its own), otherwise whether AdGuard DNS is on,
    /// then what Clamshell is doing.
    /// </summary>
    private void UpdateNetHint()
    {
        if (_optionsHint is not { IsDisposed: false }) return;
        var u = _wifiUi;
        if (u.Visible && !string.IsNullOrEmpty(u.Hint))
        {
            _optionsHint.Text = u.Hint;
            _optionsHint.ForeColor = u.HintColor == default ? Theme.Muted : u.HintColor;
            return;
        }
        _optionsHint.ForeColor = Theme.Muted;
        bool on = Settings.Current.AdGuardDns;
        string dns = _dnsBusy ? (on ? "Turning On AdGuard DNS…" : "Turning Off AdGuard DNS…")
            : !on ? ""
            : _dns.Adapters > 0 && _dns.On < _dns.Adapters ? $"AdGuard DNS On · {_dns.On} Of {_dns.Adapters} Adapters"
            : "AdGuard DNS On";
        _optionsHint.Text = string.Join(" · ", new[] { dns, _clamshellHint }.Where(t => t.Length > 0));
    }

    // ---------------- AdGuard DNS ----------------

    private void ApplyDnsUi()
    {
        if (_dnsBtn is not { IsDisposed: false }) return;
        _dnsBtn.Active = Settings.Current.AdGuardDns;
        _dnsBtn.Enabled = !_dnsBusy;
        UpdateNetHint();
    }

    private async Task ToggleDnsAsync()
    {
        if (_dnsBusy) return;
        bool on = !Settings.Current.AdGuardDns;
        Settings.Current.AdGuardDns = on;
        Settings.Save();
        Log.Info($"AdGuard DNS {(on ? "on" : "off")}");
        _dnsBusy = true;
        ApplyDnsUi();
        try
        {
            _dns = await Task.Run(() =>
            {
                if (on) AdGuardDns.Apply();
                else AdGuardDns.Remove();
                return AdGuardDns.Status();
            });
        }
        catch (Exception ex)
        {
            Log.Error("Changing AdGuard DNS failed", ex);
        }
        finally
        {
            _dnsBusy = false;
        }
        if (!IsDisposed) ApplyDnsUi();
    }

    /// <summary>While AdGuard DNS is on: puts it on any adapter that lost it or is new. Only reads when nothing to do.</summary>
    private async Task ApplyDnsAsync()
    {
        if (_dnsBusy || !Settings.Current.AdGuardDns) return;
        _dnsBusy = true;
        try
        {
            _dns = await Task.Run(() =>
            {
                AdGuardDns.Apply();
                return AdGuardDns.Status();
            });
        }
        catch (Exception ex)
        {
            Log.Error("Applying AdGuard DNS failed", ex);
        }
        finally
        {
            _dnsBusy = false;
        }
        if (!IsDisposed) ApplyDnsUi();
    }

    private void OnNetworkAddressChanged(object sender, EventArgs e)
    {
        if (!Settings.Current.AdGuardDns || !IsHandleCreated || IsDisposed) return;
        // Changes come in bursts: act once things have been quiet for a few seconds.
        BeginInvoke(new Action(() =>
        {
            _dnsApply.Stop();
            _dnsApply.Start();
        }));
    }

    /// <summary>
    /// Shows the Sound button when Creative's sound engine (Sound Blaster Studio) is on this laptop, or when it's
    /// missing but the bundled drivers can set it up (the Sound page then offers to).
    /// </summary>
    private async Task CheckSoundAsync()
    {
        try
        {
            bool setup = false;
            bool found = await Task.Run(() => Sound.SoundBlaster.Installed() || (setup = Sound.SoundSetup.Needed()));
            if (IsDisposed || found == _soundFound) return;
            _soundFound = found;
            Log.Info(setup ? "Sound Blaster needs setting up (its audio driver is missing)"
                : found ? "Sound Blaster Studio found" : "Sound Blaster Studio not found");
            if (_soundBtn is not { IsDisposed: false }) return;
            _soundBtn.Visible = found;
            // Log moves right to make room for the Sound button.
            foreach (Control c in Controls)
                if (c is TileButton { Text: "Log" } log) log.Left = _soundBtn.Left + (found ? S(88) : 0);
        }
        catch (Exception ex)
        {
            Log.Error("Checking for Sound Blaster failed", ex);
        }
    }

    /// <summary>Shows the Sound page in place of the home page; the footer stays. Clicking Sound again goes back.</summary>
    private void OpenSound()
    {
        if (_soundPage is { Visible: true, IsDisposed: false })
        {
            ShowHome();
            return;
        }
        if (_soundPage == null || _soundPage.IsDisposed)
        {
            _soundPage = new SoundPage(ContentWidth, Pad) { Location = Point.Empty, Visible = false };
            _soundPage.HomeRequested += ShowHome;
            Controls.Add(_soundPage);
        }
        _soundPage.Size = new Size(ClientSize.Width, S(_footerTop));
        _soundPage.BringToFront();
        _soundPage.Visible = true;
        if (_soundBtn is { IsDisposed: false }) { _soundBtn.Borderless = false; _soundBtn.Active = true; }
    }

    private void ShowHome()
    {
        if (_soundPage is { IsDisposed: false }) _soundPage.Visible = false;
        if (_soundBtn is { IsDisposed: false }) { _soundBtn.Borderless = true; _soundBtn.Active = false; _soundBtn.Invalidate(); }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Esc on the Sound page goes back home.
        if (keyData == Keys.Escape && _soundPage is { Visible: true })
        {
            ShowHome();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OpenCurves()
    {
        if (_curves == null || _curves.IsDisposed)
        {
            _curves = new FanCurveForm();
            var wa = Screen.FromControl(this).WorkingArea;
            _curves.Location = new Point(
                Math.Max(wa.Left, Left - _curves.Width - S(12)),
                Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - _curves.Height)));
            _curves.Show(this);
        }
        else
        {
            _curves.Activate();
        }
    }

    private (Label Caption, Label Value) Readout(int col, int colW, string caption)
    {
        var cap = new Label
        {
            Text = caption,
            Location = new Point(S(Pad + col * colW), S(_y)),
            Size = new Size(S(colW - 8), S(16)),
            ForeColor = Theme.Muted,
            Font = _fSmall,
        };
        var val = new Label
        {
            Text = "—",
            Location = new Point(S(Pad + col * colW), S(_y + 17)),
            Size = new Size(S(colW - 8), S(24)),
            ForeColor = Theme.Text,
            Font = _fBig,
        };
        Controls.Add(cap);
        Controls.Add(val);
        return (cap, val);
    }

    private Label Section(string icon, string title)
    {
        Controls.Add(new PictureBox
        {
            Image = Icons.Get(icon, Theme.Text),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(S(Pad), S(_y + 2)),
            Size = new Size(S(20), S(20)),
        });
        Controls.Add(new Label
        {
            Text = title,
            Location = new Point(S(Pad + 28), S(_y + 2)),
            AutoSize = true,
            Font = _fHead,
            ForeColor = Theme.Text,
        });
        var hint = new Label
        {
            Location = new Point(S(Pad + 200), S(_y + 4)),
            Size = new Size(S(ContentWidth - 200), S(18)),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Theme.Muted,
            Font = _fSmall,
            AutoEllipsis = true,
        };
        Controls.Add(hint);
        _y += 28;
        return hint;
    }

    private SegRow Row(int height, bool vertical, int iconSize, params SegRow.Tile[] items)
    {
        var row = new SegRow(S(height), S(6), _fBody, vertical, iconSize, items)
        {
            Location = new Point(S(Pad), S(_y)),
            Width = S(ContentWidth),
        };
        Controls.Add(row);
        _y += height + 12;
        return row;
    }

    private void BuildScreenRow()
    {
        int[] rates = Native.Display.AvailableRates();
        Log.Info($"Panel refresh rates: {string.Join(", ", rates)}");
        // Every rate the panel offers (up to 4, keeping the highest), then Auto
        var values = rates.Skip(Math.Max(0, rates.Length - 4)).ToList();
        values.Add(0);
        _screenValues = values.ToArray();

        var items = _screenValues.Select(v => new SegRow.Tile(v == 0 ? "Auto" : $"{v} Hz")).ToArray();
        _screen = Row(40, true, 0, items);
        _screen.ButtonClicked += i => Run(() => _c.SetScreenRate(_screenValues[i]));
    }

    // ---------------- actions ----------------

    private static void Run(Action action) => Task.Run(() =>
    {
        try { action(); }
        catch (Exception ex) { Log.Error("action failed", ex); }
    });

    private void OnGpuClicked(GpuMode mode)
    {
        var s = Settings.Current;
        if (mode == s.Gpu) return;

        bool needsRestart = mode == GpuMode.Ultimate || _c.InUltimate;
        if (needsRestart)
        {
            string msg = mode == GpuMode.Ultimate
                ? "dGPU Only makes the NVIDIA GPU drive the screen directly (best FPS, more battery drain).\n\nIt needs a restart. Switch now?"
                : "Leaving dGPU Only needs a restart. Switch now?";
            if (MessageBox.Show(this, msg, "C-Helper", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
        }
        else if (mode == GpuMode.Eco || mode == GpuMode.Optimized)
        {
            _gpuHint.Text = "Close Games Before Eco · Switching…";
        }

        Task.Run(() =>
        {
            bool restart = _c.SetGpuMode(mode);
            if (!restart) return;
            BeginInvoke(new Action(() =>
            {
                if (MessageBox.Show(this, "Restart now to apply the GPU mode?", "C-Helper",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
            }));
        });
    }

    private void OnOptionClicked(int index)
    {
        var s = Settings.Current;
        switch (index)
        {
            case 0:
                Run(() => _c.SetCamera(!s.Camera));
                break;
            case 1:
                Run(() => _c.SetAutoPowerMode(!s.AutoPowerMode));
                break;
            case 2:
                Run(() =>
                {
                    if (_startupEnabled) Startup.Disable();
                    else Startup.Enable();
                    _startupEnabled = Startup.IsEnabled();
                    OnStateChanged();
                });
                break;
            case 3:
                Run(() => _c.SetFnEsc(!s.FnEscOpensHelper));
                break;
            case 4:
                Run(() => _c.SetClamshell(!s.Clamshell));
                break;
        }
    }

    private void PickColor()
    {
        using var dlg = new ColorDialog
        {
            Color = Color.FromArgb(Settings.Current.KeyboardColor),
            FullOpen = true,
            AnyColor = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            var c = dlg.Color;
            Run(() => _c.SetKeyboardColor(c));
        }
    }

    private static void OpenLog()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CHelper", "log.txt");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ---------------- state ----------------

    private void OnStateChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(new Action(RefreshState));
    }

    private static string ModeName(PowerMode m) => m.DisplayName();

    private void RefreshState()
    {
        var s = Settings.Current;

        // Power mode
        _modes.Select(Array.IndexOf(ModeOrder, _c.CurrentMode));

        // GPU
        bool ultimateOk = Gpu.MuxSupports(_c.MuxMask, Gpu.MuxDgpuOnly);
        for (int i = 0; i < _gpu.Count; i++)
        {
            bool enabled = !_c.GpuBusy;
            if (GpuOrder[i] == GpuMode.Ultimate) enabled &= ultimateOk;
            if (GpuOrder[i] is GpuMode.Eco or GpuMode.Optimized) enabled &= !_c.InUltimate;
            _gpu.SetEnabled(i, enabled);
        }
        _gpu.Select(Array.IndexOf(GpuOrder, s.Gpu));

        // Fans
        _fans.Select(Array.IndexOf(FanOrder, s.Fan));
        _fanHint.Text = FanHint();

        // Screen
        _screen.Select(Array.IndexOf(_screenValues, s.ScreenRate));
        string now = _c.ScreenRate > 0 ? $"{_c.ScreenRate} Hz Now" : "";
        _screenHint.Text = s.ScreenRate == 0 ? $"{now} · Max On AC, Min On Battery" : now;

        // Keyboard
        _kbEffects.Select(Array.IndexOf(EffectOrder, s.KeyboardEffect));
        _kbColor.Fill = Color.FromArgb(s.KeyboardColor);
        _kbColor.Gradient = s.KeyboardEffect switch
        {
            KbEffect.ColorCycle => Rainbow,
            KbEffect.CpuHeat => Heat,
            _ => null,
        };
        if (!_kbSlider.IsDragging) _kbSlider.Value = s.KeyboardBrightness;
        bool animated = s.KeyboardEffect is KbEffect.Breathing or KbEffect.ColorCycle;
        bool heat = s.KeyboardEffect == KbEffect.CpuHeat;
        _kbSpeedLabel.Text = heat ? "Source" : "Speed";
        string[] labels = heat ? new[] { "CPU", "GPU", "Hottest" } : new[] { "Slow", "Normal", "Fast" };
        for (int i = 0; i < _kbSpeed.Count; i++)
        {
            _kbSpeed[i].Text = labels[i];
            _kbSpeed.SetEnabled(i, animated || heat);
        }
        _kbSpeed.Select(heat ? Array.IndexOf(HeatOrder, s.KeyboardHeatSource)
            : animated ? Array.IndexOf(SpeedOrder, s.KeyboardSpeed) : -1);
        _kbTimeoutAC.Text = s.KeyboardTimeoutAC <= 0 ? "Never" : TimeoutText(s.KeyboardTimeoutAC);
        _kbTimeoutDC.Text = s.KeyboardTimeoutDC <= 0 ? "Never" : TimeoutText(s.KeyboardTimeoutDC);
        _kbHint.Text = KeyboardHint();

        // Options
        _options.SetOn(0, s.Camera);
        _options.SetOn(1, s.AutoPowerMode);
        _options.SetOn(2, _startupEnabled);
        _options2.SetOn(0, s.FnEscOpensHelper);
        _options2.SetOn(1, s.Clamshell);
        _clamshellHint = !s.Clamshell ? ""
            : !Controller.OnAC ? "Clamshell Needs Charger"
            : Lid.Closed == true ? "Lid Closed, Still Running"
            : "Clamshell Ready";
        UpdateNetHint();

        // Battery charge limit
        var charge = _c.Charge;
        if (charge != null && !_charge.IsDragging)
        {
            _charge.Values = Charger.SliderStops(charge);
            _charge.Value = charge.Limit;
            _charge.Enabled = true;
            _chargeHint.Text = _c.FullChargeOnce
                ? $"{s.FullChargeRestore}% After Unplug"
                : ChargeText(charge.Limit, charge);
        }
        _fullCharge.Active = _c.FullChargeOnce;
        _fullCharge.Text = _c.FullChargeOnce ? "Charging To 100%" : "Full Charge Once";

        UpdateStats();
    }

    private static string ChargeText(int stop, Charger.State state)
    {
        // Short: it sits on the title row.
        if (stop >= 100) return "No Limit";
        return state != null && state.Enabled && state.Stop == stop
            ? $"Stop {stop}% · Start {state.Start}%"
            : $"Stop {stop}%";
    }

    /// <summary>Live readouts, G-Helper style: CPU/GPU temperature and fan in the section headers.</summary>
    private void UpdateStats()
    {
        var s = Settings.Current;
        var ps = SystemInformation.PowerStatus;
        string src = ps.PowerLineStatus == PowerLineStatus.Offline ? "On Battery" : "Plugged In";
        string auto = s.AutoPowerMode ? $" · Auto: AC {ModeName(s.ModeAC)}, Battery {ModeName(s.ModeDC)}" : "";
        _stats.Text = $"{src}{auto}";

        // Battery readouts
        var bat = _c.BatteryNow;
        int pct = bat?.Percent ?? (ps.BatteryLifePercent is >= 0 and <= 1 ? (int)Math.Round(ps.BatteryLifePercent * 100) : -1);
        _batCharge.Text = pct >= 0 ? $"{pct}%" : "—";
        if (bat is { } b && b.Watts < 0)
        {
            _batRateLabel.Text = "Discharging";
            _batRate.Text = $"{-b.Watts:0.0} W";
            _batRate.ForeColor = Theme.Warn;
        }
        else if (bat is { } c && c.Watts > 0)
        {
            _batRateLabel.Text = "Charging";
            _batRate.Text = $"{c.Watts:0.0} W";
            _batRate.ForeColor = Theme.Accent;
        }
        else
        {
            _batRateLabel.Text = "Battery";
            _batRate.Text = ps.PowerLineStatus == PowerLineStatus.Offline ? "On Battery" : "Plugged In";
            _batRate.ForeColor = Theme.Text;
        }
        _batHealth.Text = _c.BatteryHealth > 0 ? $"{_c.BatteryHealth:0.0}%" : "—";

        var cpu = _c.CpuFan;
        _modeHint.Text = Readout("CPU", _c.CpuLoad, cpu);
        _fanHint.Text = FanHint();
        if (s.KeyboardEffect == KbEffect.CpuHeat) _kbHint.Text = KeyboardHint();

        var gpu = _c.GpuFan;
        _gpuHint.Text = _c.GpuBusy ? "Switching…"
            : !_c.DgpuPresent && !_c.InUltimate ? $"dGPU Off · {gpu.Text}"
            : gpu.Temp > 0 || _c.GpuLoad.Usage >= 0 ? Readout("GPU", _c.GpuLoad, gpu)
            : _c.InUltimate ? "dGPU Only" : "dGPU On";
    }

    /// <summary>"CPU 12% · 3.4 GHz · 53°C · Fan 30%", leaving out what isn't known.</summary>
    private static string Readout(string name, (int Usage, int Mhz) load, FanInfo fan)
    {
        var parts = new List<string>();
        if (load.Usage >= 0) parts.Add($"{name} {load.Usage}%");
        if (load.Mhz > 0) parts.Add($"{load.Mhz / 1000.0:0.0} GHz");
        if (fan.Temp > 0) parts.Add(parts.Count == 0 ? $"{name} {fan.Temp}°C" : $"{fan.Temp}°C");
        if (fan.Temp > 0) parts.Add(fan.Text);
        return string.Join(" · ", parts);
    }

    public void ShowNearTray()
    {
        if (!Visible)
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.Right - Width - S(12), Math.Max(wa.Top, wa.Bottom - Height - S(12)));
            Show();
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        ForceForeground();
    }

    /// <summary>
    /// Windows blocks background apps from taking focus (the taskbar icon just flashes).
    /// Attaching to the current foreground thread's input lets SetForegroundWindow succeed.
    /// </summary>
    private void ForceForeground()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == Handle) return;
        uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint myThread = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
        try
        {
            BringWindowToTop(Handle);
            SetForegroundWindow(Handle);
        }
        finally
        {
            if (attached) AttachThreadInput(myThread, fgThread, false);
        }
        if (GetForegroundWindow() != Handle)
        {
            // Last resort: a quick TopMost flip brings it above other windows.
            TopMost = true;
            TopMost = false;
        }
        Activate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
