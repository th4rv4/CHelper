using System.Runtime.InteropServices;

namespace CHelper.Native;

/// <summary>System-wide keyboard shortcuts (RegisterHotKey), e.g. Ctrl+Shift+F5 like G-Helper.</summary>
public sealed class GlobalHotkeys : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    public const uint MOD_SHIFT = 0x4, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public GlobalHotkeys() => CreateHandle(new CreateParams());

    /// <summary>Registers a shortcut; logs and returns false if another app already owns it.</summary>
    public bool Register(uint modifiers, Keys key, string name, Action action)
    {
        int id = _nextId++;
        if (!RegisterHotKey(Handle, id, modifiers | MOD_NOREPEAT, (uint)key))
        {
            Log.Error($"Shortcut {name} is taken by another app (error {Marshal.GetLastWin32Error()})");
            return false;
        }
        _actions[id] = action;
        Log.Info($"Shortcut {name} registered");
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _actions.TryGetValue((int)m.WParam, out var action))
        {
            try { action(); }
            catch (Exception ex) { Log.Error("Shortcut action failed", ex); }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys) UnregisterHotKey(Handle, id);
        _actions.Clear();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
