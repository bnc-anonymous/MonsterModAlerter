using ExileCore.PoEMemory.MemoryObjects;

namespace MonsterModAlerter;

/// <summary>
/// An entity matching an alert rule, plus the mod/buff text that triggered it and the count the rule shows. The id and
/// the count are read during the scan, so the sound and the drawing read nothing of the entity but its position.
/// </summary>
/// <param name="CheckAlive">
/// Whether IsAlive says anything about this entity. True for anything out of the monster list; false for the rest,
/// where a chest or a door has no life to be alive with and asking would hide it. Their equivalent check is a Life
/// component reading zero (EntityScan.IsDead).
/// </param>
/// <param name="Slot">
/// Which of this entity's matches this is, in priority order. The icons themselves are drawn stacked on the entity, so
/// this only separates the things that cannot overlap readably - world labels - and marks the one match that draws the
/// shared box.
/// </param>
internal sealed record RuleMatch(
    Entity Entity, uint EntityId, AlertRule Rule, string MatchedName, int ModCount, bool CheckAlive = true,
    int Slot = 0, int SlotCount = 1);
