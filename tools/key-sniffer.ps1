# key-sniffer.ps1 - shows every key Windows receives (virtual-key code, scan code, flags), so we can see
# what a special key like Fn+F4 sends. Nothing is changed; close the window to stop.
#
#   powershell -ExecutionPolicy Bypass -File .\key-sniffer.ps1
# Press Fn+F4 a few times, then close the window. Output is saved to key-sniffer.txt next to this script.

$log = Join-Path $PSScriptRoot 'key-sniffer.txt'
"" | Set-Content $log

Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @"
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class KeySniffer
{
    delegate IntPtr Proc(int code, IntPtr w, IntPtr l);
    static Proc _proc;
    static IntPtr _hook;
    public static TextBox Box;
    public static string LogPath;

    public static void Start()
    {
        _proc = Callback;
        using (var m = Process.GetCurrentProcess().MainModule)
            _hook = SetWindowsHookEx(13, _proc, GetModuleHandle(m.ModuleName), 0);
    }

    public static void Stop() { if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook); }

    static IntPtr Callback(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            int vk = Marshal.ReadInt32(l), scan = Marshal.ReadInt32(l, 4), flags = Marshal.ReadInt32(l, 8);
            string kind = w == (IntPtr)0x100 ? "down" : w == (IntPtr)0x101 ? "up" : w == (IntPtr)0x104 ? "sysdown" : "sysup";
            string line = string.Format("{0:HH:mm:ss.fff}  {1,-8} vk=0x{2:X2} ({3})  scan=0x{4:X2}  flags=0x{5:X2}",
                DateTime.Now, kind, vk, (Keys)vk, scan, flags);
            System.IO.File.AppendAllText(LogPath, line + Environment.NewLine);
            Box.AppendText(line + Environment.NewLine);
        }
        return CallNextHookEx(_hook, code, w, l);
    }

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, Proc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
}
"@

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Key sniffer - press Fn+F4 a few times, then close'
$form.Size = New-Object System.Drawing.Size(720, 420)
$form.TopMost = $true
$box = New-Object System.Windows.Forms.TextBox
$box.Multiline = $true; $box.ScrollBars = 'Vertical'; $box.Dock = 'Fill'; $box.ReadOnly = $true
$box.Font = New-Object System.Drawing.Font('Consolas', 10)
$form.Controls.Add($box)
[KeySniffer]::Box = $box
[KeySniffer]::LogPath = $log
$form.Add_Shown({ [KeySniffer]::Start() })
$form.Add_FormClosed({ [KeySniffer]::Stop() })
[System.Windows.Forms.Application]::Run($form)
Write-Host "Saved to $log"
