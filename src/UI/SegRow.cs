namespace CHelper.UI;

/// <summary>A row of equal-width tiles. One can be highlighted (Select) or each toggled (SetOn).</summary>
public sealed class SegRow : TableLayoutPanel
{
    private readonly List<TileButton> _tiles = new();

    public event Action<int> ButtonClicked;

    public record Tile(string Text, string Icon = null, Color? Accent = null);

    public SegRow(int height, int gap, Font font, bool vertical, int iconSize, params Tile[] items)
    {
        ColumnCount = items.Length;
        RowCount = 1;
        Height = height;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        BackColor = Theme.Back;
        RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        int half = gap / 2;
        for (int i = 0; i < items.Length; i++)
        {
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / items.Length));
            var t = new TileButton
            {
                Text = items[i].Text,
                IconName = items[i].Icon,
                Accent = items[i].Accent ?? Theme.Standard,
                Font = font,
                Vertical = vertical,
                IconSize = iconSize,
                Dock = DockStyle.Fill,
                Margin = new Padding(i == 0 ? 0 : half, 0, i == items.Length - 1 ? 0 : gap - half, 0),
            };
            int index = i;
            t.Click += (_, _) => ButtonClicked?.Invoke(index);
            _tiles.Add(t);
            Controls.Add(t, i, 0);
        }
    }

    public int Count => _tiles.Count;

    public TileButton this[int index] => _tiles[index];

    public void SetOn(int index, bool on) => _tiles[index].Active = on;

    /// <summary>Highlights exactly one tile (or none when index is -1).</summary>
    public void Select(int index)
    {
        for (int i = 0; i < _tiles.Count; i++) _tiles[i].Active = i == index;
    }

    public void SetEnabled(int index, bool enabled) => _tiles[index].Enabled = enabled;
}
