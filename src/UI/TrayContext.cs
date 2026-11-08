using CHelper.Hardware;
using CHelper.Native;

namespace CHelper.UI;

public sealed class TrayContext : ApplicationContext
{
    public const string ShowEventName = "CHelper.ShowWindow";
    public const string QuitEventName = "CHelper.Quit";

    private readonly NotifyIcon _tray;
    private readonly MainForm _form;
    private readonly FnKeys _fnKeys;
    private readonly EventWaitHandle _showEvent;
    private readonly Controller _c = Controller.Instance;
    private GlobalHotkeys _hotkeys;
    private Instance.Window _instance;

    public TrayContext(bool startHidden)
    {
        _form = new MainForm();
        _form.QuitRequested += Quit;
        _ = _form.Handle; // create the handle so background updates can marshal to the UI thread
        _fnKeys = new FnKeys(_form);

        _tray = new NotifyIcon
        {
            Icon = Theme.TrayIcon,
            Text = "C-Helper",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Toggle();
        };

        // Second launches signal this event instead of starting another copy.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                if (_form.IsDisposed) return;
                _form.BeginInvoke(new Action(_form.ShowNearTray));
            }
        }) { IsBackground = true };
        listener.Start();

        // "CHelper.exe --quit" (installer / uninstaller) signals this to exit cleanly.
        var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        new Thread(() =>
        {
            quitEvent.WaitOne();
            if (!_form.IsDisposed) _form.BeginInvoke(new Action(Quit));
        }) { IsBackground = true }.Start();

        _c.StateChanged += () =>
        {
            if (_form.IsDisposed) return;
            _form.BeginInvoke(new Action(UpdateTrayText));
        };

        // Fn keys straight from the BIOS (works without any vendor software)
        LockKeys.Toggled += key => _fnKeys.ShowLock(key);
        LockKeys.Start();

        // Taskbar / Start menu clicks from a non-elevated copy ("show") and the installer ("quit")
        _instance = new Instance.Window();
        _instance.ShowRequested += () => _form.ShowNearTray();
        _instance.QuitRequested += Quit;

        // G-Helper's shortcuts: Ctrl+Shift+F5 next power mode, Ctrl+Shift+Alt+F5 previous
        _hotkeys = new GlobalHotkeys();
        _hotkeys.Register(GlobalHotkeys.MOD_CONTROL | GlobalHotkeys.MOD_SHIFT, Keys.F5, "Ctrl+Shift+F5", () => CycleMode(+1));
        _hotkeys.Register(GlobalHotkeys.MOD_CONTROL | GlobalHotkeys.MOD_SHIFT | 0x1 /* Alt */, Keys.F5, "Ctrl+Shift+Alt+F5", () => CycleMode(-1));

        HotkeyListener.EventReceived += code =>
        {
            if (!_form.IsDisposed) _form.BeginInvoke(new Action(() => _fnKeys.Handle(code)));
        };

        if (!startHidden) _form.ShowNearTray();
        else
        {
            // Started hidden at sign-in: trim once start-up work (hardware reads, first apply) is done.
            var once = new System.Windows.Forms.Timer { Interval = 15000 };
            once.Tick += (_, _) =>
            {
                once.Stop();
                once.Dispose();
                if (!_form.Visible) Task.Run(() => Memory.Trim("startup"));
            };
            once.Start();
        }
    }

    private void Toggle()
    {
        if (_form.Visible) _form.Hide();
        else _form.ShowNearTray();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var modes = new (string Text, PowerMode Mode)[]
        {
            ("Quiet", PowerMode.Quiet),
            ("Power Saving", PowerMode.PowerSaving),
            ("Entertainment", PowerMode.Entertainment),
            ("Performance", PowerMode.Performance),
        };
        foreach (var (text, mode) in modes)
        {
            var item = new ToolStripMenuItem(text) { Tag = mode };
            item.Click += (_, _) =>
            {
                Task.Run(() => _c.SetPowerMode(mode));
                _fnKeys.ShowMode(mode);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());

        var eco = new ToolStripMenuItem("Eco (dGPU Off)") { Name = "eco" };
        eco.Click += (_, _) =>
        {
            var target = Settings.Current.Gpu == GpuMode.Eco ? GpuMode.Standard : GpuMode.Eco;
            if (!_c.InUltimate) Task.Run(() => _c.SetGpuMode(target));
        };
        menu.Items.Add(eco);
        menu.Items.Add(new ToolStripSeparator());

        var open = new ToolStripMenuItem("Open C-Helper");
        open.Click += (_, _) => _form.ShowNearTray();
        menu.Items.Add(open);

        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) => Quit();
        menu.Items.Add(quit);

        menu.Opening += (_, _) =>
        {
            foreach (ToolStripItem i in menu.Items)
            {
                if (i is ToolStripMenuItem mi && mi.Tag is PowerMode m) mi.Checked = m == _c.CurrentMode;
            }
            eco.Checked = Settings.Current.Gpu == GpuMode.Eco;
            eco.Enabled = !_c.InUltimate && !_c.GpuBusy;
        };
        return menu;
    }

    private void UpdateTrayText()
    {
        string mode = _c.CurrentMode == PowerMode.PowerSaving ? "Power Saving" : _c.CurrentMode.ToString();
        string gpu = _c.InUltimate ? "dGPU Only" : _c.DgpuPresent ? "dGPU On" : "dGPU Off";
        _tray.Text = $"C-Helper · {mode} · {gpu}";
    }

    /// <summary>Step to the next (or previous) power mode and show its popup. Runs on the UI thread.</summary>
    private void CycleMode(int step) => _fnKeys.CycleMode(step);

    private void Quit()
    {
        _hotkeys?.Dispose();
        _instance?.Dispose();
        HotkeyListener.Shutdown();
        LockKeys.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _form.QuitRequested -= Quit;
        _form.Dispose();
        ExitThread();
    }
}
