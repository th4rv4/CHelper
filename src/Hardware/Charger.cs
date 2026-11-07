using CHelper.Native;

namespace CHelper.Hardware;

/// <summary>
/// Battery charge thresholds (vendor "FlexiCharger").
/// Read : extended 30 -> out[0] = enabled, out[1] = start %, out[2] = stop %,
///        out[16..31] = allowed start values, out[32..47] = allowed stop values (0 = unused).
/// Write: extended 31 -> [enabled, start, stop]. "Maximum" is [0, highest start, 100].
/// </summary>
public static class Charger
{
    private const byte ExRead = 30;
    private const byte ExWrite = 31;

    public sealed record State(bool Enabled, int Start, int Stop, int[] Starts, int[] Stops)
    {
        /// <summary>Effective stop level: 100 when the limit is off.</summary>
        public int Limit => Enabled ? Stop : 100;
    }

    public static State Read()
    {
        byte[] o = Dchu.Ex(ExRead);
        int[] starts = o.Skip(16).Take(16).Where(b => b > 0).Select(b => (int)b).Distinct().OrderBy(b => b).ToArray();
        int[] stops = o.Skip(32).Take(16).Where(b => b > 0).Select(b => (int)b).Distinct().OrderBy(b => b).ToArray();
        return new State(o[0] != 0, o[1], o[2], starts, stops);
    }

    /// <summary>Stop values the slider can snap to (always includes 100 = no limit).</summary>
    public static int[] SliderStops(State s)
    {
        var list = new SortedSet<int>(s.Stops.Length > 0 ? s.Stops : new[] { 60, 70, 80, 90 }) { 100 };
        return list.ToArray();
    }

    public static void SetLimit(int stop, State s)
    {
        if (stop >= 100)
        {
            int start = s.Starts.Length > 0 ? s.Starts[^1] : Math.Max(s.Start, 90);
            Dchu.Ex(ExWrite, 0, (byte)start, 100);
            return;
        }

        // Start charging 10% below the limit (like the vendor's 70/80), using a value the BIOS allows.
        int wanted = stop - 10;
        int startValue = s.Starts.Where(v => v < stop).DefaultIfEmpty(wanted)
            .OrderBy(v => Math.Abs(v - wanted)).First();
        Dchu.Ex(ExWrite, 1, (byte)startValue, (byte)stop);
    }
}
