using System.Drawing.Drawing2D;
using CHelper.Native;

namespace CHelper.UI;

/// <summary>
/// Remove The Stock Control Center: lists what the vendor software left on the laptop, removes the ticked
/// items in a safe order, and can turn the stock services back on.
/// </summary>
public sealed class StockSoftwareForm : Form
{
    private const int ContentWidth = 560;
    private const int Pad = 20;
    private const int RowH = 64;

    private readonly Font _fTitle, _fBody, _fSmall;
    private readonly Label _intro, _note;
    private readonly Panel _list;
    private readonly TileButton _remove, _restore;
    private readonly List<StockRow> _rows = new();
    private bool _busy;

    /// <summary>Raised after a removal or restore, so the main window can re-check its button.</summary>
    public event Action Changed;

    public StockSoftwareForm()
    {
        Text = "Remove The Stock Control Center";
        Icon = Theme.AppIcon;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;

        _fTitle = Theme.Px(S(17), semibold: true);
        _fBody = Theme.Px(S(13.5f), semibold: true);
        _fSmall = Theme.Px(S(12));

        Controls.Add(new Label
        {
            Text = "Remove The Stock Control Center",
            Font = _fTitle,
            AutoSize = true,
            Location = new Point(S(Pad), S(18)),
        });
        _intro = new Label
        {
            Text = "Looking for the stock software…",
            Font = _fSmall,
            ForeColor = Theme.Muted,
            Location = new Point(S(Pad), S(46)),
            Size = new Size(S(ContentWidth), S(20)),
        };
        Controls.Add(_intro);

        _list = new Panel { Location = new Point(S(Pad), S(76)), Size = new Size(S(ContentWidth), S(RowH)), BackColor = Theme.Back };
        Controls.Add(_list);

        _note = new Label
        {
            Text = "Services can be turned back on here. Store apps can't be put back from here, and the files " +
                   "stay in the Recycle Bin until you empty it. Restart Windows when it's done.",
            Font = _fSmall,
            ForeColor = Theme.Muted,
            Size = new Size(S(ContentWidth), S(36)),
        };
        Controls.Add(_note);

        _restore = new TileButton
        {
            Text = "Turn Services Back On",
            Vertical = false,
            Font = _fSmall,
            Size = new Size(S(200), S(38)),
        };
        _restore.Click += async (_, _) => await RestoreAsync();
        Controls.Add(_restore);

        _remove = new TileButton
        {
            Text = "Remove Selected",
            Vertical = false,
            Active = true,
            Accent = Theme.Turbo,
            Font = _fBody,
            Size = new Size(S(200), S(38)),
            Enabled = false,
        };
        _remove.Click += async (_, _) => await RemoveAsync();
        Controls.Add(_remove);

        Arrange(1);
        Shown += async (_, _) => await LoadAsync();
    }

