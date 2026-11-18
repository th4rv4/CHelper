using CHelper.Native;
using static CHelper.Sound.SoundPresets;

namespace CHelper.Sound;

/// <summary>
/// Sound Blaster Studio settings for the current output (speakers or headphones) or microphone.
/// Each setting is one value in the device's effect settings; Creative's engine applies a change immediately.
/// </summary>
public sealed class SoundBlaster : IDisposable
{
    /// <summary>Creative's playback effects engine; present on an output means Sound Blaster works there.</summary>
    public const string EngineClsid = "0F62DFB3-DB5B-458D-9371-6B45C4582560";

    // Creative keeps the playback settings under two contexts and the microphone settings under one.
    private static readonly string[] OutputContexts = { "3f5f306b-a033-4f19-843d-1c44a736ff4d", "852311bc-1afb-454e-92ca-c35252cacaaf" };
    private static readonly string[] MicContexts = { "852311bc-1afb-454e-92ca-c35252cacaaf" };

    public enum Effect { Surround, Crystalizer, Bass, SmartVolume, DialogPlus }

    // (on/off, level, level scale): levels are stored 0-1, Bass 0-100. The UI shows 0-100 for all.
    private static readonly Dictionary<Effect, (string On, string Level, float Scale)> Keys = new()
    {
        [Effect.Surround] = ("5b4777a4-8ad4-4d34-893a-df34da0e56ca", "a5a78ea4-c156-4db7-85aa-81cff1c3f192", 100f),
        [Effect.Crystalizer] = ("3cd83c04-868f-4f08-8d75-b4625ffe3b31", "0f03f0bb-72c7-4ec1-8422-7b8d7410694a", 100f),
        [Effect.Bass] = ("f67cf426-f8cb-4a40-bdac-580802e3e193", "dd527e35-21a5-4ca6-ab90-8ad464fb55e3", 1f),
        [Effect.SmartVolume] = ("9ad782d7-f46e-465c-8df5-3cda75424987", "80b0c7bb-0989-434e-af5b-fb9020f471b3", 100f),
        [Effect.DialogPlus] = ("ea3137f9-be10-4eaa-8fce-a36988bca7dd", "a79717e9-81cf-4272-adc6-d12b69b389a0", 100f),
    };

    private const string Crossover = "3f23dbc5-12d1-4d62-89ed-bc458337e0fc";
    private const string SmartVolumeMode = "e6ec3743-ddd2-4817-8466-b433761dcf9d";
    private const string EqOn = "9a9d0cb2-4dc9-494c-8210-9848ae1aa629";
    private const string EqBands = "2b88c76d-d07c-4e97-8922-1bac9f6d5935";
    private const string Preamp = "ddcf8d90-de27-4de4-af57-088b8ad78fdf";

    // Microphone (CrystalVoice)
    private const string NoiseOn = "3c14eccc-4a1f-47f7-91dd-bf45af920a4d";
    private const string NoiseLevel = "6a72f5dd-6c09-4147-82c5-14c64b0e4e0f";
    private const string MicFlags = "40d0d021-20bd-4d15-a93c-1dbe8922c642"; // 0 = Focus, 1 = set with Noise Reduction
    private const string EchoOn = "35f00393-1adf-43ce-84cb-7a926ac012b6";
    private const string FocusAngleKey = "72e09675-2af9-485c-89f1-898e532bf06e";
    private const string VoiceMorph = "12c98f26-05cd-458c-829e-219e482fad8e"; // 0-7 = voice values, 99 = on/off

    /// <summary>Smart Volume modes as the engine numbers them.</summary>
    public enum SvmMode { Normal = 0, Loud = 1, Night = 2 }

    /// <summary>Highest Volume Boost: 200% (+6 dB).</summary>
    public const int MaxBoost = 200;

    private readonly AudioEffects.Store _store;

    public AudioEffects.Endpoint Device { get; }
    public bool IsMicrophone => Device?.Flow == AudioEffects.Flow.Capture;
    /// <summary>
    /// What is listening to the output, chosen by hand like in Creative App: the laptop's speakers, headphones or
    /// external speakers in the jack. Headphones get the headphone version of each preset and headphone surround.
    /// </summary>
    public enum OutputKind { LineOut = 0, BuiltIn = 1, Headphones = 2 }

