using System.Collections.Generic;
using System.Numerics;

namespace MonsterModAlerter;

/// <summary>
/// Where an entity was when it was last in range, for rules with Remember Position on. Holds the grid position rather
/// than the Entity: the entity is gone from the list by the time this is drawn, and reading a stale one is not safe.
/// </summary>
internal sealed record RememberedPosition(uint Id, Vector2 GridPos, AlertRule Rule, int ModCount);

/// <summary>
/// The remembered positions by entity id. Owned by the scan; drawing only sees the list OutOfRangeOf returns.
/// </summary>
internal sealed class RememberedPositions
{
    private readonly Dictionary<uint, RememberedPosition> _byId = new();

    public int Count => _byId.Count;

    public void Remember(RememberedPosition position) => _byId[position.Id] = position;

    public void Forget(uint id) => _byId.Remove(id);

    public void Clear() => _byId.Clear();

    /// <summary>
    /// Drops remembered positions for entities that are no longer there.
    ///
    /// Seeing a monster die is not enough on its own: kill one and walk away, and it leaves the entity list without ever
    /// being observed dead, so its icon would sit on the map for the rest of the zone. Absence from the entity list is
    /// only evidence of that when the player is close enough that the game would have loaded it - further out, absence
    /// just means out of range.
    /// </summary>
    /// <param name="loadedIds">Every entity the scan saw loaded, whatever its state.</param>
    public void ForgetAbsentNear(Vector2 playerGridPos, int radius, HashSet<uint> loadedIds)
    {
        var radiusSquared = (float)radius * radius;

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
    /// The remembered positions of entities that are not currently matched live, so they can be drawn without doubling
    /// up on the ones already drawn live.
    /// </summary>
    public List<RememberedPosition> OutOfRangeOf(List<RuleMatch> live)
    {
        if (_byId.Count == 0)
            return new List<RememberedPosition>();

        var liveIds = new HashSet<uint>(live.Count);
        foreach (var match in live)
            liveIds.Add(match.Entity.Id);

        var stale = new List<RememberedPosition>(_byId.Count);
        foreach (var entry in _byId.Values)
        {
            // Dropped rather than kept when its rule is switched off or deleted, so stale icons cannot outlive the rule
            // that asked for them.
            if (!liveIds.Contains(entry.Id) && entry.Rule.Enabled && entry.Rule.RememberPosition)
                stale.Add(entry);
        }

        return stale;
    }
}
