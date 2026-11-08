using System.Runtime;
using System.Runtime.InteropServices;

namespace CHelper.Native;

/// <summary>
/// Gives memory back to Windows while C-Helper only sits in the tray: a full compacting GC, then the working set is
/// trimmed so idle pages leave RAM. Nothing is lost; pages come back as soon as the window is opened.
/// </summary>
public static class Memory
{
    public static void Trim(string reason)
    {
        try
        {
            long before = Environment.WorkingSet;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
            Log.Info($"Memory trimmed ({reason}): {before / 1048576} MB -> {Environment.WorkingSet / 1048576} MB");
        }
        catch (Exception ex)
        {
            Log.Error("Memory trim failed", ex);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}