    private const string OutputKindKey = "59fbb5c6-4c98-4741-adc0-40abb474a963";
    private const string HeadphoneMode = "e8d2d16c-a666-44a0-bb77-3051f7384d22";

    public OutputKind Kind => (OutputKind)Math.Clamp((int)(_store?.GetFloat(OutputKindKey, 0) ?? 1), 0, 2);

    public bool Headphones => Kind == OutputKind.Headphones;

    /// <summary>Switches between speakers and headphones; a Sound Mode in use is re-applied in that version.</summary>
    public void SetKind(OutputKind kind)
    {
        int mode = Available ? CurrentMode() : -1;
        Change(() =>
        {
            _store.Set(OutputKindKey, 0, (uint)kind);
            _store.Set(HeadphoneMode, 0, kind == OutputKind.Headphones);
        });
        if (mode >= 0) ApplyMode(Modes[mode]);
    }

    /// <summary>Creative's engine is attached to this device and its settings could be opened.</summary>
    public bool Available => _store?.IsOpen == true;

    private SoundBlaster(AudioEffects.Endpoint device)
    {
        Device = device;
        if (device == null || !AudioEffects.HasEffect(device, EngineClsid)) return;
        _store = new AudioEffects.Store(device, IsMicrophone ? MicContexts : OutputContexts);
    }

    public static SoundBlaster Output() => new(AudioEffects.Default(AudioEffects.Flow.Render));
    public static SoundBlaster Microphone() => new(AudioEffects.Default(AudioEffects.Flow.Capture));

    /// <summary>Whether this laptop has Sound Blaster on any output (decides if the Sound button shows).</summary>
    public static bool Installed() => AudioEffects.AnyOutputHas(EngineClsid);

    public void Dispose() => _store?.Dispose();

    /// <summary>The master switch is off: Windows' audio enhancements are off on the current output.</summary>
    public static bool EngineOff()
    {
        var ep = AudioEffects.Default(AudioEffects.Flow.Render);
        return ep != null && AudioEffects.HasEffect(ep, EngineClsid) && AudioEffects.EnhancementsOff(ep) == true;
    }

    /// <summary>
    /// Master switch: turns Windows' audio enhancements on or off for every speaker, headphone and microphone that
    /// has Creative's engine, so with it off the engine isn't loaded at all. Settings are kept. Returns how many
    /// devices Windows refused.
    /// </summary>
    public static int SetEngine(bool on)
    {
        int failed = 0;
        foreach (var flow in new[] { AudioEffects.Flow.Render, AudioEffects.Flow.Capture })
            foreach (var ep in AudioEffects.EndpointsWith(flow, EngineClsid))
                if (!AudioEffects.SetEnhancementsOff(ep, !on)) failed++;
        Log.Info($"Sound Blaster engine turned {(on ? "on" : "off")}{(failed > 0 ? $", {failed} devices refused" : "")}");
        return failed;
    }

    /// <summary>
    /// False on a laptop where Creative App never ran: the engine has no saved settings for this device yet.
    /// The Sound panel then writes Creative's defaults once, so it never depends on Creative App having set things up.
    /// </summary>
    public bool HasSettings => !Available || (IsMicrophone
        ? _store.GetBool(NoiseOn, 0) != null
        : _store.GetBool(Keys[Effect.Surround].On, 0) != null && _store.GetFloat(Keys[Effect.Surround].Level, 0) != null);

    // ---------------- playback ----------------

    public bool IsOn(Effect e) => _store.GetBool(Keys[e].On, 0) ?? false;

    public int Level(Effect e) => (int)Math.Round((_store.GetFloat(Keys[e].Level, 0) ?? 0) * Keys[e].Scale);

    public void SetOn(Effect e, bool on) => Change(() => _store.Set(Keys[e].On, 0, on));

    public void SetLevel(Effect e, int level) =>
        Change(() => _store.Set(Keys[e].Level, 0, Math.Clamp(level, 0, 100) / Keys[e].Scale));

    public SvmMode SmartMode => (SvmMode)(int)Math.Round(_store.GetFloat(SmartVolumeMode, 0) ?? 0);

    public void SetSmartMode(SvmMode mode) => Change(() => _store.Set(SmartVolumeMode, 0, (float)(int)mode));

    public bool EqualizerOn => _store.GetBool(EqOn, 0) ?? false;

    public void SetEqualizerOn(bool on) => Change(() => _store.Set(EqOn, 0, on));

