using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Management;

namespace CHelper.Hardware;

/// <summary>
/// Fn+Esc normally makes the vendor's background app (FnKey.exe) launch Control Center (CC40.exe).
/// FnKey logs "case149,openCC40" to the OutLog event log when that happens, so C-Helper watches
/// that log, closes the vendor window as it starts, and shows itself instead.
/// As a fallback it also watches for CC40.exe being started (WMI process trace, needs admin).
/// </summary>
public static class VendorHotkey
{
    private static EventLogWatcher _watcher;
    private static ManagementEventWatcher _procWatcher;
    private static DateTime _lastTrigger = DateTime.MinValue;
    private static readonly object Gate = new();
    private static readonly string[] VendorUi = { "CC40", "LoadUI" };

    public static event Action OpenRequested;

    public static bool Enabled { get; private set; }

    public static void SetEnabled(bool on)
    {
        if (on == Enabled) return;
        if (on) Start();
        else Stop();
    }

    private static void Start()
    {
        try
        {
            _watcher = new EventLogWatcher(new EventLogQuery("OutLog", PathType.LogName));
            _watcher.EventRecordWritten += OnRecord;
            _watcher.Enabled = true;
            Log.Info("Fn+Esc hook: OutLog watcher active");
        }
        catch (Exception ex)
        {
            Log.Error("Fn+Esc hook: OutLog watcher failed", ex);
        }

        try
        {
            _procWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = 'CC40.exe'"));
            _procWatcher.EventArrived += (_, _) => Trigger("CC40.exe started");
            _procWatcher.Start();
            Log.Info("Fn+Esc hook: process watcher active");
        }
        catch (Exception ex)
        {
            Log.Error("Fn+Esc hook: process watcher failed", ex);
        }

        Enabled = true;
    }

    private static void Stop()
    {
        try
        {
            if (_watcher != null)
            {
                _watcher.Enabled = false;
                _watcher.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Fn+Esc hook stop failed", ex);
        }
        try
        {
            _procWatcher?.Stop();
            _procWatcher?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Process watcher stop failed", ex);
        }
        _watcher = null;
        _procWatcher = null;
        Enabled = false;
    }

    private static void OnRecord(object sender, EventRecordWrittenEventArgs e)
    {
        using var record = e.EventRecord;
        if (record == null) return;

        string message = null;
        try { message = record.FormatDescription(); } catch { }
        if (string.IsNullOrEmpty(message) && record.Properties.Count > 0)
            message = record.Properties[0].Value?.ToString();
        if (message == null || !message.Contains("openCC40", StringComparison.OrdinalIgnoreCase)) return;
        Trigger("OutLog openCC40");
    }

    private static void Trigger(string source)
    {
        lock (Gate)
        {
            if ((DateTime.Now - _lastTrigger).TotalSeconds < 3) return;
            _lastTrigger = DateTime.Now;
        }
        Log.Info($"Fn+Esc detected ({source})");
        OpenRequested?.Invoke();
        Task.Run(CloseVendorUi);
    }

    private static void CloseVendorUi()
    {
        // The vendor UI starts a moment after the log entry; keep closing it for a few seconds.
        for (int i = 0; i < 25; i++)
        {
            foreach (string name in VendorUi)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(); }
                    catch (Exception ex) { Log.Error($"Could not close {name}", ex); }
                    finally { p.Dispose(); }
                }
            }
            Thread.Sleep(200);
        }
    }
}