    private int S(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    protected override void WndProc(ref Message m)
    {
        // Keyboard-layout changes go straight to Windows (see InputLanguageGuard).
        if (InputLanguageGuard.IsLayoutMessage(m.Msg)) { DefWndProc(ref m); return; }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    private void Arrange(int rows)
    {
        int listH = Math.Max(1, rows) * (RowH + 8) - 8;
        _list.Height = S(listH);
        int y = 76 + listH + 14;
        _note.Location = new Point(S(Pad), S(y));
        y += 36 + 14;
        _restore.Location = new Point(S(Pad), S(y));
        _remove.Location = new Point(S(Pad + ContentWidth - 200), S(y));
        ClientSize = new Size(S(ContentWidth + Pad * 2), S(y + 38 + Pad));
    }

    private async Task LoadAsync()
    {
        List<StockItem> items;
        try
        {
            items = await Task.Run(StockSoftware.Detect);
        }
        catch (Exception ex)
        {
            Log.Error("Looking for the stock software failed", ex);
            _intro.Text = "Couldn't check for the stock software (see the log).";
            return;
        }
        if (IsDisposed) return;

        _list.Controls.Clear();
        _rows.Clear();
        int y = 0;
        foreach (var item in items)
        {
            var row = new StockRow(item, _fBody, _fSmall)
            {
                Location = new Point(0, S(y)),
                Size = new Size(S(ContentWidth), S(RowH)),
            };
            row.CheckedChanged += UpdateButtons;
            _rows.Add(row);
            _list.Controls.Add(row);
            y += RowH + 8;
        }
        _intro.Text = items.Count == 0
            ? "Nothing from the stock software is left on this laptop."
            : "C-Helper doesn't need any of this. Found on this laptop:";
        Arrange(items.Count);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _remove.Enabled = !_busy && _rows.Any(r => r.Checked);
        _restore.Enabled = !_busy && _rows.Any(r =>
            r.Item.Kind is StockItemKind.HotkeyService or StockItemKind.ClipboardService);
    }

    private async Task RemoveAsync()
    {
        var chosen = _rows.Where(r => r.Checked).ToList();
        if (chosen.Count == 0) return;
        var answer = MessageBox.Show(this,
            "Remove the selected stock software?\n\nStore apps can't be put back from C-Helper. Files go to the Recycle Bin.",
            "C-Helper", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        _busy = true;
        UpdateButtons();
        int failed = 0;
        foreach (var row in chosen) // rows are already in a safe order: services, apps, then files
        {
            row.SetResult("Working…", Theme.Muted);
            try
            {
                string result = await Task.Run(() => StockSoftware.Remove(row.Item.Kind));
                row.SetResult(result, Theme.Optimized, done: true);
            }
            catch (Exception ex)
            {
                failed++;
                Log.Error($"Removing {row.Item.Title} failed", ex);
                row.SetResult("Failed (see the log)", Theme.Turbo);
            }
        }
        _busy = false;
        _intro.Text = failed == 0 ? "Done. Restart Windows to finish." : "Some items couldn't be removed. The log has the details.";
        UpdateButtons();
        Changed?.Invoke();
    }

    private async Task RestoreAsync()
    {
        _busy = true;
        UpdateButtons();
        try
        {
            await Task.Run(StockSoftware.RestoreServices);
        }
        catch (Exception ex)
        {
            Log.Error("Turning the stock services back on failed", ex);
        }
        _busy = false;
        await LoadAsync();
        Changed?.Invoke();
    }

    /// <summary>One item: tick box, name, details and what will happen (or what happened).</summary>
    private sealed class StockRow : Control
    {
        private readonly Font _title, _small;
        private string _result;
        private Color _resultColor;
        private bool _done;

        public StockItem Item { get; }
        public bool Checked { get; private set; }
        public event Action CheckedChanged;

        public StockRow(StockItem item, Font title, Font small)
        {
            Item = item;
            _title = title;
            _small = small;
            Checked = item.Removable;
            Cursor = item.Removable ? Cursors.Hand : Cursors.Default;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Back;
        }

        public void SetResult(string text, Color color, bool done = false)
        {
            _result = text;
            _resultColor = color;
            _done = done;
            if (done) Checked = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!Item.Removable || _done || _result != null) return;
            Checked = !Checked;
            Invalidate();
            CheckedChanged?.Invoke();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float k = DeviceDpi / 96f;
            int P(float v) => (int)Math.Round(v * k);

            var box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.RoundRect(box, P(10)))
            using (var fill = new SolidBrush(Color.FromArgb(0x16, 0x18, 0x1D)))
            using (var pen = new Pen(Theme.Line))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            // tick box
            var cb = new Rectangle(P(14), P(14), P(18), P(18));
            using (var cbPath = Theme.RoundRect(cb, P(4)))
            {
                if (_done)
                {
                    using var b = new SolidBrush(Theme.Optimized);
                    g.FillPath(b, cbPath);
                    DrawTick(g, cb, k);
                }
                else if (!Item.Removable)
                {
                    using var pen = new Pen(Color.FromArgb(0x44, 0x49, 0x52), P(2));
                    g.DrawPath(pen, cbPath);
                    g.DrawLine(pen, cb.Left + P(5), cb.Top + cb.Height / 2, cb.Right - P(5), cb.Top + cb.Height / 2);
                }
                else if (Checked)
                {
                    using var b = new SolidBrush(Theme.Accent);
                    g.FillPath(b, cbPath);
                    DrawTick(g, cb, k);
                }
                else
                {
                    using var pen = new Pen(Theme.LineHover, P(2));
                    g.DrawPath(pen, cbPath);
                }
            }

            string action = _result ?? Item.Action;
            Color actionColor = _result != null ? _resultColor
                : Item.Removable && Checked ? Theme.Turbo : Theme.Muted;
            var actionSize = TextRenderer.MeasureText(action, _small);
            int right = Width - P(16);
            TextRenderer.DrawText(g, action, _small, new Point(right - actionSize.Width, P(14)), actionColor);

            int textX = P(44);
            TextRenderer.DrawText(g, Item.Title, _title, new Rectangle(textX, P(11), right - actionSize.Width - textX - P(10), P(22)),
                Item.Removable || _done ? Theme.Text : Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Item.Detail, _small, new Rectangle(textX, P(35), Width - textX - P(16), P(20)),
                Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.PathEllipsis);
        }

        private static void DrawTick(Graphics g, Rectangle r, float k)
        {
            using var pen = new Pen(Theme.Back, 2.6f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(r.Left + 4.5f * k, r.Top + 9.5f * k),
                new PointF(r.Left + 8f * k, r.Top + 13f * k),
                new PointF(r.Left + 14f * k, r.Top + 5.5f * k),
            });
        }
    }
}
