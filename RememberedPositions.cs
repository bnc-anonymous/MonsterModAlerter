using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace MonsterModAlerter;

/// <summary>
/// Where an entity was when it was last in range, for rules with Remember position on. Holds the grid position rather
/// than the Entity: the entity is gone from the list by the time this is drawn, and reading a stale one is not safe.
/// </summary>
internal sealed record RememberedPosition(uint Id, Vector2 GridPos, AlertRule Rule, int ModCount);

/// <summary>
/// The remembered positions by entity id. Owned by the scan; drawing only sees the list Snapshot returns.
/// </summary>
internal sealed class RememberedPositions
{
    private readonly Dictionary<uint, RememberedPosition> _byId = new();

    /// <summary>
    /// How close the player must come to a remembered position, in grid units, before a position with nothing there is
    /// dropped (ForgetAbsentNear). It must stay inside the range at which monsters load, or a live monster just beyond
    /// that range would be forgotten while still alive. 120 is the value the Forget radius setting held in play when
    /// the setting was removed, 2026-11-13; its default had been 60.
    /// </summary>
    public const int ForgetRadius = 120;

    public int Count => _byId.Count;

    public void Remember(RememberedPosition position) => _byId[position.Id] = position;

    public void Forget(uint id) => _byId.Remove(id);

    public void Clear() => _byId.Clear();

    /// <summary>
    /// Drops remembered positions for entities that are no longer there.
    ///
    /// Seeing a monster die is not enough on its own: kill one and walk away, and it leaves the entity list without
    /// ever being observed dead, so its icon would sit on the map for the rest of the zone. Absence from the entity
    /// list is only evidence of that when the player is close enough that the game would have loaded it - further out,
    /// absence just means out of range.
    /// </summary>
    /// <param name="loadedIds">Every entity the scan saw loaded, whatever its state.</param>
    public void ForgetAbsentNear(Vector2 playerGridPos, HashSet<uint> loadedIds)
    {
        const float radiusSquared = (float)ForgetRadius * ForgetRadius;

        List<uint> gone = null;

        foreach (var entry in _byId.Values)
        {
            if (loadedIds.Contains(entry.Id))
                continue;

            // Squared distance: this runs over every remembered entity on every scan, and the square root would buy
            // nothing a squared threshold does not.
            var dx = entry.GridPos.X - playerGridPos.X;
            var dy = entry.GridPos.Y - playerGridPos.Y;

            if (dx * dx + dy * dy <= radiusSquared)
                (gone ??= []).Add(entry.Id);
        }

        if (gone == null)
            return;

        foreach (var id in gone)
            _byId.Remove(id);
    }

    /// <summary>
    /// The remembered positions whose rule still asks for them, live entities' included, for drawing, in ascending
    /// priority so a higher-priority icon and its number are drawn over a lower one's. Which of them to show is decided
    /// each frame (MatchDrawing.Draw): those whose entity is not drawn live. Deciding it here, at the scan, left a gap
    /// of up to a scan interval when a monster left range, in which neither icon was drawn.
    /// </summary>
    /// <param name="rules">
    /// The rules the scan ran. A position whose rule is not among them - switched off, in a group switched off, or
    /// deleted - is left out, so its icon cannot outlive the rule. Checking the rule's own Enabled was not enough: a
    /// deleted rule is still enabled, and the group's switch is not the rule's.
    /// </param>
    public List<RememberedPosition> Snapshot(AlertRule[] rules)
    {
        var snapshot = new List<RememberedPosition>(_byId.Count);

        foreach (var entry in _byId.Values)
        {
            if (entry.Rule.RememberPosition && rules.Contains(entry.Rule))
                snapshot.Add(entry);
        }

        snapshot.Sort((a, b) => a.Rule.Priority.Value.CompareTo(b.Rule.Priority.Value));

        return snapshot;
    }
}
