namespace CHelper.Sound;

/// <summary>
/// Built-in Sound Blaster Studio presets, the same values Creative ships for this laptop
/// (Sound Modes, Equalizer, Mic Equalizer and Voice Morph). Kept here so nothing depends on Creative App.
/// </summary>
public static class SoundPresets
{
    public sealed record EqPreset(string Id, string Name, float[] Speaker, float[] Headphone);

    public static readonly EqPreset[] Equalizer =
    {
        new("02", "Gaming", new[] { 0f, 0f, 3f, -2f, 0f, 3f, 0f, 0f, 3f, 2f }, new[] { 0f, 0f, 3f, -2f, 0f, 3f, 0f, 0f, 3f, 2f }),
        new("03", "Music", new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, -1f, 3f, -5f }, new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, -1f, 3f, -5f }),
        new("04", "Movies", new[] { 0f, 0f, 0f, 0f, 2f, 3f, 0f, 0f, 1f, 1f }, new[] { 0f, 0f, 0f, 0f, 2f, 3f, 0f, 0f, 1f, 1f }),
        new("06", "Acoustic", new[] { 0f, 0f, 2f, 0f, 0f, 2.5f, 2f, 2f, 2f, 3f }, new[] { 0f, 0f, 2f, 0f, 0f, 2.5f, 2f, 2f, 2f, 3f }),
        new("07", "Classical", new[] { 0f, 0f, 1.5f, 2f, 1f, 3f, 1f, 0f, 3f, 3f }, new[] { 0f, 0f, 1.5f, 2f, 1f, 3f, 1f, 0f, 3f, 3f }),
        new("08", "Country", new[] { 0f, 0f, 0f, 0f, 2f, 2.5f, 2.5f, 2f, 3f, 3f }, new[] { 0f, 0f, 0f, 0f, 2f, 2.5f, 2.5f, 2f, 3f, 3f }),
        new("09", "Dance", new[] { 0f, 0f, 1f, 1.5f, 0.5f, 1.5f, 2f, 1f, 2.5f, 2.5f }, new[] { 0f, 0f, 1f, 1.5f, 0.5f, 1.5f, 2f, 1f, 2.5f, 2.5f }),
        new("0A", "Flat", new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f }, new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f }),
        new("0B", "Hip Hop", new[] { 0f, 0f, 2f, 2.5f, 1.5f, 2.5f, 0f, -2.5f, -1f, -1.5f }, new[] { 0f, 0f, 2f, 2.5f, 1.5f, 2.5f, 0f, -2.5f, -1f, -1.5f }),
        new("0C", "Jazz", new[] { 0f, 0f, 0f, 2.5f, 2.5f, 3f, 1f, 1f, 3f, 3f }, new[] { 0f, 0f, 0f, 2.5f, 2.5f, 3f, 1f, 1f, 3f, 3f }),
        new("0D", "Pop", new[] { 0f, 0f, 1f, 1f, 0f, 2.5f, 0.5f, 0.5f, 3f, 3f }, new[] { 0f, 0f, 1f, 1f, 0f, 2.5f, 0.5f, 0.5f, 3f, 3f }),
        new("0E", "R & B", new[] { 0f, 0f, 0f, -1.5f, 2.5f, 1.5f, 2f, 0f, 2f, 1f }, new[] { 0f, 0f, 0f, -1.5f, 2.5f, 1.5f, 2f, 0f, 2f, 1f }),
        new("0F", "Rock", new[] { 0f, 0f, 2f, 1f, 0f, 3f, 1f, -1f, 3f, 3f }, new[] { 0f, 0f, 2f, 1f, 0f, 3f, 1f, -1f, 3f, 3f }),
        new("10", "Vocal", new[] { 0f, 0f, -1.5f, -0.5f, 3f, 3f, 3f, 0f, 0f, 1f }, new[] { 0f, 0f, -1.5f, -0.5f, 3f, 3f, 3f, 0f, 0f, 1f }),
        new("13", "Arena of Valor", new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f }, new[] { 2f, 2f, 2f, -2f, -2f, 2f, 2f, -3f, 2f, 0f }),
        new("15", "Call of Duty", new[] { 0f, 0f, 2f, 0f, 0f, 2f, 3f, 0f, 0f, 0f }, new[] { 4f, 3f, -1f, -2f, 2f, 2f, 2f, -4f, 0f, 2f }),
        new("16", "CS:GO", new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f }, new[] { -5f, 3f, -3f, -3f, -2f, -2f, 2f, 0f, 0f, 3f }),
        new("1A", "DOTA 2", new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f }, new[] { -5f, 0f, -3f, -4f, -5f, -1f, -3f, -2f, -3f, -1f }),
        new("1E", "FPS", new[] { 3f, 3f, 0f, 0f, 0f, 3f, 3f, -3f, 0f, 0f }, new[] { 2f, 3f, -2f, -2f, 0f, 2f, 2f, -5f, 2f, 1f }),
        new("23", "Overwatch", new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f }, new[] { 2f, 2.5f, 1.5f, -1.5f, 1.5f, 2f, 2f, -3.5f, 1.5f, 2f }),
        new("24", "PUBG", new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f }, new[] { 0f, 3f, -4f, -4f, 0f, 2f, 2f, 0f, 2f, 0f }),
        new("2D", "Streaming", new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, -1f, 3f, -5f }, new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, -1f, 3f, -5f }),
    };

    /// <summary>One output's worth of a Sound Mode (speakers or headphones).</summary>
    public sealed record ModeValues(
        bool Surround, float SurroundLevel, bool Crystalizer, float CrystalizerLevel,
        bool Bass, float BassLevel, float Crossover, bool SmartVolume, float SmartVolumeLevel,
        bool DialogPlus, float DialogPlusLevel, bool Eq, string EqPresetId);

    public sealed record SoundMode(string Name, ModeValues Speaker, ModeValues Headphone);

    /// <summary>The four modes on the Sound panel, in button order.</summary>
    public static readonly SoundMode[] Modes =
    {
        new("Gaming",
            new(true, 0.8f, true, 0.3f, true, 10f, 80f, true, 0.74f, false, 0.5f, true, "02"),
            new(true, 0.8f, true, 0.3f, true, 10f, 80f, true, 0.74f, false, 0.5f, true, "02")),
        new("Music",
            new(true, 0.8f, true, 0.5f, true, 40f, 80f, true, 0.6f, false, 0.5f, true, "03"),
            new(true, 0.8f, true, 0.5f, true, 40f, 80f, true, 0.6f, false, 0.5f, true, "03")),
        new("Movies",
            new(true, 0.8f, true, 0.55f, true, 20f, 80f, true, 0.74f, false, 0.3f, true, "04"),
            new(true, 0.8f, true, 0.55f, true, 20f, 80f, true, 0.74f, true, 0.3f, true, "04")),
        new("Streaming",
            new(false, 0.67f, true, 0.5f, false, 50f, 80f, true, 0.2f, true, 0.7f, true, "2D"),
            new(false, 0.67f, true, 0.5f, false, 50f, 80f, true, 0.2f, true, 0.7f, true, "2D")),
    };

    public sealed record MicPreset(string Name, float[] Values);

    /// <summary>Microphone Equalizer presets: 10 bands in dB.</summary>
    public static readonly MicPreset[] MicEqualizer =
    {
        new("Chat", new[] { 0f, -2f, -3f, 2f, 4f, 4f, 0f, -3f, 3f, 0f }),
        new("Studio Recording", new[] { 0f, -2f, -3f, 3f, 4f, 4f, 0f, 3f, 2f, 0f }),
        new("Streaming", new[] { 0f, 0f, 0f, 2f, 2f, -2f, 0f, -4f, 4f, 0f }),
        new("Gaming", new[] { 0f, -3f, -4f, -4f, 3f, 4f, 3f, 3f, 3f, 0f }),
        new("Podcasts", new[] { 0f, 0f, 6f, 6f, 8f, 8f, 0f, 0f, 3f, 0f }),
    };

    /// <summary>Voice Morph presets: the 8 voice-shaping values the engine expects.</summary>
    public static readonly MicPreset[] VoiceMorph =
    {
        new("Neutral", new[] { 400f, 1400f, 2000f, 1f, 1f, 1f, 0f, 0f }),
        new("Male", new[] { 400f, 1400f, 2000f, 0.6f, 1.05f, 1f, 0f, 0f }),
        new("Female", new[] { 400f, 1400f, 2220f, 2.37f, 0.92f, 1f, 0f, 0f }),
        new("Deep Voice", new[] { 339.5443f, 1362.496f, 2046f, 0.4291186f, 1.201318f, 1f, 0f, 0f }),
        new("Elf", new[] { 398f, 1395f, 2451f, 1.11f, 0.96f, 1f, 0f, 0f }),
        new("Robot", new[] { 400f, 1400f, 2000f, 1.394f, 1f, -0.00824f, 0f, 0f }),
    };

    public static EqPreset FindEq(string id) => Array.Find(Equalizer, p => p.Id == id);
}
