namespace CHelper.UI;

/// <summary>
/// C-Helper runs without culture data (smaller and lighter), but WinForms turns keyboard-layout messages into
/// culture objects and throws when a window gets focus with a layout such as English (India) active.
/// Forms hand these messages straight to Windows instead; C-Helper doesn't use the layout events.
/// </summary>
internal static class InputLanguageGuard
{
    private const int WM_INPUTLANGCHANGEREQUEST = 0x0050;
    private const int WM_INPUTLANGCHANGE = 0x0051;

    public static bool IsLayoutMessage(int msg) => msg is WM_INPUTLANGCHANGEREQUEST or WM_INPUTLANGCHANGE;
}

/// <summary>Context menu that hands keyboard-layout messages straight to Windows (see InputLanguageGuard).</summary>
public class SafeContextMenu : ContextMenuStrip
{
    protected override void WndProc(ref Message m)
    {
        if (InputLanguageGuard.IsLayoutMessage(m.Msg)) { DefWndProc(ref m); return; }
        base.WndProc(ref m);
    }
}
