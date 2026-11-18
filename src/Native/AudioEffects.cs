using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CHelper.Native;

/// <summary>
/// Reads and changes an audio device's sound-effect settings through Windows' own interface for them
/// (the effects property store, Windows 11). Sound effects such as Sound Blaster pick up a change immediately.
/// Everything here runs on the UI thread.
/// </summary>
public static class AudioEffects
{
    public enum Flow { Render = 0, Capture = 1 }

    /// <summary>An audio output or input as Windows lists it.</summary>
    public sealed record Endpoint(string Id, string Name, int FormFactor, Flow Flow)
    {
        /// <summary>Headphones or a headset (plugged into the jack), as opposed to speakers.</summary>
        public bool IsHeadphones => FormFactor is 3 or 5;

        public string Guid => Id[(Id.LastIndexOf('.') + 1)..];
    }

    private const int ClsctxInproc = 1;
    private const int StgmRead = 0, StgmReadWrite = 2;
    private const ushort VtEmpty = 0, VtR4 = 4, VtBool = 11, VtUi4 = 19, VtLpwstr = 31, VtClsid = 72;

    private static readonly PropKey FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    private static readonly PropKey FormFactor = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 0);

    /// <summary>The output or input Windows is using right now, or null when there is none.</summary>
    public static Endpoint Default(Flow flow)
    {
        IMMDeviceEnumerator en = null;
        IMMDevice dev = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint((int)flow, 1, out dev) != 0 || dev == null) return null;
            dev.GetId(out string id);
            string name = "";
            int form = 10;
            if (dev.OpenPropertyStore(StgmRead, out var props) == 0 && props != null)
            {
                try
                {
                    var k = FriendlyName;
                    if (props.GetValue(ref k, out var v) == 0) { name = Marshal.PtrToStringUni(v.p) ?? ""; PropVariantClear(ref v); }
                    k = FormFactor;
                    if (props.GetValue(ref k, out v) == 0) { form = v.intVal; PropVariantClear(ref v); }
                }
                finally { Marshal.ReleaseComObject(props); }
            }
            return new Endpoint(id, name, form, flow);
        }
        catch (Exception ex)
        {
            Log.Error("Reading the default audio device failed", ex);
            return null;
        }
        finally
        {
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    private static readonly PropKey DisableSysFx = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 5);
    private const string DisableSysFxName = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";

    /// <summary>
    /// Windows' "Audio enhancements" switch for the device. When off, Windows doesn't load the device's sound
    /// effects into the audio path at all. Null when it can't be read.
    /// </summary>
    public static bool? EnhancementsOff(Endpoint ep)
    {
        if (ep == null) return null;
        bool? result = null;
        WithPolicy(policy =>
        {
            var k = DisableSysFx;
            if (policy.GetPropertyValue(ep.Id, 1, ref k, out var v) == 0)
            {
                result = v.vt != VtEmpty && v.intVal != 0;
                PropVariantClear(ref v);
            }
        });
        if (result != null) return result;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{MmDevices}\{ep.Flow}\{ep.Guid}\FxProperties");
            return key?.GetValue(DisableSysFxName) is int off && off != 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Turns Windows' audio enhancements for the device off (true) or on, the same way the switch in Windows' Sound
    /// settings does, so the sound path is rebuilt right away. Returns false if Windows refused.
    /// </summary>
    public static bool SetEnhancementsOff(Endpoint ep, bool off)
    {
        if (ep == null) return false;
        bool ok = false;
        WithPolicy(policy =>
        {
            var k = DisableSysFx;
            var v = new PropVariant { vt = VtUi4, intVal = off ? 1 : 0 };
            int hr = policy.SetPropertyValue(ep.Id, 1, ref k, ref v);
            // Older Windows versions keep the switch with the device's other settings; set it there as well.
            v = new PropVariant { vt = VtUi4, intVal = off ? 1 : 0 };
            int hr2 = policy.SetPropertyValue(ep.Id, 0, ref k, ref v);
            ok = hr == 0 && EnhancementsOff(ep) == off;
            Log.Info($"Audio enhancements {(off ? "off" : "on")} for '{ep.Name}': 0x{hr:X8}/0x{hr2:X8}{(ok ? "" : ", not applied")}");
        });
        return ok;
    }

    private static void WithPolicy(Action<IPolicyConfig> use)
    {
        IPolicyConfig policy = null;
        try
        {
            policy = (IPolicyConfig)new PolicyConfigClient();
            use(policy);
        }
        catch (Exception ex)
        {
            Log.Error("Audio settings access failed", ex);
        }
        finally
        {
            if (policy != null) Marshal.ReleaseComObject(policy);
        }
    }

    /// <summary>Windows' own volume for the device, 0-100, or null when it can't be read.</summary>
    public static int? GetVolume(Endpoint ep)
    {
        int? result = null;
        WithVolume(ep, v => { if (v.GetMasterVolumeLevelScalar(out float level) == 0) result = (int)Math.Round(level * 100); });
        return result;
    }

    /// <summary>Sets Windows' own volume for the device (0-100), as the taskbar slider does.</summary>
    public static void SetVolume(Endpoint ep, int percent)
    {
        WithVolume(ep, v =>
        {
            var ctx = System.Guid.Empty;
            int hr = v.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref ctx);
            if (hr != 0) Log.Info($"Volume not set (0x{hr:X8})");
        });
    }

    private static void WithVolume(Endpoint ep, Action<IAudioEndpointVolume> use)
    {
        if (ep == null) return;
        IMMDeviceEnumerator en = null;
        IMMDevice dev = null;
        IntPtr raw = IntPtr.Zero;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDevice(ep.Id, out dev) != 0 || dev == null) return;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, ClsctxInproc, IntPtr.Zero, out raw) != 0 || raw == IntPtr.Zero) return;
            var vol = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(raw);
            try { use(vol); }
            finally { Marshal.ReleaseComObject(vol); }
        }
        catch (Exception ex)
        {
            Log.Error("Audio volume failed", ex);
        }
        finally
        {
            if (raw != IntPtr.Zero) Marshal.Release(raw);
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    private const string MmDevices = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
    private const string EffectsList = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13";

    /// <summary>True when the given effect (by its class id) is attached to this output or input.</summary>
    public static bool HasEffect(Endpoint ep, string effectClsid) =>
        ep != null && HasEffect(ep.Flow, ep.Guid, effectClsid);

    private static bool HasEffect(Flow flow, string guid, string effectClsid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{MmDevices}\{flow}\{guid}\FxProperties");
            return key?.GetValue(EffectsList) is string[] list &&
                   list.Any(s => s.Contains(effectClsid, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Every output or input that has the effect attached, plugged in or not.</summary>
    public static List<Endpoint> EndpointsWith(Flow flow, string effectClsid)
    {
        var list = new List<Endpoint>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey($@"{MmDevices}\{flow}");
            if (root == null) return list;
            string prefix = flow == Flow.Render ? "{0.0.0.00000000}." : "{0.0.1.00000000}.";
            foreach (var guid in root.GetSubKeyNames())
            {
                // Active (1) or unplugged (8); disabled and leftover devices are skipped.
                using var dev = root.OpenSubKey(guid);
                int state = dev?.GetValue("DeviceState") is int st ? st & 0xF : 0;
                if (state is 1 or 8 && HasEffect(flow, guid, effectClsid))
                    list.Add(new Endpoint(prefix + guid, guid, 10, flow));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Listing audio devices failed", ex);
        }
        return list;
    }

    /// <summary>True when any active output has the effect attached (used to decide whether to offer it at all).</summary>
    public static bool AnyOutputHas(string effectClsid)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey($@"{MmDevices}\Render");
            if (root == null) return false;
            foreach (var guid in root.GetSubKeyNames())
            {
                using var dev = root.OpenSubKey(guid);
                if (dev?.GetValue("DeviceState") is int state && state == 1 && HasEffect(Flow.Render, guid, effectClsid))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Looking for sound effects failed", ex);
        }
        return false;
    }

    /// <summary>
    /// The user settings of one output's (or input's) sound effects. An effect can keep its settings under more than
    /// one context; every write goes to all of them, like the effect's own app does.
    /// </summary>
    public sealed class Store : IDisposable
    {
        private readonly List<IPropertyStore> _stores = new();

        public Endpoint Endpoint { get; }
        public bool IsOpen => _stores.Count > 0;

        public Store(Endpoint ep, params string[] contexts)
        {
            Endpoint = ep;
            if (ep == null) return;
            IMMDeviceEnumerator en = null;
            IMMDevice dev = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (en.GetDevice(ep.Id, out dev) != 0 || dev == null) return;
                foreach (var ctx in contexts)
                {
                    var ps = OpenUser(dev, new Guid(ctx));
                    if (ps != null) _stores.Add(ps);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Opening the sound effect settings failed", ex);
            }
            finally
            {
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }

        private static IPropertyStore OpenUser(IMMDevice dev, Guid context)
        {
            IntPtr guid = Marshal.AllocCoTaskMem(16), param = Marshal.AllocCoTaskMem(24), raw = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(context, guid, false);
                for (int i = 0; i < 24; i++) Marshal.WriteByte(param, i, 0);
                Marshal.WriteInt16(param, 0, (short)VtClsid);
                Marshal.WriteIntPtr(param, 8, guid);
                var iid = typeof(IAudioSystemEffectsPropertyStore).GUID;
                int hr = dev.Activate(ref iid, ClsctxInproc, param, out raw);
                if (hr != 0 || raw == IntPtr.Zero)
                {
                    Log.Info($"Effect settings {context} not available (0x{hr:X8})");
                    return null;
                }
                var es = (IAudioSystemEffectsPropertyStore)Marshal.GetObjectForIUnknown(raw);
                try
                {
                    hr = es.OpenUserPropertyStore(StgmReadWrite, out var ps);
                    if (hr != 0) Log.Info($"Effect settings {context} can't be opened (0x{hr:X8})");
                    return hr == 0 ? ps : null;
                }
                finally { Marshal.ReleaseComObject(es); }
            }
            finally
            {
                if (raw != IntPtr.Zero) Marshal.Release(raw);
                Marshal.FreeCoTaskMem(param);
                Marshal.FreeCoTaskMem(guid);
            }
        }

        public bool? GetBool(string fmtid, uint pid)
        {
            if (!TryGet(fmtid, pid, out var v)) return null;
            return v.vt switch
            {
                VtBool => v.boolVal != 0,
                VtR4 => v.fltVal != 0,
                _ => v.intVal != 0,
            };
        }

        public float? GetFloat(string fmtid, uint pid)
        {
            if (!TryGet(fmtid, pid, out var v)) return null;
            return v.vt == VtR4 ? v.fltVal : v.intVal;
        }

        private bool TryGet(string fmtid, uint pid, out PropVariant value)
        {
            value = default;
            if (_stores.Count == 0) return false;
            var key = new PropKey(fmtid, pid);
            if (_stores[0].GetValue(ref key, out value) != 0) return false;
            bool ok = value.vt is VtBool or VtR4 or 3 or 19;
            if (!ok) PropVariantClear(ref value);
            return ok && value.vt != VtEmpty;
        }

        /// <summary>
        /// Rewrites a text setting in every context where it is set (used to move a file the effect loads).
        /// Returns how many were changed.
        /// </summary>
        public int RewriteText(string fmtid, uint pid, Func<string, string> map)
        {
            int changed = 0;
            var key = new PropKey(fmtid, pid);
            foreach (var ps in _stores)
            {
                if (ps.GetValue(ref key, out var v) != 0) continue;
                string old = v.vt == VtLpwstr ? Marshal.PtrToStringUni(v.p) : null;
                PropVariantClear(ref v);
                string now = old == null ? null : map(old);
                if (now == null || now == old) continue;
                var nv = new PropVariant { vt = VtLpwstr, p = Marshal.StringToCoTaskMemUni(now) };
                try
                {
                    int hr = ps.SetValue(ref key, ref nv);
                    if (hr == 0) changed++;
                    else Log.Info($"Effect setting {fmtid},{pid} not saved (0x{hr:X8})");
                }
                finally { PropVariantClear(ref nv); }
            }
            return changed;
        }

        /// <summary>Stores an on/off value. Call Commit once a group of changes is done.</summary>
        public void Set(string fmtid, uint pid, bool on) =>
            Write(fmtid, pid, new PropVariant { vt = VtBool, boolVal = (short)(on ? 1 : 0) });

        public void Set(string fmtid, uint pid, float value) =>
            Write(fmtid, pid, new PropVariant { vt = VtR4, fltVal = value });

        /// <summary>Stores a whole number (kept by Windows as a plain DWORD).</summary>
        public void Set(string fmtid, uint pid, uint value) =>
            Write(fmtid, pid, new PropVariant { vt = VtUi4, intVal = unchecked((int)value) });

        private void Write(string fmtid, uint pid, PropVariant value)
        {
            var key = new PropKey(fmtid, pid);
            foreach (var ps in _stores)
            {
                int hr = ps.SetValue(ref key, ref value);
                if (hr != 0) Log.Info($"Effect setting {fmtid},{pid} not saved (0x{hr:X8})");
            }
        }

        /// <summary>Saves the changes; the effect applies them straight away.</summary>
        public void Commit()
        {
            foreach (var ps in _stores)
            {
                int hr = ps.Commit();
                if (hr != 0) Log.Info($"Effect settings not committed (0x{hr:X8})");
            }
        }

        public void Dispose()
        {
            foreach (var ps in _stores) Marshal.ReleaseComObject(ps);
            _stores.Clear();
        }
    }

    // ---------------- COM ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct PropKey
    {
        public Guid fmtid;
        public uint pid;
        public PropKey(string g, uint p) { fmtid = new Guid(g); pid = p; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public short boolVal;
        [FieldOffset(8)] public int intVal;
        [FieldOffset(8)] public float fltVal;
        [FieldOffset(8)] public IntPtr p;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr activationParams, out IntPtr result);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropKey key);
        [PreserveSig] int GetValue(ref PropKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient { }

    /// <summary>The interface Windows' own Sound settings use to change a device's settings.</summary>
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultFormat, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultPeriod, out long period, out long minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fxStore, ref PropKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fxStore, ref PropKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    }

    [ComImport, Guid("302AE7F9-D7E0-43E4-971B-1F8293613D2A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSystemEffectsPropertyStore
    {
        [PreserveSig] int OpenDefaultPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int OpenUserPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int OpenVolatilePropertyStore(int access, out IPropertyStore store);
    }
}
