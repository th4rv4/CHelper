using System.Runtime.InteropServices;
using Windows.Management.Deployment;

namespace CHelper.Native;

/// <summary>
/// Wrapper around the vendor's signed InsydeDCHU.dll (Clevo ACPI bridge, _DSM GUID 93f224e4-...).
/// Call semantics copied from the vendor's own ControlGPU.exe:
///   Read          : GetDCHU_Data_Integer(cmd, ref int)
///   Write         : SetDCHU_Data(cmd, byte[], len)
///   Package_Read  : GetDCHU_Data_Buffer(cmd, byte[256])
///   Package_RW    : SetDCHU_DataEx(cmd, in[256], 256, out[256])
/// </summary>
public static class Dchu
{
    private delegate int GetIntFn(int cmd, ref int data);
    private delegate int SetDataFn(int cmd, byte[] data, int len);
    private delegate int SetDataExFn(int cmd, byte[] inBuf, int len, [In, Out] byte[] outBuf);
    private delegate int GetBufFn(int cmd, [In, Out] byte[] outBuf);

    private static GetIntFn _getInt;
    private static SetDataFn _setData;
    private static SetDataExFn _setDataEx;
    private static GetBufFn _getBuf;
    private static readonly object Gate = new();

    public static string LoadedFrom { get; private set; }

    private static readonly string LocalCopy = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CHelper", "dchu", "InsydeDCHU.dll");

    public static void Init(string preferredPath)
    {
        // C-Helper keeps its own copy of the DLL, so it keeps working after the vendor app is removed.
        // The DLL only talks to the Clevo ACPI bridge driver, which is installed separately from the app.
        string source = FindDll(preferredPath);
        Directory.CreateDirectory(Path.GetDirectoryName(LocalCopy)!);
        if (source != null)
        {
            try
            {
                if (!File.Exists(LocalCopy) || new FileInfo(LocalCopy).Length != new FileInfo(source).Length)
                    File.Copy(source, LocalCopy, true);
            }
            catch (IOException)
            {
                // Copy is locked by a previous instance; the existing copy is fine.
            }
        }
        else if (File.Exists(LocalCopy))
        {
            source = LocalCopy;
        }
        else
        {
            throw new FileNotFoundException(
                "InsydeDCHU.dll was not found. Run C-Helper once while the vendor \"Fn hot keys and OSD\" app " +
                "is still installed (C-Helper keeps its own copy), or copy InsydeDCHU.dll next to CHelper.exe.");
        }
        string local = LocalCopy;

        IntPtr lib = NativeLibrary.Load(local);
        _getInt = Marshal.GetDelegateForFunctionPointer<GetIntFn>(NativeLibrary.GetExport(lib, "GetDCHU_Data_Integer"));
        _setData = Marshal.GetDelegateForFunctionPointer<SetDataFn>(NativeLibrary.GetExport(lib, "SetDCHU_Data"));
        _setDataEx = Marshal.GetDelegateForFunctionPointer<SetDataExFn>(NativeLibrary.GetExport(lib, "SetDCHU_DataEx"));
        _getBuf = Marshal.GetDelegateForFunctionPointer<GetBufFn>(NativeLibrary.GetExport(lib, "GetDCHU_Data_Buffer"));
        LoadedFrom = source;
        Log.Info($"DCHU loaded from {source}");
    }

    private static string FindDll(string preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred)) return preferred;

        string beside = Path.Combine(AppContext.BaseDirectory, "InsydeDCHU.dll");
        if (File.Exists(beside)) return beside;

        try
        {
            var pm = new PackageManager();
            foreach (var pkg in pm.FindPackagesForUser(string.Empty))
            {
                if (!pkg.Id.Name.Contains("FnhotkeysandOSD", StringComparison.OrdinalIgnoreCase)) continue;
                string p = Path.Combine(pkg.InstalledLocation.Path, "FnKey", "InsydeDCHU.dll");
                if (File.Exists(p)) return p;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Package lookup failed", ex);
        }
        return null;
    }

    public static int GetInt(int cmd)
    {
        lock (Gate)
        {
            int v = 0;
            _getInt(cmd, ref v);
            return v;
        }
    }

    /// <param name="log">False for high-rate writes (keyboard effects, fan curve) so the log stays readable.</param>
    public static void SetData(int cmd, byte[] data, bool log = true)
    {
        lock (Gate)
        {
            int r = _setData(cmd, data, data.Length);
            if (log) Log.Info($"Write cmd=0x{cmd:X2} data={BitConverter.ToString(data)} ret={r}");
        }
    }

    /// <summary>Extended command (Package_RW, cmd 4): in[0] = sub-command, in[1..] = args.</summary>
    public static byte[] Ex(byte sub, params byte[] args)
    {
        var input = new byte[256];
        var output = new byte[256];
        input[0] = sub;
        Array.Copy(args, 0, input, 1, Math.Min(args.Length, 255));
        lock (Gate)
        {
            int r = _setDataEx(4, input, 256, output);
            Log.Info($"Ex sub={sub} args={BitConverter.ToString(args)} ret={r} out={output[0]:X2} {output[1]:X2}");
        }
        return output;
    }

    public static byte[] GetBuffer(int cmd)
    {
        var output = new byte[256];
        lock (Gate) { _getBuf(cmd, output); }
        return output;
    }
}