    public float[] Bands()
    {
        var bands = new float[10];
        for (uint i = 0; i < 10; i++) bands[i] = _store.GetFloat(EqBands, i) ?? 0;
        return bands;
    }

    public void SetBands(float[] bands) => Change(() => WriteBands(bands));

    private void WriteBands(float[] bands)
    {
        for (uint i = 0; i < 10 && i < bands.Length; i++) _store.Set(EqBands, i, Math.Clamp(bands[i], -12f, 12f));
    }

    /// <summary>Equalizer preset matching the current bands (the given one first when several match), or null.</summary>
    public EqPreset MatchingPreset(string preferId = null)
    {
        var bands = Bands();
        bool Matches(EqPreset p) => (Headphones ? p.Headphone : p.Speaker).Zip(bands).All(x => Math.Abs(x.First - x.Second) < 0.05f);
        var preferred = preferId == null ? null : FindEq(preferId);
        if (preferred != null && Matches(preferred)) return preferred;
        return Equalizer.FirstOrDefault(Matches);
    }

    public void ApplyPreset(EqPreset p) => SetBands(Headphones ? p.Headphone : p.Speaker);

    /// <summary>Volume Boost in percent (100 = off). It is the Equalizer's preamp: 200% is +6 dB.</summary>
    public int Boost
    {
        get
        {
            float db = _store.GetFloat(Preamp, 0) ?? 0;
            return (int)Math.Round(Math.Clamp(100 * Math.Pow(10, db / 20), 100, MaxBoost));
        }
    }

    /// <summary>
    /// Sets Volume Boost. Above 100% the Equalizer must be on for the preamp to apply (a flat curve if it was off),
    /// and Smart Volume stays on to hold back the peaks that would otherwise crackle.
    /// </summary>
    public void SetBoost(int percent)
    {
        percent = Math.Clamp(percent, 100, MaxBoost);
        Change(() =>
        {
            _store.Set(Preamp, 0, (float)(20 * Math.Log10(percent / 100.0)));
            if (percent > 100)
            {
                if (!EqualizerOn)
                {
                    WriteBands(new float[10]);
                    _store.Set(EqOn, 0, true);
                }
                _store.Set(Keys[Effect.SmartVolume].On, 0, true);
            }
        });
    }

    private ModeValues ValuesOf(SoundMode m) => Headphones ? m.Headphone : m.Speaker;

    public void ApplyMode(SoundMode mode)
    {
        var v = ValuesOf(mode);
        Change(() =>
        {
            Write(Effect.Surround, v.Surround, v.SurroundLevel);
            Write(Effect.Crystalizer, v.Crystalizer, v.CrystalizerLevel);
            Write(Effect.Bass, v.Bass, v.BassLevel);
            Write(Effect.SmartVolume, v.SmartVolume || Boost > 100, v.SmartVolumeLevel);
            Write(Effect.DialogPlus, v.DialogPlus, v.DialogPlusLevel);
            _store.Set(Crossover, 0, v.Crossover);
            _store.Set(Crossover, 2, v.Crossover);
            _store.Set(SmartVolumeMode, 0, 0f);
            var eq = FindEq(v.EqPresetId);
            if (eq != null) WriteBands(Headphones ? eq.Headphone : eq.Speaker);
            _store.Set(EqOn, 0, v.Eq || Boost > 100);
        });
    }

    private void Write(Effect e, bool on, float stored)
    {
        _store.Set(Keys[e].On, 0, on);
        _store.Set(Keys[e].Level, 0, stored);
    }

    /// <summary>Index of the Sound Mode the current settings match exactly, or -1.</summary>
    public int CurrentMode()
    {
        for (int i = 0; i < Modes.Length; i++)
        {
            var v = ValuesOf(Modes[i]);
            if (Same(Effect.Surround, v.Surround, v.SurroundLevel) &&
                Same(Effect.Crystalizer, v.Crystalizer, v.CrystalizerLevel) &&
                Same(Effect.Bass, v.Bass, v.BassLevel) &&
                Same(Effect.DialogPlus, v.DialogPlus, v.DialogPlusLevel) &&
                Math.Abs((_store.GetFloat(Keys[Effect.SmartVolume].Level, 0) ?? 0) - v.SmartVolumeLevel) < 0.01f &&
                EqualizerOn == (v.Eq || Boost > 100) &&
                MatchingPreset(v.EqPresetId)?.Id == v.EqPresetId)
                return i;
        }
        return -1;
    }

