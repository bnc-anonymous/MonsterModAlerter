using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore;
using RectangleF = SharpDX.RectangleF;

namespace MonsterModAlerter;

/// <summary>
/// The seen-this-map tally: how many of each kind of thing the counted rules have matched since entering the area,
/// drawn as a two-column list at a fixed spot on the screen. Counts entities, not matches: an entity several rules picked
/// out is still one thing seen once.
///
/// The counts are owned by the scan. Drawing reads only the ordered snapshot PublishRows leaves behind, rebuilt on the
/// scan that changed it rather than per frame.
/// </summary>
internal sealed class SeenTally
{
    /// <summary>One line of the tally: a shortened metadata path and how many of it.</summary>
    private sealed record Row(string Name, int Count);

    private readonly Dictionary<string, int> _countByName = new(StringComparer.Ordinal);
    private readonly HashSet<uint> _countedIds = new();
    private volatile List<Row> _rows = new();
    private bool _rowsOutOfDate;

    /// <summary>How many kinds of thing the published rows hold.</summary>
    public int KindCount => _rows.Count;

    public void Clear()
    {
        _countByName.Clear();
        _countedIds.Clear();
        _rows = new List<Row>();
        _rowsOutOfDate = false;
    }

    /// <summary>
    /// Adds one entity to the tally, the first time it is matched. Keyed by entity id, so a monster that leaves and
    /// re-enters range is the same one thing rather than two, and so a monster several rules matched is counted once.
    /// </summary>
    public void Record(uint id, string path)
    {
        if (!_countedIds.Add(id))
            return;

        var name = MetadataPaths.ShortNameOf(path);
        _countByName.TryGetValue(name, out var count);
        _countByName[name] = count + 1;
        _rowsOutOfDate = true;
    }

    /// <summary>
    /// Rebuilds the rows drawing reads, if anything was recorded since the last call. Re-sorting a dictionary per frame
    /// to draw the same twenty lines would be the expensive part of the panel.
    /// </summary>
    public void PublishRows()
    {
        if (!_rowsOutOfDate)
            return;

        var rows = new List<Row>(_countByName.Count);

        foreach (var entry in _countByName)
            rows.Add(new Row(entry.Key, entry.Value));

        // Commonest first, since a long list is cut off at the bottom and the rarest thing is the one worth losing. Ties
        // by name, so the order does not shuffle as counts climb.
        rows.Sort((a, b) => b.Count != a.Count
            ? b.Count.CompareTo(a.Count)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        _rows = rows;
        _rowsOutOfDate = false;
    }

    public void Draw(Graphics graphics, SeenPanelSettings panel)
    {
        if (!panel.Show)
            return;

        var rows = _rows;

        // Nothing tallied yet draws nothing at all: the list is the whole panel, so an empty one has nothing to say and
        // no reason to sit on the screen.
        if (rows.Count == 0)
            return;

        using (graphics.SetTextScale(panel.TextScale.Value))
        {
            var shown = Math.Min(rows.Count, panel.MaxRows.Value);

            // Names and counts are measured before anything is drawn, because the count column is right-aligned to the
            // widest row rather than to a guessed width.
            var width = 0f;
            var lineHeight = 0f;
            const float columnGap = 20f;

            for (var i = 0; i < shown; i++)
            {
                var row = rows[i];
                var nameSize = graphics.MeasureText(row.Name);
                var countSize = graphics.MeasureText(row.Count.ToString());

                width = Math.Max(width, nameSize.X + columnGap + countSize.X);
                lineHeight = Math.Max(lineHeight, Math.Max(nameSize.Y, countSize.Y));
            }

            var truncated = rows.Count - shown;
            var footer = truncated > 0 ? $"... and {truncated} more" : null;

            if (footer != null)
            {
                var footerSize = graphics.MeasureText(footer);
                width = Math.Max(width, footerSize.X);
                lineHeight = Math.Max(lineHeight, footerSize.Y);
            }

            float x = panel.X.Value;
            float y = panel.Y.Value;

            if (panel.DrawBackground)
            {
                const float pad = 6f;
                var lines = shown + (footer != null ? 1 : 0);
                graphics.DrawBox(
                    new RectangleF(x - pad, y - pad, width + pad * 2, lines * lineHeight + pad * 2),
                    panel.BackgroundColor.Value);
            }

            for (var i = 0; i < shown; i++)
            {
                var row = rows[i];
                var count = row.Count.ToString();

                graphics.DrawText(row.Name, new Vector2(x, y), panel.TextColor.Value);
                graphics.DrawText(count,
                    new Vector2(x + width - graphics.MeasureText(count).X, y), panel.CountColor.Value);

                y += lineHeight;
            }

            if (footer != null)
                graphics.DrawText(footer, new Vector2(x, y), panel.TextColor.Value);
        }
    }
}
