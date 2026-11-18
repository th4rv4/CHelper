using CHelper.Sound;
using static CHelper.Sound.SoundBlaster;

namespace CHelper.UI;

/// <summary>
/// Sound panel: Sound Blaster Studio playback effects (Sound Mode, Acoustic Engine, Volume Boost, Equalizer)
/// and CrystalVoice for the microphone. It follows whatever output and microphone Windows is using,
/// and every change applies straight away.
/// </summary>
public sealed class SoundForm : Form
{
    private const int ContentWidth = 460;
    private const int Pad = 18;
    private const int RowH = 42, Gap = 6;

    private static readonly (Effect Effect, string Name)[] Effects =
    {
        (Effect.Surround, "Surround"), (Effect.Crystalizer, "Crystalizer"), (Effect.Bass, "Bass"),
        (Effect.SmartVolume, "Smart Volume"), (Effect.DialogPlus, "Dialog+"),
    };
    private static readonly SvmMode[] SvmOrder = { SvmMode.Night, SvmMode.Normal, SvmMode.Loud };
    private static readonly OutputKind[] KindOrder = { OutputKind.BuiltIn, OutputKind.Headphones, OutputKind.LineOut };

    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 1500 };
    private SoundBlaster _out, _mic;
    private string _eqPreferred;
    private int _micMissingTop;

    /// <summary>Tag for controls that stay visible when Sound Blaster isn't available on the device.</summary>
    private const string Always = "always";

    private Font _fHead, _fBody, _fSmall;
    private Label _status;
    private SegRow _tabs;
    private Panel _playback, _micPanel;
    private Label _outMissing, _micMissing;

    // Playback
    private SegRow _kinds, _modes, _svmModes;
    private Label _outKind;
    private readonly Dictionary<Effect, (ToggleSwitch On, LevelSlider Level, Label Value)> _rows = new();
    private LevelSlider _boost;
    private Label _boostValue;
    private TileButton _eqPreset;
    private ToggleSwitch _eqOn;
    private EqGraph _eq;

    // Microphone
    private Label _micName, _micVolValue;
    private LevelSlider _micVol;
    private ToggleSwitch _nrOn, _aecOn, _msvOn, _focusOn, _vmOn, _meqOn;
    private LevelSlider _nrLevel, _focusAngle;
    private Label _nrValue, _focusValue;
    private TileButton _vmPreset, _meqPreset;

    public SoundForm()
    {
        Text = "Sound";
        Icon = Theme.AppIcon;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;

        BuildUi();
        ApplyScale(this);
        OpenDevices();
        ShowTab(0);
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
    }

    private int S(float v) => (int)Math.Round(v * DeviceDpi / 96f * Theme.MainScale);

    /// <summary>Same size factor as the main window for the custom-drawn controls.</summary>
    private static void ApplyScale(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is TileButton t) t.UiScale = Theme.MainScale;
            else if (c is EqGraph g) g.UiScale = Theme.MainScale;
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

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        RefreshValues();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Stop();
        _out?.Dispose();
        _mic?.Dispose();
        base.OnFormClosed(e);
    }

    // ---------------- devices ----------------

    private void OpenDevices()
    {
        _out?.Dispose();
        _mic?.Dispose();
        _out = SoundBlaster.Output();
        _mic = SoundBlaster.Microphone();
        Log.Info($"Sound panel: output '{_out.Device?.Name}' (Sound Blaster {(_out.Available ? "on" : "off")}), " +
                 $"microphone '{_mic.Device?.Name}' (CrystalVoice {(_mic.Available ? "on" : "off")})");
        RefreshValues();
    }

    /// <summary>Picks up a new output or microphone (headphones plugged in) and changes made elsewhere.</summary>
    private void Poll()
    {
        if (Dragging) return;
        var outNow = Native.AudioEffects.Default(Native.AudioEffects.Flow.Render);
        var micNow = Native.AudioEffects.Default(Native.AudioEffects.Flow.Capture);
        if (outNow?.Id != _out?.Device?.Id || micNow?.Id != _mic?.Device?.Id) OpenDevices();
        else RefreshValues();
    }

    private bool Dragging =>
        _eq.IsDragging || _boost.IsDragging || _nrLevel.IsDragging || _focusAngle.IsDragging ||
        _rows.Values.Any(r => r.Level.IsDragging) || _micVol.IsDragging;

    private void RefreshValues()
    {
        if (_out == null || Dragging) return;
        SuspendLayout();

        string outName = _out.Device?.Name ?? "No output";
        _status.Text = _out.Available ? $"{outName} · Sound Blaster Studio" : outName;
        SetAvailable(_playback, _outMissing, _out.Available);
        if (_out.Available)
        {
            _kinds.Select(Array.IndexOf(KindOrder, _out.Kind));
            _outKind.Text = _out.Headphones ? "Headphone Version" : "Speaker Version";
            foreach (var (effect, _) in Effects)
            {
                var row = _rows[effect];
                row.On.On = _out.IsOn(effect);
                row.Level.Value = _out.Level(effect);
                row.Value.Text = row.On.On ? row.Level.Value.ToString() : "Off";
                row.Level.Enabled = row.On.On;
                row.Value.ForeColor = row.On.On ? Theme.Text : Theme.Muted;
            }
            _svmModes.Select(Array.IndexOf(SvmOrder, _out.SmartMode));
            _svmModes.Enabled = _rows[Effect.SmartVolume].On.On;
            int mode = _out.CurrentMode();
            _modes.Select(mode);
            if (mode >= 0) _eqPreferred = SoundPresets.Modes[mode].Speaker.EqPresetId;

            int boost = _out.Boost;
            _boost.Value = boost;
            _boostValue.Text = $"{boost}%";
            _boostValue.ForeColor = boost > 100 ? Theme.Warn : Theme.Muted;

            _eqOn.On = _out.EqualizerOn;
            _eq.Bands = _out.Bands();
            _eq.Enabled = _eqOn.On;
            var preset = _out.MatchingPreset(_eqPreferred);
            _eqPreset.Text = (preset?.Name ?? "Custom") + "  ▾";
        }

        _micName.Text = _mic.Device?.Name ?? "No microphone";
        int? micVol = Native.AudioEffects.GetVolume(_mic.Device);
        _micVol.Enabled = micVol != null;
        _micVol.Value = micVol ?? 0;
        _micVolValue.Text = micVol is int mv ? $"{mv}%" : "";
        SetAvailable(_micPanel, _micMissing, _mic.Available);
        if (_mic.Available)
        {
            _nrOn.On = _mic.NoiseReduction;
            _nrLevel.Value = _mic.NoiseReductionLevel;
            _nrLevel.Enabled = _nrOn.On;
            _nrValue.Text = _nrOn.On ? _nrLevel.Value.ToString() : "Off";
            _aecOn.On = _mic.EchoCancellation;
            _msvOn.On = _mic.MicSmartVolume;
            _focusOn.On = _mic.Focus;
            _focusAngle.Value = _mic.FocusAngle;
            _focusAngle.Enabled = _focusOn.On;
            _focusValue.Text = _focusOn.On ? $"{_focusAngle.Value}°" : "Off";
            _vmOn.On = _mic.VoiceMorphOn;
            _vmPreset.Text = (_mic.CurrentVoice()?.Name ?? "Custom") + "  ▾";
            _vmPreset.Enabled = _vmOn.On;
            _meqOn.On = _mic.MicEqualizer;
            _meqPreset.Text = (_mic.CurrentMicEq()?.Name ?? "Custom") + "  ▾";
            _meqPreset.Enabled = _meqOn.On;
        }
        ResumeLayout();
    }

    /// <summary>Greys a tab out with a short explanation when Sound Blaster can't work on that device.</summary>
    private static void SetAvailable(Panel panel, Label missing, bool available)
    {
        foreach (Control c in panel.Controls)
            if (c != missing && !Always.Equals(c.Tag)) c.Visible = available;
        missing.Visible = !available;
    }

    /// <summary>Applies a change on the UI thread, then shows what the engine now reports.</summary>
    private void Change(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error("Sound change failed", ex); }
        RefreshValues();
    }

    // ---------------- layout ----------------

    private void BuildUi()
    {
        _fHead = Theme.Px(S(13.5f), semibold: true);
        _fBody = Theme.Px(S(12.5f));
        _fSmall = Theme.Px(S(11.5f));
        int y = 12;

        _status = new Label
        {
            Location = new Point(S(Pad), S(y)),
            Size = new Size(S(ContentWidth), S(18)),
            ForeColor = Theme.Muted,
            Font = _fSmall,
            AutoEllipsis = true,
        };
        Controls.Add(_status);
        y += 24;

        _tabs = new SegRow(S(34), S(8), _fBody, false, 16,
            new("Playback", null, Theme.Eco), new("Microphone", null, Theme.Eco))
        {
            Location = new Point(S(Pad), S(y)),
            Width = S(ContentWidth),
        };
        _tabs.ButtonClicked += ShowTab;
        Controls.Add(_tabs);
        y += 34 + 16;

        int playbackH = BuildPlayback();
        int micH = BuildMicrophone();
        int panelH = Math.Max(playbackH, micH);
        foreach (var p in new[] { _playback, _micPanel })
        {
            p.Location = new Point(S(Pad), S(y));
            p.Size = new Size(S(ContentWidth), S(panelH));
            Controls.Add(p);
        }
        y += panelH + 8;

        // Footer
        Controls.Add(new Panel
        {
            Location = new Point(0, S(y)),
            Size = new Size(S(ContentWidth + Pad * 2), Math.Max(1, S(1))),
            BackColor = Theme.Line,
        });
        y += 12;
        Controls.Add(new Label
        {
            Text = "Changes apply right away",
            Location = new Point(S(Pad), S(y + 8)),
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = _fSmall,
        });
        var reset = new TileButton
        {
            Text = "Reset",
            Vertical = false,
            Font = _fBody,
            Location = new Point(S(Pad + ContentWidth - 96), S(y)),
            Size = new Size(S(96), S(32)),
        };
        reset.Click += (_, _) => ResetTab();
        Controls.Add(reset);
        y += 32 + 14;

        ClientSize = new Size(S(ContentWidth + Pad * 2), S(y));
    }

    private void ShowTab(int index)
    {
        _tabs.Select(index);
        _playback.Visible = index == 0;
        _micPanel.Visible = index == 1;
    }

    private void ResetTab()
    {
        bool mic = _micPanel.Visible;
        var target = mic ? _mic : _out;
        if (target is not { Available: true }) return;
        string what = mic ? "microphone effects" : $"{(_out.Headphones ? "headphone" : "speaker")} sound";
        if (MessageBox.Show(this, $"Put the {what} back to Creative's defaults?", "Reset",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        Change(target.Reset);
    }

    private int BuildPlayback()
    {
        _playback = new Panel { BackColor = Theme.Back };
        int y = 0;

        Heading(_playback, "Listening On", y);
        y += 24;
        _kinds = new SegRow(S(34), S(8), _fBody, false, 16,
            new("Built-In Speakers", null, Theme.Eco), new("Headphones", null, Theme.Eco), new("Line Out", null, Theme.Eco))
        {
            Location = new Point(0, S(y)),
            Width = S(ContentWidth),
        };
        _kinds.ButtonClicked += i => Change(() => _out.SetKind(KindOrder[i]));
        new ToolTip().SetToolTip(_kinds[2], "External speakers plugged into the headphone jack");
        _playback.Controls.Add(_kinds);
        y += 34 + 16;

        Heading(_playback, "Sound Mode", y);
        y += 24;
        _modes = new SegRow(S(34), S(8), _fBody, false, 16,
            SoundPresets.Modes.Select(m => new SegRow.Tile(m.Name, null, Theme.Standard)).ToArray())
        {
            Location = new Point(0, S(y)),
            Width = S(ContentWidth),
        };
        _modes.ButtonClicked += i => Change(() =>
        {
            _eqPreferred = SoundPresets.Modes[i].Speaker.EqPresetId;
            _out.ApplyMode(SoundPresets.Modes[i]);
        });
        _playback.Controls.Add(_modes);
        y += 34 + 16;

        Heading(_playback, "Acoustic Engine", y);
        _outKind = HeadingHint(_playback, y);
        y += 26;

        foreach (var (effect, name) in Effects)
        {
            var card = Card(_playback, y, RowH);
            var on = Toggle(card);
            on.Toggled += v => Change(() => _out.SetOn(effect, v));
            NameLabel(card, name);
            var value = ValueLabel(card);
            var level = Slider(card, 0, 100);
            level.ValuePreview += v => value.Text = v.ToString();
            level.ValueCommitted += v => Change(() => _out.SetLevel(effect, v));
            _rows[effect] = (on, level, value);
            y += RowH + Gap;

            if (effect == Effect.SmartVolume)
            {
                _svmModes = new SegRow(S(28), S(6), _fSmall, false, 14,
                    new("Night", null, Theme.Standard), new("Normal", null, Theme.Standard), new("Loud", null, Theme.Standard))
                {
                    Location = new Point(S(170), S(y - Gap + 4)),
                    Width = S(ContentWidth - 170),
                };
                _svmModes.ButtonClicked += i => Change(() => _out.SetSmartMode(SvmOrder[i]));
                _playback.Controls.Add(_svmModes);
                y += 28 + Gap;
            }
        }

        // Volume Boost: past Windows' 100%, through the Equalizer's preamp.
        {
            var card = Card(_playback, y, RowH);
            card.Controls.Add(new Label
            {
                Text = "Volume Boost",
                Location = new Point(S(14), 2),
                Size = new Size(S(150), S(RowH) - 4),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.Text,
                BackColor = Theme.Card,
                Font = _fBody,
            });
            _boostValue = ValueLabel(card);
            _boost = Slider(card, 100, MaxBoost);
            _boost.Step = 5;
            _boost.Accent = Theme.Warn;
            _boost.ValuePreview += v => _boostValue.Text = $"{v}%";
            _boost.ValueCommitted += v => Change(() => _out.SetBoost(v));
            new ToolTip().SetToolTip(_boost,
                "Louder than Windows' 100%. Smart Volume stays on above 100% to keep peaks from crackling.");
            y += RowH + 16;
        }

        Heading(_playback, "Equalizer", y);
        _eqOn = new ToggleSwitch
        {
            Location = new Point(S(ContentWidth - 40), S(y + 1)),
            Size = new Size(S(40), S(22)),
            BackColor = Theme.Back,
        };
        _eqOn.Toggled += v => Change(() => _out.SetEqualizerOn(v));
        _playback.Controls.Add(_eqOn);
        _eqPreset = DropButton(_playback, new Point(S(ContentWidth - 40 - 12 - 150), S(y - 3)), S(150), _ =>
        {
            var menu = DarkMenu();
            foreach (var p in SoundPresets.Equalizer)
            {
                var item = new ToolStripMenuItem(p.Name) { ForeColor = Theme.Text };
                item.Click += (_, _) => Change(() =>
                {
                    _eqPreferred = p.Id;
                    _out.ApplyPreset(p);
                    if (!_out.EqualizerOn) _out.SetEqualizerOn(true);
                });
                menu.Items.Add(item);
                if (p.Id == "10") menu.Items.Add(new ToolStripSeparator()); // music presets above, game presets below
            }
            return menu;
        });
        y += 32;

        _eq = new EqGraph
        {
            Location = new Point(0, S(y)),
            Size = new Size(S(ContentWidth), S(150)),
            BackColor = Theme.Back,
            Font = _fSmall,
        };
        _eq.BandsCommitted += bands => Change(() => _out.SetBands(bands));
        _playback.Controls.Add(_eq);
        y += 150;

        _outMissing = Missing(_playback, 30,
            "Sound Blaster isn't available on this output.\n\n" +
            "It works on the laptop's speakers and on headphones in the laptop's jack. " +
            "Bluetooth, USB and HDMI audio don't go through it.");
        return y;
    }

    private int BuildMicrophone()
    {
        _micPanel = new Panel { BackColor = Theme.Back };
        int y = 0;

        // Windows' microphone level; works on any microphone, so it stays when CrystalVoice isn't available.
        var title = new Label
        {
            Text = "Microphone",
            Location = new Point(0, S(y)),
            AutoSize = true,
            ForeColor = Theme.Text,
            Font = _fHead,
            Tag = Always,
        };
        _micPanel.Controls.Add(title);
        _micName = HeadingHint(_micPanel, y);
        _micName.Tag = Always;
        y += 26;
        {
            var card = Card(_micPanel, y, RowH);
            card.Tag = Always;
            card.Controls.Add(new Label
            {
                Text = "Volume",
                Location = new Point(S(14), 2),
                Size = new Size(S(150), S(RowH) - 4),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.Text,
                BackColor = Theme.Card,
                Font = _fBody,
            });
            _micVolValue = ValueLabel(card);
            _micVol = Slider(card, 0, 100);
            _micVol.ValuePreview += v =>
            {
                _micVolValue.Text = $"{v}%";
                Native.AudioEffects.SetVolume(_mic?.Device, v); // live, like Windows' own slider
            };
            _micVol.ValueCommitted += v => Native.AudioEffects.SetVolume(_mic?.Device, v);
            y += RowH + 16;
        }

        Heading(_micPanel, "CrystalVoice", y);
        y += 26;
        _micMissingTop = y;

        // Noise Reduction: switch + strength
        {
            var card = Card(_micPanel, y, RowH);
            _nrOn = Toggle(card);
            _nrOn.Toggled += v => Change(() => _mic.SetNoiseReduction(v));
            NameLabel(card, "Noise Reduction");
            _nrValue = ValueLabel(card);
            _nrLevel = Slider(card, 0, 100);
            _nrLevel.ValuePreview += v => _nrValue.Text = v.ToString();
            _nrLevel.ValueCommitted += v => Change(() => _mic.SetNoiseReductionLevel(v));
            y += RowH + Gap;
        }
        _aecOn = SwitchRow(_micPanel, ref y, "Echo Cancellation", v => _mic.SetEchoCancellation(v));
        _msvOn = SwitchRow(_micPanel, ref y, "Smart Volume", v => _mic.SetMicSmartVolume(v));

        // Focus: switch + pickup angle
        {
            var card = Card(_micPanel, y, RowH);
            _focusOn = Toggle(card);
            _focusOn.Toggled += v => Change(() => _mic.SetFocus(v));
            NameLabel(card, "Focus");
            _focusValue = ValueLabel(card);
            _focusAngle = Slider(card, 30, 150);
            _focusAngle.Step = 5;
            _focusAngle.ValuePreview += v => _focusValue.Text = $"{v}°";
            _focusAngle.ValueCommitted += v => Change(() => _mic.SetFocusAngle(v));
            new ToolTip().SetToolTip(_focusAngle, "How wide an area in front of the laptop the microphone listens to.");
            y += RowH + Gap;
        }

        _vmOn = PresetRow(_micPanel, ref y, "Voice Morph", v => _mic.SetVoiceMorphOn(v), out _vmPreset,
            SoundPresets.VoiceMorph, p => _mic.ApplyVoice(p));
        _meqOn = PresetRow(_micPanel, ref y, "Mic Equalizer", v => _mic.SetMicEqualizer(v), out _meqPreset,
            SoundPresets.MicEqualizer, p => _mic.ApplyMicEq(p));

        _micMissing = Missing(_micPanel, _micMissingTop,
            "CrystalVoice isn't available on this microphone.\n\n" +
            "It works on the laptop's built-in microphone and on headsets in the laptop's jack. " +
            "Bluetooth and USB microphones don't go through it.");
        return y;
    }

    // ---------------- building blocks ----------------

    private void Heading(Control parent, string text, int y) => parent.Controls.Add(new Label
    {
        Text = text,
        Location = new Point(0, S(y)),
        AutoSize = true,
        ForeColor = Theme.Text,
        Font = _fHead,
    });

    private Label HeadingHint(Control parent, int y)
    {
        var l = new Label
        {
            Location = new Point(S(170), S(y + 2)),
            Size = new Size(S(ContentWidth - 170), S(18)),
            TextAlign = ContentAlignment.TopRight,
            ForeColor = Theme.Muted,
            Font = _fSmall,
            AutoEllipsis = true,
        };
        parent.Controls.Add(l);
        return l;
    }

    private CardPanel Card(Control parent, int y, int h)
    {
        var card = new CardPanel { Location = new Point(0, S(y)), Size = new Size(S(ContentWidth), S(h)) };
        parent.Controls.Add(card);
        return card;
    }

    private ToggleSwitch Toggle(Control card)
    {
        var t = new ToggleSwitch
        {
            Location = new Point(S(12), S((RowH - 22) / 2)),
            Size = new Size(S(40), S(22)),
            BackColor = Theme.Card,
        };
        card.Controls.Add(t);
        return t;
    }

    private void NameLabel(Control card, string text) => card.Controls.Add(new Label
    {
        Text = text,
        Location = new Point(S(62), 2),
        Size = new Size(S(110), S(RowH) - 4),
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.Text,
        BackColor = Theme.Card,
        Font = _fBody,
    });

    private Label ValueLabel(Control card)
    {
        var l = new Label
        {
            Location = new Point(S(ContentWidth - 62), 2),
            Size = new Size(S(50), S(RowH) - 4),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Theme.Text,
            BackColor = Theme.Card,
            Font = _fBody,
        };
        card.Controls.Add(l);
        return l;
    }

    private LevelSlider Slider(Control card, int min, int max)
    {
        var s = new LevelSlider
        {
            Minimum = min,
            Maximum = max,
            Location = new Point(S(176), S((RowH - 22) / 2)),
            Size = new Size(S(ContentWidth - 176 - 70), S(22)),
            BackColor = Theme.Card,
        };
        card.Controls.Add(s);
        return s;
    }

    private ToggleSwitch SwitchRow(Control parent, ref int y, string name, Action<bool> set)
    {
        var card = Card(parent, y, RowH);
        var t = Toggle(card);
        t.Toggled += v => Change(() => set(v));
        NameLabel(card, name);
        y += RowH + Gap;
        return t;
    }

    private ToggleSwitch PresetRow(Control parent, ref int y, string name, Action<bool> set, out TileButton preset,
        SoundPresets.MicPreset[] presets, Action<SoundPresets.MicPreset> apply)
    {
        var card = Card(parent, y, RowH);
        var t = Toggle(card);
        t.Toggled += v => Change(() => set(v));
        NameLabel(card, name);
        preset = DropButton(card, new Point(S(ContentWidth - 12 - 170), S((RowH - 30) / 2)), S(170), _ =>
        {
            var menu = DarkMenu();
            foreach (var p in presets)
            {
                var item = new ToolStripMenuItem(p.Name) { ForeColor = Theme.Text };
                item.Click += (_, _) => Change(() => apply(p));
                menu.Items.Add(item);
            }
            return menu;
        });
        y += RowH + Gap;
        return t;
    }

    private TileButton DropButton(Control parent, Point at, int width, Func<TileButton, ContextMenuStrip> menu)
    {
        var b = new TileButton
        {
            Vertical = false,
            Font = _fSmall,
            Fill = Theme.Back,
            Location = at,
            Size = new Size(width, S(30)),
            BackColor = parent.BackColor,
        };
        b.Click += (_, _) =>
        {
            if (!b.Enabled) return;
            var m = menu(b);
            m.Closed += (_, _) => BeginInvoke(new Action(m.Dispose));
            m.Show(b, new Point(0, b.Height));
        };
        parent.Controls.Add(b);
        return b;
    }

    private ContextMenuStrip DarkMenu() => new()
    {
        Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()),
        BackColor = Theme.Card,
        ForeColor = Theme.Text,
        Font = _fBody,
        ShowImageMargin = false,
    };

    private Label Missing(Panel panel, int y, string text)
    {
        var l = new Label
        {
            Text = text,
            Location = new Point(0, S(y)),
            Size = new Size(S(ContentWidth), S(120)),
            ForeColor = Theme.Muted,
            Font = _fBody,
            Visible = false,
        };
        panel.Controls.Add(l);
        return l;
    }

    /// <summary>Row background: a rounded card. Child controls paint on Theme.Card.</summary>
    private sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            BackColor = Theme.Card;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Back);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int r = (int)Math.Round(6 * DeviceDpi / 96f * Theme.MainScale);
            using var path = Theme.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), r);
            using var b = new SolidBrush(Theme.Card);
            g.FillPath(b, path);
            using var p = new Pen(Theme.Line);
            g.DrawPath(p, path);
        }
    }
}