    private bool Same(Effect e, bool on, float stored) =>
        IsOn(e) == on && Math.Abs((_store.GetFloat(Keys[e].Level, 0) ?? 0) - stored) < 0.01f * Math.Max(1, Keys[e].Scale == 1 ? 100 : 1);

    // ---------------- microphone ----------------

    public bool NoiseReduction => _store.GetBool(NoiseOn, 0) ?? false;
    public int NoiseReductionLevel => (int)Math.Round((_store.GetFloat(NoiseLevel, 0) ?? 0.5f) * 100);
    public bool EchoCancellation => _store.GetBool(EchoOn, 0) ?? false;
    public bool MicSmartVolume => _store.GetBool(Keys[Effect.SmartVolume].On, 0) ?? false;
    public bool Focus => _store.GetBool(MicFlags, 0) ?? false;
    public int FocusAngle => (int)Math.Round(_store.GetFloat(FocusAngleKey, 0) ?? 30);
    public bool VoiceMorphOn => _store.GetBool(VoiceMorph, 99) ?? false;
    public bool MicEqualizer => _store.GetBool(EqOn, 0) ?? false;

    public void SetNoiseReduction(bool on) => Change(() =>
    {
        _store.Set(NoiseOn, 0, on);
        if (on) _store.Set(MicFlags, 1, true);
    });

    public void SetNoiseReductionLevel(int level) => Change(() => _store.Set(NoiseLevel, 0, Math.Clamp(level, 0, 100) / 100f));
    public void SetEchoCancellation(bool on) => Change(() => _store.Set(EchoOn, 0, on));
    public void SetMicSmartVolume(bool on) => Change(() => _store.Set(Keys[Effect.SmartVolume].On, 0, on));
    public void SetFocus(bool on) => Change(() => _store.Set(MicFlags, 0, on));
    public void SetFocusAngle(int degrees) => Change(() => _store.Set(FocusAngleKey, 0, (float)degrees));
    public void SetVoiceMorphOn(bool on) => Change(() => _store.Set(VoiceMorph, 99, on));
    public void SetMicEqualizer(bool on) => Change(() => _store.Set(EqOn, 0, on));

    public void ApplyVoice(MicPreset p) => Change(() =>
    {
        for (uint i = 0; i < p.Values.Length; i++) _store.Set(VoiceMorph, i, p.Values[i]);
    });

    public MicPreset CurrentVoice() => SoundPresets.VoiceMorph.FirstOrDefault(p =>
        p.Values.Select((v, i) => Math.Abs((_store.GetFloat(VoiceMorph, (uint)i) ?? 0) - v) < 0.01f).All(x => x));

    public void ApplyMicEq(MicPreset p) => SetBands(p.Values);

    public MicPreset CurrentMicEq()
    {
        var bands = Bands();
        return SoundPresets.MicEqualizer.FirstOrDefault(p => p.Values.Zip(bands).All(x => Math.Abs(x.First - x.Second) < 0.05f));
    }

    /// <summary>Resets this device to Creative's defaults (Gaming mode, no boost; microphone effects off).</summary>
    public void Reset()
    {
        if (IsMicrophone)
        {
            Change(() =>
            {
                _store.Set(NoiseOn, 0, false);
                _store.Set(NoiseLevel, 0, 0.5f);
                _store.Set(EchoOn, 0, false);
                _store.Set(Keys[Effect.SmartVolume].On, 0, false);
                _store.Set(MicFlags, 0, false);
                _store.Set(FocusAngleKey, 0, 30f);
                _store.Set(VoiceMorph, 99, false);
                _store.Set(EqOn, 0, false);
            });
            ApplyVoice(SoundPresets.VoiceMorph[0]);
            ApplyMicEq(SoundPresets.MicEqualizer[0]);
            return;
        }
        Change(() =>
        {
            _store.Set(Preamp, 0, 0f);
            _store.Set(SmartVolumeMode, 0, 0f);
        });
        ApplyMode(Modes[0]);
    }

    private void Change(Action write)
    {
        if (!Available) return;
        try
        {
            write();
            _store.Commit();
        }
        catch (Exception ex)
        {
            Log.Error("Changing a Sound Blaster setting failed", ex);
        }
    }
}
